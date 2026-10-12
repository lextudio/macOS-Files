#!/usr/bin/env bash
# Drives the Uno (Skia desktop, macOS) app through its in-process DevFlow agent.
#
# The app embeds the LeXtudio.DevFlow.Agent.Uno HTTP server (default port 9223) which exposes
# a live UI tree, screenshots, synthetic tap/fill/key/scroll, and [DevFlowAction] methods.
# This wraps the HTTP surface so tests can script it without hand-rolling leases.
#
# Usage: devflow.sh <command> [args]
#   run [project] [tfm]      Build + launch the app, wait until the agent answers. Steals focus.
#   stop                    Kill the running app.
#   status                  GET /api/v1/agent/status
#   tree [outfile]          GET /api/v1/ui/tree        (default: $STATE/tree.json)
#   screenshot [outfile]    GET /api/v1/ui/screenshot  (default: $STATE/screenshot.png)
#   actions                 GET /api/v1/invoke/actions
#   invoke <name> [json]    POST an action; [json] is the args array, e.g. '["x"]'
#   tap <elementId>         POST /api/v1/ui/actions/tap
#   fill <elementId> <text> POST /api/v1/ui/actions/fill
#   key <elementId> <key>   POST /api/v1/ui/actions/key
#   scroll <elementId> <dy> POST /api/v1/ui/actions/scroll
#   raw <METHOD> <path> [json]
#
# Env: DEVFLOW_AGENT_PORT (default 9223), UNO_PROJECT (default spikes/UnoHostMac/UnoHostMac.csproj),
#      UNO_TFM (default net10.0-desktop), DEVFLOW_STATE (default ${TMPDIR:-/tmp}/files-uno-devflow).
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
port="${DEVFLOW_AGENT_PORT:-9223}"
base="http://localhost:${port}"
state="${DEVFLOW_STATE:-${TMPDIR:-/tmp}/files-uno-devflow}"
project="${UNO_PROJECT:-$repo/spikes/UnoHostMac/UnoHostMac.csproj}"
tfm="${UNO_TFM:-net10.0-desktop}"

mkdir -p "$state"
pidfile="$state/app.pid"
leasefile="$state/lease.id"
logfile="$state/app.log"

# Reuse one lease id per agent session; the agent rejects a different id while another holds it.
lease_id() {
	[[ -f "$leasefile" ]] || (uuidgen | tr 'A-Z' 'a-z' | tr -d '-') >"$leasefile"
	cat "$leasefile"
}

# Mutating endpoints require a claimed lease (AgentOptions.RequireMutationLease defaults true).
claim() {
	curl -s -m 5 -X POST "$base/api/v1/agent/lease" \
		-H 'Content-Type: application/json' \
		-d "{\"action\":\"claim\",\"leaseId\":\"$(lease_id)\",\"holderKind\":\"cli\",\"label\":\"devflow.sh\",\"force\":true}" >/dev/null
}

mutate() {
	claim
	curl -s -m 15 -X POST "$base$1" -H 'Content-Type: application/json' -H "X-DevFlow-Lease: $(lease_id)" -d "${2:-}"
}

ready() { curl -s -m 2 -o /dev/null "$base/api/v1/agent/status"; }

cmd_run() {
	local proj="${1:-$project}" vtf="${2:-$tfm}"
	echo "[devflow] building $proj ($vtf)"
	MSBUILDDISABLENODEREUSE=1 dotnet build "$proj" -c Debug -nodeReuse:false -m:2 >"$logfile" 2>&1
	local cfg
	cfg="$(/bin/ls "$(dirname "$proj")/bin/Debug/$vtf/"*.runtimeconfig.json 2>/dev/null | head -1 || true)"
	[[ -n "$cfg" ]] || { echo "[devflow] build output not found under $(dirname "$proj")/bin/Debug/$vtf" >&2; tail -20 "$logfile" >&2; exit 1; }
	local dll="${cfg%.runtimeconfig.json}.dll"
	echo "$dll" >"$state/app.dll"
	rm -f "$leasefile"
	echo "[devflow] launching (this opens a macOS window and steals focus)"
	DEVFLOW_AGENT_PORT="$port" nohup dotnet "$dll" >>"$logfile" 2>&1 &
	echo $! >"$pidfile"
	for _ in $(seq 1 40); do
		if ready; then echo "[devflow] ready on $base (pid $(cat "$pidfile"))"; return 0; fi
		sleep 0.5
	done
	echo "[devflow] agent did not come up; see $logfile" >&2
	tail -20 "$logfile" >&2
	exit 1
}

cmd_stop() {
	if [[ -f "$pidfile" ]]; then
		kill "$(cat "$pidfile")" 2>/dev/null || true
		rm -f "$pidfile"
	fi
	if [[ -f "$state/app.dll" ]]; then
		pkill -f "$(basename "$(cat "$state/app.dll")")" 2>/dev/null || true
	else
		pkill -f "$(basename "${project%.csproj}").dll" 2>/dev/null || true
	fi
	echo "[devflow] stopped"
}

case "${1:-}" in
run) shift; cmd_run "$@" ;;
stop) cmd_stop ;;
status) curl -s -m 5 "$base/api/v1/agent/status" ;;
tree) curl -s -m 15 "$base/api/v1/ui/tree" -o "${2:-$state/tree.json}"; echo "${2:-$state/tree.json}" ;;
screenshot) curl -s -m 15 "$base/api/v1/ui/screenshot" -o "${2:-$state/screenshot.png}"; echo "${2:-$state/screenshot.png}" ;;
actions) curl -s -m 5 "$base/api/v1/invoke/actions" ;;
invoke) mutate "/api/v1/invoke/actions/$2" "{\"args\":${3:-[]}}" ;;
tap) mutate "/api/v1/ui/actions/tap" "{\"id\":\"$2\"}" ;;
fill) mutate "/api/v1/ui/actions/fill" "{\"elementId\":\"$2\",\"text\":\"$3\"}" ;;
key) mutate "/api/v1/ui/actions/key" "{\"elementId\":\"$2\",\"key\":\"$3\"}" ;;
scroll) mutate "/api/v1/ui/actions/scroll" "{\"elementId\":\"$2\",\"deltaY\":${3:-0}}" ;;
raw) shift; curl -s -m 15 -X "$1" "$base$2" -H 'Content-Type: application/json' ${3:+-d "$3"} ;;
*)
	sed -n '2,25p' "${BASH_SOURCE[0]}"
	exit 2
	;;
esac
