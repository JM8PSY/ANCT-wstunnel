#!/usr/bin/env bats

@test "usage prints and exits non-zero with no args" {
  run bash vps/setup.sh
  [ "$status" -ne 0 ]
  [[ "$output" == *"Usage"* ]]
}
