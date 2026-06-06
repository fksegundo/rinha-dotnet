#!/usr/bin/env bash
# Sweep RINHA_LEAF_SIZE values, rebuild index, recreate compose stack, run benchmark, record metrics.
set -euo pipefail

DOTNET_DIR="/home/filon/Documentos/Codigos/rinha-dotnet"
BENCH_DIR="/home/filon/Documentos/Codigos/rinha-benchmark"
RESULTS="${DOTNET_DIR}/results/leaf_size_sweep_$(date +%Y%m%d_%H%M%S).csv"

mkdir -p "$(dirname "$RESULTS")"

if [ $# -eq 0 ]; then
  LEAF_VALUES=(8 16 24 32 40 48 56 64 80 96 128)
else
  LEAF_VALUES=("$@")
fi

echo "leaf_size,p50,p95,p99,fp,fn,score,idx_size_bytes" | tee "$RESULTS"

sweep_one() {
  local LEAF=$1
  echo "" >&2
  echo ">>> RUNNING SWEEP CELL: LEAF_SIZE=$LEAF" >&2

  # 1. Rebuild index locally with different leaf size
  echo "Building index with leaf_size=$LEAF..." >&2
  RINHA_LEAF_SIZE="$LEAF" make -C "$DOTNET_DIR" preprocess >/dev/null

  local IDX_SIZE
  IDX_SIZE=$(stat -c%s "${DOTNET_DIR}/test-data/rinha-specialist.idx")

  # 2. Recreate docker stack with the new index mounted
  echo "Recreating Docker Compose stack..." >&2
  make -C "$DOTNET_DIR" down >/dev/null
  make -C "$DOTNET_DIR" up >/dev/null

  # 3. Run the benchmark
  echo "Running benchmark test-1..." >&2
  local OUT
  OUT=$(make -C "$BENCH_DIR" test-1 K6_WEB_DASHBOARD=0 K6_WEB_DASHBOARD_OPEN=0 BASE_DURATION_SECONDS=30 2>&1 | tail -50)

  local P50 P95 P99 FP FN SCORE
  P50=$(echo "$OUT" | grep '"p50"' | head -1 | grep -oP '[0-9.]+ms' | head -1 || echo "?")
  P95=$(echo "$OUT" | grep '"p95"' | head -1 | grep -oP '[0-9.]+ms' | head -1 || echo "?")
  P99=$(echo "$OUT" | grep '"p99".*ms' | head -1 | grep -oP '[0-9.]+ms' | head -1 || echo "?")
  FP=$(echo "$OUT" | grep 'false_positive_detections' | grep -oP '[0-9]+' | head -1 || echo "?")
  FN=$(echo "$OUT" | grep 'false_negative_detections' | grep -oP '[0-9]+' | head -1 || echo "?")
  SCORE=$(echo "$OUT" | grep '"final_score"' | grep -oP '[0-9]+' | head -1 || echo "?")

  local LINE="$LEAF,$P50,$P95,$P99,$FP,$FN,$SCORE,$IDX_SIZE"
  echo "$LINE" | tee -a "$RESULTS"
}

for LEAF in "${LEAF_VALUES[@]}"; do
  sweep_one "$LEAF"
done

echo "" >&2
echo "=== SWEEP COMPLETE ===" >&2
echo "Results written to: $RESULTS" >&2
echo "" >&2
cat "$RESULTS"
