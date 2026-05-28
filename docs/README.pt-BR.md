# Rinha .NET — Native AOT

[![CI](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ci.yml)
[![Publish GHCR](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ghcr.yml/badge.svg)](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ghcr.yml)
[![.NET 11](https://img.shields.io/badge/.NET-11-purple)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native-AOT-blue)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![Docker](https://img.shields.io/badge/Docker-GHCR-2496ED?logo=docker&logoColor=white)](https://github.com/fksegundo/rinha-dotnet/pkgs/container/rinha-dotnet)

Solução em **.NET 11 Native AOT** para a [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026): detecção de fraude por kNN exato (k=5) sobre um índice espacial quantizado, servida atrás de um **load balancer Rust** com FD passing via Unix domain sockets.

> **English:** see [README.md](../README.md) at the repository root.

## Visão geral

A API recebe transações em `POST /fraud-score`, extrai um vetor de 14 dimensões, consulta um índice **RNSPCST2** pré-computado (3M referências) e responde com score e aprovação. O binário é compilado com **Native AOT** para `linux-x64`, sem runtime gerenciado em produção.

```
Cliente → LB Rust Evented :9999 → api1 / api2 (SCM_RIGHTS FD passing)
                                    ↓
                       mlock + pretouch + busca AVX2
```

| Componente | Papel |
| --- | --- |
| `Rinha.Api` | API HTTP (`/ready`, `/fraud-score`) |
| `Rinha.Preprocess` | Gera o índice com header de **v0-cuts** |
| `Rinha.Verify` | CLI para validar respostas contra dataset de teste |
| Rust LB | LB **Evented** na porta **9999** (`rinha-dotnet-lb:local`) |
| 2× API | Réplicas com `cpuset` e `mlock` ativos |

## Benchmarks e tuning

Configuração de produção escolhida para paridade com `rinha-rust`:

| Documento | Escopo |
| --- | --- |
| [benchmark-matrix.md](benchmark-matrix.md) | Tuning da API — warmup, thread pool, leaf size |
| [perf-rework.md](perf-rework.md) | Detalhes sobre o rework de performance 2026 |

Config recomendada: `RINHA_PRETOUCH_INDEX=1`, `RINHA_MLOCK_INDEX=1`, `cpuset`, LB evented.

## Endpoints

| Método | Rota | Descrição |
| --- | --- | --- |
| `GET` | `/ready` | `503` durante warmup; `200 ok` quando pronta |
| `POST` | `/fraud-score` | Body JSON ou texto; retorna `approved` e `fraud_score` |

## Pré-requisitos

- [.NET SDK 11](https://dotnet.microsoft.com/download) (preview; versão fixada em `global.json`)
- Docker e Docker Compose
- Arquivo **`resources/references.json.gz`** — dataset oficial de referências (~48 MB)

```bash
mkdir -p resources
cp /caminho/para/references.json.gz resources/
```

## Início rápido

```bash
make up
curl http://localhost:9999/ready
# ok
```

Constrói a imagem Docker, sobe **api1**, **api2** e o **Rust LB**, e aguarda o readiness.

```bash
make down    # para a stack
make help    # lista todos os alvos
```

### Imagem no GHCR

Imagens publicadas automaticamente no push para `main`:

```bash
docker pull ghcr.io/fksegundo/rinha-dotnet:latest
```

## Comandos (Makefile)

| Comando | Descrição |
| --- | --- |
| `make test` | Testes unitários e de integração |
| `make build` | Build da imagem Docker |
| `make up` | Build + sobe a stack |
| `make down` | Para containers e remove volumes |
| `make ready` | Aguarda `GET /ready` retornar 200 |
| `make preprocess` | Gera índice local em `test-data/` |
| `make verify` | Valida respostas (requer índice + `test-data.json`) |
| `make publish` | Publish Native AOT local (`linux-x64`) |

## Arquitetura Docker

Stack (`docker/docker-compose.yml`):

- **Rust LB** (`ghcr.io/fksegundo/rinha-api-lb`) expõe `:9999` e entrega conexões TCP às APIs via **SCM_RIGHTS**
- Canais de controle LB → API via **Unix domain sockets** (`/sockets/*.sock`) em volume tmpfs
- Limites: `0.45 + 0.45 + 0.10` CPU, `165M + 165M + 20M` RAM

A imagem é multi-stage: preprocess do índice no build, publish AOT com `IlcInstructionSet=avx2`, runtime `runtime-deps` enxuto (~10 MB de binário).

## Desenvolvimento local

```bash
dotnet test -c Release

dotnet run --project src/Rinha.Preprocess -c Release -- \
  resources/references.json.gz test-data/rinha-specialist.idx

dotnet publish src/Rinha.Api/Rinha.Api.csproj -c Release -r linux-x64 \
  -p:PublishAot=true -p:StripSymbols=true

dotnet run --project src/Rinha.Verify -c Release -- \
  test-data/rinha-specialist.idx /caminho/test-data.json
```

## Variáveis de ambiente

| Variável | Padrão (compose) | Descrição |
| --- | --- | --- |
| `RINHA_FD_SOCKET` | *(obrigatório no compose)* | Socket de controle FD passing (ex.: `/sockets/api1.sock`) |
| `RINHA_UDS_SOCKET` | — | Kestrel UDS para dev local sem o Rust LB |
| `RINHA_INDEX_PATH` | `/app/index/rinha-specialist.idx` | Caminho do índice |
| `RINHA_WARMUP_QUERIES` | `64` | Queries de warmup antes do `/ready` |
| `RINHA_PRETOUCH_INDEX` | `1` | Efetua leitura completa do índice no startup (warmup de cache) |
| `RINHA_MLOCK_INDEX` | `1` | Trava as páginas do índice na RAM (requer ulimit memlock) |
| `RINHA_SEARCH_MODE` | `key-first` | Estratégia de busca (key-first utiliza pruning e active keys) |

## Estrutura do repositório

```
src/          API, preprocess e verify
test/         Testes automatizados
docker/       Dockerfile, compose e config legada do HAProxy
docs/         Documentação e matrizes de benchmark
resources/    Dataset oficial (não versionado)
```

## Detalhes de implementação

- **Parser** em três camadas: transaction-first → customer-first → fallback JSON
- **Vetor** 14D quantizado, alinhado ao formato oficial
- **kNN** exato com k=5; respostas JSON pré-computadas
- **Índice** carregado com `mmap` + `madvise`; busca com **AVX2** quando disponível
- **Body** lido com `ArrayPool` e `ReadExactlyAsync` (sem alocação por request)
- **Readiness**: warmup assíncrono; aguardar `GET /ready` antes de benchmark

## Git hooks

Este repositório rejeita trailers `Co-authored-by` nos commits. Ative o hook local uma vez:

```bash
git config core.hooksPath .githooks
```

## Licença

Projeto de participação na Rinha de Backend 2026.
