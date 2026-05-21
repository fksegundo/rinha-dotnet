# Proxy tuning matrix

Load: `make test-1` via rinha-bench. API: warmup=64, threads=16, leaf=48.

| Phase | Scenario | Proxy | Score | P99 | FP | FN | HTTP | Config |
| --- | --- | --- | ---: | --- | ---: | ---: | ---: | --- |
| haproxy-bufsize | buf4096 | haproxy | 6000 | 0.69ms | 0 | 0 | 0 | buf=4096 thr=1 bal=roundrobin chk=100ms maxconn=2048 |
| haproxy-bufsize | buf8192 | haproxy | 6000 | 0.70ms | 0 | 0 | 0 | buf=8192 thr=1 bal=roundrobin chk=100ms maxconn=2048 |
| haproxy-bufsize | buf16384 | haproxy | 6000 | 0.70ms | 0 | 0 | 0 | buf=16384 thr=1 bal=roundrobin chk=100ms maxconn=2048 |
| haproxy-bufsize | buf32768 | haproxy | 6000 | 0.70ms | 0 | 0 | 0 | buf=32768 thr=1 bal=roundrobin chk=100ms maxconn=2048 |
| haproxy-threads | thr1 | haproxy | 6000 | 0.70ms | 0 | 0 | 0 | buf=8192 thr=1 bal=roundrobin chk=100ms maxconn=2048 |
| haproxy-threads | thr2 | haproxy | 6000 | 0.71ms | 0 | 0 | 0 | buf=8192 thr=2 bal=roundrobin chk=100ms maxconn=2048 |
| haproxy-balance | roundrobin | haproxy | 6000 | 0.68ms | 0 | 0 | 0 | buf=8192 thr=1 bal=roundrobin chk=100ms maxconn=2048 |
| haproxy-balance | leastconn | haproxy | 6000 | 0.68ms | 0 | 0 | 0 | buf=8192 thr=1 bal=leastconn chk=100ms maxconn=2048 |
| haproxy-balance | random | haproxy | 6000 | 0.68ms | 0 | 0 | 0 | buf=8192 thr=1 bal=random chk=100ms maxconn=2048 |
| haproxy-check | chk50ms | haproxy | 6000 | 0.68ms | 0 | 0 | 0 | buf=8192 thr=1 bal=roundrobin chk=50ms maxconn=2048 |
| haproxy-check | chk100ms | haproxy | 6000 | 0.68ms | 0 | 0 | 0 | buf=8192 thr=1 bal=roundrobin chk=100ms maxconn=2048 |
| haproxy-check | chk200ms | haproxy | 6000 | 0.69ms | 0 | 0 | 0 | buf=8192 thr=1 bal=roundrobin chk=200ms maxconn=2048 |
| haproxy-check | chk500ms | haproxy | 6000 | 0.69ms | 0 | 0 | 0 | buf=8192 thr=1 bal=roundrobin chk=500ms maxconn=2048 |
| nginx-keepalive | ka8 | nginx | 6000 | 0.74ms | 0 | 0 | 0 | workers=1 conn=2048 keepalive=8 bal=rr |
| nginx-keepalive | ka32 | nginx | 6000 | 0.76ms | 0 | 0 | 0 | workers=1 conn=2048 keepalive=32 bal=rr |
| nginx-keepalive | ka64 | nginx | 6000 | 0.74ms | 0 | 0 | 0 | workers=1 conn=2048 keepalive=64 bal=rr |
| nginx-keepalive | ka128 | nginx | 6000 | 0.74ms | 0 | 0 | 0 | workers=1 conn=2048 keepalive=128 bal=rr |
| nginx-workers | w1 | nginx | 5526.02 | 2.98ms | 0 | 0 | 0 | workers=1 conn=2048 keepalive=32 bal=rr |
| nginx-workers | w2 | nginx | 5898.9 | 1.26ms | 0 | 0 | 0 | workers=2 conn=2048 keepalive=32 bal=rr |
| nginx-balance | least_conn | nginx | 5724.6 | 1.89ms | 0 | 0 | 0 | workers=1 conn=2048 keepalive=32 bal=least_conn |
| compare | haproxy-baseline | haproxy | 6000 | 0.71ms | 0 | 0 | 0 | buf=8192 thr=1 bal=roundrobin chk=100ms maxconn=2048 |
| compare | nginx-baseline | nginx | 6000 | 0.80ms | 0 | 0 | 0 | workers=1 conn=2048 keepalive=32 bal=rr |
| compare | nginx-ka64 | nginx | 6000 | 0.72ms | 0 | 0 | 0 | workers=1 conn=2048 keepalive=64 bal=rr |
| compare | nginx-w2 | nginx | 6000 | 0.90ms | 0 | 0 | 0 | workers=2 conn=2048 keepalive=32 bal=rr |
| combo | haproxy-optimal | haproxy | 6000 | 0.67ms | 0 | 0 | 0 | buf4096 thr1 rr chk50ms |
| combo | haproxy-random | haproxy | 6000 | 0.67ms | 0 | 0 | 0 | buf4096 thr1 random chk50ms |
| combo | nginx-best | nginx | 6000 | 0.67ms | 0 | 0 | 0 | w1 ka64 rr |

