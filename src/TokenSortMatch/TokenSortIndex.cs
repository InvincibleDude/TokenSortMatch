using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace TokenSortMatch;

/// <summary>A best-match result: the item index (into the constructor list) and its score.</summary>
public readonly record struct BestMatch(int ItemIndex, int Score);

/// <summary>
/// Precomputed index over a list of candidate items for fast token-sort best-matching.
///
/// Scores are exactly <see cref="TokenSort.Ratio"/> — no approximation is ever traded
/// for speed. Construction normalizes the items, sorts them by normalized length and
/// packs them into length buckets of transposed pattern bitmasks: items whose
/// normalized form fits 16/32/64 bits run a bit-parallel LCS (Hyyrö/Myers) in
/// 16/32/64-bit SIMD lanes — 16, 8 or 4 items per <see cref="Vector256{T}"/> pass —
/// when the hardware accelerates it (an equivalent scalar path is used otherwise).
/// Groups of items are pruned with an exactly-rounded score upper bound against the
/// running best. Items whose normalized form exceeds 64 chars use a multi-block
/// scalar path.
///
/// Instances are immutable and safe for concurrent use.
/// </summary>
public sealed class TokenSortIndex
{
	readonly string[] _itemsNorm;    // normalized items, sorted by normalized length
	readonly int[] _sortedToOriginal;
	readonly ushort[] _charIdx;      // char -> alphabet code; 0 = absent (zero mask)
	readonly Bucket<ushort> _b16;    // normalized length 1..16, 16 lanes per pass
	readonly Bucket<uint> _b32;      // 17..32, 8 lanes per pass
	readonly Bucket<ulong> _b64;     // 33..64 (or 1..64 without SIMD), 4 lanes
	readonly LongItem[] _longItems;  // normalized length > 64, scalar fallback path
	readonly LongGroup[] _longGroups; // normalized length > 64, 4 ulong lanes per pass
	readonly int _maxBlocks;

	const int LongLanes = 4; // Vector256<ulong>.Count

	/// <summary>
	/// A length bucket: lane-transposed bitmasks for items whose LCS state fits the
	/// lane type <typeparamref name="T"/>. Column layout is padded to whole
	/// <see cref="Vector256{T}"/> groups; zero-length pad lanes never score.
	/// </summary>
	sealed class Bucket<T> where T : unmanaged
	{
		public required T[] PeqT;         // transposed bitmasks: [code * Width + col]
		public required T[] LenMaskCol;   // per-lane initial state == length mask
		public required int[] ItemLenCol;
		public required int[] GroupMinLen;
		public required int[] GroupMaxLen;
		public required int SortedBase;   // first sorted-order index in this bucket
		public int Width => ItemLenCol.Length;
	}

	sealed class LongItem
	{
		public required int OriginalIndex;
		public required int Length;
		public required int Blocks;
		public required ulong[] Peq; // [code * Blocks + block]
		public required ulong LastMask;
	}

	/// <summary>
	/// Four long items scored per pass in <see cref="Vector256{T}"/> ulong lanes, with
	/// the multi-block carry/borrow chains carried per lane. Lanes past a lane's real
	/// block count stay all-ones pads (their bitmask rows are zero and block carries
	/// only propagate upward), so short lanes coexist with the group's longest item.
	/// </summary>
	sealed class LongGroup
	{
		public required int Blocks;         // max blocks across lanes
		public required ulong[] PeqT;       // [(code * Blocks + block) * 4 + lane]
		public required ulong[] InitState;  // [block * 4 + lane]
		public required int[] Lengths;      // per lane; 0 = pad lane
		public required int[] OriginalIndex;
		public required int MinLen;
		public required int MaxLen;
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

		_charIdx = new ushort[65536];
		var alpha = 1;
		for (var t = bpStart; t < n; t++)
			foreach (var c in _itemsNorm[t])
				if (_charIdx[c] == 0)
					_charIdx[c] = (ushort)alpha++;

		// Without SIMD every <= 64-char item goes to the 64-bit bucket, whose scalar
		// path matches the vector recurrence exactly.
		var e16 = bpStart;
		var e32 = bpStart;
		if (Vector256.IsHardwareAccelerated)
		{
			while (e16 < bpEnd && _itemsNorm[e16].Length <= 16) e16++;
			e32 = e16;
			while (e32 < bpEnd && _itemsNorm[e32].Length <= 32) e32++;
		}
		_b16 = BuildBucket<ushort>(bpStart, e16, alpha);
		_b32 = BuildBucket<uint>(e16, e32, alpha);
		_b64 = BuildBucket<ulong>(e32, bpEnd, alpha);

