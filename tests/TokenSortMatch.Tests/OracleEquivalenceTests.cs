using FuzzySharp;
using TokenSortMatch;
using Xunit;

namespace TokenSortMatch.Tests;

/// <summary>
/// Randomized equivalence against FuzzySharp as the oracle: every score the
/// library produces must be bit-identical to
/// <c>Fuzz.Ratio(OracleNormalize(a), OracleNormalize(b))</c> — the exact definition the
/// automatch harness verifies against. The oracle package is a test-only dependency.
/// </summary>
public class OracleEquivalenceTests
{
	const string Alphabet =
		"абвгдежзиклмнопрстуфхцчшщыэюяАБВГДЕЖЗИКЛМНОПРСТУФХЦЧШЩЫЭЮЯ" +
		"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" +
		"    --__{}!?.,;:'\"“”«»";

	/// <summary>
	/// Independent reimplementation of full preprocessing (lowercase letters/digits,
	/// everything else becomes a token separator) followed by ordinal token sort.
	/// </summary>
	static string OracleNormalize(string s)
	{
		var mapped = new char[s.Length];
		for (var i = 0; i < s.Length; i++)
			mapped[i] = char.IsLetterOrDigit(s[i]) ? char.ToLower(s[i]) : ' ';
		var tokens = new string(mapped)
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		Array.Sort(tokens, StringComparer.Ordinal);
		return string.Join(' ', tokens);
	}

	static int OracleRatio(string a, string b) =>
		Fuzz.Ratio(OracleNormalize(a), OracleNormalize(b));

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
			var golden = -1;
			foreach (var it in items)
			{
				var score = OracleRatio(queries[i], it);
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