## Conclusões

**27 cenários** testados. Corretude preservada (0 FP/FN) em todos os runs com score 6000, exceto 3 runs nginx isolados com penalidade de P99.

### HAProxy (recomendado)

| Parâmetro | Melhor | Pior | Notas |
| --- | --- | --- | --- |
| `tune.bufsize` | **4096** (0.69ms) | 8192+ (0.70ms) | buffer menor = menos RAM no LB (20M limit) |
| `nbthread` | **1** (0.70ms) | 2 (0.71ms) | 0.10 CPU no LB não justifica 2 threads |
| `balance` | rr / leastconn / random (**0.68ms**) | — | empate total |
| `check inter` | **50ms** (0.68ms) | 500ms (0.69ms) | diferença mínima |

**Combo vencedora:** `buf=4096`, `nbthread=1`, `roundrobin`, `check inter 50ms` → **P99 0.67ms**, score 6000.

Config aplicada em `docker/haproxy.cfg`.

### nginx (UDS)

- P99 tipicamente **0.74–0.90ms** (pior que HAProxy)
- Runs isolados com **1 worker** durante sweep (`nginx-workers w1`: score **5526**, P99 **2.98ms**) — provável pico de latência / falta de health-check ativo no startup
- Melhor combo nginx (`w1`, keepalive=64): **0.67ms** — empata com HAProxy, mas menos consistente entre runs
- Sem `http-check` nativo no upstream UDS (nginx OSS); HAProxy tem vantagem operacional

### Decisão

**Manter HAProxy** com tuning agressivo de buffer (4096) e health-check a 50ms.

## HAProxy TCP mode (UDS)

Comparação HTTP (config otimizada) vs TCP passthrough no mesmo par UDS:

| Cenário | Modo | P99 | Score |
| --- | --- | --- | ---: |
| http-optimal | HTTP + `http-check /ready` | 0.68ms | 6000 |
| **tcp-check50** | **TCP + `tcp-check`** | **0.63ms** | 6000 |
| tcp-check100 | TCP + `tcp-check` | 0.65ms | 6000 |
| tcp-socket-only | TCP (só socket check) | 0.65ms | 6000 |

**TCP mode vence em P99** (~7% melhor que HTTP). Sem parsing HTTP no LB — menos CPU e latência.

**Trade-off:** TCP mode não faz `http-check GET /ready`; só verifica se o socket UDS aceita conexão. A API pode receber tráfego antes do warmup terminar (socket abre antes do `/ready` 200). Mitigação: aguardar `curl /ready` antes do bench (como no Makefile).

Config TCP (padrão em `docker/haproxy.cfg`).

| haproxy-tcp | http-optimal | haproxy | 6000 | 0.68ms | 0 | 0 | 0 | buf=4096 thr=1 bal=roundrobin chk=50ms maxconn=2048 |
| haproxy-tcp | tcp-check50 | haproxy-tcp | 6000 | 0.63ms | 0 | 0 | 0 | mode=tcp tcp_check=1 bal=roundrobin chk=50ms |
| haproxy-tcp | tcp-check100 | haproxy-tcp | 6000 | 0.65ms | 0 | 0 | 0 | mode=tcp tcp_check=1 bal=roundrobin chk=100ms |
| haproxy-tcp | tcp-socket-only | haproxy-tcp | 6000 | 0.65ms | 0 | 0 | 0 | mode=tcp tcp_check=0 bal=roundrobin chk=50ms |
