using System.Diagnostics;
using Raffinert.FuzzySharp;
using Raffinert.FuzzySharp.PreProcess;
using TokenSortMatch;

/// <summary>
/// Fast Stopwatch-based comparison (best run within a 3 s budget after warmup) — the
/// direct counterpart of <c>uv run bench/rapidfuzz_bench.py</c>. For rigorous .NET-side
/// numbers use the BenchmarkDotNet mode (no args) instead.
/// </summary>
static class QuickBench
{
	public static void Run()
	{
		Bench("short: 200 items x 10 000 queries (~25 chars)", DataSet.Load("items_short.txt"),
			DataSet.Load("queries_short.txt"), DataSet.Load("expected_short.txt"));
		Bench("long: 100 items x 1 000 queries (~300 chars, multi-block)", DataSet.Load("items_long.txt"),
			DataSet.Load("queries_long.txt"), DataSet.Load("expected_long.txt"));
	}

	static void Bench(string name, string[] items, string[] queries, string[] expected)
	{
		Console.WriteLine($"== {name} ==");
		var index = new TokenSortIndex(items);
		var pairs = (double)items.Length * queries.Length;

		Time("TokenSortIndex.FindBestScores cutoff=80", () => index.FindBestScores(queries, 80), pairs);
		Time("TokenSortIndex.FindBestScores cutoff=0", () => index.FindBestScores(queries, 0), pairs);

		// Raffinert.FuzzySharp has no batch API: parallel brute force over a query slice,
		// throughput is still comparable via Mpairs/s. Its token sort orders tokens with a
		// culture-aware comparison instead of an ordinal one; on these datasets both orders
		// agree, which the golden-score check confirms.
		var slice = queries.Take(Math.Min(queries.Length, 500)).ToArray();
		var mismatches = RaffinertBrute(items, slice).Where((s, i) => s.ToString() != expected[i]).Count();
		Console.WriteLine($"Raffinert.FuzzySharp best scores vs golden: " +
			$"{(mismatches == 0 ? "all identical" : $"{mismatches} MISMATCHES")} ({slice.Length} queries)");
		Time($"Raffinert.FuzzySharp TokenSortRatio brute ({slice.Length}q)",
			() => RaffinertBrute(items, slice), (double)items.Length * slice.Length);
		Console.WriteLine();
	}

	internal static int[] RaffinertBrute(string[] items, string[] queries)
	{
		var best = new int[queries.Length];
		Parallel.For(0, queries.Length, i =>
		{
			var b = 0.0;
			foreach (var it in items)
				b = Math.Max(b, Fuzz.TokenSortRatio(queries[i], it, StringPreprocessor.Full));
			best[i] = (int)Math.Round(b);
		});
		return best;
	}

	static void Time<T>(string label, Func<T> run, double pairs)
	{
		// Sustained steady-state timing: loop for a fixed budget so tiered JIT,
		// thread-pool wake-up and CPU frequency all settle (mirrors the Python side).
		run(); // warmup (JIT + buffers)
		var bestMs = double.MaxValue;
		var budget = Stopwatch.StartNew();
		do
		{
			var sw = Stopwatch.StartNew();
			run();
			sw.Stop();
			bestMs = Math.Min(bestMs, sw.Elapsed.TotalMilliseconds);
		} while (budget.ElapsedMilliseconds < 3000);
		Console.WriteLine($"{label,-45} {bestMs,9:F1} ms {pairs / bestMs / 1000,10:F2} Mpairs/s");
	}
}
