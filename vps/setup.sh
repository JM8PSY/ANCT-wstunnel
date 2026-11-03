#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  cat >&2 <<USAGE
Usage: setup.sh (--host <ddns> | --ip <addr>) [--out <path>]
  --host <ddns>  public DDNS hostname of this VPS
  --ip <addr>    public IP address of this VPS
  --out <path>   bundle output path (default: ./wgvpn-bundle.json)
USAGE
  exit 1
}

HOST=""
IP=""
OUT="./wgvpn-bundle.json"

while [ $# -gt 0 ]; do
  case "$1" in
    --host) [ $# -ge 2 ] || usage; HOST="$2"; shift 2 ;;
    --ip)   [ $# -ge 2 ] || usage; IP="$2"; shift 2 ;;
    --out)  [ $# -ge 2 ] || usage; OUT="$2"; shift 2 ;;
    *) usage ;;
  esac
done

if [ -z "$HOST" ] && [ -z "$IP" ]; then
  usage
fi

# shellcheck source=lib/keys.sh
. "$SCRIPT_DIR/lib/keys.sh"
# shellcheck source=lib/render.sh
. "$SCRIPT_DIR/lib/render.sh"
# shellcheck source=lib/net.sh
. "$SCRIPT_DIR/lib/net.sh"
# shellcheck source=lib/install.sh
. "$SCRIPT_DIR/lib/install.sh"

# TODO Task 7: orchestrate install, key generation, rendering, bundle output.
: "$HOST" "$IP" "$OUT"
