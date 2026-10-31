# TokenSortMatch

Fast token-sort fuzzy best-matching for .NET. Zero dependencies.

Scores are **bit-identical** to RapidFuzz's
`fuzz.token_sort_ratio(a, b, processor=utils.default_process)` with banker's rounding
(apart from a few character classes, see [Compatibility](#compatibility)) — verified by
a randomized oracle test suite and a RapidFuzz cross-check — while batch matching runs
**~7-10× faster than RapidFuzz's native `cdist`** on short items (~7.7× on long ones)
and ~50-450× faster than per-pair Raffinert.FuzzySharp, under plain AVX2
([benchmarks](https://github.com/InvincibleDude/TokenSortMatch/blob/main/bench/README.md)),
via:

- transposed pattern bitmasks + bit-parallel LCS (Hyyrö 2004) in length-bucketed
  16/32/64-bit lanes — 16, 8 or 4 items per `Vector256` pass (auto opt-in; scalar
  fallback for CPUs without 256-bit SIMD),
- allocation-free query normalization (char-map table, in-place token sort),
- exactly-rounded score upper bounds pruning item groups against the running best,
- 4-lane vectorized multi-block path (per-lane carry chains) for items whose
  normalized form exceeds 64 chars,
- bitmask tables that only hold rows for their own items' characters, so indexes over
  large alphabets (CJK) stay compact.

## Usage

```csharp
using TokenSortMatch;

// Pairwise
int score = TokenSort.Ratio("Лекционные занятия", "занятия ЛЕКЦИОННЫЕ!"); // 100
string norm = TokenSort.Normalize("The-Quick! Brown?? fox"); // "brown fox quick the"

// Batch: build an index over candidate items once, query many times
var index = new TokenSortIndex(["Лекционные занятия", "Практические занятия", "Экзамен"]);

BestMatch? best = index.FindBest("занятия лекционные 5", threshold: 80);
// -> BestMatch(ItemIndex: 0, Score: 95)

int[] scores = index.FindBestScores(queries, threshold: 80);      // parallel, -1 if below
BestMatch?[] all = index.FindBestMatches(queries, threshold: 80); // parallel, null if below
```

Semantics:

- `Normalize`: letters and digits are lowercased culture-invariantly (`İ` → `i`, as in
  Unicode's simple case mapping), everything else → spaces, tokens sorted ordinally,
  joined with single spaces.
- Score: `round(100 · (1 − indelDistance / (len1 + len2)))` with banker's rounding,
  evaluated with the same double operations as RapidFuzz, so exact .5 scores round the
  same way; empty normalized strings score 0.
- `FindBest*`: thresholds ≤ 0 always match (scores are non-negative); ties return one
  deterministic best-scoring item. `TokenSortIndex` is immutable and thread-safe.

## Compatibility

- **RapidFuzz** `token_sort_ratio` + `default_process`: identical, except that characters
  in Unicode categories No/Nl (`²`, `½`, `Ⅳ`, `①`) and characters above U+FFFF are
  separators here, and a few dozen letters added in recent Unicode versions are letters
  here but not in RapidFuzz's tables.
- **Raffinert.FuzzySharp**: `Fuzz.Ratio(TokenSort.Normalize(a), TokenSort.Normalize(b))`,
  rounded, is identical. Its own `Fuzz.TokenSortRatio(a, b, StringPreprocessor.Full)` sorts
  tokens with a culture-aware comparison and keeps `İ`, so it can differ on mixed-script
  text.
- **FuzzySharp** (the original package): `PreprocessMode.Full` removes every non-ASCII
  letter, so its token-sort scores differ on Cyrillic and other scripts. Its `Fuzz.Ratio`
  computes `(len1 + len2 − dist) / (len1 + len2)`, which rounds a few exact .5 scores the
  other way.

## License

MIT
