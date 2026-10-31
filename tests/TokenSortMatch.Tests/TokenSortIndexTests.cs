using TokenSortMatch;
using Xunit;

namespace TokenSortMatch.Tests;

public class TokenSortIndexTests
{
	static readonly string[] Items =
	[
		"Лекционные занятия",
		"Практические занятия",
		"Лабораторные работы",
		"Курсовая работа",
		"Экзамен",
	];

	[Fact]
	public void Finds_the_best_item()
	{
		var index = new TokenSortIndex(Items);
		var match = index.FindBest("занятия лекционные 5", threshold: 80);
		Assert.NotNull(match);
		Assert.Equal(0, match.Value.ItemIndex);
		Assert.Equal(TokenSort.Ratio("занятия лекционные 5", Items[0]), match.Value.Score);
	}

	[Fact]
	public void Score_equals_brute_force_over_all_items()
	{
		var index = new TokenSortIndex(Items);
		string[] queries =
		[
			"Лекционные занятия 123 курс 4",
			"практические ЗАНЯТИЯ!",
			"Экзамен 900",
			"что-то совсем другое",
			"работа",
		];
		foreach (var q in queries)
		{
			var expected = Items.Max(it => TokenSort.Ratio(q, it));
			var match = index.FindBest(q, threshold: 0);
			Assert.NotNull(match);
			Assert.Equal(expected, match.Value.Score);
			Assert.Equal(expected, TokenSort.Ratio(q, Items[match.Value.ItemIndex]));
		}
	}

	[Fact]
	public void Threshold_gates_matches()
	{
		var index = new TokenSortIndex(Items);
		var best = Items.Max(it => TokenSort.Ratio("Экзамен 900", it));
		Assert.NotNull(index.FindBest("Экзамен 900", best));
		Assert.Null(index.FindBest("Экзамен 900", best + 1));
		Assert.Equal(-1, index.FindBestScores(["Экзамен 900"], best + 1)[0]);
		Assert.Equal(best, index.FindBestScores(["Экзамен 900"], best)[0]);
	}

	[Fact]
	public void Batch_scores_match_single_queries()
	{
		var index = new TokenSortIndex(Items);
		string[] queries = ["Лекционные занятия", "экзамен", "???", "", "работа курсовая 7"];
		var scores = index.FindBestScores(queries, threshold: 60);
		var matches = index.FindBestMatches(queries, threshold: 60);
		for (var i = 0; i < queries.Length; i++)
		{
			var single = index.FindBest(queries[i], threshold: 60);
			Assert.Equal(single?.Score ?? -1, scores[i]);
			Assert.Equal(single, matches[i]);
		}
	}

	[Fact]
	public void ScoreAll_equals_Ratio_for_every_item()
	{
		// Every length bucket, the multi-block path, empty and punctuation-only items, and
		// queries that equal, are edited from or share nothing with the items.
		var rnd = new Random(23);
		string Word(int length) =>
			new(Enumerable.Range(0, length).Select(_ => "abcdefgзанятия"[rnd.Next(14)]).ToArray());
		var items = new List<string> { "", "???", "Экзамен" };
		foreach (var length in new[] { 1, 2, 5, 15, 16, 17, 31, 32, 33, 63, 64, 65, 130, 300 })
			for (var copy = 0; copy < 3; copy++)
				items.Add(copy == 2 ? $"{Word(length / 2)} {Word(length - length / 2)}" : Word(length));
		var queries = items
			.Select(it => it.Length > 1 ? it.Remove(rnd.Next(it.Length), 1) : it)
			.Concat(items)
			.Concat(["", "!!!", "zzz", "занятия abc"])
			.ToArray();
		var index = new TokenSortIndex(items);

		foreach (var threshold in new[] { -5, 0, 1, 50, 80, 100, 101 })
			foreach (var q in queries)
			{
				var expected = items.Select(it => TokenSort.Ratio(q, it)).Select(r => r >= threshold ? r : -1);
				Assert.Equal(expected, index.ScoreAll(q, threshold));
			}
	}

