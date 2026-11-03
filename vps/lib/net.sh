#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club
# shellcheck shell=bash

WG_SUBNET="10.88.0.0/24"

# Print the interface of the default route (never hardcode eth0).
detect_egress_iface() {
  local iface
  iface="$(ip route show default | awk '{for(i=1;i<NF;i++) if($i=="dev"){print $(i+1); exit}}')"
  if [ -z "$iface" ]; then
    echo "detect_egress_iface: no default route found" >&2
    return 1
  fi
  printf '%s\n' "$iface"
}

# Persistent (sysctl.d survives reboot) + applied immediately.
enable_forwarding() {
  mkdir -p "${WGVPN_SYSCTL_D:-/etc/sysctl.d}"
  printf 'net.ipv4.ip_forward=1\n' > "${WGVPN_SYSCTL_D:-/etc/sysctl.d}/99-wgvpn.conf"
  sysctl -p "${WGVPN_SYSCTL_D:-/etc/sysctl.d}/99-wgvpn.conf" >/dev/null
}

# Idempotent MASQUERADE for the WG subnet out <egress_iface>.
# Persistence: netfilter-persistent (iptables-persistent) saves the live
# ruleset to /etc/iptables/rules.v4, restored at boot. Saving after the
# idempotent add keeps the saved file free of duplicates.
apply_nat() {
  local iface="${1:?apply_nat: egress interface required}"
  iptables -t nat -C POSTROUTING -s "$WG_SUBNET" -o "$iface" -j MASQUERADE 2>/dev/null \
    || iptables -t nat -A POSTROUTING -s "$WG_SUBNET" -o "$iface" -j MASQUERADE
  if ! command -v netfilter-persistent >/dev/null 2>&1; then
    DEBIAN_FRONTEND=noninteractive apt-get install -y iptables-persistent
  fi
  netfilter-persistent save
}
