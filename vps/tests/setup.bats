#!/usr/bin/env bats

@test "usage prints and exits non-zero with no args" {
  run bash vps/setup.sh
  [ "$status" -ne 0 ]
  [[ "$output" == *"Usage"* ]]
}

@test "detect_egress_iface parses default route interface" {
  stubdir="$BATS_TEST_TMPDIR/bin"
  mkdir -p "$stubdir"
  cat > "$stubdir/ip" <<'STUB'
#!/usr/bin/env bash
echo "default via 10.0.0.1 dev ens3 proto dhcp src 10.0.0.5 metric 100"
STUB
  chmod +x "$stubdir/ip"
  PATH="$stubdir:$PATH" run bash -c 'source vps/lib/net.sh; detect_egress_iface'
  [ "$status" -eq 0 ]
  [ "$output" = "ens3" ]
}

setup_stubs() {
  stubdir="$BATS_TEST_TMPDIR/stubs"
  mkdir -p "$stubdir"
  for c in debconf-set-selections wg-quick apt-get systemctl iptables sysctl netfilter-persistent; do
    printf '#!/usr/bin/env bash\nexit 0\n' > "$stubdir/$c"
  done
  cat > "$stubdir/wg" <<'STUB'
#!/usr/bin/env bash
case "$1" in
  genkey) echo "PRIV$RANDOM$RANDOM$RANDOM" ;;
  pubkey) read -r k; echo "PUB-$k" ;;
esac
STUB
  cat > "$stubdir/openssl" <<'STUB'
#!/usr/bin/env bash
if [ "$1" = req ]; then
  while [ $# -gt 0 ]; do
    case "$1" in -keyout) echo k > "$2" ;; -out) echo c > "$2" ;; esac
    shift
  done
elif [ "$1" = x509 ]; then
  echo "sha256 Fingerprint=AA:BB:CC:DD"
fi
STUB
  cat > "$stubdir/ip" <<'STUB'
#!/usr/bin/env bash
echo "default via 10.0.0.1 dev ens3 proto dhcp"
STUB
  # Hermetic wstunnel install: stub curl serves a local tarball whose sha256
  # is pinned via a temp VERSIONS file.
  mkdir -p "$BATS_TEST_TMPDIR/src"
  echo bin > "$BATS_TEST_TMPDIR/src/wstunnel"
  tar -czf "$BATS_TEST_TMPDIR/ws.tar.gz" -C "$BATS_TEST_TMPDIR/src" wstunnel
  sha="$(shasum -a 256 "$BATS_TEST_TMPDIR/ws.tar.gz" | awk '{print $1}')"
  printf 'WSTUNNEL_VERSION=v11.1.0\nWSTUNNEL_SHA256_AMD64=%s\nWSTUNNEL_SHA256_ARM64=%s\n' "$sha" "$sha" > "$BATS_TEST_TMPDIR/VERSIONS"
  cat > "$stubdir/curl" <<STUB