	[Fact]
	public void ScoreAll_fills_only_the_first_Count_entries_of_a_longer_buffer()
	{
		var index = new TokenSortIndex(Items);
		var buffer = Enumerable.Repeat(42, Items.Length + 3).ToArray();
		index.ScoreAll("экзамен", 0, buffer);
		Assert.Equal(index.ScoreAll("экзамен", 0), buffer[..Items.Length]);
		Assert.Equal([42, 42, 42], buffer[Items.Length..]);
	}

	[Fact]
	public void Empty_index_never_matches()
	{
		var index = new TokenSortIndex([]);
		Assert.Equal(0, index.Count);
		Assert.Null(index.FindBest("anything", 0));
		Assert.Equal(-1, index.FindBestScores(["anything"], 80)[0]);
		Assert.Empty(index.ScoreAll("anything", 0));
	}

	[Fact]
	public void Empty_or_punctuation_queries_do_not_match()
	{
		var index = new TokenSortIndex(Items);
		Assert.Null(index.FindBest("", 80));
		Assert.Null(index.FindBest("!!! ---", 80));
		Assert.Null(index.FindBest("   ", 80));
	}

	[Fact]
	public void Empty_items_score_0()
	{
		var index = new TokenSortIndex(["", "???", "Экзамен"]);
		var match = index.FindBest("экзамен", 80);
		Assert.NotNull(match);
		Assert.Equal(2, match.Value.ItemIndex);
		Assert.Equal(100, match.Value.Score);
		// A query sharing no letters with any item scores 0 against non-empty items
		// and 0 against empty ones, so threshold 1 filters everything out.
		Assert.Null(index.FindBest("ффф", 1));
	}

	[Fact]
	public void Long_items_use_the_multiblock_path()
	{
		// Normalized lengths straddle the 64-char single-word boundary.
		var w63 = string.Concat(Enumerable.Repeat("abcdefg ", 8)).TrimEnd();   // 63
		var w64 = w63 + "h";                                                    // 64
		var w65 = w63 + "hi";                                                   // 65
		var big = string.Concat(Enumerable.Repeat("занятия лекционные практика ", 10)); // ~280
		string[] items = [w63, w64, w65, big];
		var index = new TokenSortIndex(items);

		string[] queries = [w63, w64, w65, big, "занятия практика", "abcdefg abcdefg"];
		foreach (var q in queries)
		{
			var expected = items.Max(it => TokenSort.Ratio(q, it));
			var match = index.FindBest(q, 0);
			Assert.NotNull(match);
			Assert.Equal(expected, match.Value.Score);
		}
	}

	[Fact]
	public void Lane_bucket_boundaries_score_exactly()
	{
		// Normalized lengths straddle every lane-bucket boundary (16/32/64 bits) plus
		// the multi-block threshold. Items are single tokens so normalization keeps
		// their length; distinct suffix letters prevent accidental full matches.
		int[] lengths = [1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 130];
		var items = lengths
			.Select((len, i) => new string((char)('a' + i), len))
			.Concat(lengths.Select(len => string.Concat(
				Enumerable.Range(0, len).Select(j => (char)('a' + j % 7)))))
			.ToArray();
		var index = new TokenSortIndex(items);

		var queries = items
			.Concat(new[] { 1, 8, 16, 17, 32, 33, 64, 65, 100 }.Select(len => new string('a', len)))
			.Concat(["abcdefg", "gfedcba abc", "zzz"])
			.ToArray();
		foreach (var q in queries)
		{
			var expected = items.Max(it => TokenSort.Ratio(q, it));
			var match = index.FindBest(q, 0);
			Assert.NotNull(match);
			Assert.Equal(expected, match.Value.Score);
			Assert.Equal(expected, TokenSort.Ratio(q, items[match.Value.ItemIndex]));
		}
	}

	[Fact]
	public void Item_count_not_divisible_by_lane_width_works()
	{
		for (var n = 1; n <= 9; n++)
		{
			var items = Enumerable.Range(0, n).Select(i => $"пример элемент {i}").ToArray();
			var index = new TokenSortIndex(items);
			var match = index.FindBest("элемент пример 3", 0);
			Assert.NotNull(match);
			var expected = items.Max(it => TokenSort.Ratio("элемент пример 3", it));
			Assert.Equal(expected, match.Value.Score);
		}
	}

