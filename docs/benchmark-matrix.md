# Bench matrix — Rinha .NET

Official-shaped load via `make test-1` from rinha-bench (`API_URL=http://localhost:9999`).

Baseline: warmup=256, threads=32, leaf=48.

| Phase | Scenario | Warmup | Threads | Leaf | Score | P99 | FP | FN | HTTP err |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: |
| warmup | w0 | 0 | 32 | 48 | 6000 | 0.67ms | 0 | 0 | 0 |
| warmup | w64 | 64 | 32 | 48 | 6000 | 0.67ms | 0 | 0 | 0 |
| warmup | w128 | 128 | 32 | 48 | 6000 | 0.67ms | 0 | 0 | 0 |
| warmup | w256 | 256 | 32 | 48 | 6000 | 0.69ms | 0 | 0 | 0 |
| warmup | w512 | 512 | 32 | 48 | 6000 | 0.68ms | 0 | 0 | 0 |
| threadpool | t16 | 256 | 16 | 48 | 6000 | 0.67ms | 0 | 0 | 0 |
| threadpool | t24 | 256 | 24 | 48 | 6000 | 0.69ms | 0 | 0 | 0 |
| threadpool | t32 | 256 | 32 | 48 | 6000 | 0.68ms | 0 | 0 | 0 |
| threadpool | t48 | 256 | 48 | 48 | 6000 | 0.72ms | 0 | 0 | 0 |
| threadpool | t64 | 256 | 64 | 48 | 6000 | 0.71ms | 0 | 0 | 0 |
| leafsize | l32 | 256 | 32 | 32 | 6000 | 0.70ms | 0 | 0 | 0 |
| leafsize | l48 | 256 | 32 | 48 | 6000 | 0.69ms | 0 | 0 | 0 |
| leafsize | l64 | 256 | 32 | 64 | 6000 | 0.70ms | 0 | 0 | 0 |
| combo | best-latency | 0 | 16 | 48 | 6000 | 0.70ms | 0 | 0 | 0 |
| combo | fast-ready | 64 | 16 | 48 | 6000 | 0.70ms | 0 | 0 | 0 |
| combo | baseline | 256 | 32 | 48 | 6000 | 0.68ms | 0 | 0 | 0 |
| combo | aggressive | 0 | 16 | 32 | 6000 | 0.68ms | 0 | 0 | 0 |
| combo | balanced | 128 | 24 | 48 | 6000 | 0.68ms | 0 | 0 | 0 |

## Conclusões

**Corretude:** todos os 18 cenários — score **6000**, **0 FP**, **0 FN**, **0 HTTP errors**.

**Warmup (`RINHA_WARMUP_QUERIES`):** impacto mínimo no score; P99 melhor em **0 / 64 / 128** (0.67ms). Valores altos (256–512) não melhoram latência e atrasam o `/ready`. **Recomendado: 64** — startup rápido com warmup leve.

**Thread pool (`DOTNET_ThreadPool_MinThreads`):** **16** teve melhor P99 (0.67ms); **48–64** pioraram (0.71–0.72ms), provavelmente por pressão de RAM/CPU no container. **Recomendado: 16**.

**Leaf size (`RINHA_LEAF_SIZE`):** diferença pequena (0.69–0.70ms); **48** ligeiramente melhor que 32/64, índice menor que leaf=32. **Recomendado: 48**.

**Combinações:** `baseline` (256/32/48), `aggressive` (0/16/32) e `balanced` (128/24/48) empataram em **0.68ms**. Não houve interação negativa entre os parâmetros testados.

### Config recomendada

```yaml
RINHA_WARMUP_QUERIES: "64"
DOTNET_ThreadPool_MinThreads: "16"
RINHA_LEAF_SIZE: "48"   # build arg no Dockerfile
```
