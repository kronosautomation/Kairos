#!/bin/bash
# Runs the same stages as Azure DevOps against a copy of the repo. Usage: ci-run.sh <Fast|Integration|Simulation|Mutation|Supply|All> [tfm]
set -euo pipefail
STAGE="${1:-Fast}"; TFM="${2:-net10.0}"
STAMP="$(date +%Y%m%d-%H%M%S)"; OUT="/results/$STAMP-$STAGE-$TFM"; mkdir -p "$OUT"
LOCKED=true; [ "$TFM" != "net10.0" ] && LOCKED=false   # lock files are generated for net10.0

echo "== Copying repo (bin/obj excluded) =="
rsync -a --delete --exclude bin --exclude obj --exclude .ci-local --exclude .git /src/ /work/
cd /work
[ "$TFM" != "net10.0" ] && rm -f global.json   # the newer-SDK check is the one job that does not use the pinned SDK, as in azure-pipelines.yml

restore() { dotnet restore Kairos.sln -p:KairosTargetFramework="$TFM" -p:RestoreLockedMode=$LOCKED; }
build()   { dotnet build Kairos.sln -c Release --no-restore -p:KairosTargetFramework="$TFM"; }
run_tests() {
  local args=(test Kairos.sln -c Release --no-build -p:KairosTargetFramework="$TFM" --filter "$1" --logger trx --results-directory "$OUT")
  [ "${2:-}" = "coverage" ] && args+=(--collect "XPlat Code Coverage")
  dotnet "${args[@]}"
}

case "$STAGE" in
  Fast)
    restore; build
    run_tests "Category!=Nightly&Category!=Soak&Category!=CloudSandbox" coverage ;;
  Integration)
    restore; build
    run_tests "Category=Integration|Category=CrashMatrix|Category=FaultInjection" ;;
  Simulation)
    restore; build
    run_tests "Category=Simulation|Category=Nightly" ;;
  Mutation)
    restore; build
    dotnet tool install --global dotnet-stryker >/dev/null 2>&1 || true
    export PATH="$PATH:/root/.dotnet/tools"
    (cd tests/Kairos.Core.Tests && dotnet stryker --project Kairos.Core.csproj --reporter html --reporter progress --output "$OUT/stryker") ;;
  Supply)
    dotnet restore Kairos.sln -p:RestoreLockedMode=true
    dotnet list Kairos.sln package --vulnerable --include-transitive | tee "$OUT/vulnerable.txt"
    if grep -qE "\b(High|Critical)\b" "$OUT/vulnerable.txt"; then echo "High/critical vulnerable package"; exit 1; fi ;;
  All)
    "$0" Supply "$TFM"; "$0" Fast "$TFM"; "$0" Integration "$TFM" ;;
  *) echo "Unknown stage: $STAGE"; exit 2 ;;
esac
echo "== Results: .ci-local/$STAMP-$STAGE-$TFM =="
