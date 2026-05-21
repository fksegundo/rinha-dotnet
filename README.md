# Rinha .NET — Native AOT

[![CI](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ci.yml)
[![Publish GHCR](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ghcr.yml/badge.svg)](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ghcr.yml)
[![.NET 11](https://img.shields.io/badge/.NET-11-purple)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native-AOT-blue)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![Docker](https://img.shields.io/badge/Docker-GHCR-2496ED?logo=docker&logoColor=white)](https://github.com/fksegundo/rinha-dotnet/pkgs/container/rinha-dotnet)

**[Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026)** submission in **.NET 11 Native AOT**: exact kNN fraud detection (k=5) over a quantized spatial index, served by **Kestrel** behind **HAProxy** over Unix domain sockets.

> **Português:** see [docs/README.pt-BR.md](docs/README.pt-BR.md)

## Overview

The API accepts transactions on `POST /fraud-score`, extracts a 14-dimensional vector, queries a pre-built **RNSPCST1** index (3M references), and returns a fraud score and approval flag. The production binary is **Native AOT** for `linux-x64` with no managed runtime.

```
Client → HAProxy :9999 → api1 / api2 (Unix domain sockets)
                              ↓
                         mmap index + AVX2 search
```

| Component | Role |
| --- | --- |
| `Rinha.Api` | HTTP API (`/ready`, `/fraud-score`) |
| `Rinha.Preprocess` | Builds the index from `references.json.gz` |
| `Rinha.Verify` | CLI to validate responses against a test dataset |
| HAProxy | Load balancer on port **9999** (TCP mode) |
| 2× API | Replicas within challenge CPU/RAM limits |

## Benchmark tuning

Production settings were chosen from local benchmark matrices (score **6000**, **0 FP/FN**):

| Document | Scope |
| --- | --- |
| [docs/benchmark-matrix.md](docs/benchmark-matrix.md) | API tuning — warmup, thread pool, leaf size |
| [docs/proxy-benchmark-matrix.md](docs/proxy-benchmark-matrix.md) | Load balancer tuning — HAProxy TCP vs HTTP, nginx comparison |

Recommended stack config: `RINHA_WARMUP_QUERIES=64`, `DOTNET_ThreadPool_MinThreads=16`, `RINHA_LEAF_SIZE=48`, HAProxy **TCP mode** with `tcp-check inter 50ms`.

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

Builds the Docker image, starts **api1**, **api2**, and **HAProxy**, then waits for readiness.

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

- **HAProxy** exposes `:9999` and balances across two API instances
- LB → API traffic uses **Unix domain sockets** (`/sockets/*.sock`) on a tmpfs volume
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
| `RINHA_UDS_SOCKET` | *(required)* | Unix socket path (e.g. `/sockets/api1.sock`) |
| `RINHA_INDEX_PATH` | `/app/index/rinha-specialist.idx` | Index file path |
| `RINHA_WARMUP_QUERIES` | `64` | Warmup queries before `/ready` |
| `RINHA_SEARCH_MODE` | `key-first` | Index search strategy |
| `RINHA_MAX_BODY_BYTES` | `8192` | Maximum request body size |
| `DOTNET_gcServer` | `0` | Workstation GC (better in small containers) |
| `DOTNET_ThreadPool_MinThreads` | `16` | Thread pool minimum threads |
| `MALLOC_ARENA_MAX` | `2` | Limits native memory arena fragmentation |

## Repository layout

```
src/
  Rinha.Api/          Kestrel API (parser, index, endpoints)
  Rinha.Preprocess/   RNSPCST1 index builder
  Rinha.Verify/       Dataset verifier CLI
test/
  Rinha.Tests/        Automated tests
docker/
  Dockerfile          AOT build + preprocess
  docker-compose.yml  HAProxy + 2 APIs (UDS)
  haproxy.cfg         HAProxy configuration
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
- **Readiness:** async warmup; wait for `GET /ready` before load tests (HAProxy **TCP mode** — health check is socket-only)

## Git hooks

This repo rejects `Co-authored-by` commit trailers. Enable the local hook once:

```bash
git config core.hooksPath .githooks
```

## License

Rinha de Backend 2026 participation project.
