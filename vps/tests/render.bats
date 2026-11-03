#!/usr/bin/env bats

setup() {
  # shellcheck source=../lib/render.sh
  source vps/lib/render.sh
}

@test "wg server conf has interface, peer, and no NAT" {
  run render_wg_server_conf SERVERPRIV CLIENTPUB
  [ "$status" -eq 0 ]
  [[ "$output" == *"[Interface]"* ]]
  [[ "$output" == *"Address = 10.88.0.1/24"* ]]
  [[ "$output" == *"ListenPort = 51820"* ]]
  [[ "$output" == *"PrivateKey = SERVERPRIV"* ]]
  [[ "$output" == *"PublicKey = CLIENTPUB"* ]]
  [[ "$output" == *"AllowedIPs = 10.88.0.2/32"* ]]
  [[ "$output" != *PostUp* ]]
  [[ "$output" != *iptables* ]]
}

@test "wstunnel unit runs restricted TLS server" {
  run render_wstunnel_unit 51820
  [ "$status" -eq 0 ]
  [[ "$output" == *"[Unit]"* && "$output" == *"[Service]"* && "$output" == *"[Install]"* ]]
  [[ "$output" == *"wss://0.0.0.0:443"* ]]
  [[ "$output" == *"--restrict-to localhost:51820"* ]]
  [[ "$output" == *"--tls-certificate /etc/wgvpn/cert.pem"* ]]
  [[ "$output" == *"--tls-private-key /etc/wgvpn/key.pem"* ]]
  [[ "$output" == *"Restart=always"* ]]
  [[ "$output" == *"WantedBy=multi-user.target"* ]]
}

@test "bundle is valid JSON with contract fields" {
  run render_bundle example.ddns.net 443 abc123 PRIV PUB
  [ "$status" -eq 0 ]
  echo "$output" | jq -e '.version==1 and .server.host=="example.ddns.net" and .server.port==443 and .server.tlsSha256=="abc123"'
  echo "$output" | jq -e '.proxy.host=="websec.asahikawa-nct.ac.jp" and .proxy.port==8080'
  echo "$output" | jq -e '.wstunnel.localUdpPort==51820 and .wstunnel.remoteTarget=="localhost:51820"'
  echo "$output" | jq -e '.wireguard.privateKey=="PRIV" and .wireguard.serverPublicKey=="PUB" and .wireguard.address=="10.88.0.2/32"'
  echo "$output" | jq -e '.wireguard.allowedIps=="0.0.0.0/0" and .wireguard.dns=="1.1.1.1" and .wireguard.mtu==1280 and .wireguard.persistentKeepalive==25'
}
