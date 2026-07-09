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

AMD Ryzen 7 8845H (8C/16T, AVX2 — no AVX-512 used), Linux, .NET 10, RapidFuzz 3.14.5
(July 2026). Sustained steady state: best run within a 3 s budget after warmup, all
16 threads. Score verification: **all 11 000 best scores bit-identical** between the
two libraries.

| workload | scorer | best time | Mpairs/s |
|---|---|---:|---:|
| short (2M pairs) | **TokenSortMatch** cutoff=80 | **0.9 ms** | **2 288** |
| short (2M pairs) | **TokenSortMatch** no cutoff | **1.1 ms** | **1 774** |
| short (2M pairs) | RapidFuzz `cdist` cutoff=80 | 7.8 ms | 258 |
| short (2M pairs) | RapidFuzz `cdist` no cutoff | 7.7 ms | 258 |
| short (100k pairs) | FuzzySharp per-pair (parallel) | 51.8 ms | 1.9 |
| long (100k pairs) | **TokenSortMatch** cutoff=80 | 56.4 ms | 1.77 |
| long (100k pairs) | RapidFuzz `cdist` cutoff=80 | 58.0 ms | 1.72 |
| long (50k pairs) | FuzzySharp per-pair (parallel) | 223 ms | 0.22 |

BenchmarkDotNet cross-check (means): short cutoff=80 1.50 ms ± 0.05, cutoff=0
1.70 ms ± 0.01, long ~62 ms; zero Gen0 collections, ~45 KB allocated per 10k-query
batch (the result array).

Summary: on short items TokenSortMatch is **~7-9× faster** than RapidFuzz's C++
SIMD batch API under identical ISA conditions (AVX2), driven by 16/32-bit lane
packing (16/8 items per Vector256 pass), transposed bitmasks precomputed at index
construction, exactly-rounded group pruning and static-chunk parallelism. On long
multi-block items the two are within a few percent. Both are far ahead of per-pair
FuzzySharp (~1 200× / ~8×). TokenSortMatch is measured through its public .NET API;
RapidFuzz through its native batch API from Python — calling RapidFuzz *from .NET*
would add interop + UTF-16 marshalling costs not shown here. RapidFuzz re-derives
pattern bitmasks per `cdist` call, while `TokenSortIndex` amortizes them across
calls — that amortization is the point of the index API.
