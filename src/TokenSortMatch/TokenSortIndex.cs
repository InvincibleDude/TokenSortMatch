using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace TokenSortMatch;

/// <summary>A best-match result: the item index (into the constructor list) and its score.</summary>
public readonly record struct BestMatch(int ItemIndex, int Score);

/// <summary>
/// Precomputed index over a list of candidate items for fast token-sort best-matching.
///
/// Scores are exactly <see cref="TokenSort.Ratio"/> — no approximation is ever traded
/// for speed. Construction normalizes the items, sorts them by normalized length and
/// builds transposed pattern bitmasks so queries run a bit-parallel LCS (Hyyrö/Myers)
/// over 4 items per pass with <see cref="Vector256{T}"/> when the hardware accelerates
/// it (an equivalent scalar path is used otherwise). Groups of items are pruned with an
/// exactly-rounded score upper bound against the running best. Items whose normalized
/// form exceeds 64 chars use a multi-block scalar path.
///
/// Instances are immutable and safe for concurrent use.
/// </summary>
public sealed class TokenSortIndex
{
	const int Lanes = 4;

	readonly string[] _itemsNorm;    // normalized items, sorted by normalized length
	readonly int[] _sortedToOriginal;
	readonly int _bpStart;           // first non-empty normalized item (sorted order)
	readonly int _bpEnd;             // end of the <= 64-char range == first long item
	readonly ushort[] _charIdx;      // char -> alphabet code; 0 = absent (zero mask)
	readonly int _width;             // bit-parallel column count, padded to Lanes
	readonly ulong[] _peqT;          // transposed bitmasks: [code * _width + col]
	readonly ulong[] _lenMaskCol;
	readonly int[] _itemLenCol;
	readonly int[] _groupMinLen;
	readonly int[] _groupMaxLen;
	readonly LongItem[] _longItems;  // normalized length > 64
	readonly int _maxBlocks;

	sealed class LongItem
	{
		public required int OriginalIndex;
		public required int Length;
		public required int Blocks;
		public required ulong[] Peq; // [code * Blocks + block]
		public required ulong LastMask;
	}

	/// <summary>Number of items in the index.</summary>
	public int Count { get; }

	/// <summary>Builds an index over <paramref name="items"/>.</summary>
	/// <exception cref="ArgumentNullException"><paramref name="items"/> is null.</exception>
	/// <exception cref="ArgumentException">An element of <paramref name="items"/> is null.</exception>
	public TokenSortIndex(IReadOnlyList<string> items)
	{
		ArgumentNullException.ThrowIfNull(items);
		var n = Count = items.Count;
		for (var i = 0; i < n; i++)
			if (items[i] is null)
				throw new ArgumentException($"items[{i}] is null.", nameof(items));

		var order = Enumerable.Range(0, n)
			.OrderBy(i => TokenSort.Normalize(items[i]).Length)
			.ToArray(); // OrderBy is stable: equal lengths keep original order
		_itemsNorm = new string[n];
		_sortedToOriginal = order;
		for (var t = 0; t < n; t++)
			_itemsNorm[t] = TokenSort.Normalize(items[order[t]]);

		// Partition (sorted by length): [0,bpStart) empty, [bpStart,bpEnd) bit-parallel,
		// [bpEnd,n) long items.
		var bpStart = 0;
		while (bpStart < n && _itemsNorm[bpStart].Length == 0) bpStart++;
		var bpEnd = bpStart;
		while (bpEnd < n && _itemsNorm[bpEnd].Length <= 64) bpEnd++;
		_bpStart = bpStart;
		_bpEnd = bpEnd;

		_charIdx = new ushort[65536];
		var alpha = 1;
		for (var t = bpStart; t < n; t++)
			foreach (var c in _itemsNorm[t])
				if (_charIdx[c] == 0)
					_charIdx[c] = (ushort)alpha++;

		_width = (bpEnd - bpStart + Lanes - 1) / Lanes * Lanes;
		_peqT = new ulong[alpha * _width];
		_lenMaskCol = new ulong[_width];
		_itemLenCol = new int[_width];
		for (var t = bpStart; t < bpEnd; t++)
		{
			var s = _itemsNorm[t];
			var col = t - bpStart;
			_itemLenCol[col] = s.Length;
			_lenMaskCol[col] = s.Length == 64 ? ulong.MaxValue : (1UL << s.Length) - 1;
			for (var j = 0; j < s.Length; j++)
				_peqT[_charIdx[s[j]] * _width + col] |= 1UL << j;
		}

		// Lanes ascend within a group (items are length-sorted; zero pads only at the tail).
		_groupMinLen = new int[_width / Lanes];
		_groupMaxLen = new int[_width / Lanes];
		for (var g = 0; g < _groupMinLen.Length; g++)
		{
			_groupMinLen[g] = _itemLenCol[g * Lanes];
			var mx = 0;
			for (var lane = 0; lane < Lanes; lane++)
				mx = Math.Max(mx, _itemLenCol[g * Lanes + lane]);
			_groupMaxLen[g] = mx;
		}

		_longItems = new LongItem[n - bpEnd];
		for (var t = bpEnd; t < n; t++)
		{
			var s = _itemsNorm[t];
			var blocks = (s.Length + 63) >> 6;
			var peq = new ulong[alpha * blocks];
			for (var j = 0; j < s.Length; j++)
				peq[_charIdx[s[j]] * blocks + (j >> 6)] |= 1UL << (j & 63);
			_longItems[t - bpEnd] = new LongItem
			{
				OriginalIndex = order[t],
				Length = s.Length,
				Blocks = blocks,
				Peq = peq,
				LastMask = TokenSort.LastBlockMask(s.Length),
			};
			_maxBlocks = Math.Max(_maxBlocks, blocks);
		}
	}

