# Windows Client App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Tailscale-like Windows tray app that imports a VPS bundle and toggles a wstunnel+WireGuard tunnel ON/OFF, with no per-use UAC.

**Architecture:** Three C# projects. `WgVpn.Core` holds all pure logic (bundle model/parser, wstunnel arg builder, WireGuard `.conf` renderer, proxy-route command builder, IPC message contract) and is fully unit-tested. `WgVpn.Service` is a LocalSystem Windows Service that owns the privileged work (launch `wstunnel.exe`, add a host route to the proxy, start/stop the WireGuard tunnel via the official `wireguard.exe` tunnel service) and exposes a named-pipe IPC. `WgVpn.App` is a WPF tray front-end that sends Connect/Disconnect/GetStatus over the pipe. The GUI never needs admin because the service, installed once with admin, does the privileged steps.

**Tech Stack:** .NET 8, C#, WPF + WinForms NotifyIcon (tray), Windows Service (`Microsoft.Extensions.Hosting.WindowsServices`), named-pipe IPC, System.Text.Json, xUnit. Bundles `wstunnel.exe`; relies on official WireGuard for Windows (wintun + `wireguard.exe`). Installer via WiX or Inno Setup. Self-contained single-file publish.

**Spec:** `docs/superpowers/specs/2026-11-03-anct-wstunnel-design.md`
**Consumes interface from:** `docs/superpowers/plans/2026-11-03-vps-setup.md` (bundle JSON v1).

## Global Constraints

- .NET 8, C#; Windows-only client (non-goal: other OSes).
- Self-contained single-file publish; users download and run, no runtime install (§5.2).
- WPF + tray; minimal UI — ON/OFF toggle, status, import, log (§5.5). No multi-profile/peer-management UI (non-goal).
- Admin required only at install; daily ON/OFF via LocalSystem service, no UAC (§5.3).
- WireGuard via official WireGuard for Windows (wintun + `wireguard.exe`); do not reimplement (§5.2).
- Endpoint default is the bundle's DDNS host; TLS self-signed — pin `server.tlsSha256` (preferred over no-verify) (§5.4).
- wstunnel client connects via HTTP proxy `websec.asahikawa-nct.ac.jp:8080`; no proxy auth (§1).
- Unsigned binaries; ship SmartScreen "More info → Run anyway" guidance (§5.3 decision).
- Consume bundle schema version 1 exactly; refuse unknown versions.
- No SNI spoofing / evasion; plain host+port only (anti-goal).

## Review Focus

- **Bundle with an unsupported `version`:** parser must refuse with a clear message, never crash or half-apply. Covered by Task 2.
- **Connect while already connected / rapid toggle spam:** state machine must not spawn duplicate wstunnel processes or tunnels. Covered by Task 6.
- **wstunnel process dies mid-session:** service must detect, set Error state, and tear the tunnel down (no half-up state). Covered by Task 7 (unit-test the monitor policy in Task 6).
- **Proxy hostname resolves to multiple / changing IPs:** add routes for all resolved IPs and remove exactly those on disconnect (no stale routes). Covered by Task 5.
- **GUI launched with the service missing/stopped:** show a clear "service not running" state, not a silent hang. Covered by Task 8.

## File Structure

- Create: `windows/WgVpn.sln`
- Create: `windows/WgVpn.Core/` — `ConnectionBundle.cs`, `BundleParser.cs`, `WstunnelArgs.cs`, `WireguardConf.cs`, `ProxyRoute.cs`, `Ipc/Messages.cs`, `TunnelState.cs`.
- Create: `windows/WgVpn.Service/` — `TunnelController.cs`, `PipeServer.cs`, `Program.cs`.
- Create: `windows/WgVpn.App/` — `TrayApp`, `MainWindow.xaml(.cs)`, `PipeClient.cs`.
- Create: `windows/WgVpn.Tests/` — xUnit tests for Core.
- Create: `windows/installer/` — WiX/Inno script; `windows/README.md` (install + SmartScreen guidance).

