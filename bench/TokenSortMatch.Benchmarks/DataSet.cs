using TokenSortMatch;

/// <summary>
/// Deterministic benchmark datasets, written to <c>bench/data</c> so the .NET and
/// RapidFuzz (Python) benchmarks run on byte-identical inputs. Run from the repo root:
/// <c>dotnet run --project bench/TokenSortMatch.Benchmarks -c Release -- dump</c>.
/// </summary>
static class DataSet
{
	public const string Dir = "bench/data";

	static readonly string[] Stems =
	[
		"Лекционные занятия", "Практические занятия", "Лабораторные работы",
		"Самостоятельная работа", "Курсовая работа", "Контрольная работа", "Экзамен",
	];

	public static void Dump()
	{
		Directory.CreateDirectory(Dir);
		var rnd = new Random(2026);
		var words = Stems.SelectMany(s => s.Split(' ')).ToArray();

		var itemsShort = Gen(200, () => $"{Stems[rnd.Next(Stems.Length)]} {rnd.Next(1000)}");
		var queriesShort = Gen(10_000, () => $"{Stems[rnd.Next(Stems.Length)]} {rnd.Next(1000)} курс {rnd.Next(20)}");
		var itemsLong = Gen(100, () => Sentence(rnd, words, 40));
		var queriesLong = Gen(1_000, () => Sentence(rnd, words, 40));

		Write("items_short.txt", itemsShort);
		Write("queries_short.txt", queriesShort);
		Write("items_long.txt", itemsLong);
		Write("queries_long.txt", queriesLong);

		// Golden per-query best scores (threshold 0 => the true best, no gating) for
		// cross-library verification by bench/rapidfuzz_bench.py.
		Write("expected_short.txt", Scores(itemsShort, queriesShort));
		Write("expected_long.txt", Scores(itemsLong, queriesLong));
		Console.WriteLine($"Datasets written to {Path.GetFullPath(Dir)}");
	}

	static string[] Scores(string[] items, string[] queries) =>
		new TokenSortIndex(items).FindBestScores(queries, 0).Select(s => s.ToString()).ToArray();

	static void Write(string name, string[] lines) =>
		File.WriteAllLines(Path.Combine(Dir, name), lines);

	static string[] Gen(int n, Func<string> make) =>
		Enumerable.Range(0, n).Select(_ => make()).ToArray();

	static string Sentence(Random rnd, string[] words, int count) =>
		string.Join(' ', Enumerable.Range(0, count).Select(_ => words[rnd.Next(words.Length)]));

	public static string[] Load(string name)
	{
		var path = Path.Combine(Dir, name);
		if (!File.Exists(path))
			throw new FileNotFoundException(
				$"'{path}' not found. Run from the repo root, and generate data first with " +
				"'dotnet run --project bench/TokenSortMatch.Benchmarks -c Release -- dump'.");
		return File.ReadAllLines(path);
	}
}
