#!/usr/bin/env bash
# Model-check the Kairos protocol with TLC (https://github.com/tlaplus/tlaplus).
# Usage: TLA2TOOLS=/path/to/tla2tools.jar spec/check.sh
# Needs Java 11+. The jar is not vendored; download a pinned release yourself and set TLA2TOOLS.
set -u
cd "$(dirname "$0")"
: "${TLA2TOOLS:?set TLA2TOOLS to the path of tla2tools.jar}"
tlc() { java -XX:+UseParallelGC -cp "$TLA2TOOLS" tlc2.TLC -workers auto -deadlock "$@"; }

# TLC_MODE=quick (default, every commit: seconds) or full (nightly: tens of minutes, ~12M+ states).
MODE="${TLC_MODE:-quick}"
if [ "$MODE" = "full" ]; then CFG=Kairos.cfg; MUT=KairosUnfenced.cfg; else CFG=KairosQuick.cfg; MUT=KairosUnfencedQuick.cfg; fi
echo "== mode: $MODE ($CFG, $MUT) =="

echo "== engine model: invariants I2 I3 I9, properties AppendOnly + Liveness (I1) =="
tlc -config "$CFG" Kairos.tla || { echo "FAIL: the engine model violates a property"; exit 1; }

echo "== mutation: fencing off, TLC must find a violation =="
if tlc -config "$MUT" Kairos.tla; then
  echo "FAIL: the unfenced model passed, so the invariants do not detect missing fencing"
  exit 1
fi
echo "OK: both checks behaved as expected"