---

### Task 1: Solution scaffold + test/build gate

**Files:** Create `windows/WgVpn.sln`, the four project files, `WgVpn.Tests` referencing Core, `.github/workflows/windows-ci.yml` (`dotnet build` + `dotnet test`) or documented local run.

- [ ] **Step 1:** Add a trivial xUnit test `Smoke_BuildsAndRuns` asserting `true`.
- [ ] **Step 2:** `dotnet test windows/WgVpn.sln` — Expected: FAIL (projects missing).
- [ ] **Step 3:** Scaffold solution + projects (Core = classlib net8; Service = worker w/ `Microsoft.Extensions.Hosting.WindowsServices`; App = WPF net8-windows; Tests = xUnit ref Core).
- [ ] **Step 4:** `dotnet test` — PASS.
- [ ] **Step 5:** Commit.

### Task 2: Bundle model + parser (`ConnectionBundle`, `BundleParser`)

**Files:** Modify Core; Create `WgVpn.Tests/BundleParserTests.cs`; add `windows/WgVpn.Tests/fixtures/bundle-v1.json` (copy of the Shared Interface sample).

**Interfaces:**
- Produces: records `ConnectionBundle(Server, Proxy, Wstunnel, Wireguard)` with nested records matching the bundle JSON fields exactly (Server: Host, Port, TlsSha256; Proxy: Host, Port; Wstunnel: LocalUdpPort, RemoteTarget; Wireguard: PrivateKey, ServerPublicKey, Address, AllowedIps, Dns, Mtu, PersistentKeepalive). `BundleParser.Parse(string json) -> ConnectionBundle`; throws `BundleFormatException` on unknown `version` or missing required field.

- [ ] **Step 1:** Write `BundleParserTests`: parsing `bundle-v1.json` yields `Server.Host=="example.ddns.net"`, `Wireguard.Mtu==1280`, `Proxy.Port==8080`; parsing `{"version":2,...}` throws `BundleFormatException` with a message naming the version; parsing JSON missing `wireguard.privateKey` throws `BundleFormatException`.
- [ ] **Step 2:** `dotnet test --filter BundleParser` — Expected: FAIL.
- [ ] **Step 3:** Implement records + `BundleParser.Parse` (System.Text.Json; validate `version==1`; validate required fields non-empty).
- [ ] **Step 4:** `dotnet test --filter BundleParser` — PASS.
- [ ] **Step 5:** Commit.

### Task 3: wstunnel argument builder (`WstunnelArgs`)

**Files:** Modify Core; Create `WstunnelArgsTests.cs`.

**Interfaces:**
- Consumes: `ConnectionBundle`.
- Produces: `WstunnelArgs.Build(ConnectionBundle b) -> string[]` — the exact `wstunnel.exe` v11 client argv: client subcommand, local UDP forward `udp://<localUdpPort>:<remoteTarget>?timeout_sec=0`, `--http-proxy http://<proxy.Host>:<proxy.Port>`, TLS pin/verify option tied to `server.TlsSha256`, and target `wss://<server.Host>:<server.Port>`.

- [ ] **Step 1:** Write `WstunnelArgsTests`: `Build(sample)` contains `client`, an arg matching `udp://51820:localhost:51820?timeout_sec=0`, `--http-proxy http://websec.asahikawa-nct.ac.jp:8080`, and `wss://example.ddns.net:443`; and the TLS-pin/verify arg is present (assert on whichever exact flag the pinned v11 supports).
- [ ] **Step 2:** `dotnet test --filter WstunnelArgs` — Expected: FAIL.
- [ ] **Step 3:** Implement `Build`. Confirm the exact v11 flag names (`wstunnel client --help` of the pinned version) for local UDP forward, HTTP proxy, and TLS cert pin/skip; encode them as constants.
- [ ] **Step 4:** `dotnet test --filter WstunnelArgs` — PASS.
- [ ] **Step 5:** Commit.

### Task 4: WireGuard `.conf` renderer (`WireguardConf`)

