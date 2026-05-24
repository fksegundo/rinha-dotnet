# Rinha .NET — Native AOT

[![CI](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ci.yml)
[![Publish GHCR](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ghcr.yml/badge.svg)](https://github.com/fksegundo/rinha-dotnet/actions/workflows/ghcr.yml)
[![.NET 11](https://img.shields.io/badge/.NET-11-purple)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native-AOT-blue)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![Docker](https://img.shields.io/badge/Docker-GHCR-2496ED?logo=docker&logoColor=white)](https://github.com/fksegundo/rinha-dotnet/pkgs/container/rinha-dotnet)

Solução em **.NET 11 Native AOT** para a [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026): detecção de fraude por kNN exato (k=5) sobre um índice espacial quantizado, servida atrás de um **load balancer Rust** com FD passing via Unix domain sockets.

> **English:** see [README.md](../README.md) at the repository root.

## Visão geral

A API recebe transações em `POST /fraud-score`, extrai um vetor de 14 dimensões, consulta um índice **RNSPCST1** pré-computado (3M referências) e responde com score e aprovação. O binário é compilado com **Native AOT** para `linux-x64`, sem runtime gerenciado em produção.

```
Cliente → Rust LB :9999 → api1 / api2 (SCM_RIGHTS FD passing)
                              ↓
                         índice mmap + busca AVX2
```

| Componente | Papel |
| --- | --- |
| `Rinha.Api` | API HTTP (`/ready`, `/fraud-score`) |
| `Rinha.Preprocess` | Gera o índice a partir de `references.json.gz` |
| `Rinha.Verify` | CLI para validar respostas contra dataset de teste |
| Rust LB | Load balancer na porta **9999** (`ghcr.io/fksegundo/rinha-api-lb`) |
| 2× API | Réplicas com limites de CPU/RAM do desafio |

## Benchmarks e tuning

Configuração de produção escolhida a partir das matrizes de benchmark (score **6000**, **0 FP/FN**):

| Documento | Escopo |
| --- | --- |
| [benchmark-matrix.md](benchmark-matrix.md) | Tuning da API — warmup, thread pool, leaf size |
| [proxy-benchmark-matrix.md](proxy-benchmark-matrix.md) | Histórico de tuning — HAProxy/nginx |

Config recomendada: `RINHA_WARMUP_QUERIES=64`, `DOTNET_ThreadPool_MinThreads=16`, `RINHA_LEAF_SIZE=48`, Rust LB com FD passing.

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
| `RINHA_SEARCH_MODE` | `key-first` | Estratégia de busca no índice |
| `RINHA_MAX_BODY_BYTES` | `8192` | Tamanho máximo do body |
| `DOTNET_gcServer` | `0` | Workstation GC (melhor em containers pequenos) |
| `DOTNET_ThreadPool_MinThreads` | `16` | Threads mínimas do thread pool |
| `MALLOC_ARENA_MAX` | `2` | Reduz fragmentação de memória nativa |

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
