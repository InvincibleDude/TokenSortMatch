using BenchmarkDotNet.Attributes;
using TokenSortMatch;

/// <summary>
/// BenchmarkDotNet suite over the shared <c>bench/data</c> datasets. One op = the full
/// batch (all queries against all items). Run from the repo root:
/// <c>dotnet run --project bench/TokenSortMatch.Benchmarks -c Release</c>.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class TokenSortBenchmarks
{
	string[] _queriesShort = [], _queriesLong = [], _itemsShort = [];
	TokenSortIndex _short = null!, _long = null!;

	[GlobalSetup]
	public void Setup()
	{
		_itemsShort = DataSet.Load("items_short.txt");
		_queriesShort = DataSet.Load("queries_short.txt");
		_queriesLong = DataSet.Load("queries_long.txt");
		_short = new TokenSortIndex(_itemsShort);
		_long = new TokenSortIndex(DataSet.Load("items_long.txt"));
	}

	[Benchmark] public int[] Index_Short_Cutoff80() => _short.FindBestScores(_queriesShort, 80);
	[Benchmark] public int[] Index_Short_Cutoff0() => _short.FindBestScores(_queriesShort, 0);
	[Benchmark] public int[] Index_Long_Cutoff80() => _long.FindBestScores(_queriesLong, 80);
	[Benchmark] public int[] Index_Long_Cutoff0() => _long.FindBestScores(_queriesLong, 0);

	/// <summary>Per-pair FuzzySharp baseline on the first 500 short queries (100k pairs).</summary>
	[Benchmark] public int[] FuzzySharp_Short_Brute500() =>
		QuickBench.FuzzySharpBrute(_itemsShort, _queriesShort[..500]);
}
