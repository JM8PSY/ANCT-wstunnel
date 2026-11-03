# VPS end-to-end checklist

These steps are to be executed by the operator on their own VPS; record results
here (check the box, add notes). Nothing below has been run yet.

- [ ] Run `sudo bash vps/setup.sh --host <vps-ddns>` on a clean Ubuntu VPS; it
      completes and `wgvpn-bundle.json` is written with mode 600 (`ls -l`).
- [ ] Services: `systemctl is-active wstunnel` and `systemctl is-active wg-quick@wg0`
      are `active`; `wg show` lists the interface; `ss -tlnp | grep :443` shows wstunnel.
- [ ] WG UDP 51820 is not reachable externally over IPv4 or IPv6 (probe from
      another host); it is reachable only via loopback (`iptables -S INPUT` and
      `ip6tables -S INPUT` show the `! -i lo ... DROP` rule).
- [ ] From a client with the bundle (ideally behind the campus proxy): bring up
      the tunnel; `curl https://ifconfig.me` returns the VPS public IP.
- [ ] A download larger than 10 MB completes without stall (MTU 1280 sanity).
- [ ] Reboot the VPS: wstunnel, wg0, ip_forward (`sysctl net.ipv4.ip_forward`)
      and the MASQUERADE rule return, and the tunnel reconnects.
- [ ] Re-run `setup.sh`: the bundle's keys are unchanged (idempotency) and
      `sudo iptables -t nat -S | grep -c MASQUERADE` reports 1.
- [ ] If traffic does not forward, check the FORWARD policy (`iptables -S FORWARD`,
      `ufw status`) and apply the README caveat.
