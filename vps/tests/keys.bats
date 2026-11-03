#!/usr/bin/env bats

setup() {
  # shellcheck source=../lib/keys.sh
  source vps/lib/keys.sh
}

teardown() {
  rm -rf "$BATS_TEST_TMPDIR/tls"
}

@test "wg_genkeypair prints private then public key" {
  command -v wg >/dev/null || skip "wg not installed"
  run wg_genkeypair
  [ "$status" -eq 0 ]
  [ "${#lines[@]}" -eq 2 ]
  [[ "${lines[0]}" =~ ^[A-Za-z0-9+/]{43}=$ ]]
  [[ "${lines[1]}" =~ ^[A-Za-z0-9+/]{43}=$ ]]
  [ "${lines[0]}" != "${lines[1]}" ]
  [ "$(printf '%s' "${lines[0]}" | wg pubkey)" = "${lines[1]}" ]
}

@test "tls_gen_selfsigned writes 600 cert/key and prints matching fingerprint" {
  command -v openssl >/dev/null || skip "openssl not installed"
  dir="$BATS_TEST_TMPDIR/tls"
  rm -rf "$dir"
  run tls_gen_selfsigned "$dir"
  [ "$status" -eq 0 ]
  [ -f "$dir/cert.pem" ]
  [ -f "$dir/key.pem" ]
  [ "$(stat -c %a "$dir/key.pem" 2>/dev/null || stat -f %Lp "$dir/key.pem")" = "600" ]
  [ "$(stat -c %a "$dir/cert.pem" 2>/dev/null || stat -f %Lp "$dir/cert.pem")" = "600" ]
  [[ "$output" =~ ^[0-9a-f]{64}$ ]]
  expected="$(openssl x509 -in "$dir/cert.pem" -noout -fingerprint -sha256 | sed 's/^.*=//' | tr -d ':' | tr 'A-F' 'a-f')"
  [ "$output" = "$expected" ]
}
