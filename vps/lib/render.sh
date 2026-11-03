#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club
# shellcheck shell=bash
# Pure render functions: print to stdout, no side effects.

# render_wg_server_conf <server_priv> <client_pub>
render_wg_server_conf() {
  local server_priv=$1 client_pub=$2
  cat <<CONF
[Interface]
Address = 10.88.0.1/24
ListenPort = 51820
PrivateKey = ${server_priv}

[Peer]
PublicKey = ${client_pub}
AllowedIPs = 10.88.0.2/32
CONF
}

# render_wstunnel_unit <wg_udp_port>
render_wstunnel_unit() {
  local wg_udp_port=$1
  cat <<UNIT
[Unit]
Description=wstunnel server (WireGuard over WSS)
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/usr/local/bin/wstunnel server wss://0.0.0.0:443 --restrict-to localhost:${wg_udp_port} --tls-certificate /etc/wgvpn/cert.pem --tls-private-key /etc/wgvpn/key.pem
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
UNIT
}

# render_bundle <host> <port> <tls_sha> <client_priv> <server_pub>
render_bundle() {
  jq -n \
    --arg host "$1" --argjson port "$2" --arg sha "$3" \
    --arg priv "$4" --arg spub "$5" '
    {
      version: 1,
      server: { host: $host, port: $port, tlsSha256: $sha },
      proxy: { host: "websec.asahikawa-nct.ac.jp", port: 8080 },
      wstunnel: { localUdpPort: 51820, remoteTarget: "localhost:51820" },
      wireguard: {
        privateKey: $priv, serverPublicKey: $spub,
        address: "10.88.0.2/32", allowedIps: "0.0.0.0/0",
        dns: "1.1.1.1", mtu: 1280, persistentKeepalive: 25
      }
    }'
}
