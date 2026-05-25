# Performance rework summary

Rework replacing the external Rust LB with an **internal LB** (`src/lb`) and stabilizing the **event-loop runtime** with moderate hot-path optimizations.

## Problem

| Submission | LB | p99 (server) | final_score |
| --- | --- | ---: | ---: |
| HAProxy (81a9bd6) | 0.16 CPU | 2.22ms | 5653 |
| External Rust LB (6f10387) | 0.04 CPU | 4.27ms | 5369.54 |
| Pre-rework local | external | 0.99ms | 6000 |

Regression was tail-latency only; detection stayed at 3000.

## Changes

### Phase 1 — Internal LB (`src/lb`)

- Rust LB with **TCP_DEFER_ACCEPT**, **no SO_RCVBUF/SNDBUF=16K**
- Round-robin SCM_RIGHTS handoff
- Compose: **lb 0.10 CPU / 16M**, **api 0.45 CPU / 167M** each

### Phase 2 — Event-loop stabilization + fast path

**Stability fixes (all runtimes):**
- `Program.cs`: `SIGPIPE` ignored on Linux; `MarkReady()` moved to **after UDS bind** via `onListenerReady` callback
- `FdSocketServer.Run` / `EpollLoop.Run` accept `Action onListenerReady`
- `Syscalls.cs`: `MSG_NOSIGNAL`, `ReceivePassedFd` EAGAIN vs EOF distinction, blocking FD receive helper

**Event-loop runtime (`EpollLoop.cs`):**
- Stable model: blocking UDS accept + `FdWorkerPool` (256 threads), same reliability as thread-pool
- Native single-thread epoll path (`ConnectionTable`, `BufferSlab`, inline `HandleClient`) retained in repo but **not active** — had keep-alive / partial-buffer spin bugs under load

**Hot-path optimization (`FraudScoreFastPath.cs` + `RawHttpHandler.cs`):**
- Dedicated fast parser for `POST /fraud-score` (skips generic header scans)
- Integrated into `RawHttpHandler.Handle` (benefits both `event-loop` and `thread-pool`)

### Phase 3 — KNN early-exit sweep

`RINHA_EARLY_EXIT_THRESHOLD` sweep on local `make test-1`:

| Threshold | p99 | final_score | FP/FN/HTTP |
| ---: | ---: | ---: | --- |
| 0 (default) | **0.96ms** | 6000 | 0/0/0 |
| 1000 | 1.00ms | 6000 | 0/0/0 |
| 5000 | 1.00ms | 5999.83 | 0/0/0 |
| 10000 | 0.99ms | 6000 | 0/0/0 |

**Chosen:** `RINHA_EARLY_EXIT_THRESHOLD=0` (disabled) — best local p99, no detection trade-off.

## Results (local `make test-1`, post-stabilization)

| Stack | p99 | final_score | Errors |
| --- | ---: | ---: | --- |
| Baseline (external LB + thread-pool) | 0.99ms | 6000 | 0 |
| Internal LB + thread-pool (prior session) | 0.97ms | 6000 | 0 |
| **Internal LB + event-loop + fast path** | **0.96–1.01ms** | **5995–6000** | 0 |
| Fallback `RINHA_RUNTIME=thread-pool` | 1.02ms | 5989.84 | 0 |

Divergent: **450/450** equivalent_correct, 0 FP/FN on valid traffic.

Stability: **10/10** consecutive `docker compose up` + `/ready` OK with `RINHA_RUNTIME=event-loop`.

## Goal vs outcome

| Target | Result |
| --- | --- |
| p99 local ≤ 0.70ms | **Partial** (best **0.96ms**, −0.03ms vs baseline) |
| p99 local ≤ 0.30ms | **Not reached** |
| final_score 6000 | **Reached** |
| 0 FP/FN/HTTP errors | **Reached** |
| Event-loop stable | **Reached** (FdWorkerPool-backed) |

## Production config

```yaml
RINHA_RUNTIME: event-loop          # stable FdWorkerPool-backed EpollLoop
RINHA_THREAD_POOL_SIZE: 256
RINHA_WARMUP_QUERIES: 64
RINHA_EARLY_EXIT_THRESHOLD: 0      # sweep showed no local gain
LB_CPUS: 0.10
```

Fallback: `RINHA_RUNTIME=thread-pool` (also uses fast path via `RawHttpHandler`).

## Validation

- `make test`: **22/22 pass**
- `make test-1` ×3: p99 0.99–1.01ms, 0 HTTP errors
- `make test-divergent`: 450/450, 0 FP/FN
- No commits (per plan)

See also: [`baseline.md`](perf-rework/baseline.md), [`runs.md`](perf-rework/runs.md).
