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
  for c in apt-get systemctl iptables sysctl netfilter-persistent; do
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
  chmod +x "$stubdir"/*
  export PATH="$stubdir:$PATH"
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
