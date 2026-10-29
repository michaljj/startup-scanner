# StartupScanner — Design

**Date:** 2026-07-30
**Status:** Approved, pending implementation plan

## Problem

Windows has no single place that lists everything configured to run at boot or logon. The
configuration is spread across registry keys, filesystem folders, the service database, the
Task Scheduler store, WMI subscriptions, and Group Policy script directories. Task Manager's
Startup tab shows a small subset. `msconfig` shows the same subset. Autoruns shows nearly
everything but is a read-heavy forensic tool with no notion of "what changed since I last
looked."

StartupScanner covers the real autostart surface, remembers what it saw last time, reports
what changed, and lets the user disable entries through whatever mechanism Windows itself
uses for that location.

## Goals

1. Enumerate ~60 autostart locations across 19 providers, including per-user hives for users
   who are not logged in.
2. Diff each scan against the previous one and report additions, removals, value changes, and
   — importantly — entries that were re-enabled by something other than this app.
3. Disable and re-enable entries, using the OS's native disable flag where one exists and a
   journaled backup-and-remove where none does.
4. Show every fact available about a selected entry, offline.
5. Never make the machine unbootable.

## Non-Goals

- No permanent deletion. The only user-facing actions are Disable and Enable. Backup-and-remove
  is an internal mechanism and is always reversible from the write journal.
- No editing of entry values.
- No network calls of any kind. No VirusTotal, no reputation lookups, no telemetry.
- No remote machines.
- No self-installation. The app does not register a service or scheduled task for itself; it
  runs only when launched.

## Platform Decisions

| Decision | Choice |
|---|---|
| Stack | C# .NET 8, WPF |
| Elevation | `requireAdministrator` in the app manifest — always elevated |
| Disable strategy | Native OS flag first, journaled backup-and-remove as fallback |
| Scan breadth | ~60 locations / 19 providers |
| Baseline store | JSON snapshot + append-only JSONL logs |
| Details depth | Full offline forensics, lazily computed |
| Guardrails | Tiered: hard-block boot-critical, risk-tiered confirmation for the rest |

Always-elevated was chosen over elevate-on-demand because it removes an entire class of
partial-failure and state-handoff code. The cost is a UAC prompt per launch, which is
acceptable for a tool that is run occasionally rather than continuously.

## Architecture

Three projects:

```
StartupScanner.Core/     net8.0-windows, no UI references
                         providers, models, diff engine, write engine, guardrails, stores
StartupScanner.App/      net8.0-windows, WPF, requireAdministrator manifest
StartupScanner.Tests/    xunit, tests Core only
```

Core carries no WPF reference. The entire scan, diff, and toggle engine is therefore testable
headless, and a CLI front end can be added later without restructuring.

### Provider Model

```csharp
interface IStartupProvider {
    string Id { get; }                                  // "registry.run", "task-scheduler"
    StartupCategory Category { get; }
    IEnumerable<StartupItem> Scan(ScanContext ctx);
}

interface IToggleProvider {                             // implemented only where writable
    ToggleCapability GetCapability(StartupItem item);   // Native | BackupRemove | Blocked
    void SetEnabled(StartupItem item, bool enabled, IWriteJournal journal);
}
```

Providers are registered in a list and run in parallel. A provider that throws is caught and
recorded; it never aborts the scan.

### The 19 Providers