**Files:** Modify Core; Create `WireguardConfTests.cs`.

**Interfaces:**
- Produces: `WireguardConf.Render(ConnectionBundle b) -> string` — `[Interface]` PrivateKey, Address=`b.Wireguard.Address`, DNS, MTU=`b.Wireguard.Mtu`; `[Peer]` PublicKey=`b.Wireguard.ServerPublicKey`, **Endpoint=`127.0.0.1:<b.Wstunnel.LocalUdpPort>`**, AllowedIPs=`b.Wireguard.AllowedIps`, PersistentKeepalive.

- [ ] **Step 1:** Write `WireguardConfTests`: output contains `Endpoint = 127.0.0.1:51820`, `MTU = 1280`, `AllowedIPs = 0.0.0.0/0`, the server public key, and the client private key; and parses as valid INI (section headers present).
- [ ] **Step 2:** `dotnet test --filter WireguardConf` — Expected: FAIL.
- [ ] **Step 3:** Implement `Render`.
- [ ] **Step 4:** `dotnet test --filter WireguardConf` — PASS.
- [ ] **Step 5:** Commit.

### Task 5: Proxy host-route command builder (`ProxyRoute`)

**Files:** Modify Core; Create `ProxyRouteTests.cs`.

**Interfaces:**
- Produces: `ProxyRoute.BuildAdd(IReadOnlyList<string> proxyIps, string gateway, string ifIndex) -> IReadOnlyList<ProcessSpec>` and `ProxyRoute.BuildRemove(IReadOnlyList<string> proxyIps) -> IReadOnlyList<ProcessSpec>`, where `ProcessSpec(FileName, Args[])` describes a `route`/`netsh` invocation per IP that pins the proxy's traffic to the physical LAN path (so it is not swallowed by `AllowedIPs=0.0.0.0/0`). `ProcessSpec` is the shared type the service executes.

- [ ] **Step 1:** Write `ProxyRouteTests`: `BuildAdd(["203.0.113.9","203.0.113.10"], gw, idx)` returns 2 specs, each referencing its IP as a /32 host route; `BuildRemove` returns matching delete specs for the same IPs.
- [ ] **Step 2:** `dotnet test --filter ProxyRoute` — Expected: FAIL.
- [ ] **Step 3:** Implement using the chosen command (`route add <ip> mask 255.255.255.255 <gw> if <idx>` or `netsh`); one spec per IP.
- [ ] **Step 4:** `dotnet test --filter ProxyRoute` — PASS.
- [ ] **Step 5:** Commit.

### Task 6: IPC contract + connection state machine (`Ipc/Messages`, `TunnelState`, `ConnectionStateMachine`)

**Files:** Modify Core; Create `IpcMessagesTests.cs`, `StateMachineTests.cs`.

**Interfaces:**
- Produces:
  - `enum TunnelState { Disconnected, Connecting, Connected, Error }`.
  - Request/reply records: `ConnectRequest`, `DisconnectRequest`, `GetStatusRequest`; `StatusReply(TunnelState State, string? ServerHost, string? LastError)`. JSON-serializable with a discriminator so `PipeServer`/`PipeClient` can round-trip them.
  - `ConnectionStateMachine` with `Connect()`, `Disconnect()`, `OnProcessExited()`, exposing `State`; **Connect while Connecting/Connected is a no-op** (guards duplicate launches); `OnProcessExited()` from Connected/Connecting → `Error`.

- [ ] **Step 1:** Write `IpcMessagesTests` (round-trip serialize/deserialize each message; discriminator preserved) and `StateMachineTests` (`Connect` from Disconnected → Connecting; second `Connect` while Connecting is a no-op/idempotent; `OnProcessExited` → Error; `Disconnect` from any state → Disconnected).
- [ ] **Step 2:** `dotnet test --filter "Ipc|StateMachine"` — Expected: FAIL.
- [ ] **Step 3:** Implement messages (polymorphic System.Text.Json) and the state machine.
- [ ] **Step 4:** `dotnet test --filter "Ipc|StateMachine"` — PASS.
- [ ] **Step 5:** Commit.

