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
	public void Empty_index_never_matches()
	{
		var index = new TokenSortIndex([]);
		Assert.Equal(0, index.Count);
		Assert.Null(index.FindBest("anything", 0));
		Assert.Equal(-1, index.FindBestScores(["anything"], 80)[0]);
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
	}
}
