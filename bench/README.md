# Benchmarks

Head-to-head comparison of `TokenSortMatch` against [RapidFuzz](https://github.com/rapidfuzz/RapidFuzz)
(`process.cdist`, the fastest known implementation of this metric) and
[FuzzySharp](https://www.nuget.org/packages/FuzzySharp) (per-pair brute force), on
byte-identical datasets committed under `bench/data/`.

All commands run from the repo root.

```bash
# (Re)generate datasets + golden scores (deterministic, seed 2026)
dotnet run --project bench/TokenSortMatch.Benchmarks -c Release -- dump

# .NET quick pass (Stopwatch, best of 5)
dotnet run --project bench/TokenSortMatch.Benchmarks -c Release -- quick

# .NET rigorous pass (BenchmarkDotNet)
dotnet run --project bench/TokenSortMatch.Benchmarks -c Release

# RapidFuzz counterpart (also verifies score equivalence)
uv run bench/rapidfuzz_bench.py
```

## Workloads

| dataset | items | queries | shape |
|---|---|---|---|
| short | 200 | 10 000 | `"стем NNN"` vs `"стем NNN курс KK"`, ~25 normalized chars |
| long | 100 | 1 000 | 40 random words, ~300 normalized chars (multi-block LCS) |

One measured operation = the full batch: best score per query across all items.
`Mpairs/s` = (items × queries) / time. The RapidFuzz side computes the full score
matrix (`cdist`) plus a row max — that is its canonical batch API; both sides use all
cores and the same 80 cutoff (which enables pruning in TokenSortMatch and banded
computation in RapidFuzz).

The Python script first verifies that RapidFuzz scores (banker's-rounded) are
identical to TokenSortMatch's golden scores (`expected_*.txt`) for every query.

## Results

AMD Ryzen 7 8845H (8C/16T, AVX2), Linux, .NET 10, RapidFuzz 3.14.5 (July 2026).
Best of 5 runs after warmup; all 16 threads; score verification: **all 11 000
best scores bit-identical** between the two libraries.

| workload | scorer | time | Mpairs/s |
|---|---|---:|---:|
| short (2M pairs) | RapidFuzz `cdist` cutoff=80 | 8.6 ms | 232 |
| short (2M pairs) | **TokenSortMatch** cutoff=80 | 12.7 ms | 157 |
| short (100k pairs) | FuzzySharp per-pair (parallel) | 121 ms | 0.8 |
| long (100k pairs) | RapidFuzz `cdist` cutoff=80 | 57.5 ms | 1.74 |
| long (100k pairs) | **TokenSortMatch** cutoff=80 | 61.8 ms | 1.62 |
| long (50k pairs) | FuzzySharp per-pair (parallel) | 210 ms | 0.24 |

Summary: RapidFuzz's C++ SIMD core is ~1.5× faster on short items (its AVX2 path
packs 8×32-bit lanes per pass vs our 4×64-bit); on long multi-block items the two
are within ~7%. Both are ~200× (short) / ~7× (long) faster than per-pair FuzzySharp.
TokenSortMatch is measured through its public .NET API; RapidFuzz through its
native batch API from Python — calling RapidFuzz *from .NET* would add interop +
UTF-16 marshalling costs not shown here.
