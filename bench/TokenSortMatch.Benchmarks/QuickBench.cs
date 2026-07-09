using System.Diagnostics;
using FuzzySharp;
using FuzzySharp.PreProcess;
using TokenSortMatch;

/// <summary>
/// Fast Stopwatch-based comparison (best of 5 runs after warmup) — the direct
/// counterpart of <c>uv run bench/rapidfuzz_bench.py</c>. For rigorous .NET-side
/// numbers use the BenchmarkDotNet mode (no args) instead.
/// </summary>
static class QuickBench
{
	public static void Run()
	{
		Bench("short: 200 items x 10 000 queries (~25 chars)",
			DataSet.Load("items_short.txt"), DataSet.Load("queries_short.txt"));
		Bench("long: 100 items x 1 000 queries (~300 chars, multi-block)",
			DataSet.Load("items_long.txt"), DataSet.Load("queries_long.txt"));
	}

	static void Bench(string name, string[] items, string[] queries)
	{
		Console.WriteLine($"== {name} ==");
		var index = new TokenSortIndex(items);
		var pairs = (double)items.Length * queries.Length;

		Time("TokenSortIndex.FindBestScores cutoff=80", () => index.FindBestScores(queries, 80), pairs);
		Time("TokenSortIndex.FindBestScores cutoff=0", () => index.FindBestScores(queries, 0), pairs);

		// FuzzySharp has no batch API: parallel brute force over a query slice,
		// throughput is still comparable via Mpairs/s.
		var slice = queries.Take(Math.Min(queries.Length, 500)).ToArray();
		Time($"FuzzySharp TokenSortRatio brute ({slice.Length}q)",
			() => FuzzySharpBrute(items, slice), (double)items.Length * slice.Length);
		Console.WriteLine();
	}

	internal static int[] FuzzySharpBrute(string[] items, string[] queries)
	{
		var best = new int[queries.Length];
		Parallel.For(0, queries.Length, i =>
		{
			var b = 0;
			foreach (var it in items)
				b = Math.Max(b, Fuzz.TokenSortRatio(queries[i], it, PreprocessMode.Full));
			best[i] = b;
		});
		return best;
	}

	static void Time<T>(string label, Func<T> run, double pairs)
	{
		run(); // warmup (JIT + buffers)
		var bestMs = double.MaxValue;
		for (var r = 0; r < 5; r++)
		{
			var sw = Stopwatch.StartNew();
			run();
			sw.Stop();
			bestMs = Math.Min(bestMs, sw.Elapsed.TotalMilliseconds);
		}
		Console.WriteLine($"{label,-45} {bestMs,9:F1} ms {pairs / bestMs / 1000,10:F2} Mpairs/s");
	}
}
