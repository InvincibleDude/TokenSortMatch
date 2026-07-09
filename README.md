# TokenSortMatch

Fast token-sort fuzzy best-matching for .NET. Zero dependencies.

Scores are **bit-identical** to `Fuzz.Ratio(normalize(a), normalize(b))` from
RapidFuzz/FuzzySharp (token-sort ratio with full preprocessing) — verified by a
randomized oracle test suite — but batch matching runs **~100× faster** than calling the
library per pair, via:

- transposed pattern bitmasks + bit-parallel LCS (Hyyrö/Myers), 4 items per
  `Vector256<ulong>` pass (auto opt-in; scalar fallback for CPUs without 256-bit SIMD),
- allocation-free query normalization (char-map table, in-place token sort),
- exactly-rounded score upper bounds pruning item groups against the running best,
- multi-block path for items whose normalized form exceeds 64 chars.

## Usage

```csharp
using TokenSortMatch;

// Pairwise
int score = TokenSort.Ratio("Лекционные занятия", "занятия ЛЕКЦИОННЫЕ!"); // 100
string norm = TokenSort.Normalize("The-Quick! Brown?? fox"); // "brown fox quick the"

// Batch: build an index over candidate items once, query many times
var index = new TokenSortIndex(["Лекционные занятия", "Практические занятия", "Экзамен"]);

BestMatch? best = index.FindBest("занятия лекционные 5", threshold: 80);
// -> BestMatch(ItemIndex: 0, Score: 87)

int[] scores = index.FindBestScores(queries, threshold: 80);      // parallel, -1 if below
BestMatch?[] all = index.FindBestMatches(queries, threshold: 80); // parallel, null if below
```

Semantics:

- `Normalize`: lowercase letters/digits, everything else → spaces, tokens sorted
  ordinally, joined with single spaces (same per-char behavior as
  `StringPreprocessor.Full` / RapidFuzz `default_process`).
- Score: `round(100 · (1 − indelDistance / (len1 + len2)))` with banker's rounding;
  empty normalized strings score 0.
- `FindBest*`: thresholds ≤ 0 always match (scores are non-negative); ties return one
  deterministic best-scoring item. `TokenSortIndex` is immutable and thread-safe.

## License

MIT
