using BenchmarkDotNet.Running;

if (args is ["dump"])
{
	DataSet.Dump();
	return;
}
if (args is ["quick"])
{
	QuickBench.Run();
	return;
}
if (args is ["profile", var mode])
{
	// Hammer one workload for ~5s so `perf record` gets clean samples.
	var items = DataSet.Load("items_short.txt");
	var queries = DataSet.Load("queries_short.txt");
	var index = new TokenSortMatch.TokenSortIndex(items);
	var cutoff = mode == "c80" ? 80 : 0;
	index.FindBestScores(queries, cutoff); // warmup
	var sw = System.Diagnostics.Stopwatch.StartNew();
	var reps = 0;
	while (sw.ElapsedMilliseconds < 5000)
	{
		index.FindBestScores(queries, cutoff);
		reps++;
	}
	Console.WriteLine($"{reps} reps, {sw.Elapsed.TotalMilliseconds / reps:F1} ms/rep");
	return;
}
BenchmarkRunner.Run<TokenSortBenchmarks>();
