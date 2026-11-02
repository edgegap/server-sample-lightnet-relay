#!/usr/bin/env bash
# Tanks clients press Find Match on the Edgegap matchmaker, on one netcode or all four.
# The matchmaker starts a relay for the match, one client hosts it and the others join.
# Usage: ./e2e-matchmaker.sh MATCHMAKER_URL MATCHMAKER_TOKEN [netcode...]     netcode: Mirror FishNet PurrNet NGO (default: all)
# Runs on Windows (Git Bash). Expects tanks/Builds/Windows/Tanks.exe (Tanks > Build Windows client) and a matchmaker
# created from matchmaker-config.json (see README). Free tier: one deployment at a time, so netcodes run one after another.
#
# Default: two games, one match. Options (environment variables):
#   MM_WEBSOCKET=1  connect to the match relay over wss:// (the app version's relay-ws port) instead of TCP/UDP
#   BACKFILL=1      a third game searches once the match runs: it must join that match through the relay's backfill
#                   (needs MATCHMAKER_URL and MATCHMAKER_TOKEN on the app version); all three must see three tanks
#   MULTIROOM=1     fill the first room (ROOM_SIZE players, the relay's RELAY_MATCH_MAX_PLAYERS, default 4), then two
#                   more games search: the first must host a new room on the same relay, the second must join it
#   EXTRA_ARGS=...  more game arguments, e.g. "-beacons Toronto=20" (fixed pings) or "-noBeacons"
set -u
cd "$(dirname "$0")"
if [ $# -lt 2 ]; then echo "usage: $0 MATCHMAKER_URL MATCHMAKER_TOKEN [netcode...]"; exit 2; fi
URL=$1
TOKEN=$2
shift 2
NETCODES=${*:-Mirror FishNet PurrNet NGO}
ROOM_SIZE=${ROOM_SIZE:-4}
WS_ARG=""
[ "${MM_WEBSOCKET:-0}" = 1 ] && WS_ARG="-matchmakerWebSocket"
EXE=tanks/Builds/Windows/Tanks.exe
if [ ! -f "$EXE" ]; then echo "$EXE not found: open tanks/ in Unity and run Tanks > Build Windows client"; exit 1; fi
FAILED=""

# start NAME EXPECTED_TANKS [HOLD_SECONDS]: one game that searches at start; as host it keeps its room HOLD s after passing
start() {
  "$EXE" -batchmode -nographics -netcode "$NC" -matchmaker "$URL" -matchmakerToken "$TOKEN" -findMatch \
    -e2e -expectTanks "$2" -timeout 300 -holdAfterPass "${3:-3}" $WS_ARG ${EXTRA_ARGS:-} -logFile "$OUT/$1.log" &
  PIDS="$PIDS $!"
}
# wait_for PATTERN COUNT: until COUNT log lines match (up to 120 s)
wait_for() {
  for _ in $(seq 1 120); do [ "$(cat "$OUT"/*.log 2>/dev/null | grep -c -E "$1")" -ge "$2" ] && return 0; sleep 1; done
  echo "timed out waiting for $2 x '$1'"; return 1
}
room_of() { grep -h -o -E "Tanks matchmaker: (hosting|joining) match room [A-Z]+" "$OUT/$1.log" | awk '{print $NF}'; }

for NC in $NETCODES; do
  OUT=e2e-out/mm-$NC
  rm -rf "$OUT" && mkdir -p "$OUT"
  echo "== $NC"
  PIDS=""
  CHECK=0
  if [ "${MULTIROOM:-0}" = 1 ]; then
    # the first room stays up until the second room has played: the relay must not stop when one room ends
    start p1 "$ROOM_SIZE" 60; start p2 "$ROOM_SIZE" 60
    wait_for "Tanks matchmaker: hosting" 1
    for i in $(seq 3 "$ROOM_SIZE"); do sleep 3; start "p$i" "$ROOM_SIZE" 60; done
    wait_for "Tanks matchmaker: (hosting|joining)" "$ROOM_SIZE"
    sleep 3; start new1 2
    wait_for "Tanks matchmaker: (hosting|joining)" $((ROOM_SIZE + 1))
    sleep 3; start new2 2
  elif [ "${BACKFILL:-0}" = 1 ]; then
    start a 3; start b 3
    wait_for "Tanks matchmaker: hosting" 1
    sleep 5; start c 3
  else
    start a 2; start b 2
  fi
  RESULT=0
  for P in $PIDS; do wait "$P" || RESULT=1; done
  grep -H -E "Tanks matchmaker: (relay|hosting|joining)|E2E " "$OUT"/*.log | sed "s|$OUT/||"
  if [ "${MULTIROOM:-0}" = 1 ]; then
    R1=$(room_of p1); N1=$(room_of new1); N2=$(room_of new2)
    RELAYS=$(grep -h -o -E "relay for the match at [^,]+" "$OUT"/*.log | sort -u | wc -l)
    echo "first room $R1, new room $N1 / $N2, relays used: $RELAYS"
    grep -q "Tanks matchmaker: hosting" "$OUT/new1.log" && [ -n "$N1" ] && [ "$N1" != "$R1" ] && [ "$N1" = "$N2" ] && [ "$RELAYS" = 1 ] || CHECK=1
  fi
  echo "games exit=$RESULT checks=$CHECK"
  [ $RESULT -eq 0 ] && [ $CHECK -eq 0 ] || FAILED="$FAILED $NC"
  # the relay stops itself when its last room ends; give it a moment before the next match
  sleep 8
done

if [ -n "$FAILED" ]; then echo "FAILED:$FAILED"; exit 1; fi
echo "ALL PASSED: $NETCODES"