		if (Vector256.IsHardwareAccelerated)
		{
			// Long items in groups of 4 ulong lanes, vectorized carry/borrow chains.
			_longItems = [];
			_longGroups = new LongGroup[(n - bpEnd + LongLanes - 1) / LongLanes];
			for (var g = 0; g < _longGroups.Length; g++)
			{
				var start = bpEnd + g * LongLanes;
				var laneCount = Math.Min(LongLanes, n - start);
				// Items are length-sorted, so the last lane has the group's max blocks.
				var blocks = (_itemsNorm[start + laneCount - 1].Length + 63) >> 6;
				var grp = new LongGroup
				{
					Blocks = blocks,
					PeqT = new ulong[alpha * blocks * LongLanes],
					InitState = new ulong[blocks * LongLanes],
					Lengths = new int[LongLanes],
					OriginalIndex = new int[LongLanes],
					MinLen = _itemsNorm[start].Length,
					MaxLen = _itemsNorm[start + laneCount - 1].Length,
				};
				grp.InitState.AsSpan().Fill(ulong.MaxValue);
				for (var lane = 0; lane < laneCount; lane++)
				{
					var s = _itemsNorm[start + lane];
					grp.Lengths[lane] = s.Length;
					grp.OriginalIndex[lane] = order[start + lane];
					// Pad blocks past a lane's last stay all-ones: their peq rows are zero
					// and upward carries never feed back into real blocks.
					grp.InitState[(((s.Length + 63) >> 6) - 1) * LongLanes + lane] =
						TokenSort.LastBlockMask(s.Length);
					for (var j = 0; j < s.Length; j++)
						grp.PeqT[(_charIdx[s[j]] * blocks + (j >> 6)) * LongLanes + lane] |= 1UL << (j & 63);
				}
				_longGroups[g] = grp;
				_maxBlocks = Math.Max(_maxBlocks, blocks);
			}
		}
		else
		{
			_longGroups = [];
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
	}

	Bucket<T> BuildBucket<T>(int start, int end, int alpha) where T : unmanaged, IBinaryInteger<T>
	{
		var lanes = Vector256<T>.Count;
		var width = (end - start + lanes - 1) / lanes * lanes;
		var bkt = new Bucket<T>
		{
			PeqT = new T[alpha * width],
			LenMaskCol = new T[width],
			ItemLenCol = new int[width],
			GroupMinLen = new int[width / lanes],
			GroupMaxLen = new int[width / lanes],
			SortedBase = start,
		};
		for (var t = start; t < end; t++)
		{
			var s = _itemsNorm[t];
			var col = t - start;
			bkt.ItemLenCol[col] = s.Length;
			bkt.LenMaskCol[col] = T.CreateTruncating(LaneMask(s.Length));
			for (var j = 0; j < s.Length; j++)
				bkt.PeqT[_charIdx[s[j]] * width + col] |= T.CreateTruncating(1UL << j);
		}
		// Lanes ascend within a group (items are length-sorted; zero pads only at the tail).
		for (var g = 0; g < bkt.GroupMinLen.Length; g++)
		{
			bkt.GroupMinLen[g] = bkt.ItemLenCol[g * lanes];
			var mx = 0;
			for (var lane = 0; lane < lanes; lane++)
				mx = Math.Max(mx, bkt.ItemLenCol[g * lanes + lane]);
			bkt.GroupMaxLen[g] = mx;
		}
		return bkt;
	}

	static ulong LaneMask(int len) => len == 64 ? ulong.MaxValue : (1UL << len) - 1;

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
		ParallelChunks(m, (lo, hi) =>
		{
			var worker = GetWorker();
			for (var i = lo; i < hi; i++)
			{
				var (score, _) = FindBestCore(queries[i], threshold, worker);
				best[i] = score >= threshold ? score : -1;
			}
		});
		return best;
	}

