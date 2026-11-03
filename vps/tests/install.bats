#!/usr/bin/env bats

setup() {
  work="$BATS_TEST_TMPDIR"
  mkdir -p "$work/src"
  printf 'fake-wstunnel-binary\n' > "$work/src/wstunnel"
  tar -czf "$work/good.tar.gz" -C "$work/src" wstunnel
  good_sha="$(shasum -a 256 "$work/good.tar.gz" | awk '{print $1}')"
  export WGVPN_WSTUNNEL_BIN="$work/bin/wstunnel"
  export WGVPN_VERSIONS="$work/VERSIONS"
  # Hermetic: replace the network download with a local copy.
  # shellcheck disable=SC2016
  DL_STUB='_wstunnel_download() { cp "$GOOD_TARBALL" "$2"; }'
  export GOOD_TARBALL="$work/good.tar.gz"
  export DL_STUB
}

write_versions() {
  printf 'WSTUNNEL_VERSION=v11.1.0\nWSTUNNEL_SHA256_AMD64=%s\nWSTUNNEL_SHA256_ARM64=%s\n' "$1" "$1" > "$WGVPN_VERSIONS"
}

run_install() {
  run bash -c 'source vps/lib/install.sh; eval "$DL_STUB"; install_wstunnel'
}

@test "install_wstunnel refuses on sha256 mismatch and installs nothing" {
  write_versions "0000000000000000000000000000000000000000000000000000000000000000"
  run_install
  [ "$status" -ne 0 ]
  [ ! -e "$WGVPN_WSTUNNEL_BIN" ]
}

@test "install_wstunnel installs mode 755 when sha256 matches" {
  write_versions "$good_sha"
  run_install
  [ "$status" -eq 0 ]
  [ -f "$WGVPN_WSTUNNEL_BIN" ]
  mode="$(stat -c %a "$WGVPN_WSTUNNEL_BIN" 2>/dev/null || stat -f %Lp "$WGVPN_WSTUNNEL_BIN")"
  [ "$mode" = "755" ]
  grep -q fake-wstunnel-binary "$WGVPN_WSTUNNEL_BIN"
}

@test "mismatch does not overwrite an existing binary" {
  mkdir -p "$work/bin"; echo old > "$WGVPN_WSTUNNEL_BIN"
  write_versions "1111111111111111111111111111111111111111111111111111111111111111"
  run_install
  [ "$status" -ne 0 ]
  [ "$(cat "$WGVPN_WSTUNNEL_BIN")" = "old" ]
}

@test "enable_services adds WG drop rule once (idempotent) and enables units" {
  stubs="$work/stubs"; mkdir -p "$stubs"
  cat > "$stubs/iptables" <<'STUB'
#!/usr/bin/env bash
echo "iptables $*" >> "$IPT_LOG"
if [ "$1" = -C ]; then grep -q "^RULE$" "$IPT_STATE" 2>/dev/null; exit $?; fi
if [ "$1" = -I ] || [ "$1" = -A ]; then echo RULE >> "$IPT_STATE"; fi
STUB
  for c in systemctl netfilter-persistent; do
    printf '#!/usr/bin/env bash\necho "%s $*" >> "$IPT_LOG"\n' "$c" > "$stubs/$c"
  done
  chmod +x "$stubs"/*
  export IPT_LOG="$work/log" IPT_STATE="$work/state"
  export WGVPN_WG_CONF="$work/wg0.conf"
  PATH="$stubs:$PATH" run bash -c 'source vps/lib/install.sh; enable_services; enable_services'
  [ "$status" -eq 0 ]
  [ "$(grep -c '^RULE$' "$IPT_STATE")" = "1" ]
  grep -q 'dport 51820' "$IPT_LOG"
  grep -q 'enable --now wg-quick@wg0' "$IPT_LOG"
  grep -q 'enable --now wstunnel' "$IPT_LOG"
  grep -q 'netfilter-persistent save' "$IPT_LOG"
}
