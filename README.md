# MyNetworkTool

A single-executable Windows utility (C# / .NET 9) that lets a **standard (unelevated) user** change
network settings without repeated UAC prompts. Elevation is required **once**, during install.

It does this with a split-privilege design in one `.exe`:

* a **Windows Service** (`MyNetworkToolSvc`) that runs as **LocalSystem** and performs the actual,
  validated changes, and
* an **unelevated WinForms system-tray app** that is the UI and talks to the service over a
  **named pipe** (`\\.\pipe\MyNetworkTool`) using small **structured JSON** messages.

The IPC surface is intentionally narrow: the client can only ask for a fixed set of *structured*
actions. It can **never** send raw shell / PowerShell / `netsh` strings or arbitrary executable
paths. The service validates every input before acting.

> The build produces **`MyNetworkTool.exe`** (the Visual Studio project and C# namespaces remain
> `NetworkingTool`; only the assembly name differs).

---

## Modes (one exe, multiple entry points)

| Command | What it does |
|---|---|
| `MyNetworkTool.exe` | Checks GitHub for a newer release and offers to download/install it (with a release-page link). Then, if the service is installed → launches the tray UI; otherwise offers to run the one-time elevated install. |
| `MyNetworkTool.exe install` | Self-elevates (UAC once), copies the exe to `C:\Program Files\MyNetworkTool`, registers + starts the LocalSystem service, adds a logon auto-start entry. |
| `MyNetworkTool.exe uninstall` | Self-elevates, stops + removes the service, clears the auto-start entry, removes installed files. |
| `MyNetworkTool.exe service` | Runs the Windows Service host (started by the SCM as LocalSystem; also runnable from an admin console for debugging). |
| `MyNetworkTool.exe tray` | Runs the unelevated tray UI directly. |

---

## Project layout

```
NetworkingTool/
  Program.cs               # mode dispatch + default-launch logic
  app.manifest             # asInvoker (tray never triggers UAC; install self-elevates)
  NetworkingTool.csproj
  Shared/
    Constants.cs           # pipe/service names, paths (single source of truth)
    IpcModels.cs           # IpcAction enum, IpcRequest, IpcResponse, JSON options
    AdapterInfo.cs         # adapter snapshot DTO
    Validation.cs          # IP / prefix / category / proxy-URL validation
  Service/
    ServiceEntry.cs        # builds the generic host + Windows Service lifetime
    PipeServerWorker.cs    # BackgroundService: secured named-pipe accept loop
    RequestHandler.cs      # validates + routes each request (enforcement point)
    NetworkOperations.cs   # static IP / DHCP / profile via validated PowerShell
    AdapterService.cs      # adapter enumeration + GUID->ifIndex resolution
    ProxyOperations.cs     # HTTP(S)_PROXY presets (machine scope) + broadcast
    NativeMethods.cs       # WM_SETTINGCHANGE broadcast P/Invoke
    ServiceLog.cs          # tiny always-on file logger (ProgramData)
  Tray/
    TrayEntry.cs           # WinForms bootstrap
    TrayApplicationContext.cs  # NotifyIcon + Open/Exit menu
    MainForm.cs            # the UI (built entirely in code)
    PipeClient.cs          # the only path from UI to service
  Install/
    Installer.cs           # self-elevation, copy, register/start, auto-start, uninstall
    ServiceControl.cs      # native SCM (advapi32) create/start/stop/delete
```

---

## Build

Requires the **.NET 9 SDK** and the **Windows Desktop** runtime (for WinForms).

```bash
# from the NetworkingTool folder
dotnet build NetworkingTool.csproj -c Release
```

> Build the **`.csproj`** (or the `.sln` with the default *Any CPU* platform). The bundled `.sln`
> only defines *Any CPU* configurations, so `dotnet build NetworkingTool.sln -p:Platform=x64`
> fails with MSB4126 — that is a solution-config quirk, not a code problem.

### Publish as a single executable

Self-contained (no runtime install needed on the target; ~100 MB exe — recommended for handing
the file to another machine):

```bash
dotnet publish NetworkingTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
# -> publish\MyNetworkTool.exe
```

Framework-dependent (small exe, requires the .NET 9 *Windows Desktop* runtime on the target):

```bash
dotnet publish NetworkingTool.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

`PublishSingleFile` and `IncludeNativeLibrariesForSelfExtract` are already set in the csproj.

### NuGet references

* **`Microsoft.Extensions.Hosting.WindowsServices`** `9.0.0` — the generic host + Windows Service
  lifetime (`AddWindowsService`) + EventLog logging. It transitively brings in
  `Microsoft.Extensions.Hosting`.

Everything else (`System.IO.Pipes` ACLs, `Microsoft.Win32.Registry`, `System.ServiceProcess` via
raw P/Invoke, WinForms) is in-box for `net9.0-windows`.

---

## Install / uninstall / run

1. **Build/publish** to get `MyNetworkTool.exe`.
2. **Install (one-time elevation):**
   ```bat
   MyNetworkTool.exe install
   ```
   Accept the single UAC prompt. This copies the exe to `C:\Program Files\MyNetworkTool\`,
   registers `MyNetworkToolSvc` (LocalSystem, automatic start), starts it, and adds an
   `HKLM\...\Run` entry so the tray auto-starts at logon. A dialog confirms success.
3. **Run the tray (no elevation):**
   ```bat
   MyNetworkTool.exe
   ```
   or just wait for the next logon. Right-click the tray icon → **Open**. Pick an adapter, fill in
   the fields, and use the buttons. Each result (success or error) from the service is shown in the
   output box.
4. **Uninstall (one-time elevation):**
   ```bat
   MyNetworkTool.exe uninstall
   ```

Logs: `C:\ProgramData\MyNetworkTool\service.log`.

---

## Operations & the wire protocol

One line of JSON per request, one line per response, over `\\.\pipe\MyNetworkTool`.
`action` is a string; property names are camelCase.

**Static IP**
```json
{ "action": "SetStaticIp", "interfaceId": "{GUID}", "ipAddress": "192.168.1.50",
  "prefixLength": 24, "gateway": "192.168.1.1", "dnsServers": ["1.1.1.1", "8.8.8.8"] }
```
**DHCP**
```json
{ "action": "SetDhcp", "interfaceId": "{GUID}" }
```
**Network profile** (only `Private` / `Public`)
```json
{ "action": "SetNetworkProfile", "interfaceId": "{GUID}", "category": "Private" }
```
**Proxy preset / reset** (machine scope)
```json
{ "action": "SetProxyPreset", "presetName": "Work" }
{ "action": "ResetProxy" }
```
**Read-only helpers used by the UI:** `Ping`, `ListAdapters`, `ListProxyPresets`, `GetProxyStatus`.

**Response**
```json
{ "success": true,  "message": "Static IP applied successfully." }
{ "success": false, "message": "Adapter not found: {GUID}" }
```
`ListAdapters` adds an `adapters` array; `ListProxyPresets` adds a `presets` array.
`GetProxyStatus` returns the current machine-scope `HTTP_PROXY`/`HTTPS_PROXY` values in `message`.

`interfaceId` is the **interface GUID** (`NetworkInterface.Id`) — a stable identifier. The service
resolves it to the integer interface index used by the `Net*` cmdlets, and rejects an unknown id.

### Proxy presets

Built-in: `Work` → `http://proxy.example.com:8080`, `Local` → `http://127.0.0.1:8080`.
Override/extend by dropping `C:\ProgramData\MyNetworkTool\presets.json`:
```json
[ { "name": "Corp", "httpProxy": "http://proxy.corp.local:3128", "httpsProxy": "http://proxy.corp.local:3128" } ]
```
`SetProxyPreset`/`ResetProxy` set/clear `HTTP_PROXY` and `HTTPS_PROXY` at **Machine** scope and
broadcast `WM_SETTINGCHANGE` so newly launched processes pick up the change.

---

## How the network changes are actually applied

The service constructs a fixed PowerShell command **itself** from already-validated values and runs
`powershell.exe` via `ProcessStartInfo` with `UseShellExecute = false`, a constant argument list
(`-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command <script>`), captured stdout/stderr,
a 60 s timeout, and exit-code checking. Cmdlets used: `New-NetIPAddress`,
`Set-DnsClientServerAddress`, `Set-NetIPInterface -Dhcp`, `Remove-NetIPAddress` / `Remove-NetRoute`,
`Set-NetConnectionProfile`.

The script text is built only from: the integer interface index, IP strings re-emitted from a parsed
`System.Net.IPAddress` (canonical form only), and the literal strings `Private`/`Public`. No client
string is ever embedded verbatim.

---

## Security model & notes

* **No arbitrary execution.** The only process the service ever starts is `powershell.exe` with a
  fixed argument list; the script is assembled from a closed set of validated values.
* **Defense against command injection (3 layers):** client IP/gateway/DNS values are (1) parsed with
  `IPAddress.TryParse`, (2) rejected if they contain an IPv6 zone/scope id (`%…`), (3) re-emitted in
  canonical form and additionally checked at the sink to contain only IP-literal characters
  (`[0-9A-Fa-f.:]`). On .NET 9, `IPAddress.TryParse` already rejects non-numeric zone ids, but these
  layers make injection impossible regardless of runtime/version or future callers.
* **Pipe ACL.** The pipe grants **Authenticated Users** read/write (so the unelevated tray can talk
  to it — the whole point) and Administrators/SYSTEM full control. ⚠️ This means **any logged-in
  local user** can request these network/proxy changes; there is no additional per-user
  authorization. That is acceptable for a single-user workstation utility but should be tightened
  (e.g. restrict the ACL to a specific group, or add a caller check) before multi-user deployment.

---

## Verified

* `dotnet build` — **0 warnings, 0 errors**.
* Single-file self-contained publish produces one working `MyNetworkTool.exe`.
* End-to-end IPC exercised on a real Windows 11 / .NET 9 machine: `Ping`, `ListProxyPresets`, and
  `ListAdapters` (enumerated 73 adapters with GUID / index / DHCP / status) round-trip correctly.
* Validation rejects malformed IPs, and crafted command-injection payloads are rejected with **no
  side effect** (a marker-file probe confirmed no injected statement executed).
* This draft passed a multi-agent adversarial review; the confirmed findings were fixed (see below).

> The **mutating** operations (`SetStaticIp`, `SetDhcp`, `SetNetworkProfile`, proxy set/reset) and
> `install`/`uninstall` change real machine state and require admin, so they were **not** executed
> against the live machine during development. Test them in a VM first.

---

## Known limitations (first draft)

* **Pipe authorization is coarse** — any Authenticated User can drive the service (see Security
  notes). No rate-limiting.
* **Static-IP changes are not transactional.** DHCP is disabled and the address/gateway are applied
  in one step; the DNS step runs separately and is reported independently, but there is no rollback
  if a step fails after an earlier one succeeded. Verify the result and re-apply or switch to DHCP
  if needed.
* **Proxy is Machine scope only.** The service is LocalSystem, so a per-user (`User`) target would
  write to the service account's profile, not the interactive user's; only `Machine` is implemented.
* **Auto-start uses `HKLM\...\Run`, not `HKCU`.** Intentional: under over-the-shoulder UAC the
  elevated installer's `HKCU` is the *admin's* hive, so an HKCU write would not auto-start the tray
  for the standard user. HKLM starts the tray (unelevated) for every interactive user at logon.
* **Adapter index resolution** relies on IPv4/IPv6 interface properties; a fully disabled adapter
  may report no index, in which case the operation is refused with a clear message.
* **`Set-NetConnectionProfile`** only works when the adapter has an active connection profile (i.e.
  it is connected).
* **Uninstall can't delete the running image** if you uninstall from the installed copy; remaining
  files are scheduled for delete-on-reboot and the dialog says so.
* **Single-file self-contained exe is large (~100 MB).** Use the framework-dependent publish for a
  small exe if the .NET 9 Desktop runtime is present on the target.
* **No code signing / custom icon.** SmartScreen may warn on first run; the tray uses the default
  application icon.
