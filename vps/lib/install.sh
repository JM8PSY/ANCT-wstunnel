#!/usr/bin/env bash
# shellcheck shell=bash

_INSTALL_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

install_packages() {
  export DEBIAN_FRONTEND=noninteractive
  # Pre-seed so iptables-persistent never prompts (we save explicitly later).
  printf '%s\n' \
    'iptables-persistent iptables-persistent/autosave_v4 boolean false' \
    'iptables-persistent iptables-persistent/autosave_v6 boolean false' \
    | debconf-set-selections
  apt-get update
  apt-get install -y wireguard-tools iptables iptables-persistent jq openssl curl ca-certificates tar
}

# Download <url> to <dest>. Separate function so tests can replace it.
_wstunnel_download() {
  curl -fsSL --retry 3 -o "$2" "$1"
}

_sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

# Download the pinned wstunnel release, verify sha256 (hard gate), install.
install_wstunnel() {
  local versions="${WGVPN_VERSIONS:-$_INSTALL_LIB_DIR/../VERSIONS}"
  local bin="${WGVPN_WSTUNNEL_BIN:-/usr/local/bin/wstunnel}"
  local WSTUNNEL_VERSION WSTUNNEL_SHA256_AMD64 WSTUNNEL_SHA256_ARM64
  # shellcheck disable=SC1091,SC1090
  . "$versions"

  local arch expected
  case "$(uname -m)" in
    x86_64|amd64)  arch=amd64; expected="$WSTUNNEL_SHA256_AMD64" ;;
    aarch64|arm64) arch=arm64; expected="$WSTUNNEL_SHA256_ARM64" ;;
    *) echo "install_wstunnel: unsupported architecture $(uname -m)" >&2; return 1 ;;
  esac

  local ver="${WSTUNNEL_VERSION#v}"
  local url="https://github.com/erebe/wstunnel/releases/download/${WSTUNNEL_VERSION}/wstunnel_${ver}_linux_${arch}.tar.gz"

  local tmp
  tmp="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf '$tmp'" RETURN

  _wstunnel_download "$url" "$tmp/wstunnel.tar.gz" || return 1

  local actual
  actual="$(_sha256_of "$tmp/wstunnel.tar.gz")"
  if [ -z "$expected" ] || [ "$actual" != "$expected" ]; then
    echo "install_wstunnel: sha256 mismatch (expected $expected, got $actual); refusing to install" >&2
    return 1
  fi

  mkdir "$tmp/x"
  tar -xzf "$tmp/wstunnel.tar.gz" -C "$tmp/x" wstunnel || return 1
  mkdir -p "$(dirname "$bin")"
  install -m 755 "$tmp/x/wstunnel" "$bin"
}

# Idempotent: UDP/51820 is reachable only from loopback (i.e. via wstunnel).
_restrict_wg_port() {
  local port="${WG_UDP_PORT:-51820}"
  iptables -C INPUT ! -i lo -p udp --dport "$port" -j DROP 2>/dev/null \
    || iptables -I INPUT 1 ! -i lo -p udp --dport "$port" -j DROP
  # WireGuard also binds [::]:port; close the IPv6 side too when available.
  # Skipped (non-fatal) when ip6tables is unusable, e.g. no IPv6 netfilter in
  # the kernel: WireGuard cannot serve IPv6 there, so no protection is lost.
  if command -v ip6tables >/dev/null 2>&1 && ip6tables -L >/dev/null 2>&1; then
    if ! ip6tables -C INPUT ! -i lo -p udp --dport "$port" -j DROP 2>/dev/null; then
      ip6tables -I INPUT 1 ! -i lo -p udp --dport "$port" -j DROP \
        || echo "warning: could not add ip6tables rule" >&2
    fi
  fi
  netfilter-persistent save
}

enable_services() {
  local wg_if
  wg_if="$(basename "${WGVPN_WG_CONF:-/etc/wireguard/wg0.conf}" .conf)"
  _restrict_wg_port
  systemctl daemon-reload
  systemctl enable --now "wg-quick@${wg_if}"
  systemctl enable --now wstunnel
}
