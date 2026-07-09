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
BenchmarkRunner.Run<TokenSortBenchmarks>();
