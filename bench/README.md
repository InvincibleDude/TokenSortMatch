# Benchmarks

Head-to-head comparison of `TokenSortMatch` against [RapidFuzz](https://github.com/rapidfuzz/RapidFuzz)
(`process.cdist`, the fastest known implementation of this metric) and
[Raffinert.FuzzySharp](https://www.nuget.org/packages/Raffinert.FuzzySharp) (per-pair
brute force), on byte-identical datasets committed under `bench/data/`. The original
FuzzySharp package is not a like-for-like baseline: its `PreprocessMode.Full` removes
non-ASCII letters, so on these Cyrillic datasets it would score different strings.

All commands run from the repo root.

```bash
# (Re)generate datasets + golden scores (deterministic, seed 2026)
dotnet run --project bench/TokenSortMatch.Benchmarks -c Release -- dump

# .NET quick pass (Stopwatch, best run within a 3 s budget)
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
identical to TokenSortMatch's golden scores (`expected_*.txt`) for every query. The
.NET quick pass does the same for Raffinert.FuzzySharp on the first 500 queries of
each dataset: its token sort orders tokens with a culture-aware comparison, which
agrees with the ordinal order on these datasets.

## Results

AMD Ryzen 7 8845H (8C/16T), Linux, .NET 10.0.12, RapidFuzz 3.14.6, Raffinert.FuzzySharp
6.0.1 (September 2026). AVX2 only: the .NET side ran with
`DOTNET_EnableAVX512F=0 DOTNET_EnableAVX512=0`. Sustained steady state: best run within
a 3 s budget after warmup, all 16 threads. Score verification: **all 11 000 best
scores bit-identical** with RapidFuzz, and all 1 000 checked best scores identical with
Raffinert.FuzzySharp.

| workload | scorer | best time | Mpairs/s |
|---|---|---:|---:|
| short (2M pairs) | **TokenSortMatch** cutoff=80 | **0.8 ms** | **2 376** |
| short (2M pairs) | **TokenSortMatch** no cutoff | **1.2 ms** | **1 737** |
| short (2M pairs) | RapidFuzz `cdist` cutoff=80 | 8.3 ms | 242 |
| short (2M pairs) | RapidFuzz `cdist` no cutoff | 8.2 ms | 244 |
| short (100k pairs) | Raffinert.FuzzySharp per-pair (parallel) | 19.3 ms | 5.2 |
| long (100k pairs) | **TokenSortMatch** cutoff=80 | **7.2 ms** | **13.8** |
| long (100k pairs) | **TokenSortMatch** no cutoff | **7.2 ms** | **14.0** |
| long (100k pairs) | RapidFuzz `cdist` cutoff=80 | 55.8 ms | 1.79 |
| long (100k pairs) | RapidFuzz `cdist` no cutoff | 56.3 ms | 1.78 |
| long (50k pairs) | Raffinert.FuzzySharp per-pair (parallel) | 185.7 ms | 0.27 |

BenchmarkDotNet cross-check (mean ± stddev): short cutoff=80 1.19 ± 0.03 ms, cutoff=0
1.59 ± 0.01 ms; long cutoff=80 8.28 ± 0.11 ms, cutoff=0 8.77 ± 0.18 ms. Zero Gen0
collections; ~45 KB allocated per 10k-query batch (the result array). The
Raffinert.FuzzySharp baseline allocates ~94 MB per 100k pairs.

Summary: under identical ISA conditions (AVX2) TokenSortMatch is **~7-10× faster**
than RapidFuzz's C++ SIMD batch API on short items and **~7.7× faster** on long
multi-block items, driven by 16/32-bit lane packing (16/8 items per Vector256
pass), a 4-lane vectorized multi-block path with per-lane carry chains,
transposed bitmasks precomputed at index construction, exactly-rounded group
pruning and static-chunk parallelism. Both are far ahead of per-pair
Raffinert.FuzzySharp (~50-450×).
TokenSortMatch is measured through its public .NET API; RapidFuzz through its
native batch API from Python — calling RapidFuzz *from .NET* would add interop +
UTF-16 marshalling costs not shown here. RapidFuzz re-derives pattern bitmasks per
`cdist` call, while `TokenSortIndex` amortizes them across calls — that
amortization is the point of the index API.