	/// <summary>
	/// Finds an item with the highest <see cref="TokenSort.Ratio"/> against
	/// <paramref name="query"/>, or null if no item reaches <paramref name="threshold"/>.
	/// Ties return one deterministic best-scoring item.
	/// </summary>
	/// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
	public BestMatch? FindBest(string query, int threshold)
	{
		ArgumentNullException.ThrowIfNull(query);
		var (score, index) = FindBestCore(query, threshold, GetWorker());
		return score >= threshold && index >= 0 ? new BestMatch(index, score) : null;
	}

	/// <summary>
	/// Batch variant of <see cref="FindBest"/>; scores all queries in parallel.
	/// <c>result[i]</c> is the best score of <c>queries[i]</c> if it reaches
	/// <paramref name="threshold"/>, else -1.
	/// </summary>
	/// <exception cref="ArgumentNullException"><paramref name="queries"/> is null.</exception>
	/// <exception cref="ArgumentException">An element of <paramref name="queries"/> is null.</exception>
	public int[] FindBestScores(IReadOnlyList<string> queries, int threshold)
	{
		ArgumentNullException.ThrowIfNull(queries);
		var m = queries.Count;
		for (var i = 0; i < m; i++)
			if (queries[i] is null)
				throw new ArgumentException($"queries[{i}] is null.", nameof(queries));

		var best = new int[m];
		Parallel.ForEach(Partitioner.Create(0, m), range =>
		{
			var worker = GetWorker();
			for (var i = range.Item1; i < range.Item2; i++)
			{
				var (score, _) = FindBestCore(queries[i], threshold, worker);
				best[i] = score >= threshold ? score : -1;
			}
		});
		return best;
	}

	/// <summary>Batch variant of <see cref="FindBest"/> returning matches with item indices.</summary>
	/// <exception cref="ArgumentNullException"><paramref name="queries"/> is null.</exception>
	/// <exception cref="ArgumentException">An element of <paramref name="queries"/> is null.</exception>
	public BestMatch?[] FindBestMatches(IReadOnlyList<string> queries, int threshold)
	{
		ArgumentNullException.ThrowIfNull(queries);
		var m = queries.Count;
		for (var i = 0; i < m; i++)
			if (queries[i] is null)
				throw new ArgumentException($"queries[{i}] is null.", nameof(queries));

		var best = new BestMatch?[m];
		Parallel.ForEach(Partitioner.Create(0, m), range =>
		{
			var worker = GetWorker();
			for (var i = range.Item1; i < range.Item2; i++)
			{
				var (score, index) = FindBestCore(queries[i], threshold, worker);
				best[i] = score >= threshold && index >= 0 ? new BestMatch(index, score) : null;
			}
		});
		return best;
	}

