# /// script
# requires-python = ">=3.10"
# dependencies = ["rapidfuzz>=3.9", "numpy>=1.26"]
# ///
"""RapidFuzz counterpart of the .NET quick benchmark.

Run from the repo root (after dumping the datasets from the .NET side):

    dotnet run --project bench/TokenSortMatch.Benchmarks -c Release -- dump
    uv run bench/rapidfuzz_bench.py

Uses process.cdist (RapidFuzz's batch API: full score matrix + row max) with
fuzz.token_sort_ratio + utils.default_process — the exact metric TokenSortMatch
implements. Also verifies that RapidFuzz's scores, rounded like .NET Math.Round
(banker's), are identical to the golden best scores dumped by TokenSortMatch.
"""

import time
from pathlib import Path

import numpy as np
from rapidfuzz import fuzz, process, utils

DATA = Path("bench/data")


def load(name: str) -> list[str]:
    path = DATA / name
    if not path.exists():
        raise SystemExit(
            f"'{path}' not found. Run from the repo root and dump datasets first:\n"
            "  dotnet run --project bench/TokenSortMatch.Benchmarks -c Release -- dump"
        )
    return path.read_text(encoding="utf-8").splitlines()


def cdist(queries: list[str], items: list[str], cutoff: int | None) -> np.ndarray:
    kwargs = {} if cutoff is None else {"score_cutoff": cutoff}
    return process.cdist(
        queries, items,
        scorer=fuzz.token_sort_ratio,
        processor=utils.default_process,
        workers=-1,
        **kwargs,
    )


def bench(label: str, items: list[str], queries: list[str], expected: np.ndarray) -> None:
    print(f"== {label} ==")
    pairs = len(items) * len(queries)

    # Equivalence check: banker's rounding (np.rint) == .NET Math.Round default.
    best = np.rint(cdist(queries, items, cutoff=None)).astype(int).max(axis=1)
    mismatches = int((best != expected).sum())
    status = "all identical" if mismatches == 0 else f"{mismatches} MISMATCHES"
    print(f"score verification vs TokenSortMatch: {status} ({len(expected)} queries)")

    for cutoff in (80, None):
        cdist(queries, items, cutoff=cutoff)  # warmup
        best_s = float("inf")
        for _ in range(5):
            t0 = time.perf_counter()
            cdist(queries, items, cutoff=cutoff)
            best_s = min(best_s, time.perf_counter() - t0)
        name = f"process.cdist cutoff={cutoff if cutoff is not None else 'none'}"
        print(f"{name:<45} {best_s * 1000:9.1f} ms {pairs / best_s / 1e6:10.2f} Mpairs/s")
    print()


def main() -> None:
    bench(
        "short: 200 items x 10 000 queries (~25 chars)",
        load("items_short.txt"), load("queries_short.txt"),
        np.array([int(x) for x in load("expected_short.txt")]),
    )
    bench(
        "long: 100 items x 1 000 queries (~300 chars, multi-block)",
        load("items_long.txt"), load("queries_long.txt"),
        np.array([int(x) for x in load("expected_long.txt")]),
    )


if __name__ == "__main__":
    main()
