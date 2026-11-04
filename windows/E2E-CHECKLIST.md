# Windows end-to-end checklist

**Not yet executed.** Nothing below has been run: it needs a clean Windows machine, a real VPS (Plan A,
`wgvpn-bundle.json`) and the campus LAN, plus an administrator for the install. Record results here
(check the box, add notes, date, Windows version, whether Smart App Control was on).

Do not paste the bundle, private keys or its contents into notes.

## Install (clean machine)

- [ ] Run `WgVpn-Setup-<version>.exe`; exactly one UAC prompt; SmartScreen "詳細情報 → 実行" works as the README says.
- [ ] Smart App Control state recorded (`(Get-MpComputerStatus).SmartAppControlState`); if On, note whether
      `wstunnel.exe` / the exes were blocked and what the app shows at connect.
- [ ] WireGuard for Windows was missing beforehand: the bundled MSI installed silently (no UI, no auto-launch);
      with it already installed, the installer skipped it.
- [ ] `sc qc WgVpnService` shows `LocalSystem`, `AUTO_START`, quoted path under `C:\Program Files\WgVpn`;
      `sc qfailure WgVpnService` shows three restarts; the service is RUNNING after setup.
- [ ] `C:\ProgramData\WgVpn` exists; `icacls` shows only SYSTEM and Administrators (no inheritance, no Users).
- [ ] Junction refused at ProgramData: with `C:\ProgramData\WgVpn` pre-created as a junction (or as a folder
      owned by a standard user), Setup aborts with the clear message and installs nothing.
- [ ] Extra ACEs: with `C:\ProgramData\WgVpn` pre-created by a standard user (explicit ACE for Users, owner =
      that user), Setup aborts; with one created by an elevated admin plus an explicit extra ACE for Users, Setup
      replaces the DACL so only SYSTEM and Administrators remain (`icacls` after).
- [ ] Pre-existing children: a planted junction/symlink inside the folder, or a child owned by a standard user,
      makes Setup abort; legitimate `bundle.json`/`state.json` from a previous install do not.
- [ ] Forced service start failure (e.g. temporarily make `WgVpn.Service.exe` unrunnable by SAC/AV, or run with a
      wrong binPath): Setup shows an error telling you to re-run Setup, a silent install exits non-zero, and the GUI
      is not launched.
- [ ] Directory page is not shown on a clean install (install location is fixed to Program Files).
- [ ] Upgrade failure path: if the data-folder check or the WireGuard MSI fails during an upgrade, the previously
      running service is started again.
- [ ] Uninstall with an undeletable/suspect `C:\ProgramData\WgVpn` (link or non-admin owner): the user is told the
      path and that the bundle (private key) may remain.
- [ ] The GUI starts without admin (Task Manager: not elevated) and shows the service as reachable.
- [ ] Upgrade: re-running Setup over an existing install stops the service first, keeps the imported bundle,
      and restarts the service.
- [ ] Japanese Windows: Setup UI and messages are Japanese; icacls with SIDs works (no localized-name errors).

## Connect (campus LAN, real bundle)

- [ ] Import `wgvpn-bundle.json` via the GUI; "設定ファイルを読み込みました" appears.
- [ ] Toggle ON; state goes 接続中 -> 接続済み; `https://ifconfig.me` shows the VPS IP.
- [ ] A download larger than 10 MB completes without stall (MTU 1280).
- [ ] wstunnel flags as actually run: inspect the command line of the running `wstunnel.exe` (Task Manager /
      `Get-CimInstance Win32_Process`); it matches `WstunnelArgs` (proxy, loopback UDP listener, remote target).
- [ ] Loopback endpoint works with WireGuard: the WireGuard config uses the local UDP endpoint and the
      handshake completes (`wg show wgvpn`, latest handshake present).
