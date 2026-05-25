# Benchmark runs

All runs use `rinha-benchmark` (`make test-1`, `API_URL=http://localhost:9999`) unless noted.

## Phase 0 — Baseline (external Rust LB + thread-pool)

| p50 | p99 | final_score | FP/FN/HTTP err |
| ---: | ---: | ---: | --- |
| 0.61ms | **0.99ms** | 6000 | 0/0/0 |

## Phase 1 — Internal LB + thread-pool

| p50 | p99 | final_score | FP/FN/HTTP err |
| ---: | ---: | ---: | --- |
| 0.57ms | **0.97ms** | 6000 | 0/0/0 |

## Phase 2 — Event-loop stabilization + fast path (2026-05-25)

Stack: internal LB, `RINHA_RUNTIME=event-loop` (FdWorkerPool-backed `EpollLoop`), `FraudScoreFastPath` in `RawHttpHandler`.

| Run | p99 | final_score | HTTP err |
| --- | ---: | ---: | ---: |
| Single | **0.96ms** | 6000 | 0 |
| Repeat 1 | 1.01ms | 5995.92 | 0 |
| Repeat 2 | 0.99ms | 6000 | 0 |
| Repeat 3 | 1.00ms | 6000 | 0 |

Divergent: p99 1.08ms valid traffic, **equivalent_correct=450**, 0 FP/FN, 0 HTTP errors.

Stability: **10/10** consecutive deploys, `/ready` OK.

## Phase 3 — Early-exit threshold sweep

`RINHA_EARLY_EXIT_THRESHOLD` on same stack:

| Threshold | p99 | final_score | FP/FN/HTTP |
| ---: | ---: | ---: | --- |
| 0 | **0.96ms** | 6000 | 0/0/0 |
| 1000 | 1.00ms | 6000 | 0/0/0 |
| 5000 | 1.00ms | 5999.83 | 0/0/0 |
| 10000 | 0.99ms | 6000 | 0/0/0 |

**Chosen:** threshold **0** (disabled) — best p99, no detection impact.

## Fallback — thread-pool + fast path

| p99 | final_score | HTTP err |
| ---: | ---: | ---: |
| 1.02ms | 5989.84 | 0 |

## Notes

- Native single-thread epoll (`Syscalls`/`ConnectionTable`/`BufferSlab`/`HandleClient`) caused keep-alive spin and mass HTTP errors under load; production path uses FdWorkerPool dispatch until native epoll is reworked.
- Best local p99 this session: **0.96ms** (−0.03ms vs 0.99ms baseline).
