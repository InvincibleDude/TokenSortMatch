using Raffinert.FuzzySharp;
using TokenSortMatch;
using Xunit;

namespace TokenSortMatch.Tests;

/// <summary>
/// Randomized equivalence against Raffinert.FuzzySharp as the oracle: every score the
/// library produces must be bit-identical to
/// <c>round(Fuzz.Ratio(OracleNormalize(a), OracleNormalize(b)))</c> with banker's
/// rounding. Its <c>Fuzz.Ratio</c> uses RapidFuzz's floating-point expression, so exact
/// .5 scores round like RapidFuzz; the original FuzzySharp package rounds some of them
/// the other way. The oracle package is a test-only dependency.
/// </summary>
public class OracleEquivalenceTests
{
	const string Alphabet =
		"абвгдежзиклмнопрстуфхцчшщыэюяАБВГДЕЖЗИКЛМНОПРСТУФХЦЧШЩЫЭЮЯ" +
		"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" +
		"    --__{}!?.,;:'\"“”«»";

	/// <summary>
	/// Independent reimplementation of full preprocessing (letters/digits lowercased
	/// culture-invariantly with Unicode's 'İ' -> 'i', everything else becomes a token
	/// separator) followed by ordinal token sort.
	/// </summary>
	static string OracleNormalize(string s)
	{
		var mapped = new char[s.Length];
		for (var i = 0; i < s.Length; i++)
			mapped[i] = !char.IsLetterOrDigit(s[i]) ? ' ' : s[i] == 'İ' ? 'i' : char.ToLowerInvariant(s[i]);
		var tokens = new string(mapped)
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		Array.Sort(tokens, StringComparer.Ordinal);
		return string.Join(' ', tokens);
	}

	static int OracleRatio(string a, string b) =>
		(int)Math.Round(Fuzz.Ratio(OracleNormalize(a), OracleNormalize(b)));

	static string RandomString(Random rnd, int maxLen)
	{
		var len = rnd.Next(maxLen + 1);
		var chars = new char[len];
		for (var i = 0; i < len; i++)
			chars[i] = Alphabet[rnd.Next(Alphabet.Length)];
		var s = new string(chars);
		if (rnd.Next(10) == 0) s += "😀🚀";
		return s;
	}

	[Fact]
	public void Normalize_matches_oracle()
	{
		var rnd = new Random(42);
		for (var i = 0; i < 2000; i++)
		{
			var s = RandomString(rnd, 120);
			Assert.Equal(OracleNormalize(s), TokenSort.Normalize(s));
		}
	}

	[Fact]
	public void Ratio_matches_oracle_on_random_pairs()
	{
		var rnd = new Random(43);
		for (var i = 0; i < 2000; i++)
		{
			var a = RandomString(rnd, 120);
			var b = RandomString(rnd, 120);
			Assert.Equal(OracleRatio(a, b), TokenSort.Ratio(a, b));
		}
	}

	[Fact]
	public void Ratio_matches_oracle_on_long_strings()
	{
		// Normalized lengths far beyond 64 chars exercise the multi-block LCS.
		var rnd = new Random(44);
		for (var i = 0; i < 200; i++)
		{
			var a = RandomString(rnd, 400);
			var b = RandomString(rnd, 400);
			Assert.Equal(OracleRatio(a, b), TokenSort.Ratio(a, b));
		}
	}

	/// <summary>
	/// Every exact .5 score up to a combined length of 1000. Random pairs almost never hit
	/// one, and they are the only cases where the floating-point expression decides the
	/// rounding. The index check with threshold == score fails if the pruning bound is
	/// computed differently from the score and rounds below it.
	/// </summary>
	[Fact]
	public void Exact_half_scores_round_like_the_oracle()
	{
		for (var total = 2; total <= 1000; total++)
			for (var lcs = 1; 2 * lcs <= total; lcs++)
			{
				if (400 * lcs % total != 0 || 400 * lcs / total % 2 == 0) continue; // 200·lcs/total == k + 0.5
				// An even split (lcs below both lengths) and a contained string (lcs equals the
				// shorter length, so the pruning bound is evaluated at the score itself).
				foreach (var l1 in new[] { total / 2, lcs })
				{
					var a = new string('a', lcs) + new string('x', l1 - lcs);
					var b = new string('a', lcs) + new string('y', total - l1 - lcs);
					var expected = OracleRatio(a, b);
					Assert.Equal(expected, TokenSort.Ratio(a, b));
					Assert.Equal(new BestMatch(0, expected), new TokenSortIndex([b]).FindBest(a, expected));
				}
			}
	}

	[Theory]
	[InlineData(80)]
	[InlineData(60)]
	[InlineData(100)]
	[InlineData(0)]
	public void Index_matches_the_oracle_golden_loop(int threshold)
	{
		var rnd = new Random(45);
		var items = Enumerable.Range(0, 60).Select(_ => RandomString(rnd, 120)).ToArray();
		var queries = Enumerable.Range(0, 500).Select(_ => RandomString(rnd, 120)).ToArray();
		var index = new TokenSortIndex(items);

		var scores = index.FindBestScores(queries, threshold);
		var matches = index.FindBestMatches(queries, threshold);

		for (var i = 0; i < queries.Length; i++)
		{
			var all = index.ScoreAll(queries[i], threshold);
			var golden = -1;
			for (var j = 0; j < items.Length; j++)
			{
				var score = OracleRatio(queries[i], items[j]);
				Assert.Equal(score >= threshold ? score : -1, all[j]);
				if (score > golden) golden = score;
			}
			var expected = golden >= threshold ? golden : -1;
			Assert.Equal(expected, scores[i]);

			if (expected == -1)
			{
				Assert.Null(matches[i]);
			}
			else
			{
				Assert.NotNull(matches[i]);
				Assert.Equal(expected, matches[i]!.Value.Score);
				// The returned index must actually attain the best score.
				Assert.Equal(expected, OracleRatio(queries[i], items[matches[i]!.Value.ItemIndex]));
			}
		}
	}

	[Fact]
	public void Index_matches_oracle_on_harness_style_workload()
	{
		// Same generator shape as the automatch harness (seeded differently).
		string[] stems =
		[
			"Лекционные занятия", "Практические занятия", "Лабораторные работы",
			"Самостоятельная работа", "Курсовая работа", "Контрольная работа", "Экзамен",
		];
		var rnd = new Random(7);
		var items = Enumerable.Range(0, 50)
			.Select(_ => $"{stems[rnd.Next(stems.Length)]} {rnd.Next(1000)}").ToArray();
		var queries = Enumerable.Range(0, 2000)
			.Select(_ => $"{stems[rnd.Next(stems.Length)]} {rnd.Next(1000)} курс {rnd.Next(20)}").ToArray();

		var index = new TokenSortIndex(items);
		var scores = index.FindBestScores(queries, 80);
		for (var i = 0; i < queries.Length; i++)
		{
			var golden = items.Max(it => OracleRatio(queries[i], it));
			Assert.Equal(golden >= 80 ? golden : -1, scores[i]);
		}
	}
}