| # | Provider | Coverage |
|---|---|---|
| 1 | `RegistryRunProvider` | Table-driven, ~35 keys: `Run`, `RunOnce`, `RunOnceEx`, `RunServices`, `RunServicesOnce`, `Policies\Explorer\Run` across HKCU/HKLM and native/`WOW6432Node` views |
| 2 | `StartupFolderProvider` | Per-user Startup, common Startup, and every profile's Startup folder; resolves `.lnk` via `IShellLink` |
| 3 | `ServiceProvider` | `HKLM\SYSTEM\CurrentControlSet\Services`, services and drivers separated by `Type` |
| 4 | `ScheduledTaskProvider` | Task Scheduler 2.0 COM, full recursive tree walk |
| 5 | `WinlogonProvider` | `Userinit`, `Shell`, `Taskman`, `AppSetup`, `GinaDLL`, `Notify\*` |
| 6 | `BootExecuteProvider` | `BootExecute`, `SetupExecute`, `PlatformExecute`, `S0InitialCommand` |
| 7 | `ActiveSetupProvider` | `Installed Components` `StubPath`, compared against the HKCU version stamp |
| 8 | `IfeoProvider` | Image File Execution Options `Debugger`, `VerifierDlls`, `GlobalFlag` |
| 9 | `AppInitProvider` | `AppInit_DLLs`, `LoadAppInit_DLLs`, `AppCertDlls` |
| 10 | `ExplorerHooksProvider` | `Load`, `Run`, `ShellExecuteHooks`, `SharedTaskScheduler`, `ShellServiceObjectDelayLoad` |
| 11 | `AppxStartupTaskProvider` | UWP/AppX `StartupTasks` and their per-user enable state |
| 12 | `WmiSubscriptionProvider` | `root\subscription`: `__EventFilter`, `__EventConsumer`, `__FilterToConsumerBinding` |
| 13 | `ComHijackProvider` | HKCU `CLSID` `InprocServer32`/`TreatAs` entries shadowing an HKLM registration |
| 14 | `LsaProvider` | `Authentication Packages`, `Notification Packages`, `Security Packages` |
| 15 | `NetshHelperProvider` | `HKLM\SOFTWARE\Microsoft\Netsh` |
| 16 | `PrintMonitorProvider` | `Control\Print\Monitors` and `\Providers` |
| 17 | `WinsockLspProvider` | `Protocol_Catalog9`, `NameSpace_Catalog5` |
| 18 | `GpoScriptProvider` | `GroupPolicy\{Machine,User}\Scripts\scripts.ini` plus the `State\...\Scripts` registry keys |
| 19 | `UserHiveProvider` | Wraps providers 1, 7, 10, 11, and 13. `RegLoadKey`s each profile's `NTUSER.DAT` so logged-out users are scanned, then unloads it |

Adding a location to provider 1 is one row in a static table, not new code.

Several items in the source Reddit post are views rather than stores and are covered
implicitly: the Task Manager Startup tab and `msconfig` are both views over providers 1 and 2
plus the `StartupApproved` state keys; `gpedit.msc`'s logon-script and script-policy nodes are
views over provider 18 and the policy registry keys already covered by provider 1.

## Data Model

```csharp
record StartupItem {
    string   Id;                // stable identity, see below
    string   ProviderId;
    string   Location;          // e.g. HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
    string   Name;              // value name, task name, or service name
    string   RawValue;          // exactly as stored
    StartupCategory Category;
    string?  ImagePath;         // parsed out of RawValue
    string?  Arguments;
    EnableState State;          // Enabled | Disabled | Unknown
    string   StateSource;       // "StartupApproved" | "Service.Start=4" | "Task.Enabled" | "RemovedByScanner"
    RiskTier Risk;
    string?  UserSid;           // whose context the entry runs in
    FileFacts? File;            // null until the details panel requests it
}
```

### Identity

```
Id = SHA256($"{ProviderId}|{UserSid}|{Location}|{Name}")
```

`RawValue` is deliberately excluded. A Run key whose command line changes therefore reports as
`Modified` rather than as a `Removed` plus an `Added`.

## Diff Engine

Four event kinds:

| Event | Rule |
|---|---|
| `Added` | Id present in the current scan, absent from the snapshot |
| `Removed` | Id present in the snapshot, absent from the current scan |
| `Modified` | Same Id, `RawValue` differs |
| `StateChanged` | Same Id, `State` differs |

`StateChanged` is the event most comparable tools omit. It catches an installer or updater
silently re-enabling an entry the user disabled previously. It is rendered in its own colour
in the UI.

## Storage

