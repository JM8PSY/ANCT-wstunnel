# VPS setup (wstunnel + WireGuard)

Sets up a VPS as the egress point for a campus LAN that only permits HTTPS
`CONNECT` through a proxy. wstunnel (WSS on :443) carries WireGuard UDP traffic
to this VPS, which forwards it to the internet. Each user runs their own VPS.
The script produces a connection bundle that the Windows client app loads.

## Prerequisites

- A clean Ubuntu LTS or Debian 12 VPS (amd64 or arm64), with root/sudo. Setup has been run on Debian 12.
- A global IP address or a DDNS name pointing at the VPS.
- TCP port 443 free (and allowed by the provider's firewall).

## Usage

```
sudo bash vps/setup.sh --host <your-vps-ddns>
# or
sudo bash vps/setup.sh --ip <addr>
# optional: --out <path>   (default ./wgvpn-bundle.json)
```

`--host` is the endpoint written into the bundle; if both are given, `--host`
wins. The TLS certificate is self-signed; the bundle carries its SHA-256 so the
client can pin it.

## What it does

- Installs `wireguard-tools`, `iptables`, `iptables-persistent`, `jq`, `openssl`,
  `curl`, and the pinned wstunnel release (see `vps/VERSIONS`, v11.1.0) after a
  hard SHA-256 check.
- wstunnel server on `wss://0.0.0.0:443` (self-signed TLS), restricted to
  forwarding to `localhost:51820`.
- WireGuard server `wg0` (10.88.0.1/24) on UDP 51820; the server config has one
  peer, 10.88.0.2/32.
- Closes UDP 51820 to everything except loopback via iptables and ip6tables
  (IPv4 and IPv6), so WireGuard is reachable only through wstunnel.
- Enables `net.ipv4.ip_forward` (persistent via `/etc/sysctl.d/99-wgvpn.conf`)
  and adds an iptables MASQUERADE rule for 10.88.0.0/24 out the default-route
  interface.
- Installs systemd units (`wstunnel`, `wg-quick@wg0`), enabled and started.
  Rules are saved with `netfilter-persistent`.
- Secrets live in `/etc/wgvpn` (mode 700, keys mode 600).

## Output: the bundle

`wgvpn-bundle.json` (schema version 1) is the contract consumed by the Windows
client app: server host/port/TLS fingerprint, proxy, wstunnel ports, and the
WireGuard client config (client private key, server public key, 10.88.0.2/32,
`0.0.0.0/0`, DNS 1.1.1.1, MTU 1280, keepalive 25). Load it into the app.

Keep it secret: it contains the client private key. It is written mode 600;
transfer it securely.

## Idempotency and reboot

- Re-running reuses existing server/client keys and TLS cert, so an already
  distributed bundle stays valid. NAT and firewall rules are checked before
  being added and are not duplicated.
- After a reboot, services, ip_forward, NAT and the WG port rules all return.

## Caveats

1. **Default-DROP FORWARD policy.** Masquerade alone does not forward traffic if
   the FORWARD chain defaults to DROP (e.g. ufw active). Allow the WG subnet:

   ```
   sudo iptables -I FORWARD -s 10.88.0.0/24 -j ACCEPT
   sudo iptables -I FORWARD -d 10.88.0.0/24 -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT
   sudo netfilter-persistent save
   ```

   With ufw: set `DEFAULT_FORWARD_POLICY="ACCEPT"` in `/etc/default/ufw` (or add
   the rules above to `/etc/ufw/before.rules`), allow `443/tcp`, then
   `sudo ufw reload`. The setup script does not change FORWARD rules.
2. **No auto-restart on re-run.** Re-running after editing a conf or unit
   rewrites the files, but running services are not restarted (`enable --now`
   does not restart active units). Apply changes manually:

   ```
   sudo systemctl daemon-reload
   sudo systemctl restart wg-quick@wg0 wstunnel
   ```

## Optional: adding a second peer (manual)

Generate another keypair (`wg genkey | tee c2.key | wg pubkey > c2.pub`), append
a `[Peer]` with `PublicKey = <c2.pub>` and a new `AllowedIPs = 10.88.0.3/32` to
`/etc/wireguard/wg0.conf`, and restart `wg-quick@wg0`. Copy the bundle, replacing
`wireguard.privateKey` and `wireguard.address` (`10.88.0.3/32`). Note that
re-running `setup.sh` rewrites `wg0.conf` with only the first peer, so re-add it.

## Development

```
bats vps/tests
shellcheck -x vps/setup.sh vps/lib/*.sh
```

CI runs both (`.github/workflows/vps-ci.yml`). Live end-to-end verification is
tracked in `vps/E2E-CHECKLIST.md`.

## License

Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club.

This program is free software: you can redistribute it and/or modify it under
the terms of the GNU General Public License as published by the Free Software
Foundation, either version 3 of the License, or (at your option) any later
version (`GPL-3.0-or-later`). See [`LICENSE`](../LICENSE) for the full text.
