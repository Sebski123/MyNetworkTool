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

<img width="562" height="799" alt="image" src="https://github.com/user-attachments/assets/1eb4b28d-b0e6-44ea-8a9b-6f0ebf9a390f" />


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

## Testing updates without GitHub

You can force the normal update flow to use a local published `.exe` instead of checking GitHub.

1. Build the **currently installed / older** version and install it normally.
2. Publish the **newer / candidate** build to any local path, for example:
  ```powershell
  dotnet publish NetworkingTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-test
  ```
3. In the shell where you launch the old build, set these optional environment variables:
  ```powershell
  $env:MYNETWORKTOOL_UPDATE_PATH = "C:\path\to\publish-test\MyNetworkTool.exe"
  $env:MYNETWORKTOOL_UPDATE_VERSION = "local-test"
  ```
4. Start the old build with no arguments.

When `MYNETWORKTOOL_UPDATE_PATH` is set, the app skips the GitHub release check and offers that local file as the update package. The rest of the flow stays the same: prompt, copy to a temp update folder, run `install`, replace the installed exe, restart the tray.

Notes:

* `MYNETWORKTOOL_UPDATE_PATH` can be absolute or relative to the current working directory.
* `MYNETWORKTOOL_UPDATE_VERSION` is optional; if omitted, the file version of the local `.exe` is shown when available.
* Remove the environment variable when you want to go back to real GitHub-based update checks.

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
Override/extend with `C:\ProgramData\MyNetworkTool\presets.json`. Because that directory is locked to
SYSTEM/Administrators, the file must be created by an administrator (the service ignores it unless it
is owned by SYSTEM or Administrators):
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
* **Pipe ACL (local interactive only).** The pipe grants the **INTERACTIVE** logon group (`S-1-5-4`)
  read/write — present in a console or RDP session token but **not** in a network (SMB) logon token —
  plus Administrators/SYSTEM full control. A caller reaching the pipe remotely over
  `\\host\pipe\MyNetworkTool` is therefore denied; the privileged interface is local-only. ⚠️ Any
  user *interactively* logged on can still request these network/proxy changes (no per-user
  authorization) — acceptable for a single-user workstation, but tighten the ACL to a specific group
  before multi-user deployment.
* **Hardened data directory.** The installer replaces the inherited `C:\ProgramData` ACL on
  `C:\ProgramData\MyNetworkTool` with an explicit one (SYSTEM/Administrators write, Users read-only,
  inheritance off). This stops a standard user from planting a `presets.json` the LocalSystem service
  would otherwise act on (e.g. pointing the machine-wide proxy at an attacker host). As defense in
  depth, the service also **ignores `presets.json` unless it is owned by SYSTEM or Administrators**.
* **Signed updates only.** The service-driven updater verifies that a **downloaded** release is
  Authenticode-signed by a pinned certificate (`Constants.ExpectedCodeSigningThumbprint`) before it
  replaces the SYSTEM service binary. The check is **fail-closed**: while the thumbprint is empty,
  every downloaded update is refused. See *Signing & updates* below.
* **Bounded privileged operations.** Update downloads have a hard 5-minute timeout, and only one
  update install runs at a time (concurrent requests are rejected immediately), so a slow or repeated
  update request cannot tie up every pipe-server slot.

---

## Signing & updates

The LocalSystem service-driven updater (and the legacy fallback) refuse to install any **downloaded**
binary unless it is Authenticode-signed by the certificate whose SHA-1 thumbprint is pinned in
[`Constants.ExpectedCodeSigningThumbprint`](Shared/Constants.cs). This pins the publisher to **your
own key** — a publicly-trusted CA is not required, so a free self-signed certificate works. The check
is fail-closed: while the constant is empty, downloaded updates are rejected.

To enable updates, create a (long-lived) code-signing certificate, pin its thumbprint, and sign each
release:

```powershell
# 1. Create a self-signed code-signing cert (a long lifetime avoids breakage on expiry).
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=MyNetworkTool" `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(20) -KeyExportPolicy Exportable

# 2. Print the thumbprint to paste into Constants.ExpectedCodeSigningThumbprint (spaces/case ignored).
$cert.Thumbprint

# 3. Back up the private key (losing it means you must re-pin a new thumbprint).
$pwd = ConvertTo-SecureString "choose-a-password" -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath MyNetworkTool-signing.pfx -Password $pwd
```

Then sign the published exe on every release before uploading it to the GitHub release:

```powershell
signtool sign /fd SHA256 /a /n "MyNetworkTool" publish\MyNetworkTool.exe
```

The verifier pins the certificate by thumbprint and uses `WinVerifyTrust` only to confirm the file's
bytes still match the signature (tamper detection); because the self-signed root is intentionally not
in the trusted store, an "untrusted root" result is accepted while a bad digest, missing signature, or
wrong signer is rejected. The local test-override path (`MYNETWORKTOOL_UPDATE_PATH`, settable only by
an administrator) is exempt so unsigned local builds can still be tested.

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

* **Pipe authorization is coarse** — any *interactively* logged-on user can drive the service (see
  Security notes); access is restricted to local interactive logons but not to a specific group. No
  general per-request rate-limiting (only the privileged update is single-flighted).
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
* **Self-update requires signing.** The in-app/service updater is fail-closed until you configure a
  code-signing certificate (see *Signing & updates*). Until then it refuses downloaded updates — the
  manual build-and-install flow is unaffected. A self-signed certificate is not trusted by SmartScreen,
  so a warning may still appear on first run.
