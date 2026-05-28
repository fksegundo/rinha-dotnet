# Rinha .NET — Native AOT

[![CI](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ci.yml)
[![Publish GHCR](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ghcr.yml/badge.svg)](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ghcr.yml)
[![.NET 11](https://img.shields.io/badge/.NET-11-purple)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native-AOT-blue)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![Docker](https://img.shields.io/badge/Docker-GHCR-2496ED?logo=docker&logoColor=white)](https://github.com/fksegundo/rinha-dotnet/pkgs/container/rinha-dotnet)

**[Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026)** submission in **.NET 11 Native AOT**: exact kNN fraud detection (k=5) over a quantized spatial index, served behind a **Rust load balancer** with Unix socket FD passing.

> **Português:** see [docs/README.pt-BR.md](docs/README.pt-BR.md)

## Overview

The API accepts transactions on `POST /fraud-score`, extracts a 14-dimensional vector, queries a pre-built **RNSPCST2** index (3M references), and returns a fraud score and approval flag. The production binary is **Native AOT** for `linux-x64` with no managed runtime.

```
Client → Evented Rust LB :9999 → api1 / api2 (SCM_RIGHTS FD passing)
                                    ↓
                       mlock + pretouch + AVX2 search
```

| Component | Role |
| --- | --- |
| `Rinha.Api` | HTTP API (`/ready`, `/fraud-score`) |
| `Rinha.Preprocess` | Builds the index with **v0-cuts** header |
| `Rinha.Verify` | CLI to validate responses against a test dataset |
| Rust LB | **Evented** Load balancer on port **9999** (`rinha-dotnet-lb:local`) |
| 2× API | Replicas with `cpuset` and `mlock` enabled |

## Benchmark tuning

Production settings were chosen for parity with `rinha-rust`:

| Document | Scope |
| --- | --- |
| [docs/benchmark-matrix.md](docs/benchmark-matrix.md) | API tuning — warmup, thread pool, leaf size |
| [docs/perf-rework.md](docs/perf-rework.md) | Details on the 2026 performance rework |

Recommended stack config: `RINHA_PRETOUCH_INDEX=1`, `RINHA_MLOCK_INDEX=1`, `cpuset` pinning, evented LB.

## Endpoints

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/ready` | `503` during warmup; `200 ok` when ready |
| `POST` | `/fraud-score` | JSON or text body; returns `approved` and `fraud_score` |

## Prerequisites

- [.NET SDK 11](https://dotnet.microsoft.com/download) (preview; pinned in `global.json`)
- Docker and Docker Compose
- **`resources/references.json.gz`** — official reference dataset (~48 MB)

```bash
mkdir -p resources
cp /path/to/references.json.gz resources/
```

## Quick start

```bash
make up
curl http://localhost:9999/ready
# ok
```

Builds the Docker image, starts **api1**, **api2**, and the **Rust LB**, then waits for readiness.

```bash
make down    # stop the stack
make help    # list all targets
```

### Container image

Images are published to GHCR on pushes to `main`:

```bash
docker pull ghcr.io/fksegundo/rinha-dotnet:latest
```

## Makefile targets

| Command | Description |
| --- | --- |
| `make test` | Unit and integration tests |
| `make build` | Build the Docker image |
| `make up` | Build + start the stack |
| `make down` | Stop containers and remove volumes |
| `make ready` | Wait until `GET /ready` returns 200 |
| `make preprocess` | Build index locally → `test-data/` |
| `make verify` | Validate responses (requires index + `test-data.json`) |
| `make publish` | Native AOT publish (`linux-x64`) |

## Docker stack

Defined in `docker/docker-compose.yml`:

- **Rust LB** (`ghcr.io/fksegundo/rinha-api-lb`) exposes `:9999` and hands TCP connections to the APIs via **SCM_RIGHTS**
- LB → API control channels use **Unix domain sockets** (`/sockets/*.sock`) on a tmpfs volume
- Resource limits: `0.45 + 0.45 + 0.10` CPU, `165M + 165M + 20M` RAM

The image is multi-stage: index preprocess at build time, AOT publish with `IlcInstructionSet=avx2`, slim `runtime-deps` runtime (~10 MB binary).

## Local development

```bash
dotnet test -c Release

dotnet run --project src/Rinha.Preprocess -c Release -- \
  resources/references.json.gz test-data/rinha-specialist.idx

dotnet publish src/Rinha.Api/Rinha.Api.csproj -c Release -r linux-x64 \
  -p:PublishAot=true -p:StripSymbols=true

dotnet run --project src/Rinha.Verify -c Release -- \
  test-data/rinha-specialist.idx /path/test-data.json
```

## Environment variables

| Variable | Default (compose) | Description |
| --- | --- | --- |
| `RINHA_FD_SOCKET` | *(required in compose)* | FD-passing control socket (e.g. `/sockets/api1.sock`) |
| `RINHA_UDS_SOCKET` | — | Kestrel UDS path for local dev without the Rust LB |
| `RINHA_INDEX_PATH` | `/app/index/rinha-specialist.idx` | Index file path |
| `RINHA_WARMUP_QUERIES` | `64` | Warmup queries before `/ready` |
| `RINHA_PRETOUCH_INDEX` | `1` | Eagerly fault-in index mapping at startup |
| `RINHA_MLOCK_INDEX` | `1` | Locks index pages in RAM (requires memlock ulimit) |
| `RINHA_SEARCH_MODE` | `key-first` | Index search strategy (key-first uses active keys) |

## Repository layout

```
src/
  Rinha.Api/          HTTP API (parser, index, endpoints)
  Rinha.Preprocess/   RNSPCST1 index builder
  Rinha.Verify/       Dataset verifier CLI
test/
  Rinha.Tests/        Automated tests
docker/
  Dockerfile          AOT build + preprocess
  docker-compose.yml  Rust LB + 2 APIs (FD passing)
  haproxy.cfg         Legacy HAProxy config (benchmark override via `LB_IMAGE`)
docs/
  README.pt-BR.md              Portuguese documentation
  benchmark-matrix.md          API benchmark results
  proxy-benchmark-matrix.md    Load balancer benchmark results
resources/
  references.json.gz  Official dataset (not versioned)
```

## Implementation notes

- **Parser:** transaction-first → customer-first → JSON fallback
- **Vector:** 14D quantized, aligned with the official format
- **kNN:** exact search with k=5; precomputed JSON responses
- **Index:** `mmap` + `madvise`; **AVX2** search when available
- **Body:** `ArrayPool` + `ReadExactlyAsync` (no per-request allocation)
- **Readiness:** async warmup; wait for `GET /ready` before load tests


## License

Rinha de Backend 2026 participation project.
