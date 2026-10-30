#!/usr/bin/env bash
# Two Tanks clients (host + joiner) through the LightNet relay, on one netcode or all four.
# Usage: ./e2e.sh [relay-address] [port] [netcode...]     netcode: Mirror FishNet PurrNet NGO (default: all)
# Runs on Windows (Git Bash). Expects tanks/Builds/Windows/Tanks.exe (Tanks > Build Windows client) and a running relay (see README).
set -u
cd "$(dirname "$0")"
RELAY=${1:-127.0.0.1}
PORT=${2:-7777}
shift $(( $# < 2 ? $# : 2 ))
NETCODES=${*:-Mirror FishNet PurrNet NGO}
EXE=tanks/Builds/Windows/Tanks.exe
if [ ! -f "$EXE" ]; then echo "$EXE not found: open tanks/ in Unity and run Tanks > Build Windows client"; exit 1; fi
FAILED=""

for NC in $NETCODES; do
  OUT=e2e-out/$NC
  rm -rf "$OUT" && mkdir -p "$OUT"
  echo "== $NC"
  COMMON="-batchmode -nographics -netcode $NC -relay $RELAY -relayPort $PORT -e2e -expectTanks 2 -timeout 90"
  "$EXE" $COMMON -host -codeFile "$OUT/code.txt" -logFile "$OUT/host.log" &
  HOST=$!

  for _ in $(seq 1 30); do [ -s "$OUT/code.txt" ] && break; sleep 1; done
  if [ ! -s "$OUT/code.txt" ]; then
    echo "no room code after 30 s"; kill $HOST 2>/dev/null; tail -20 "$OUT/host.log"; FAILED="$FAILED $NC"; continue
  fi
  CODE=$(cat "$OUT/code.txt")
  echo "room code: $CODE"

  "$EXE" $COMMON -join "$CODE" -logFile "$OUT/client.log" &
  CLIENT=$!

  wait $CLIENT; C=$?
  wait $HOST; H=$?
  grep -h "E2E " "$OUT/host.log" "$OUT/client.log"
  echo "host exit=$H client exit=$C"
  [ $H -eq 0 ] && [ $C -eq 0 ] || FAILED="$FAILED $NC"
done

if [ -n "$FAILED" ]; then echo "FAILED:$FAILED"; exit 1; fi
echo "ALL PASSED: $NETCODES"
