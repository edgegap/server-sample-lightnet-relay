#!/usr/bin/env bash
# Two Tanks clients press Find Match on the Edgegap matchmaker, on one netcode or all four.
# The matchmaker starts a relay for the match, one client hosts it and the other joins.
# Usage: ./e2e-matchmaker.sh MATCHMAKER_URL MATCHMAKER_TOKEN [netcode...]     netcode: Mirror FishNet PurrNet NGO (default: all)
# Runs on Windows (Git Bash). Expects tanks/Builds/Windows/Tanks.exe (Tanks > Build Windows client) and a matchmaker
# created from matchmaker-config.json (see README). Free tier: one deployment at a time, so netcodes run one after another.
# MM_WEBSOCKET=1 connects both games to the match relay over wss:// (the app version's relay-ws port) instead of TCP/UDP.
# BACKFILL=1 adds a third game that presses Find Match once the match is running: it must join the running match
# through the relay's backfill (needs MATCHMAKER_URL and MATCHMAKER_TOKEN on the app version), and all three must see
# three tanks.
set -u
cd "$(dirname "$0")"
if [ $# -lt 2 ]; then echo "usage: $0 MATCHMAKER_URL MATCHMAKER_TOKEN [netcode...]"; exit 2; fi
URL=$1
TOKEN=$2
shift 2
NETCODES=${*:-Mirror FishNet PurrNet NGO}
EXE=tanks/Builds/Windows/Tanks.exe
if [ ! -f "$EXE" ]; then echo "$EXE not found: open tanks/ in Unity and run Tanks > Build Windows client"; exit 1; fi
FAILED=""

for NC in $NETCODES; do
  OUT=e2e-out/mm-$NC
  rm -rf "$OUT" && mkdir -p "$OUT"
  echo "== $NC"
  TANKS=2; C=""
  [ "${BACKFILL:-0}" = 1 ] && TANKS=3
  COMMON="-batchmode -nographics -netcode $NC -matchmaker $URL -matchmakerToken $TOKEN -findMatch -e2e -expectTanks $TANKS -timeout 240"
  [ "${MM_WEBSOCKET:-0}" = 1 ] && COMMON="$COMMON -matchmakerWebSocket"
  "$EXE" $COMMON -logFile "$OUT/a.log" &
  A=$!
  "$EXE" $COMMON -logFile "$OUT/b.log" &
  B=$!
  RC=0
  if [ "$TANKS" = 3 ]; then
    # wait until the first two are playing, then the late player searches
    for _ in $(seq 1 120); do grep -q "Tanks matchmaker: hosting" "$OUT/a.log" "$OUT/b.log" 2>/dev/null && break; sleep 1; done
    sleep 5
    "$EXE" $COMMON -logFile "$OUT/c.log" &
    C=$!
  fi
  wait $A; RA=$?
  wait $B; RB=$?
  [ "$TANKS" = 3 ] && { wait $C; RC=$?; }
  grep -h -E "Tanks matchmaker: (relay|hosting|joining)|E2E " "$OUT"/*.log
  echo "a exit=$RA b exit=$RB${C:+ c exit=$RC}"
  [ $RA -eq 0 ] && [ $RB -eq 0 ] && [ $RC -eq 0 ] || FAILED="$FAILED $NC"
  # the relay stops itself when the host leaves; give it a moment before the next match
  sleep 5
done

if [ -n "$FAILED" ]; then echo "FAILED:$FAILED"; exit 1; fi
echo "ALL PASSED: $NETCODES"