	/// <summary>
	/// Static contiguous chunking: one lock-free range per work item instead of
	/// Partitioner.Create's shared-enumerator grabbing, whose contention dominates
	/// batch runtime for cheap per-query work. ~4 chunks per core keeps stragglers
	/// bounded while chunks stay large enough to amortize dispatch.
	/// </summary>
	static void ParallelChunks(int m, Action<int, int> body)
	{
		var chunks = Math.Min(Environment.ProcessorCount * 4, (m + 31) / 32);
		if (chunks <= 1)
		{
			body(0, m);
			return;
		}
		Parallel.For(0, chunks, c =>
			body((int)((long)m * c / chunks), (int)((long)m * (c + 1) / chunks)));
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
		ParallelChunks(m, (lo, hi) =>
		{
			var worker = GetWorker();
			for (var i = lo; i < hi; i++)
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
		var l1 = worker.Normalize(query, _charIdx);
		if (l1 > 0)
		{
			var done = false;
			if (Vector256.IsHardwareAccelerated)
			{
				// Bucket order is fixed; the per-group upper-bound check skips whole
				// groups whose lengths cannot beat the running best.
				ScanVector(_b16, worker, l1, ref b, ref bestIdx, ref done);
				ScanVector(_b32, worker, l1, ref b, ref bestIdx, ref done);
				ScanVector(_b64, worker, l1, ref b, ref bestIdx, ref done);
			}
			else
			{
				ScanScalar(_b64, worker, l1, ref b, ref bestIdx, ref done);
			}

			if (!done)
			{
				foreach (var grp in _longGroups)
				{
					ScanLongGroup(grp, worker, l1, ref b, ref bestIdx, ref done);
					if (done) break;
				}
				foreach (var item in _longItems)
				{
					if (done) break;
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

	/// <summary>
	/// Scores one bucket, <c>Vector256&lt;T&gt;.Count</c> items per pass. The Hyyrö
	/// recurrence is lane-width-agnostic: carries only propagate upward within a lane
	/// and bits above the item length are discarded by the popcount mask, so 16/32-bit
	/// lanes produce the same LCS as the 64-bit implementation.
	/// </summary>
	void ScanVector<T>(Bucket<T> bkt, Worker worker, int l1, ref int b, ref int bestIdx, ref bool done)
		where T : unmanaged, IBinaryInteger<T>
	{
		var width = bkt.Width;
		if (width == 0 || done) return;
		var lanes = Vector256<T>.Count;
		var offsets = worker.Premultiply(width, l1); // codes[j] * width, hoisted
		ref var peq0 = ref MemoryMarshal.GetArrayDataReference(bkt.PeqT);
		ref var off0 = ref MemoryMarshal.GetArrayDataReference(offsets);
		for (int g = 0, col0 = 0; col0 < width && !done; g++, col0 += lanes)
		{
			// Clamped group bound: ub(l1, l2) is unimodal in l2 with peak at l2 == l1,
			// so ub(l1, clamp(l1, minLen, maxLen)) bounds every lane in the group.
			var l2c = Math.Clamp(l1, bkt.GroupMinLen[g], bkt.GroupMaxLen[g]);
			if (TokenSort.UpperBoundScore(l1, l2c) <= b) continue;

			var s = Vector256.LoadUnsafe(ref bkt.LenMaskCol[col0]);
			ref var peqCol = ref Unsafe.Add(ref peq0, col0);
			for (var j = 0; j < l1; j++)
			{
				var m = Vector256.LoadUnsafe(ref Unsafe.Add(ref peqCol, Unsafe.Add(ref off0, j)));
				var u = s & m;
				s = (s + u) | (s - u);
			}

			for (var lane = 0; lane < lanes; lane++)
			{
				var l2 = bkt.ItemLenCol[col0 + lane];
				if (l2 == 0) continue;
				var lcs = BitOperations.PopCount(~ulong.CreateTruncating(s.GetElement(lane)) & LaneMask(l2));
				var score = TokenSort.ScoreFromLcs(l1, l2, lcs);
				if (score > b)
				{
					b = score;
					bestIdx = _sortedToOriginal[bkt.SortedBase + col0 + lane];
					if (b == 100) { done = true; return; }
				}
			}
		}
	}

	/// <summary>Scalar fallback over the 64-bit bucket (all short items when SIMD is off).</summary>
	void ScanScalar(Bucket<ulong> bkt, Worker worker, int l1, ref int b, ref int bestIdx, ref bool done)
	{
		var width = bkt.Width;
		if (width == 0) return;
		var lanes = Vector256<ulong>.Count;
		var codes = worker.Codes;
		for (int g = 0, col0 = 0; col0 < width && !done; g++, col0 += lanes)
		{
			var l2c = Math.Clamp(l1, bkt.GroupMinLen[g], bkt.GroupMaxLen[g]);
			if (TokenSort.UpperBoundScore(l1, l2c) <= b) continue;

			for (var lane = 0; lane < lanes; lane++)
			{
				var l2 = bkt.ItemLenCol[col0 + lane];
				if (l2 == 0) continue;
				if (TokenSort.UpperBoundScore(l1, l2) <= b) continue;
				var s = bkt.LenMaskCol[col0 + lane];
				for (var j = 0; j < l1; j++)
				{
					var u = s & bkt.PeqT[codes[j] * width + col0 + lane];
					unchecked { s = (s + u) | (s - u); }
				}
				var lcs = BitOperations.PopCount(~s & bkt.LenMaskCol[col0 + lane]);
				var score = TokenSort.ScoreFromLcs(l1, l2, lcs);
				if (score > b)
				{
					b = score;
					bestIdx = _sortedToOriginal[bkt.SortedBase + col0 + lane];
					if (b == 100) { done = true; break; }
				}
			}
		}
	}

	int LcsLong(LongItem item, Worker worker, int l1)
	{
		var blocks = item.Blocks;
		var s = worker.Blocks(_maxBlocks).AsSpan(0, blocks);
		s.Fill(ulong.MaxValue);
		s[blocks - 1] = item.LastMask;
		var peq = item.Peq;
		var codes = worker.Codes;
		for (var j = 0; j < l1; j++)
		{
			var off = codes[j] * blocks;
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

	/// <summary>
	/// Multi-block LCS over four long items at once: the scalar carry/borrow chains of
	/// <see cref="LcsLong"/> run per ulong lane. Carry-out of <c>sk + u + carry</c> is
	/// the classic full-adder MSB <c>(a&amp;b | (a|b)&amp;~sum) &gt;&gt; 63</c>; the
	/// subtraction never borrows from <c>sk - u</c> itself (u ⊆ sk bitwise), only from
	/// the injected borrow bit, so borrow-out is <c>(sk - u == 0) &amp; borrow</c>.
	/// </summary>
	void ScanLongGroup(LongGroup grp, Worker worker, int l1, ref int b, ref int bestIdx, ref bool done)
	{
		var l2c = Math.Clamp(l1, grp.MinLen, grp.MaxLen);
		if (TokenSort.UpperBoundScore(l1, l2c) <= b) return;

		var blocks = grp.Blocks;
		var state = worker.Blocks(_maxBlocks * LongLanes).AsSpan(0, blocks * LongLanes);
		grp.InitState.AsSpan().CopyTo(state);
		ref var state0 = ref MemoryMarshal.GetReference(state);
		ref var peq0 = ref MemoryMarshal.GetArrayDataReference(grp.PeqT);
		var codes = worker.Codes;
		var rowStride = blocks * LongLanes;

		for (var j = 0; j < l1; j++)
		{
			ref var row = ref Unsafe.Add(ref peq0, codes[j] * rowStride);
			var carry = Vector256<ulong>.Zero;
			var borrow = Vector256<ulong>.Zero;
			for (var k = 0; k < rowStride; k += LongLanes)
			{
				var sk = Vector256.LoadUnsafe(ref Unsafe.Add(ref state0, k));
				var u = sk & Vector256.LoadUnsafe(ref Unsafe.Add(ref row, k));
				var sum = sk + u + carry;
				carry = ((sk & u) | ((sk | u) & ~sum)) >>> 63;
				var t1 = sk - u;
				var diff = t1 - borrow;
				borrow = Vector256.Equals(t1, Vector256<ulong>.Zero) & borrow;
				(sum | diff).StoreUnsafe(ref Unsafe.Add(ref state0, k));
			}
		}

		for (var lane = 0; lane < LongLanes; lane++)
		{
			var l2 = grp.Lengths[lane];
			if (l2 == 0) continue;
			if (TokenSort.UpperBoundScore(l1, l2) <= b) continue;
			var laneBlocks = (l2 + 63) >> 6;
			var zeros = 0;
			for (var k = 0; k < laneBlocks - 1; k++)
				zeros += BitOperations.PopCount(~state[k * LongLanes + lane]);
			zeros += BitOperations.PopCount(
				~state[(laneBlocks - 1) * LongLanes + lane] & TokenSort.LastBlockMask(l2));
			var score = TokenSort.ScoreFromLcs(l1, l2, zeros);
			if (score > b)
			{
				b = score;
				bestIdx = grp.OriginalIndex[lane];
				if (b == 100) { done = true; return; }
			}
		}
	}

	[ThreadStatic] static Worker? _worker;

	static Worker GetWorker() => _worker ??= new Worker();

	/// <summary>Per-thread scratch buffers; content never outlives one FindBestCore call.</summary>
	sealed class Worker
	{
		readonly Normalizer _normalizer = new();
		ulong[] _blocks = [];

		/// <summary>Alphabet code of each normalized query char.</summary>
		public int[] Codes = new int[128];

		public int Normalize(string query, ushort[] charIdx)
		{
			var len = _normalizer.Normalize(query);
			var norm = _normalizer.Buffer;
			if (Codes.Length < len) Codes = new int[Math.Max(len, Codes.Length * 2)];
			for (var j = 0; j < len; j++)
				Codes[j] = charIdx[norm[j]];
			return len;
		}

		/// <summary>Bitmask row offsets (code * width) for one bucket; valid until the next call.</summary>
		int[] _offsets = new int[128];

		public int[] Premultiply(int width, int l1)
		{
			if (_offsets.Length < l1) _offsets = new int[Math.Max(l1, _offsets.Length * 2)];
			for (var j = 0; j < l1; j++)
				_offsets[j] = Codes[j] * width;
			return _offsets;
		}

		public ulong[] Blocks(int maxBlocks)
		{
			if (_blocks.Length < maxBlocks) _blocks = new ulong[maxBlocks];
			return _blocks;
		}
	}
}