#!/usr/bin/env bash
while [ \$# -gt 0 ]; do [ "\$1" = -o ] && out="\$2"; shift; done
cp "$BATS_TEST_TMPDIR/ws.tar.gz" "\$out"
STUB
  export WGVPN_VERSIONS="$BATS_TEST_TMPDIR/VERSIONS"
  export WGVPN_WSTUNNEL_BIN="$BATS_TEST_TMPDIR/bin/wstunnel"
  chmod +x "$stubdir"/*
  export PATH="$stubdir:$PATH"
  export WGVPN_SKIP_ROOT_CHECK=1
  export WGVPN_ETC="$BATS_TEST_TMPDIR/etc"
  export WGVPN_SYSCTL_D="$BATS_TEST_TMPDIR/sysctl.d"
  export WGVPN_WG_CONF="$BATS_TEST_TMPDIR/wg/wg0.conf"
  export WGVPN_UNIT="$BATS_TEST_TMPDIR/unit/wstunnel.service"
}

teardown() {
  rm -rf "$BATS_TEST_TMPDIR"
}

@test "setup.sh writes a valid mode-600 bundle with chosen host" {
  setup_stubs
  out="$BATS_TEST_TMPDIR/b.json"
  run bash vps/setup.sh --host x.ddns.net --out "$out"
  [ "$status" -eq 0 ]
  [ -f "$out" ]
  jq -e . "$out" >/dev/null
  [ "$(jq -r .server.host "$out")" = "x.ddns.net" ]
  mode="$(stat -c %a "$out" 2>/dev/null || stat -f %Lp "$out")"
  [ "$mode" = "600" ]
  [[ "$output" == *"$out"* ]]
  [[ "$output" == *"x.ddns.net"* ]]
  [[ "$output" != *"$(jq -r .wireguard.privateKey "$out")"* ]]
  [[ "$output" != *"$(cat "$WGVPN_ETC/server.key")"* ]]
  for f in server.key client.key; do
    m="$(stat -c %a "$WGVPN_ETC/$f" 2>/dev/null || stat -f %Lp "$WGVPN_ETC/$f")"
    [ "$m" = "600" ]
  done
  m="$(stat -c %a "$WGVPN_SYSCTL_D/99-wgvpn.conf" 2>/dev/null || stat -f %Lp "$WGVPN_SYSCTL_D/99-wgvpn.conf")"
  [ "$m" = "644" ]
}

@test "client keys regenerate independently; server keypair kept" {
  setup_stubs
  out="$BATS_TEST_TMPDIR/b.json"
  run bash vps/setup.sh --host x.ddns.net --out "$out"
  [ "$status" -eq 0 ]
  s1="$(cat "$WGVPN_ETC/server.key")"; c1="$(cat "$WGVPN_ETC/client.key")"
  rm "$WGVPN_ETC/client.key" "$WGVPN_ETC/client.pub"
  run bash vps/setup.sh --host x.ddns.net --out "$out"
  [ "$status" -eq 0 ]
  [ "$s1" = "$(cat "$WGVPN_ETC/server.key")" ]
  [ "$c1" != "$(cat "$WGVPN_ETC/client.key")" ]
}

@test "--host wins over --ip" {
  setup_stubs
  out="$BATS_TEST_TMPDIR/b.json"
  run bash vps/setup.sh --ip 1.2.3.4 --host x.ddns.net --out "$out"
  [ "$status" -eq 0 ]
  [ "$(jq -r .server.host "$out")" = "x.ddns.net" ]
}

@test "second run reuses keys (bundle privateKey unchanged)" {
  setup_stubs
  out="$BATS_TEST_TMPDIR/b.json"
  run bash vps/setup.sh --host x.ddns.net --out "$out"
  [ "$status" -eq 0 ]
  k1="$(jq -r .wireguard.privateKey "$out")"
  s1="$(jq -r .wireguard.serverPublicKey "$out")"
  run bash vps/setup.sh --host x.ddns.net --out "$out"
  [ "$status" -eq 0 ]
  [ "$k1" = "$(jq -r .wireguard.privateKey "$out")" ]
  [ "$s1" = "$(jq -r .wireguard.serverPublicKey "$out")" ]
}

@test "non-root without override exits non-zero with message" {
  stubdir="$BATS_TEST_TMPDIR/rstubs"; mkdir -p "$stubdir"
  printf '#!/usr/bin/env bash\necho 1000\n' > "$stubdir/id"; chmod +x "$stubdir/id"
  PATH="$stubdir:$PATH" run env -u WGVPN_SKIP_ROOT_CHECK bash vps/setup.sh --host x.ddns.net
  [ "$status" -ne 0 ]
  [[ "$output" == *"must be run as root"* ]]
}

@test "missing tls.sha256 with existing cert+key does not rotate the cert" {
  setup_stubs
  out="$BATS_TEST_TMPDIR/b.json"
  run bash vps/setup.sh --host x.ddns.net --out "$out"
  [ "$status" -eq 0 ]
  echo "ORIGINAL-CERT" > "$WGVPN_ETC/cert.pem"
  echo "ORIGINAL-KEY" > "$WGVPN_ETC/key.pem"
  rm "$WGVPN_ETC/tls.sha256"
  run bash vps/setup.sh --host x.ddns.net --out "$out"
  [ "$status" -eq 0 ]
  [ "$(cat "$WGVPN_ETC/cert.pem")" = "ORIGINAL-CERT" ]
  [ "$(cat "$WGVPN_ETC/key.pem")" = "ORIGINAL-KEY" ]
  [ "$(cat "$WGVPN_ETC/tls.sha256")" = "aabbccdd" ]
}
