#!/usr/bin/env bash
# Two Tanks clients (host + joiner) through the LightNet relay running in Docker.
# Usage: ./e2e.sh [relay-address] [tcp-port] [udp-port]
# Expects: tanks/Builds/Windows/Tanks.exe and the image lightnet-relay:<tag> (see README).
set -u
cd "$(dirname "$0")"
RELAY=${1:-127.0.0.1}
TCP=${2:-7777}
UDP=${3:-$TCP}
EXE=tanks/Builds/Windows/Tanks.exe
OUT=e2e-out
rm -rf "$OUT" && mkdir -p "$OUT"

COMMON="-batchmode -nographics -relay $RELAY -relayPort $TCP -relayUdpPort $UDP -e2e -expectTanks 2 -timeout 60"
"$EXE" $COMMON -host -codeFile "$OUT/code.txt" -logFile "$OUT/host.log" &
HOST=$!

for _ in $(seq 1 30); do [ -s "$OUT/code.txt" ] && break; sleep 1; done
if [ ! -s "$OUT/code.txt" ]; then
  echo "no room code after 30 s"; kill $HOST 2>/dev/null; tail -20 "$OUT/host.log"; exit 1
fi
CODE=$(cat "$OUT/code.txt")
echo "room code: $CODE"

"$EXE" $COMMON -join "$CODE" -logFile "$OUT/client.log" &
CLIENT=$!

wait $CLIENT; C=$?
wait $HOST; H=$?
grep -h "E2E " "$OUT/host.log" "$OUT/client.log"
echo "host exit=$H client exit=$C"
[ $H -eq 0 ] && [ $C -eq 0 ]
