#!/bin/bash
# Registers this container as an Azure Pipelines agent, runs jobs, and unregisters on stop.
set -euo pipefail
: "${AZP_URL:?AZP_URL is required}"
: "${AZP_TOKEN:?AZP_TOKEN is required}"
AZP_POOL="${AZP_POOL:-Default}"; AZP_AGENT_NAME="${AZP_AGENT_NAME:-$(hostname)}"; AZP_WORK="${AZP_WORK:-_work}"

# Keep the token out of job environments.
TOKEN_FILE=/azp/.token; umask 077; printf '%s' "$AZP_TOKEN" > "$TOKEN_FILE"; unset AZP_TOKEN
export VSO_AGENT_IGNORE="AZP_TOKEN,AZP_TOKEN_FILE"

cleanup() {
  if [ -e ./config.sh ]; then
    for i in 1 2 3 4 5; do ./config.sh remove --unattended --auth PAT --token "$(cat "$TOKEN_FILE")" && break || sleep 5; done
  fi
}
trap 'cleanup; exit 0' EXIT INT TERM

if [ ! -e ./config.sh ]; then
  echo "Finding latest agent package for $TARGETARCH"
  PKG_URL=$(curl -fsS -u "user:$(cat "$TOKEN_FILE")" \
    "$AZP_URL/_apis/distributedtask/packages/agent?platform=$TARGETARCH&top=1" | jq -r '.value[0].downloadUrl')
  [ -n "$PKG_URL" ] && [ "$PKG_URL" != "null" ] || { echo "Could not find agent package (check AZP_URL and token scope)"; exit 1; }
  curl -fsSL "$PKG_URL" | tar -xz
fi

./config.sh --unattended --url "$AZP_URL" --auth PAT --token "$(cat "$TOKEN_FILE")" \
  --pool "$AZP_POOL" --agent "$AZP_AGENT_NAME" --work "$AZP_WORK" --replace --acceptTeeEula
./run.sh "$@" & wait $!
