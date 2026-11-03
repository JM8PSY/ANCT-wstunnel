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