- [ ] AllowedIPs written as `0.0.0.0/1, 128.0.0.0/1`; confirm there is NO kill-switch (no WireGuard WFP block
      filters; `/0` would have engaged one and blocked wstunnel's LAN TCP). While connected, kill `wstunnel.exe`
      and record actual behaviour: does traffic leak direct (`https://ifconfig.me` shows the campus IP) before the
      service tears down, and for how long.
- [ ] `wg show wgvpn latest-handshakes` (run elevated) over the loopback endpoint shows a recent non-zero
      handshake timestamp for the server peer.
- [ ] The proxy accepts wstunnel's real request: capture or log at the proxy that wstunnel's `CONNECT ... HTTP/1.0`
      is answered with 200 (the preflight now sends HTTP/1.0 too, so a refusal shows up at preflight).
- [ ] DNS while connected: `Resolve-DnsName <a name you sinkholed or that only your VPS resolver answers>`;
      record which resolver answers (campus or the VPN's) and whether `Get-DnsClientServerAddress` per interface
      explains it. See the README design-differences note (no metric change).
- [ ] Pin mismatch refused: import a bundle whose `server.tlsSha256` is altered; connect shows
      "The server's TLS certificate does not match the fingerprint in the bundle." and no tunnel/routes appear.
- [ ] Handshake timeout: with the VPS WireGuard stopped (or a wrong server public key in a test bundle),
      connect ends in Error with "WireGuard handshake timed out" within the timeout and everything is cleaned up.

## wireguard.exe under LocalSystem

- [ ] `wireguard.exe /installtunnelservice` as LocalSystem works; the service `WireGuardTunnel$wgvpn` exists while connected.
- [ ] Uninstalling an unknown tunnel (OFF when already torn down) is handled: no error shown, no leftovers.
- [ ] Uninstall of the tunnel service on OFF does not block (note how long OFF takes).

## route.exe behavior

- [ ] Failed `route add` (e.g. proxy address with no route) exits non-zero and the GUI shows
      "No route to the proxy address ..." / a clear error; note the exit code.
- [ ] `route delete` of an absent route: note the exit code and confirm the cleanup treats it as already gone
      (log "Cleanup: route ... was already gone"), not as a failure.

## Toggle, crash, recovery

- [ ] Rapid OFF then ON three times within 2 s: each ON succeeds (no "Tunnel already installed and running"),
      the uninstall waits for the `WireGuardTunnel$wgvpn` service to disappear, and there is one `wstunnel.exe`.
- [ ] Reboot while connected: after boot, measure how long traffic is blackholed (the auto-start tunnel service
      has no wstunnel until the service's startup recovery uninstalls it); confirm "Recovery: removing a leftover
      tunnel service." or the state-file recovery message appears in the log, and no `WireGuardTunnel$wgvpn` remains.
- [ ] Error state: after a failed connect the toggle is OFF; one click retries Connect, and 切断 is still offered.
- [ ] Logon autostart shows only the tray icon (no window); the post-install launch and the Start Menu entry show the window.
- [ ] Toggle OFF/ON several times rapidly: never more than one `wstunnel.exe` (Task Manager / `Get-Process wstunnel`).
- [ ] After OFF: no leftover routes (`route print` has no entry for the proxy/VPS host route, 0.0.0.0/1, 128.0.0.0/1
      via the tunnel) and no `WireGuardTunnel$wgvpn` service.
- [ ] Kill `wstunnel.exe` mid-session: GUI shows エラー with "wstunnel exited with code ...", the tunnel is torn
      down, and normal connectivity returns (direct traffic works again after teardown; note any leak window).
- [ ] Crash recovery: kill `WgVpn.Service.exe` while connected; the service restarts (about 5 s), cleans the
      stale tunnel/routes/wstunnel on startup ("Recovery: cleaning up after an unclean stop."), and OFF/ON works.
- [ ] Daily ON/OFF (GUI and tray menu) never raises UAC.

## GUI against a live service

- [ ] Pipe ACL from a normal user: a standard (non-admin) user can Connect/Disconnect/Import from the GUI; a
      different account/process without rights cannot damage state (note what the pipe ACL allows).
- [ ] Connect, Disconnect, Import via the main window and the tray menu (接続 / 切断 / 開く / 終了) all work.
- [ ] Closing the window hides to tray; 終了 exits the GUI only, the service and tunnel keep running; relaunching
      the GUI shows the current state.
- [ ] Single instance: starting the GUI twice leaves one tray icon.
- [ ] Logoff with the app running (and tunnel ON): behavior noted (tunnel stays up for the service, or not); after
      logon the autostarted GUI shows the correct state.
- [ ] Stop the service (`sc stop WgVpnService`) while connected: tunnel is torn down; the GUI shows
      バックグラウンドサービスが起動していません; `sc start` brings it back to 未接続.

## Uninstall

- [ ] Uninstall (admin): GUI closed, service stopped and deleted, tunnel gone, HKLM Run `WgVpn` value removed,
      `C:\ProgramData\WgVpn` (incl. `bundle.json`) deleted, `C:\Program Files\WgVpn` removed.
- [ ] WireGuard for Windows is still installed afterwards, and the closing message says so.
