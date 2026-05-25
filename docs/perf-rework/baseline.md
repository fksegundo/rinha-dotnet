# Baseline (pre-rework)

Captured from existing local benchmark before internal LB + event-loop changes.

## Stack (before rework)

- LB: `ghcr.io/fksegundo/rinha-api-lb:latest` (external Rust LB)
- API runtime: `FdSocketServer` + `FdWorkerPool` (256 sync threads)
- CPU: lb=0.04, api1=0.48, api2=0.48
- Warmup: 4096 queries

## Official-shaped load (`make test-1`)

Source: `/home/filon/Documentos/Codigos/rinha-benchmark/results/test-1-r1-d1/results.json`

| Metric | Value |
| --- | ---: |
| p50 | 0.61ms |
| p95 | 0.87ms |
| p99 | **0.99ms** |
| final_score | 6000 |
| FP / FN / HTTP err | 0 / 0 / 0 |

## Rinha server reference (previous submissions)

| Submission | p99 | final_score |
| --- | --- | ---: |
| HAProxy (commit 81a9bd6) | 2.22ms | 5653 |
| External Rust LB (commit 6f10387) | 4.27ms | 5369.54 |

## Divergent traffic (`make test-divergent`)

Source: `/home/filon/Documentos/Codigos/rinha-benchmark/results/divergent/results.json`

| Metric | Value |
| --- | ---: |
| p99 (valid traffic) | 1.03ms |
| FP / FN / HTTP err | 0 / 0 / 0 |
| equivalent_correct | 450 |
