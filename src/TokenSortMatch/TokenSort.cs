using System.Numerics;

namespace TokenSortMatch;

/// <summary>
/// Token-sort similarity with exact RapidFuzz/FuzzySharp semantics:
/// <c>Ratio(a, b) == Fuzz.Ratio(Normalize(a), Normalize(b))</c> where normalization
/// lowercases letters/digits, replaces everything else with spaces, splits, sorts the
/// tokens ordinally and re-joins with single spaces. The score is the indel similarity
/// <c>round(100 * (1 - dist / (len1 + len2)))</c> (banker's rounding), where
/// <c>dist = len1 + len2 - 2 * LCS</c>.
/// </summary>
public static class TokenSort
{
	/// <summary>
	/// Normalized char (lowercase letter/digit, or ' ') for every UTF-16 code unit;
	/// surrogate halves map to ' ', matching per-char preprocessors.
	/// </summary>
	internal static readonly char[] MapChar = BuildMapChar();

	static char[] BuildMapChar()
	{
		var table = new char[65536];
		for (var c = 0; c < 65536; c++)
			table[c] = char.IsLetterOrDigit((char)c) ? char.ToLower((char)c) : ' ';
		return table;
	}

	[ThreadStatic] static Normalizer? _normalizer;

	/// <summary>
	/// Token-sort normalization: lowercase letters/digits, everything else to spaces,
	/// tokens sorted ordinally and joined with single spaces.
	/// </summary>
	/// <exception cref="ArgumentNullException"><paramref name="s"/> is null.</exception>
	public static string Normalize(string s)
	{
		ArgumentNullException.ThrowIfNull(s);
		var n = _normalizer ??= new Normalizer();
		var len = n.Normalize(s);
		return len == 0 ? string.Empty : new string(n.Buffer, 0, len);
	}

	/// <summary>
	/// Token-sort indel similarity in [0, 100]. Returns 0 when either side normalizes
	/// to the empty string.
	/// </summary>
	/// <exception cref="ArgumentNullException">Either argument is null.</exception>
	public static int Ratio(string a, string b)
	{
		var an = Normalize(a);
		var bn = Normalize(b);
		return IndelScore(an, bn);
	}