	[Fact]
	public void Duplicate_items_return_a_valid_best()
	{
		var index = new TokenSortIndex(["Экзамен", "Экзамен"]);
		var match = index.FindBest("экзамен!", 100);
		Assert.NotNull(match);
		Assert.Equal(100, match.Value.Score);
		Assert.InRange(match.Value.ItemIndex, 0, 1);
	}

	[Fact]
	public void Threshold_extremes()
	{
		var index = new TokenSortIndex(Items);
		// <= 0: scores are never negative, so the best always qualifies.
		Assert.NotNull(index.FindBest("zzz", 0));
		Assert.NotNull(index.FindBest("zzz", -5));
		Assert.NotNull(index.FindBest("", 0)); // every score is 0
		Assert.Null(index.FindBest(Items[0], 101));
	}

	[Fact]
	public void Large_alphabets_score_exactly()
	{
		// ~2,000 distinct CJK characters over 1,000 items: every length bucket splits its
		// bitmask table into several segments with their own alphabets, and the long items
		// get per-group alphabets. Half the queries are edited items, so best scores are
		// high and pruning and early exits kick in.
		var rnd = new Random(11);
		string Word(int length) =>
			new(Enumerable.Range(0, length).Select(_ => (char)(0x4E00 + rnd.Next(3000))).ToArray());
		var items = Enumerable.Range(0, 1000)
			.Select(i => i % 10 == 9 ? $"{Word(40)} {Word(40)}" : Word(1 + rnd.Next(64)))
			.ToArray();
		var queries = Enumerable.Range(0, 150).Select(i =>
		{
			if (i % 2 == 1) return Word(1 + rnd.Next(64));
			var chars = items[rnd.Next(items.Length)].ToCharArray();
			chars[rnd.Next(chars.Length)] = (char)(0x4E00 + rnd.Next(3000));
			return new string(chars);
		}).ToArray();
		var index = new TokenSortIndex(items);

		var scores = index.FindBestScores(queries, 0);
		var matches = index.FindBestMatches(queries, 60);
		for (var i = 0; i < queries.Length; i++)
		{
			var ratios = items.Select(it => TokenSort.Ratio(queries[i], it)).ToArray();
			var expected = ratios.Max();
			Assert.Equal(expected, scores[i]);
			Assert.Equal(ratios.Select(r => r >= 60 ? r : -1), index.ScoreAll(queries[i], 60));
			if (expected < 60)
			{
				Assert.Null(matches[i]);
				continue;
			}
			Assert.Equal(expected, matches[i]!.Value.Score);
			Assert.Equal(expected, TokenSort.Ratio(queries[i], items[matches[i]!.Value.ItemIndex]));
		}
	}

	[Fact]
	public void Surrogate_pairs_are_treated_as_non_alphanumeric()
	{
		// Emoji are surrogate pairs; per-char preprocessing maps both halves to spaces.
		Assert.Equal(100, TokenSort.Ratio("Экзамен 😀😀", "экзамен"));
		var index = new TokenSortIndex(["Экзамен 😀"]);
		var match = index.FindBest("экзамен", 100);
		Assert.NotNull(match);
	}

	[Fact]
	public void Null_arguments_throw()
	{
		Assert.Throws<ArgumentNullException>(() => new TokenSortIndex(null!));
		Assert.Throws<ArgumentException>(() => new TokenSortIndex(["ok", null!]));
		var index = new TokenSortIndex(Items);
		Assert.Throws<ArgumentNullException>(() => index.FindBest(null!, 80));
		Assert.Throws<ArgumentNullException>(() => index.FindBestScores(null!, 80));
		Assert.Throws<ArgumentException>(() => index.FindBestScores(["ok", null!], 80));
		Assert.Throws<ArgumentException>(() => index.FindBestMatches([null!], 80));
		Assert.Throws<ArgumentNullException>(() => index.ScoreAll(null!, 80));
		Assert.Throws<ArgumentException>(() => index.ScoreAll("ok", 80, new int[Items.Length - 1]));
	}
}
