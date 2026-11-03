#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
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

WGVPN_ETC="${WGVPN_ETC:-/etc/wgvpn}"
WGVPN_WG_CONF="${WGVPN_WG_CONF:-/etc/wireguard/wg0.conf}"
WGVPN_UNIT="${WGVPN_UNIT:-/etc/systemd/system/wstunnel.service}"
WG_UDP_PORT=51820
WSS_PORT=443

# --host (DDNS) wins over --ip.
ENDPOINT="${HOST:-$IP}"

# TODO Task 6: install_packages; install_wstunnel (slot in here, before keygen
# since wg_genkeypair/openssl need the packages).

mkdir -p "$WGVPN_ETC"
chmod 700 "$WGVPN_ETC"

# Idempotency: each keypair is generated only if ITS files are missing/empty.
# Regenerating would invalidate an already-distributed client bundle, and the
# server and client pairs are decoupled so losing one never rotates the other.
# The restrictive umask is scoped to subshells that write secrets.
if [ ! -s "$WGVPN_ETC/server.key" ] || [ ! -s "$WGVPN_ETC/server.pub" ]; then
  (
    umask 077
    { read -r k; read -r p; } < <(wg_genkeypair)
    printf '%s\n' "$k" > "$WGVPN_ETC/server.key"
    printf '%s\n' "$p" > "$WGVPN_ETC/server.pub"
  )
fi
if [ ! -s "$WGVPN_ETC/client.key" ] || [ ! -s "$WGVPN_ETC/client.pub" ]; then
  (
    umask 077
    { read -r k; read -r p; } < <(wg_genkeypair)
    printf '%s\n' "$k" > "$WGVPN_ETC/client.key"
    printf '%s\n' "$p" > "$WGVPN_ETC/client.pub"
  )
fi

if [ ! -s "$WGVPN_ETC/cert.pem" ] || [ ! -s "$WGVPN_ETC/key.pem" ] \
   || [ ! -s "$WGVPN_ETC/tls.sha256" ]; then
  (umask 077; tls_gen_selfsigned "$WGVPN_ETC" > "$WGVPN_ETC/tls.sha256")
fi
chmod 600 "$WGVPN_ETC"/*.key "$WGVPN_ETC"/*.pem

server_priv="$(cat "$WGVPN_ETC/server.key")"
server_pub="$(cat "$WGVPN_ETC/server.pub")"
client_priv="$(cat "$WGVPN_ETC/client.key")"
client_pub="$(cat "$WGVPN_ETC/client.pub")"
tls_sha="$(cat "$WGVPN_ETC/tls.sha256")"

mkdir -p "$(dirname "$WGVPN_WG_CONF")" "$(dirname "$WGVPN_UNIT")"
(umask 077; render_wg_server_conf "$server_priv" "$client_pub" > "$WGVPN_WG_CONF")
chmod 600 "$WGVPN_WG_CONF"
render_wstunnel_unit "$WG_UDP_PORT" > "$WGVPN_UNIT"
chmod 644 "$WGVPN_UNIT"

(umask 077; render_bundle "$ENDPOINT" "$WSS_PORT" "$tls_sha" "$client_priv" "$server_pub" > "$OUT.tmp")
chmod 600 "$OUT.tmp"
mv "$OUT.tmp" "$OUT"
unset server_priv client_priv

enable_forwarding
egress_iface="$(detect_egress_iface)"
apply_nat "$egress_iface"

# TODO Task 6: enable_services (wg-quick@wg0, wstunnel).

echo "Setup complete."
echo "  Endpoint host: $ENDPOINT"
echo "  Client bundle: $OUT (mode 600; contains secrets, transfer securely)"