### Task 7: Windows Service (`TunnelController`, `PipeServer`, `Program`)

**Files:** Modify Service. Deliverable verified by Task 9 E2E (privileged + driver — not unit-tested); the decision logic it uses is already tested in Task 6.

**Interfaces:**
- Consumes: Core (`WstunnelArgs`, `WireguardConf`, `ProxyRoute`, `ConnectionStateMachine`, IPC messages).
- Produces: a LocalSystem service that on `ConnectRequest`: writes the rendered `.conf` to a mode-restricted temp path, resolves the proxy host to IP(s), launches `wstunnel.exe` with built args, waits for the local UDP listener, runs `ProxyRoute.BuildAdd` specs, then `wireguard.exe /installtunnelservice <conf>`; monitors the wstunnel process (exit → tear down, state Error). On `DisconnectRequest`: `wireguard.exe /uninstalltunnelservice`, kill wstunnel, run `ProxyRoute.BuildRemove`. `GetStatusRequest` → current `StatusReply`. Named pipe ACL allows the interactive user to connect.

- [ ] **Step 1:** Implement `TunnelController` (orchestration using Core), `PipeServer` (serves IPC), `Program` (host as Windows Service). Ensure teardown order reverses setup; never leave a half-up tunnel.
- [ ] **Step 2:** `dotnet build` the Service — compiles.
- [ ] **Step 3:** Commit. (Functional verification deferred to Task 9.)

### Task 8: WPF tray GUI (`TrayApp`, `MainWindow`, `PipeClient`)

**Files:** Modify App.

**Interfaces:**
- Consumes: Core IPC messages via `PipeClient`.
- Produces: tray icon reflecting `TunnelState` (distinct icon/color per state); context menu Connect/Disconnect/Open/Exit; `MainWindow` with a large ON/OFF toggle bound to Connect/Disconnect, a status line, the server host, an **Import bundle** button (file picker → sends the bundle to the service to store), and a scrolling log/last-error pane. Status is polled via `GetStatusRequest`. **If the pipe cannot be reached, show a clear "background service not running" state** (Review Focus) rather than hanging.

- [ ] **Step 1:** Implement `PipeClient` (connect with timeout; surface unreachable as a typed result), tray app, and `MainWindow` with bindings to a view-model that maps `StatusReply` → UI.
- [ ] **Step 2:** `dotnet build` the App — compiles; launch locally and confirm the window shows "service not running" when the service is absent.
- [ ] **Step 3:** Commit.

### Task 9: Installer + end-to-end on Windows (campus LAN)

**Files:** Create `windows/installer/*`, `windows/README.md`, `windows/E2E-CHECKLIST.md`.

Deliverable: an installer and an executed, documented E2E.

- [ ] **Step 1:** Build the installer: self-contained single-file publish of App + Service; bundle `wstunnel.exe`; register the Windows Service (admin once); install or require official WireGuard for Windows (choose MSI-bundle vs prompt to minimize junior effort) and document it.
- [ ] **Step 2:** On a clean Windows machine, install (accept the one admin prompt); confirm the service runs as LocalSystem and the GUI starts without admin.
- [ ] **Step 3:** Import a real `wgvpn-bundle.json` from a Plan-A VPS; on the campus LAN, toggle ON; confirm the tunnel comes up through the proxy, `https://ifconfig.me` shows the VPS IP, and a >10 MB download completes without stall (MTU).
- [ ] **Step 4:** Toggle OFF/ON several times rapidly; confirm no duplicate `wstunnel.exe` processes and no leftover routes after OFF; kill `wstunnel.exe` mid-session and confirm the GUI shows Error and the tunnel is torn down. Confirm daily ON/OFF never raises UAC.
- [ ] **Step 5:** Write `README.md` (install steps, the SmartScreen "More info → Run anyway" guidance, import, troubleshooting) and record results in `E2E-CHECKLIST.md`. Commit.