	(int Score, int Index) FindBestCore(string query, int threshold, Worker worker)
	{
		// Scores are never negative, so any threshold <= 0 behaves like "best always
		// qualifies"; clamping also avoids overflow for extreme thresholds.
		var b = threshold > 0 ? threshold - 1 : -1;
		var bestIdx = -1;
		var l1 = worker.Normalize(query, _charIdx, _width);
		if (l1 > 0)
		{
			var codes = worker.Codes;
			var done = false;

			for (int g = 0, col0 = 0; col0 < _width && !done; g++, col0 += Lanes)
			{
				// Clamped group bound: ub(l1, l2) is unimodal in l2 with peak at l2 == l1,
				// so ub(l1, clamp(l1, minLen, maxLen)) bounds every lane in the group.
				var l2c = Math.Clamp(l1, _groupMinLen[g], _groupMaxLen[g]);
				if (TokenSort.UpperBoundScore(l1, l2c) <= b) continue;

				if (Vector256.IsHardwareAccelerated)
				{
					// 4 items per pass: lanes are independent in the LCS recurrence.
					var s = Vector256.LoadUnsafe(ref _lenMaskCol[col0]);
					for (var j = 0; j < l1; j++)
					{
						var m = Vector256.LoadUnsafe(ref _peqT[codes[j] + col0]);
						var u = s & m;
						s = (s + u) | (s - u);
					}

					for (var lane = 0; lane < Lanes; lane++)
					{
						var l2 = _itemLenCol[col0 + lane];
						if (l2 == 0) continue;
						var lcs = BitOperations.PopCount(~s.GetElement(lane) & _lenMaskCol[col0 + lane]);
						var score = TokenSort.ScoreFromLcs(l1, l2, lcs);
						if (score > b)
						{
							b = score;
							bestIdx = _sortedToOriginal[_bpStart + col0 + lane];
							if (b == 100) { done = true; break; }
						}
					}
				}
				else
				{
					for (var lane = 0; lane < Lanes; lane++)
					{
						var l2 = _itemLenCol[col0 + lane];
						if (l2 == 0) continue;
						if (TokenSort.UpperBoundScore(l1, l2) <= b) continue;
						var s = _lenMaskCol[col0 + lane];
						for (var j = 0; j < l1; j++)
						{
							var u = s & _peqT[codes[j] + col0 + lane];
							unchecked { s = (s + u) | (s - u); }
						}
						var lcs = BitOperations.PopCount(~s & _lenMaskCol[col0 + lane]);
						var score = TokenSort.ScoreFromLcs(l1, l2, lcs);
						if (score > b)
						{
							b = score;
							bestIdx = _sortedToOriginal[_bpStart + col0 + lane];
							if (b == 100) { done = true; break; }
						}
					}
				}
			}

			if (!done)
			{
				foreach (var item in _longItems)
				{
					if (TokenSort.UpperBoundScore(l1, item.Length) <= b) continue;
					var lcs = LcsLong(item, worker, l1);
					var score = TokenSort.ScoreFromLcs(l1, item.Length, lcs);
					if (score > b)
					{
						b = score;
						bestIdx = item.OriginalIndex;
						if (b == 100) break;
					}
				}
			}
		}
		if (b < 0 && Count > 0)
		{
			// No pair was scored (empty query, or only empty items): every score is 0.
			b = 0;
			bestIdx = 0;
		}
		return (b, bestIdx);
	}

	int LcsLong(LongItem item, Worker worker, int l1)
	{
		var blocks = item.Blocks;
		var s = worker.Blocks(_maxBlocks).AsSpan(0, blocks);
		s.Fill(ulong.MaxValue);
		s[blocks - 1] = item.LastMask;
		var peq = item.Peq;
		var norm = worker.Norm;
		for (var j = 0; j < l1; j++)
		{
			var off = _charIdx[norm[j]] * blocks;
			ulong carry = 0, borrow = 0;
			for (var k = 0; k < blocks; k++)
			{
				var sk = s[k];
				var u = sk & peq[off + k];
				unchecked
				{
					var t = sk + u;
					var c1 = t < sk ? 1UL : 0UL;
					var sum = t + carry;
					var c2 = sum < t ? 1UL : 0UL;
					carry = c1 | c2;
					var t1 = sk - u;
					var b1 = sk < u ? 1UL : 0UL;
					var diff = t1 - borrow;
					var b2 = t1 < borrow ? 1UL : 0UL;
					borrow = b1 | b2;
					s[k] = sum | diff;
				}
			}
		}
		return TokenSort.CountZeroBits(s, item.LastMask);
	}

	[ThreadStatic] static Worker? _worker;

	static Worker GetWorker() => _worker ??= new Worker();

	/// <summary>Per-thread scratch buffers; content never outlives one FindBestCore call.</summary>
	sealed class Worker
	{
		readonly Normalizer _normalizer = new();
		ulong[] _blocks = [];

		public int[] Codes = new int[128];

		public char[] Norm => _normalizer.Buffer;

		public int Normalize(string query, ushort[] charIdx, int width)
		{
			var len = _normalizer.Normalize(query);
			var norm = _normalizer.Buffer;
			if (Codes.Length < len) Codes = new int[Math.Max(len, Codes.Length * 2)];
			for (var j = 0; j < len; j++)
				Codes[j] = charIdx[norm[j]] * width;
			return len;
		}

		public ulong[] Blocks(int maxBlocks)
		{
			if (_blocks.Length < maxBlocks) _blocks = new ulong[maxBlocks];
			return _blocks;
		}
	}
}
