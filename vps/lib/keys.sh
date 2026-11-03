#!/usr/bin/env bash
# shellcheck shell=bash

# Prints private key (line 1) and public key (line 2).
wg_genkeypair() {
  local priv
  priv="$(wg genkey)"
  printf '%s\n' "$priv"
  printf '%s\n' "$priv" | wg pubkey
}

# Writes cert.pem/key.pem (mode 600) into <dir>; prints sha256 fingerprint
# as lowercase hex without colons.
tls_gen_selfsigned() {
  local dir="$1" fp
  (
    umask 077
    mkdir -p "$dir"
    openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj "/CN=wgvpn" \
      -keyout "$dir/key.pem" -out "$dir/cert.pem" 2>/dev/null
  )
  chmod 600 "$dir/cert.pem" "$dir/key.pem"
  fp="$(openssl x509 -in "$dir/cert.pem" -noout -fingerprint -sha256)"
  fp="${fp#*=}"
  fp="${fp//:/}"
  printf '%s\n' "$fp" | tr 'A-F' 'a-f'
}
