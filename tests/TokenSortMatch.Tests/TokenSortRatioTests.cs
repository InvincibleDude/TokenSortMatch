using TokenSortMatch;
using Xunit;

namespace TokenSortMatch.Tests;

/// <summary>
/// Adapted from FuzzySharp's FuzzySharp.Test/FuzzyTests/RatioTests.cs for
/// token-sort-ratio semantics: <see cref="TokenSort.Ratio"/> is equivalent to
/// <c>Fuzz.TokenSortRatio(a, b)</c> with full preprocessing, so expectations follow
/// the full-preprocessing token-sort variants of the original suite.
/// </summary>
public class TokenSortRatioTests
{
	const string S1 = "new york mets";
	const string S1A = "new york mets";
	const string S2 = "new YORK mets";
	const string S3 = "the wonderful new york mets";
	const string S4 = "new york mets vs atlanta braves";
	const string S5 = "atlanta braves vs new york mets";

	[Fact]
	public void Equal_strings_score_100()
	{
		Assert.Equal(100, TokenSort.Ratio(S1, S1A));
		Assert.Equal(100, TokenSort.Ratio("{a", "{a")); // "{a" normalizes to "a"
	}

	[Fact]
	public void Case_is_ignored()
	{
		Assert.Equal(100, TokenSort.Ratio(S1, S2));
	}

	[Fact]
	public void Token_order_is_ignored()
	{
		Assert.Equal(100, TokenSort.Ratio(S4, S5));
		Assert.Equal(100, TokenSort.Ratio("geeks for geeks", "for geeks geeks"));
	}

	[Fact]
	public void Punctuation_is_stripped()
	{
		// "a{" -> "a", "{b" -> "b": disjoint single-char tokens score 0.
		Assert.Equal(0, TokenSort.Ratio("a{", "{b"));
		// Punctuation-only strings normalize to empty and score 0 (matches
		// Fuzz.TokenSortRatio("{", "{", StringPreprocessor.Full)).
		Assert.Equal(0, TokenSort.Ratio("{", "{"));
	}

	[Fact]
	public void Empty_strings_score_0()
	{
		Assert.Equal(0, TokenSort.Ratio("test_string", ""));
		Assert.Equal(0, TokenSort.Ratio("", ""));
		Assert.Equal(0, TokenSort.Ratio("   ", "   "));
	}

	[Fact]
	public void Disjoint_strings_score_0()
	{
		Assert.Equal(0, TokenSort.Ratio("abc", "def"));
	}

	[Fact]
	public void Partial_overlap_scores_between_0_and_100()
	{
		var score = TokenSort.Ratio(S1, S3);
		Assert.InRange(score, 1, 99);
		// "ab" vs "abcd": dist 2 over max 6 -> 66.67 -> 67.
		Assert.Equal(67, TokenSort.Ratio("ab", "abcd"));
	}

	[Fact]
	public void Cyrillic_token_sort()
	{
		Assert.Equal(100, TokenSort.Ratio("Лекционные занятия", "занятия лекционные"));
		Assert.Equal(100, TokenSort.Ratio("Курсовая работа", "РАБОТА курсовая"));
		Assert.InRange(TokenSort.Ratio("Лекционные занятия", "Практические занятия"), 1, 99);
	}

	[Fact]
	public void Normalize_sorts_tokens_ordinally()
	{
		// Digits sort before Cyrillic letters ordinally.
		Assert.Equal("12 5 занятия курс лекционные", TokenSort.Normalize("Лекционные занятия 12 курс 5"));
		Assert.Equal("brown fox quick the", TokenSort.Normalize("The-Quick! Brown?? fox"));
		Assert.Equal(string.Empty, TokenSort.Normalize("!!! ---"));
	}

	[Fact]
	public void Null_arguments_throw()
	{
		Assert.Throws<ArgumentNullException>(() => TokenSort.Ratio(null!, "a"));
		Assert.Throws<ArgumentNullException>(() => TokenSort.Ratio("a", null!));
		Assert.Throws<ArgumentNullException>(() => TokenSort.Normalize(null!));
	}
}