	internal static int IndelScore(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
	{
		if (a.Length == 0 || b.Length == 0) return 0;
		var pat = a.Length <= b.Length ? a : b;
		var txt = a.Length <= b.Length ? b : a;
		var lcs = pat.Length <= 64 ? LcsSingle(pat, txt) : LcsMulti(pat, txt);
		return ScoreFromLcs(a.Length, b.Length, lcs);
	}

	/// <summary>
	/// FuzzySharp's score formula replicated operation-for-operation: results must stay
	/// bit-identical, including banker's rounding at .5 boundaries.
	/// </summary>
	internal static int ScoreFromLcs(int l1, int l2, int lcs)
	{
		var dist = l1 + l2 - 2 * lcs;
		return (int)Math.Round(100 * (1 - dist / (double)(l1 + l2)));
	}

	/// <summary>Exactly-rounded score upper bound for lengths (l1, l2): lcs &lt;= min.</summary>
	internal static int UpperBoundScore(int l1, int l2)
	{
		var minDist = l1 >= l2 ? l1 - l2 : l2 - l1;
		return (int)Math.Round(100 * (1 - minDist / (double)(l1 + l2)));
	}

	// Bit-parallel LCS (Hyyrö/Myers), pattern <= 64 chars.
	static int LcsSingle(ReadOnlySpan<char> pat, ReadOnlySpan<char> txt)
	{
		var peq = new Dictionary<char, ulong>(pat.Length);
		for (var j = 0; j < pat.Length; j++)
		{
			peq.TryGetValue(pat[j], out var m);
			peq[pat[j]] = m | (1UL << j);
		}
		var mask = pat.Length == 64 ? ulong.MaxValue : (1UL << pat.Length) - 1;
		var s = mask;
		foreach (var c in txt)
		{
			peq.TryGetValue(c, out var m);
			var u = s & m;
			unchecked { s = (s + u) | (s - u); }
		}
		return BitOperations.PopCount(~s & mask);
	}

	// Multi-block bit-parallel LCS for patterns longer than 64 chars.
	static int LcsMulti(ReadOnlySpan<char> pat, ReadOnlySpan<char> txt)
	{
		var blocks = (pat.Length + 63) >> 6;
		var peq = new Dictionary<char, ulong[]>();
		for (var j = 0; j < pat.Length; j++)
		{
			if (!peq.TryGetValue(pat[j], out var m))
				peq[pat[j]] = m = new ulong[blocks];
			m[j >> 6] |= 1UL << (j & 63);
		}
		var lastMask = LastBlockMask(pat.Length);
		var s = new ulong[blocks];
		s.AsSpan().Fill(ulong.MaxValue);
		s[blocks - 1] = lastMask;
		foreach (var c in txt)
		{
			peq.TryGetValue(c, out var row);
			ulong carry = 0, borrow = 0;
			for (var k = 0; k < blocks; k++)
			{
				var sk = s[k];
				var u = row is null ? 0 : sk & row[k];
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
		return CountZeroBits(s, lastMask);
	}

	internal static ulong LastBlockMask(int length)
	{
		var rem = length & 63;
		return rem == 0 ? ulong.MaxValue : (1UL << rem) - 1;
	}

	internal static int CountZeroBits(ReadOnlySpan<ulong> s, ulong lastMask)
	{
		var zeros = 0;
		for (var k = 0; k < s.Length - 1; k++)
			zeros += BitOperations.PopCount(~s[k]);
		zeros += BitOperations.PopCount(~s[^1] & lastMask);
		return zeros;
	}
}

/// <summary>
/// Reusable token-sort normalizer; allocation-free after warm-up.
/// </summary>
internal sealed class Normalizer
{
	char[] _mapped = new char[128];
	int[] _tokStart = new int[65];
	int[] _tokLen = new int[65];

	/// <summary>Normalized output; valid for the length returned by <see cref="Normalize"/>.</summary>
	public char[] Buffer = new char[128];

	/// <summary>Writes the normalized form of <paramref name="s"/> into <see cref="Buffer"/>.</summary>
	/// <returns>The normalized length (0 for strings with no letter/digit tokens).</returns>
	public int Normalize(string s)
	{
		var len = s.Length;
		if (len == 0) return 0;
		if (_mapped.Length < len)
		{
			var cap = Math.Max(len, _mapped.Length * 2);
			_mapped = new char[cap];
			Buffer = new char[cap];
			_tokStart = new int[cap / 2 + 1];
			_tokLen = new int[cap / 2 + 1];
		}
		var mapped = _mapped;
		var table = TokenSort.MapChar;
		for (var i = 0; i < len; i++)
			mapped[i] = table[s[i]];

		var tokStart = _tokStart;
		var tokLen = _tokLen;
		var ntok = 0;
		for (var i = 0; i < len;)
		{
			while (i < len && mapped[i] == ' ') i++;
			var start = i;
			while (i < len && mapped[i] != ' ') i++;
			if (i > start)
			{
				tokStart[ntok] = start;
				tokLen[ntok] = i - start;
				ntok++;
			}
		}
		if (ntok == 0) return 0;

		// Insertion sort of token spans; SequenceCompareTo matches StringComparer.Ordinal.
		var span = mapped.AsSpan();
		for (var k = 1; k < ntok; k++)
		{
			var ks = tokStart[k];
			var kl = tokLen[k];
			var j = k - 1;
			while (j >= 0 && span.Slice(tokStart[j], tokLen[j]).SequenceCompareTo(span.Slice(ks, kl)) > 0)
			{
				tokStart[j + 1] = tokStart[j];
				tokLen[j + 1] = tokLen[j];
				j--;
			}
			tokStart[j + 1] = ks;
			tokLen[j + 1] = kl;
		}

		var dest = Buffer;
		var pos = 0;
		for (var k = 0; k < ntok; k++)
		{
			if (pos > 0) dest[pos++] = ' ';
			var st = tokStart[k];
			var end = st + tokLen[k];
			for (var i = st; i < end; i++)
				dest[pos++] = mapped[i];
		}
		return pos;
	}
}
