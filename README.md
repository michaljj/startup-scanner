# StartupScanner

A Windows autostart and persistence scanner. It enumerates the places Windows can
silently launch code, explains what each entry actually is, and lets you disable
the suspicious ones with guardrails rather than a bare "delete" button.

Most "startup manager" tools cover the Run keys and the Startup folder and stop
there. Real persistence lives in about twenty more places — WMI subscriptions,
netsh helper DLLs, print monitors, LSA packages, COM hijacks, IFEO debuggers.
StartupScanner enumerates those too, and treats writing to them as the dangerous
operation it is.

> Built for Windows 10/11. Requires .NET 8 SDK to build.

---

## What it covers

| Provider | Surface |
|---|---|
| `RegistryRun` | `Run` / `RunOnce` keys, HKLM + HKCU + per-user hives |
| `RunKeyTable` | Policy-restricted run keys |
| `StartupFolder` | Per-user and all-users Startup folders |
| `ScheduledTask` | Task Scheduler jobs with logon/boot triggers |
| `Service` | Services with automatic start |
| `Winlogon` | `Shell`, `Userinit`, and friends |
| `Ifeo` | Image File Execution Options debuggers (classic hijack) |
| `AppInit` | AppInit_DLLs injection |
| `AppxStartupTask` | UWP/AppX startup tasks |
| `ActiveSetup` | Active Setup stubs replayed per user |
| `ComHijack` | COM CLSID hijacks via `ClsidResolver` |
| `ExplorerHooks` | Explorer shell extensions and hooks |
| `BootExecute` | Native boot-time execution (`smss`) |
| `GpoScript` | Group Policy logon/startup scripts |
| `WmiSubscription` | Permanent WMI event consumers |
| `NetshHelper` | netsh helper DLLs |
| `WinsockLsp` | Winsock layered service providers |
| `PrintMonitor` | Print monitor DLLs |
| `Lsa` | LSA authentication packages |
| `UserHive` | Replays per-user providers across loaded profiles |

---

## Safety model

Writing to autostart locations is where these tools usually go wrong, so writes are
gated rather than direct:

- **Risk tiers** (`RiskTiers`) classify each item by blast radius; risky changes
  require an explicit typed confirmation (`RiskConfirmationDialog`).
- **Restore point gate** (`RestorePointGate`) creates a System Restore point before
  a batch of risky writes.
- **Undo** (`UndoService`) records every write so a change can be reverted.
- **Write verification** (`WriteVerifier`) re-reads the location afterwards and
  fails loudly if the change did not stick.
- **Blocked toggles** (`BlockedToggle`) refuse to touch locations that cannot be
  modified safely, instead of attempting and hoping.

Every write produces a `WriteRecord`, so the tool can tell you what it changed and
why.

---

## Architecture

```
StartupScanner.Core   scanning, diffing, enrichment, writes — no UI dependency
StartupScanner.App    WPF (MVVM) shell: main grid, details panel, risk dialog
StartupScanner.Tests  unit + integration tests, with fakes for every abstraction
```

`Core` depends only on interfaces (`IRegistry`, `IFileSystem`, `IWmi`,
`ITaskSchedulerService`, `ISystemRestore`, `IClock`, `IProfileEnumerator`,
`IUserHiveLoader`, `IShellLinkResolver`, `IEnrichmentSources`). That is what makes
the provider logic unit-testable on a machine without those surfaces, and what lets
the integration tests run a real scan through the same composition
(`ScannerHost`) the UI uses.

Two behaviours worth calling out:

- **Diffing** (`DiffEngine`) compares scans over time and emits `Added` /
  `Removed` / `Modified` / `StateChanged` events, so you can watch for new
  persistence between sessions rather than eyeballing a list.
- **Enrichment** (`FileFactsService`) resolves each entry's command line to a real
  image path — including unquoted paths with spaces — then attaches signature and
  file facts, so "unknown binary in `%TEMP%`" is visible at a glance.

---

## Build and run

```powershell
dotnet build StartupScanner.sln
dotnet run --project StartupScanner.App
```

Scanning broadly touches HKLM and other users' hives, so run elevated for full
coverage. Without elevation the scanner reports what it could not read rather than
silently returning a short list.

## Tests

```powershell
dotnet test StartupScanner.sln
```

The suite includes provider-level tests against fakes plus integration tests that
exercise a real scan through `ScannerHost`.

---

## License

Licensed under the [GNU Affero General Public License v3.0](LICENSE.md). You may
use, modify and redistribute it freely; if you run a modified version as a network
service you must offer its source to users. No warranty.

Use it on systems you own or are authorised to audit.