Location: `%ProgramData%\StartupScanner\`

```
snapshot.json    the last scan's items, excluding lazily-computed FileFacts
history.jsonl    append-only diff events: { ts, event, id, name, before, after }
writes.jsonl     append-only write journal: { ts, id, mechanism, before, after }
```

`writes.jsonl` is the single source of truth for both undo and for restoring backed-up values
on re-enable. It is loaded at startup into an in-memory last-write-wins dictionary keyed by
item Id. There is no separate backup file and no duplicated state.

### Directory ACL

`%ProgramData%` is writable by standard users by default. The app creates its own folder with
an explicit ACL: Administrators and SYSTEM get full control, Users get read-only. This matters
because `writes.jsonl` holds values that the elevated app will write back into HKLM when an
entry is re-enabled. Without the restrictive ACL, a non-administrator could seed a malicious
value into the journal and have the elevated process install it. On startup the app verifies
the ACL and repairs it if it has been loosened.

## Write Engine

`GetCapability` returns one of three values per item.

### Native

Used wherever Windows itself has a disable flag, so the app's changes stay consistent with
Task Manager and `services.msc`.

- **Run keys and Startup folders** → `StartupApproved\Run`, `\Run32`, `\StartupFolder` under
  the matching hive. The value is a 12-byte blob. The engine reads any existing blob, sets
  `byte[0]` to `0x02` when enabling or `0x03` when disabling, writes the current `FILETIME`
  into bytes 4–11 on disable and zeroes those bytes on enable, and preserves the remaining
  bytes. If no blob exists, a fresh 12-byte blob is created.
- **Services and drivers** → the `Start` DWORD, set to `4`. The prior value is journaled so
  re-enable restores `2` versus `3` exactly rather than guessing.
- **Scheduled tasks** → `IRegisteredTask.Enabled`.

### BackupRemove

Used where Windows has no disable flag: Winlogon values, BootExecute, Active Setup, IFEO,
AppInit/AppCert, Explorer hooks, netsh helpers, print monitors, Winsock LSPs, COM hijacks, GPO
scripts, and WMI subscriptions. The engine journals the current value, then deletes or blanks
it. Re-enable rewrites the value from the journal.

### Blocked

Shown and fully detailed, but never writable. The panel displays the reason.

- `Winlogon\Userinit` when it resolves to `%systemroot%\system32\userinit.exe`
- `Winlogon\Shell` when it is `explorer.exe`
- `BootExecute` when it is `autocheck autochk *`
- Any service or driver with `Start` of `0` or `1`, plus a named critical-service list
  (`DcomLaunch`, `RpcSs`, `RpcEptMapper`, `PlugPlay`, `Power`, `ProfSvc`, `LSM`, `BrokerInfrastructure`,
  `SamSs`, `EventLog`)
- All LSA package entries, as a category. Removing an authentication package can prevent
  logon, and legitimate additions are rare enough that manual editing is the safer path.

### Risk Tiers

Risk tier controls the confirmation dialog. Blocked status is decided separately by the
blocked list above; `Critical` is simply the tier assigned to items that list already blocks,
so no dialog is ever reached for them.

| Tier | Members | Behaviour |
|---|---|---|
| Low | Run keys, Startup folders, AppX startup tasks | Toggles immediately, no prompt |
| Medium | Services, tasks under `\Microsoft\Windows`, Active Setup, GPO scripts | Plain confirmation |
| High | Winlogon values, BootExecute, IFEO `Debugger`, `AppInit_DLLs`, Winsock LSP | Confirmation naming the concrete consequence |
| Critical | The blocked list above | Not writable |

### Restore Point

Before the first write of each session the app calls `SystemRestore.CreateRestorePoint` once.
If System Protection is disabled the call fails; the app reports this and asks whether to
continue without protection rather than proceeding silently.

### Write Verification

Every write is read back and compared against the intended value before the journal entry is
appended. A mismatch triggers a rollback and an error report. The journal therefore never
records a write that did not land, so undo cannot corrupt state.

### Undo

Reverse-application of `writes.jsonl`. Two commands: undo the last change, and undo everything
from the current session.

## User Interface

```
┌ Rescan │ 🔍filter │ [Run][Services][Tasks][Winlogon][…] │ ☑Changes only ☐Hide MS-signed │ Undo ▾ │ Export ┐
├──────────────────────────────────────────────┬───────────────────────────────┤
│ ▾ Run keys (24)                              │  DETAILS                      │
│  ☑ 🟢 OneDrive      Microsoft   HKCU\…\Run   │  Identity                     │
│  ☐ 🟡 Spotify       Spotify     HKCU\…\Run   │  File                         │
│  ☑ 🔴 svhost        —unsigned—  HKLM\…\Run ⊕ │  Signature                    │
│ ▾ Services (118)                             │  Location & state             │
│ ▾ Scheduled tasks (203)                      │  Type-specific                │
│ …                                            │  History                      │
├──────────────────────────────────────────────┴───────────────────────────────┤
│ 412 items · 19 providers · ⊕3 added ⊖1 removed ✎1 modified ↻1 re-enabled since 2026-07-28 09:12 │
└──────────────────────────────────────────────────────────────────────────────┘
```

The checkbox is the enabled state and writes immediately, after any risk confirmation. The `⊕`
badge marks an entry that is new since the previous run. Blocked entries show a lock glyph in
place of the checkbox. Export writes the currently filtered rows to a file, with the user
choosing CSV or JSON in the save dialog.

### Details Panel Sections

- **Identity** — name, provider, category, raw value verbatim, parsed image path, arguments,
  environment variables expanded, running user and SID, first-seen date from `history.jsonl`.
- **File** — exists, SHA-256, size, created and modified timestamps, PE version block (Company,
  Product, FileDescription, FileVersion), ACL owner.
- **Signature** — signed status, signer chain, countersignature timestamp, embedded versus
  catalog signature, Microsoft-signed flag.
- **Location and state** — exact key or file path, current state, which mechanism controls the
  state, whether the entry is blocked and why.
- **Type-specific** — services show start type, logon account, dependencies, description, and
  current running state; tasks show the full trigger list, action list, principal and run
  level, last run time, last result code, and next run time; startup folder entries show the
  `.lnk` target, arguments, working directory, and icon; WMI subscriptions show the filter's
  WQL text and the consumer type.
- **History** — every event recorded for this Id in `history.jsonl` and `writes.jsonl`.

## Scan Performance

Two phases.

**Phase 1** enumerates all 19 providers in parallel using registry, filesystem, and COM reads
only. Target is under 2 seconds for a typical 400–800 item machine. The list renders as soon
as phase 1 completes.

**Phase 2** enriches in the background: PE version info, SHA-256, and Authenticode
verification, computed in parallel with a progress indicator. The Publisher column fills in as
results arrive. `WinVerifyTrust` is called with revocation network retrieval disabled —
otherwise CRL and OCSP fetches stall enrichment for tens of seconds on a slow or filtered
network. Results are cached in memory keyed by `(path, size, mtime)`.

## Error Handling

- Provider isolation: an exception in one provider is caught, recorded as a `ScanError`, and
  the remaining providers complete. Failures surface as a dismissible banner naming the failed
  providers.
- Access denied on an individual entry, even when elevated — TrustedInstaller-owned keys and
  protected tasks are the common cases — marks that item partially-read rather than aborting
  the provider.
- A profile hive that cannot be loaded is skipped, and the affected user is named in the
  banner. Hives are always unloaded in a `finally` block.
- Write failures roll back and report, per the write-verification rule above.

## Testing

Core depends on `IRegistry`, `IFileSystem`, and `ITaskScheduler` abstractions and is tested
against in-memory fakes. Development follows TDD.

- `StartupApproved` blob encode and decode, round-tripped, including real captured blobs from
  both enabled and disabled entries.
- Diff engine: all four event kinds, plus Id stability when only `RawValue` changes.
- Guardrails: each blocked rule asserted to block.
- Undo: apply then reverse, asserting the resulting state equals the original.
- Journal integrity: a failed write leaves no journal entry.
- An opt-in integration suite, trait-marked and requiring elevation, that writes a real value
  under a scratch `Run` key, scans it, disables it, asserts the actual `StartupApproved` blob
  bytes, undoes, and asserts the key is clean.

## Open Risks

- `RegLoadKey` on a profile hive fails if that user is logged on, since the hive is already
  mounted under `HKU\<SID>`. Provider 19 must check `HKU` first and only load from disk when
  the SID is absent.
- AppX startup task state is partly stored in a package-private location that changes between
  Windows releases. Provider 11 reads what is present and degrades to read-only if the
  expected state value is missing rather than guessing at a format.
- Some third-party software rewrites its own Run key on every launch. Such entries will appear
  as recurring `StateChanged` events; this is correct reporting, not a bug, and the details
  panel's history makes the pattern visible.
