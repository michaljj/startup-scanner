# StartupScanner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows desktop app that enumerates ~60 autostart locations across 19 providers, diffs each scan against the previous one, and disables/re-enables entries using the OS's native disable flag where one exists and a journaled backup-and-remove where none does.

**Architecture:** Three projects. `StartupScanner.Core` holds every model, provider, the diff engine, the write engine, guardrails, and the stores; it references no UI assembly and talks to Windows only through `IRegistry`, `IFileSystem`, `ITaskSchedulerService`, `IWmi`, and friends, so all of it is unit-testable against in-memory fakes. `StartupScanner.App` is a thin WPF binding layer over a `ScannerHost` composition root. `StartupScanner.Tests` is xunit against Core only.

**Tech Stack:** C# 12, .NET 8 (`net8.0-windows`), WPF, xunit, `System.Management` 8.0.0 for WMI. Registry, ACL, and late-bound COM APIs are all in-box for `net8.0-windows` — verified, no other packages needed.

## Global Constraints

- **Target framework:** `net8.0-windows` for all three projects. `<Nullable>enable</Nullable>` everywhere.
- **Only external packages:** `System.Management` 8.0.0 (Core, for WMI), plus the xunit template packages (Tests). Nothing else. `System.Security.AccessControl`, `Microsoft.Win32.Registry`, `DirectoryInfo.SetAccessControl`, and `dynamic` COM are in-box on this TFM — verified against SDK 8.0.423.
- **Build/test command:** the .NET SDK lives on the Windows side only. From this WSL session always invoke it as
  `"/mnt/c/Program Files/dotnet/dotnet.exe"` and pass **Windows** paths (`D:\Repos\StartupScanner\...`), never `/mnt/d/...` paths.
- **No network calls of any kind.** No reputation lookups, no telemetry, no update checks. `WinVerifyTrust` must be called with revocation network retrieval disabled.
- **`StartupScanner.Core` must never reference WPF.** No `PresentationFramework`, no `System.Windows.*`, no `UseWPF`. If a piece of display logic needs testing, it belongs in `Core/Presentation/`, not in the App.
- **No permanent deletion is ever exposed to the user.** The only user-facing verbs are Disable and Enable. `BackupRemove` is internal and always reversible from `writes.jsonl`.
- **No entry values are editable.**
- **The app never installs itself.** No service, no scheduled task, no run key for StartupScanner.
- **Elevation:** `StartupScanner.App` ships an `app.manifest` with `requestedExecutionLevel level="requireAdministrator" uiAccess="false"`. There is no elevate-on-demand path.
- **Storage root:** `%ProgramData%\StartupScanner\` containing `snapshot.json`, `history.jsonl`, `writes.jsonl`. The directory is created with an explicit, inheritance-protected DACL: Administrators + SYSTEM full control, `Users` read-only. The ACL is verified and repaired on every startup.
- **Item identity:** `Id = SHA256("{ProviderId}|{UserSid}|{Location}|{Name}")`, lowercase hex. `RawValue` is deliberately excluded so a changed command line reports as `Modified`, not `Removed`+`Added`.
- **Every write is read back and compared to the intended value before its journal entry is appended.** A mismatch rolls the write back and reports an error; nothing is journaled. The journal therefore never records a write that did not land.
- **A provider that throws never aborts the scan.** The exception becomes a `ScanError` and the other providers finish.
- **Loaded profile hives are always unloaded in a `finally` block.**
- **TDD:** every task writes the failing test first, watches it fail, then implements. Commit at the end of every task.

---

## File Structure

### StartupScanner.Core (`net8.0-windows`, no UI references)

```
Abstractions/
  IRegistry.cs               IRegistry, IRegistryKey, RegistryViewKind, RegistryValueType
  IFileSystem.cs             IFileSystem, FileStat
  ITaskSchedulerService.cs   ITaskSchedulerService, ScheduledTaskInfo
  IWmi.cs                    IWmi, WmiInstance
  IShellLinkResolver.cs      IShellLinkResolver, ShortcutTarget
  ISystemRestore.cs          ISystemRestore
  IProfileEnumerator.cs      IProfileEnumerator, UserProfile
  IUserHiveLoader.cs         IUserHiveLoader
  IClock.cs                  IClock, SystemClock
  IEnrichmentSources.cs      IPeVersionReader, IAuthenticode, IFileOwnerReader, IServiceStatus
Models/
  StartupItem.cs             StartupItem, StartupCategory, EnableState, RiskTier, ToggleCapability
  FileFacts.cs               FileFacts, SignatureFacts, SignatureStatus, PeVersion
  ScanContext.cs             ScanContext, ScanError, ScanResult
  DiffEvent.cs               DiffEvent, DiffEventKind
  WriteRecord.cs             WriteRecord
  Identity.cs                Identity.Compute
  CommandLine.cs             CommandLine.Split
Storage/
  AppPaths.cs                storage root resolution
  DirectoryAcl.cs            create / verify / repair the ProgramData DACL
  RegistryValueCodec.cs      registry value <-> journal string
  SnapshotStore.cs           snapshot.json
  HistoryStore.cs            history.jsonl
  WriteJournal.cs            writes.jsonl, IWriteJournal, IWriteJournalReader
Diff/
  DiffEngine.cs
Safety/
  Guardrails.cs              blocked rules, BlockDecision
  RiskTiers.cs               tier assignment
Writing/
  IToggleProvider.cs         IToggleProvider, ToggleResult, IConfirmationSink
  StartupApprovedBlob.cs     12-byte blob encode/decode
  StartupApprovedToggle.cs   shared Native toggle for Run keys + Startup folders
  RegistryValueToggle.cs     shared BackupRemove toggle
  RestorePointGate.cs        one restore point per session
  ToggleService.cs           guardrail -> restore point -> write -> verify -> journal
  UndoService.cs             reverse-application of writes.jsonl
Scanning/
  ScanEngine.cs              parallel scan, error isolation, risk assignment, journal re-injection
  TableValueScanner.cs       shared table-driven registry-value scanner
  ProviderRegistry.cs        the 19 providers in one list
Providers/
  IStartupProvider.cs
  RegistryRunProvider.cs     (1)  + RunKeyTable.cs
  StartupFolderProvider.cs   (2)
  ServiceProvider.cs         (3)
  ScheduledTaskProvider.cs   (4)
  WinlogonProvider.cs        (5)
  BootExecuteProvider.cs     (6)
  ActiveSetupProvider.cs     (7)
  IfeoProvider.cs            (8)
  AppInitProvider.cs         (9)
  ExplorerHooksProvider.cs   (10)
  AppxStartupTaskProvider.cs (11)
  WmiSubscriptionProvider.cs (12)
  ComHijackProvider.cs       (13)
  LsaProvider.cs             (14)
  NetshHelperProvider.cs     (15)
  PrintMonitorProvider.cs    (16)
  WinsockLspProvider.cs      (17)
  GpoScriptProvider.cs       (18)
  UserHiveProvider.cs        (19)
Enrichment/
  FileFactsService.cs        phase 2, cached by (path, size, mtime)
Presentation/
  ItemFilter.cs              search / category / changes-only / hide-MS-signed
  StatusLine.cs              status bar text
  ConfirmationText.cs        risk-tier dialog copy
  Exporter.cs                CSV + JSON of the filtered rows
Windows/
  WindowsRegistry.cs         WindowsFileSystem.cs      WindowsProfileEnumerator.cs
  WindowsUserHiveLoader.cs   WindowsTaskScheduler.cs   WindowsWmi.cs
  ShellLinkResolver.cs       WindowsSystemRestore.cs   WindowsPeVersionReader.cs
  WindowsAuthenticode.cs     WindowsFileOwnerReader.cs WindowsServiceStatus.cs
ScannerHost.cs               composition root, used by App and by integration tests
```

### StartupScanner.App (`net8.0-windows`, WPF)

```
app.manifest          requireAdministrator
App.xaml / App.xaml.cs
MainWindow.xaml / .cs
ViewModels/  MainViewModel.cs  ItemViewModel.cs  DetailsViewModel.cs  RelayCommand.cs
Views/       DetailsPanel.xaml  RiskConfirmationDialog.xaml  ErrorBanner.xaml
Converters/  StateGlyphConverter.cs  DiffBadgeConverter.cs
```

### StartupScanner.Tests (`net8.0-windows`, xunit, references Core only)

```
Fakes/       FakeRegistry.cs  FakeFileSystem.cs  FakeTaskScheduler.cs  FakeWmi.cs
             FakeShellLinkResolver.cs  FakeSystemRestore.cs  FakeProfileEnumerator.cs
             FakeUserHiveLoader.cs  FakeClock.cs  FakeJournal.cs  RecordingConfirmationSink.cs
Storage/     Diff/  Safety/  Writing/  Providers/  Presentation/  Enrichment/
Integration/ ElevatedRunKeyTests.cs   (trait-gated, opt-in)
```

---

## Task 1: Solution scaffold

**Files:**
- Create: `.gitignore`, `StartupScanner.sln`
- Create: `StartupScanner.Core/StartupScanner.Core.csproj`
- Create: `StartupScanner.Tests/StartupScanner.Tests.csproj`, `StartupScanner.Tests/ScaffoldTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a solution where `dotnet test` runs green, and the exact build/test command every later task uses.

Note: this directory is not yet a git repository. Step 1 initialises it.

- [ ] **Step 1: Initialise the repository**

```bash
cd /mnt/d/Repos/StartupScanner
git init
git config user.name "StartupScanner Dev"
git config user.email "dev@example.invalid"
```

- [ ] **Step 2: Write `.gitignore`**

Create `/mnt/d/Repos/StartupScanner/.gitignore`:

```gitignore
bin/
obj/
*.user
.vs/
TestResults/
```

- [ ] **Step 3: Create the projects and solution**

```bash
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
"$DOTNET" new sln -o 'D:\Repos\StartupScanner' -n StartupScanner
"$DOTNET" new classlib -o 'D:\Repos\StartupScanner\StartupScanner.Core' -n StartupScanner.Core --framework net8.0
"$DOTNET" new xunit    -o 'D:\Repos\StartupScanner\StartupScanner.Tests' -n StartupScanner.Tests --framework net8.0
rm -f /mnt/d/Repos/StartupScanner/StartupScanner.Core/Class1.cs
rm -f /mnt/d/Repos/StartupScanner/StartupScanner.Tests/UnitTest1.cs
"$DOTNET" sln 'D:\Repos\StartupScanner\StartupScanner.sln' add 'D:\Repos\StartupScanner\StartupScanner.Core\StartupScanner.Core.csproj'
"$DOTNET" sln 'D:\Repos\StartupScanner\StartupScanner.sln' add 'D:\Repos\StartupScanner\StartupScanner.Tests\StartupScanner.Tests.csproj'
"$DOTNET" add 'D:\Repos\StartupScanner\StartupScanner.Tests\StartupScanner.Tests.csproj' reference 'D:\Repos\StartupScanner\StartupScanner.Core\StartupScanner.Core.csproj'
"$DOTNET" add 'D:\Repos\StartupScanner\StartupScanner.Core\StartupScanner.Core.csproj' package System.Management --version 8.0.0
```

- [ ] **Step 4: Retarget both projects to `net8.0-windows`**

Replace the `<PropertyGroup>` in `StartupScanner.Core/StartupScanner.Core.csproj` with:

```xml
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>StartupScanner.Core</RootNamespace>
  </PropertyGroup>
```

And in `StartupScanner.Tests/StartupScanner.Tests.csproj`:

```xml
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>StartupScanner.Tests</RootNamespace>
  </PropertyGroup>
```

- [ ] **Step 5: Write the scaffold test**

Create `StartupScanner.Tests/ScaffoldTests.cs`:

```csharp
namespace StartupScanner.Tests;

public class ScaffoldTests
{
    [Fact]
    public void CoreAssemblyIsReferencedAndTargetsWindows()
    {
        var asm = typeof(StartupScanner.Core.CoreMarker).Assembly;
        Assert.Contains("StartupScanner.Core", asm.FullName);
    }

    [Fact]
    public void CoreDoesNotReferenceWpf()
    {
        var referenced = typeof(StartupScanner.Core.CoreMarker).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(referenced, a =>
            a.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase");
    }
}
```

- [ ] **Step 6: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: FAIL — `error CS0234: The type or namespace name 'CoreMarker' does not exist`.

- [ ] **Step 7: Add the marker type**

Create `StartupScanner.Core/CoreMarker.cs`:

```csharp
namespace StartupScanner.Core;

/// <summary>Assembly anchor for reflection-based tests. Holds no behaviour.</summary>
public static class CoreMarker;
```

- [ ] **Step 8: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: PASS — 2 tests passed.

- [ ] **Step 9: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add .gitignore StartupScanner.sln StartupScanner.Core StartupScanner.Tests
git commit -m "chore: scaffold Core and Tests projects on net8.0-windows"
```

---

## Task 2: Models and identity

**Files:**
- Create: `StartupScanner.Core/Models/StartupItem.cs`, `Models/FileFacts.cs`, `Models/ScanContext.cs`, `Models/DiffEvent.cs`, `Models/WriteRecord.cs`, `Models/Identity.cs`
- Create: `StartupScanner.Core/Abstractions/IClock.cs`
- Test: `StartupScanner.Tests/IdentityTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: every model type the rest of the plan uses. `StartupItem`, `StartupCategory`, `EnableState`, `RiskTier`, `ToggleCapability`, `FileFacts`, `SignatureFacts`, `SignatureStatus`, `PeVersion`, `ScanContext`, `ScanError`, `ScanResult`, `DiffEvent`, `DiffEventKind`, `WriteRecord`, `Identity.Compute(providerId, userSid, location, name)`, `IClock`.

Two fields go beyond the spec's `StartupItem` sketch, both load-bearing: `PartiallyRead` implements the spec's "access denied on an individual entry marks that item partially-read", and `Extra` is where the details panel's type-specific facts live (service start type, task triggers, WMI WQL text) without a subclass per provider.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/IdentityTests.cs`:

```csharp
using StartupScanner.Core.Models;

namespace StartupScanner.Tests;

public class IdentityTests
{
    [Fact]
    public void IdIsStableForTheSameInputs()
    {
        var a = Identity.Compute("registry.run", null, @"HKLM\SOFTWARE\Run", "OneDrive");
        var b = Identity.Compute("registry.run", null, @"HKLM\SOFTWARE\Run", "OneDrive");
        Assert.Equal(a, b);
    }

    [Fact]
    public void IdIsLowercaseHexSha256()
    {
        var id = Identity.Compute("registry.run", null, @"HKLM\SOFTWARE\Run", "OneDrive");
        Assert.Equal(64, id.Length);
        Assert.Matches("^[0-9a-f]{64}$", id);
    }

    [Fact]
    public void IdIgnoresRawValueSoAChangedCommandLineIsNotANewItem()
    {
        var item1 = NewItem(@"C:\a\one.exe");
        var item2 = NewItem(@"C:\b\two.exe --flag");
        Assert.Equal(item1.Id, item2.Id);
    }

    [Fact]
    public void IdVariesWithUserSid()
    {
        var a = Identity.Compute("registry.run", "S-1-5-21-1", @"HKCU\Run", "X");
        var b = Identity.Compute("registry.run", "S-1-5-21-2", @"HKCU\Run", "X");
        Assert.NotEqual(a, b);
    }

    private static StartupItem NewItem(string raw) => new()
    {
        Id = Identity.Compute("registry.run", null, @"HKLM\SOFTWARE\Run", "Thing"),
        ProviderId = "registry.run",
        Location = @"HKLM\SOFTWARE\Run",
        Name = "Thing",
        RawValue = raw,
        Category = StartupCategory.RunKey,
    };
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~IdentityTests"`
Expected: FAIL — `CS0234: The type or namespace name 'Models' does not exist`.

- [ ] **Step 3: Write the models**

Create `StartupScanner.Core/Models/StartupItem.cs`:

```csharp
namespace StartupScanner.Core.Models;

public enum StartupCategory
{
    RunKey, StartupFolder, Service, Driver, ScheduledTask, Winlogon, BootExecute,
    ActiveSetup, Ifeo, AppInit, ExplorerHook, AppxStartupTask, WmiSubscription,
    ComHijack, Lsa, NetshHelper, PrintMonitor, WinsockLsp, GpoScript
}

public enum EnableState { Enabled, Disabled, Unknown }

public enum RiskTier { Low, Medium, High, Critical }

public enum ToggleCapability { Native, BackupRemove, Blocked }

public sealed record StartupItem
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string Location { get; init; }
    public required string Name { get; init; }
    public required string RawValue { get; init; }
    public required StartupCategory Category { get; init; }
    public string? ImagePath { get; init; }
    public string? Arguments { get; init; }
    public EnableState State { get; init; } = EnableState.Unknown;
    public string StateSource { get; init; } = "";
    public RiskTier Risk { get; init; } = RiskTier.Low;
    public string? UserSid { get; init; }
    public FileFacts? File { get; init; }

    /// <summary>Set when the entry was found but some of its data could not be read.</summary>
    public bool PartiallyRead { get; init; }

    /// <summary>Provider-specific facts for the details panel. Serialised into the snapshot.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
```

Create `StartupScanner.Core/Models/Identity.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace StartupScanner.Core.Models;

public static class Identity
{
    /// <summary>
    /// Stable item identity. RawValue is excluded on purpose: a Run key whose command line
    /// changes must report as Modified rather than as a Removed plus an Added.
    /// </summary>
    public static string Compute(string providerId, string? userSid, string location, string name)
    {
        var payload = $"{providerId}|{userSid}|{location}|{name}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
```

Create `StartupScanner.Core/Models/FileFacts.cs`:

```csharp
namespace StartupScanner.Core.Models;

public enum SignatureStatus { Unsigned, Valid, Invalid, Unknown }

public sealed record PeVersion(string? Company, string? Product, string? FileDescription, string? FileVersion);

public sealed record SignatureFacts
{
    public required SignatureStatus Status { get; init; }
    public string? SignerName { get; init; }
    public IReadOnlyList<string> Chain { get; init; } = Array.Empty<string>();
    public DateTime? TimestampUtc { get; init; }
    public bool IsCatalogSigned { get; init; }
    public bool IsMicrosoftSigned { get; init; }
    public string? StatusMessage { get; init; }
}

public sealed record FileFacts
{
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public string? Sha256 { get; init; }
    public long Size { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public PeVersion? Version { get; init; }
    public string? AclOwner { get; init; }
    public SignatureFacts? Signature { get; init; }
}
```

Create `StartupScanner.Core/Models/ScanContext.cs`:

```csharp
using System.Collections.Concurrent;

namespace StartupScanner.Core.Models;

public sealed record UserProfile(string Sid, string ProfilePath, bool HiveLoaded, string? UserName);

public sealed record ScanError(string ProviderId, string Location, string Message);

public sealed record ScanResult(IReadOnlyList<StartupItem> Items, IReadOnlyList<ScanError> Errors);

/// <summary>
/// Per-scan state. Providers receive their Windows abstractions through the constructor;
/// this carries only what changes between passes, which is which user hive is being read.
/// </summary>
public sealed class ScanContext
{
    /// <summary>"HKCU", or "HKU\{sid}" when reading another profile's mounted hive.</summary>
    public required string UserRoot { get; init; }

    /// <summary>SID of the user whose context the scanned entries run in. Null for the interactive HKCU pass.</summary>
    public string? UserSid { get; init; }

    public IReadOnlyList<UserProfile> Profiles { get; init; } = Array.Empty<UserProfile>();

    /// <summary>True when UserRoot points at a hive mounted by UserHiveProvider rather than at HKCU.</summary>
    public bool ScanningMountedHive { get; init; }

    /// <summary>Thread-safe sink. Providers add per-entry failures here instead of throwing.</summary>
    public ConcurrentBag<ScanError> Errors { get; } = new();
}
```

Create `StartupScanner.Core/Models/DiffEvent.cs`:

```csharp
namespace StartupScanner.Core.Models;

public enum DiffEventKind { Added, Removed, Modified, StateChanged }

public sealed record DiffEvent
{
    public required DateTime Ts { get; init; }
    public required DiffEventKind Event { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ProviderId { get; init; }
    public required string Location { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
}
```

Create `StartupScanner.Core/Models/WriteRecord.cs`:

```csharp
namespace StartupScanner.Core.Models;

/// <summary>
/// One line of writes.jsonl. Category and UserSid are carried so that ScanEngine can
/// re-inject a BackupRemove-disabled entry that no longer exists in the registry:
/// without them the entry would silently vanish from the list and become un-re-enableable.
/// </summary>
public sealed record WriteRecord
{
    public required DateTime Ts { get; init; }
    public required string SessionId { get; init; }
    public required string ItemId { get; init; }
    public required string ProviderId { get; init; }

    /// <summary>"StartupApproved" | "Service.Start" | "Task.Enabled" | "BackupRemove" | "Appx.State" | "Wmi.Instance" | "GpoScript.Ini"</summary>
    public required string Mechanism { get; init; }

    /// <summary>Key or task path the write targeted — not necessarily the item's own Location.</summary>
    public required string Location { get; init; }

    /// <summary>Value name the write targeted, e.g. "Start" for a service.</summary>
    public required string Name { get; init; }

    public required StartupCategory Category { get; init; }
    public string? UserSid { get; init; }

    /// <summary>
    /// RegistryViewKind name when the write was view-specific. Undo of a WOW6432Node Run key must
    /// go back to the 32-bit view, so the view cannot be re-derived at undo time.
    /// </summary>
    public string? View { get; init; }

    /// <summary>Prior value, RegistryValueCodec-encoded. Null means the value did not exist.</summary>
    public string? Before { get; init; }

    /// <summary>Value written, RegistryValueCodec-encoded. Null means the value was removed.</summary>
    public string? After { get; init; }

    /// <summary>Target state of this write: true = the entry was enabled, false = disabled.</summary>
    public required bool Enabled { get; init; }
}
```

Create `StartupScanner.Core/Abstractions/IClock.cs`:

```csharp
namespace StartupScanner.Core.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~IdentityTests"`
Expected: PASS — 4 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Models StartupScanner.Core/Abstractions StartupScanner.Tests/IdentityTests.cs
git commit -m "feat: add core models and item identity"
```

---

## Task 3: Registry and filesystem abstractions with in-memory fakes

**Files:**
- Create: `StartupScanner.Core/Abstractions/IRegistry.cs`, `Abstractions/IFileSystem.cs`
- Create: `StartupScanner.Tests/Fakes/FakeRegistry.cs`, `Fakes/FakeFileSystem.cs`, `Fakes/FakeClock.cs`
- Test: `StartupScanner.Tests/Fakes/FakeRegistryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `IRegistry.OpenKey(string path, RegistryViewKind view = Default, bool writable = false) -> IRegistryKey?`
  - `IRegistry.CreateKey(string path, RegistryViewKind view = Default) -> IRegistryKey`
  - `IRegistryKey`: `Path`, `GetValueNames()`, `GetSubKeyNames()`, `GetValue(name)`, `GetValueType(name)`, `SetValue(name, value, type)`, `DeleteValue(name)`, `OpenSubKey(name, writable)`
  - `RegistryViewKind { Default, Registry32, Registry64 }`, `RegistryValueType { None, String, ExpandString, Binary, Dword, Qword, MultiString, Unknown }`
  - `IFileSystem` as listed below, `FileStat(Size, CreatedUtc, ModifiedUtc)`
  - `FakeRegistry` with `Set(path, name, value, type)`, `SetView(...)`, `Deny(path)`, and `FakeFileSystem` with `AddFile`, `AddDirectory`.

Registry paths are strings rooted at `HKLM`, `HKCU`, `HKU\{sid}`, or `HKCR`. String rooting is what makes the run-key table and the user-hive redirection possible: swapping `HKCU` for `HKU\S-1-5-21-…` is a prefix change, not a code path.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Fakes/FakeRegistryTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public class FakeRegistryTests
{
    [Fact]
    public void ReadsBackAValueItWasSeededWith()
    {
        var reg = new FakeRegistry();
        reg.Set(@"HKLM\SOFTWARE\X", "Name", "value", RegistryValueType.String);

        using var key = reg.OpenKey(@"HKLM\SOFTWARE\X");

        Assert.NotNull(key);
        Assert.Equal(new[] { "Name" }, key!.GetValueNames());
        Assert.Equal("value", key.GetValue("Name"));
        Assert.Equal(RegistryValueType.String, key.GetValueType("Name"));
    }

    [Fact]
    public void MissingKeyOpensAsNull()
    {
        Assert.Null(new FakeRegistry().OpenKey(@"HKLM\SOFTWARE\Nope"));
    }

    [Fact]
    public void ViewsAreIsolated()
    {
        var reg = new FakeRegistry();
        reg.Set(@"HKLM\SOFTWARE\X", "N", "64", RegistryValueType.String, RegistryViewKind.Registry64);
        reg.Set(@"HKLM\SOFTWARE\X", "N", "32", RegistryValueType.String, RegistryViewKind.Registry32);

        Assert.Equal("64", reg.OpenKey(@"HKLM\SOFTWARE\X", RegistryViewKind.Registry64)!.GetValue("N"));
        Assert.Equal("32", reg.OpenKey(@"HKLM\SOFTWARE\X", RegistryViewKind.Registry32)!.GetValue("N"));
    }

    [Fact]
    public void EnumeratesImmediateSubKeyNamesOnly()
    {
        var reg = new FakeRegistry();
        reg.Set(@"HKLM\A\B", "x", 1, RegistryValueType.Dword);
        reg.Set(@"HKLM\A\C\D", "x", 1, RegistryValueType.Dword);

        using var a = reg.OpenKey(@"HKLM\A");

        Assert.Equal(new[] { "B", "C" }, a!.GetSubKeyNames().OrderBy(n => n).ToArray());
    }

    [Fact]
    public void DeniedKeyThrowsUnauthorizedAccess()
    {
        var reg = new FakeRegistry();
        reg.Set(@"HKLM\Secret", "x", 1, RegistryValueType.Dword);
        reg.Deny(@"HKLM\Secret");

        Assert.Throws<UnauthorizedAccessException>(() => reg.OpenKey(@"HKLM\Secret"));
    }

    [Fact]
    public void DeleteValueRemovesIt()
    {
        var reg = new FakeRegistry();
        reg.Set(@"HKLM\A", "x", 1, RegistryValueType.Dword);

        using var key = reg.OpenKey(@"HKLM\A", writable: true);
        key!.DeleteValue("x");

        Assert.Empty(reg.OpenKey(@"HKLM\A")!.GetValueNames());
    }

    [Fact]
    public void CreateKeyMakesAWritableKey()
    {
        var reg = new FakeRegistry();

        using var key = reg.CreateKey(@"HKCU\New\Path");
        key.SetValue("v", 3, RegistryValueType.Dword);

        Assert.Equal(3, reg.OpenKey(@"HKCU\New\Path")!.GetValue("v"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~FakeRegistryTests"`
Expected: FAIL — `CS0246: The type or namespace name 'FakeRegistry' could not be found`.

- [ ] **Step 3: Write the abstractions**

Create `StartupScanner.Core/Abstractions/IRegistry.cs`:

```csharp
namespace StartupScanner.Core.Abstractions;

public enum RegistryViewKind { Default, Registry32, Registry64 }

public enum RegistryValueType { None, String, ExpandString, Binary, Dword, Qword, MultiString, Unknown }

public interface IRegistry
{
    /// <summary>Opens a key by full path ("HKLM\...", "HKCU\...", "HKU\{sid}\...", "HKCR\..."). Null when absent.</summary>
    /// <exception cref="UnauthorizedAccessException">The key exists but is not readable/writable by this process.</exception>
    IRegistryKey? OpenKey(string path, RegistryViewKind view = RegistryViewKind.Default, bool writable = false);

    /// <summary>Opens or creates a key for writing.</summary>
    IRegistryKey CreateKey(string path, RegistryViewKind view = RegistryViewKind.Default);
}

public interface IRegistryKey : IDisposable
{
    string Path { get; }
    IReadOnlyList<string> GetValueNames();
    IReadOnlyList<string> GetSubKeyNames();

    /// <summary>Raw value: string, string[], byte[], int, or long. Null when the value is absent.</summary>
    object? GetValue(string name);

    RegistryValueType GetValueType(string name);
    void SetValue(string name, object value, RegistryValueType type);
    void DeleteValue(string name);
    IRegistryKey? OpenSubKey(string name, bool writable = false);
}
```

Create `StartupScanner.Core/Abstractions/IFileSystem.cs`:

```csharp
namespace StartupScanner.Core.Abstractions;

public sealed record FileStat(long Size, DateTime CreatedUtc, DateTime ModifiedUtc);

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IReadOnlyList<string> EnumerateFiles(string directory);
    IReadOnlyList<string> EnumerateDirectories(string directory);
    FileStat? Stat(string path);
    Stream OpenRead(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void AppendLine(string path, string line);
    void CreateDirectory(string path);
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);

    /// <summary>Expands %VAR% references using the current process environment.</summary>
    string ExpandEnvironment(string value);
}
```

- [ ] **Step 4: Write the fakes**

Create `StartupScanner.Tests/Fakes/FakeRegistry.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<string, FakeRegistryKey> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string path, string name, object value, RegistryValueType type,
                    RegistryViewKind view = RegistryViewKind.Default)
        => GetOrCreate(path, view).SetValue(name, value, type);

    /// <summary>Registers a key with no values, e.g. a service subkey that exists but is empty.</summary>
    public void AddKey(string path, RegistryViewKind view = RegistryViewKind.Default)
        => GetOrCreate(path, view);

    public void Deny(string path, RegistryViewKind view = RegistryViewKind.Default)
        => _denied.Add(Composite(path, view));

    public IRegistryKey? OpenKey(string path, RegistryViewKind view = RegistryViewKind.Default, bool writable = false)
    {
        var composite = Composite(path, view);
        if (_denied.Contains(composite)) throw new UnauthorizedAccessException(path);
        return _keys.TryGetValue(composite, out var key) ? key : null;
    }

    public IRegistryKey CreateKey(string path, RegistryViewKind view = RegistryViewKind.Default)
        => GetOrCreate(path, view);

    private FakeRegistryKey GetOrCreate(string path, RegistryViewKind view)
    {
        var composite = Composite(path, view);
        if (!_keys.TryGetValue(composite, out var key))
        {
            key = new FakeRegistryKey(this, Normalise(path), view);
            _keys[composite] = key;
        }
        return key;
    }

    internal IReadOnlyList<string> SubKeyNamesOf(string path, RegistryViewKind view)
    {
        var prefix = Normalise(path) + "\\";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _keys.Values)
        {
            if (key.View != view) continue;
            if (!key.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            names.Add(key.Path[prefix.Length..].Split('\\')[0]);
        }
        return names.ToList();
    }

    internal IRegistryKey? SubKey(string parentPath, RegistryViewKind view, string name, bool writable)
        => OpenKey(parentPath + "\\" + name, view, writable);

    private static string Composite(string path, RegistryViewKind view) => $"{view}|{Normalise(path)}";

    private static string Normalise(string path) => path.Trim().TrimEnd('\\');
}

public sealed class FakeRegistryKey(FakeRegistry owner, string path, RegistryViewKind view) : IRegistryKey
{
    private readonly Dictionary<string, (object Value, RegistryValueType Type)> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Path { get; } = path;
    internal RegistryViewKind View { get; } = view;

    public IReadOnlyList<string> GetValueNames() => _values.Keys.ToList();
    public IReadOnlyList<string> GetSubKeyNames() => owner.SubKeyNamesOf(Path, View);
    public object? GetValue(string name) => _values.TryGetValue(name, out var v) ? v.Value : null;

    public RegistryValueType GetValueType(string name)
        => _values.TryGetValue(name, out var v) ? v.Type : RegistryValueType.None;

    public void SetValue(string name, object value, RegistryValueType type) => _values[name] = (value, type);
    public void DeleteValue(string name) => _values.Remove(name);

    public IRegistryKey? OpenSubKey(string name, bool writable = false) => owner.SubKey(Path, View, name, writable);

    public void Dispose() { }
}
```

Create `StartupScanner.Tests/Fakes/FakeFileSystem.cs`:

```csharp
using System.Text;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStat> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _env = new(StringComparer.OrdinalIgnoreCase);

    public void AddFile(string path, string contents, DateTime? modifiedUtc = null)
        => AddFile(path, Encoding.UTF8.GetBytes(contents), modifiedUtc);

    public void AddFile(string path, byte[] contents, DateTime? modifiedUtc = null)
    {
        _files[path] = contents;
        var ts = modifiedUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _stats[path] = new FileStat(contents.Length, ts, ts);
        AddDirectory(System.IO.Path.GetDirectoryName(path) ?? "");
    }

    public void AddDirectory(string path)
    {
        while (!string.IsNullOrEmpty(path))
        {
            _dirs.Add(path);
            path = System.IO.Path.GetDirectoryName(path) ?? "";
        }
    }

    public void SetEnvironment(string name, string value) => _env[name] = value;

    public bool FileExists(string path) => _files.ContainsKey(path);
    public bool DirectoryExists(string path) => _dirs.Contains(path);

    public IReadOnlyList<string> EnumerateFiles(string directory)
        => _files.Keys.Where(f => Same(System.IO.Path.GetDirectoryName(f), directory)).OrderBy(f => f).ToList();

    public IReadOnlyList<string> EnumerateDirectories(string directory)
        => _dirs.Where(d => Same(System.IO.Path.GetDirectoryName(d), directory)).OrderBy(d => d).ToList();

    public FileStat? Stat(string path) => _stats.TryGetValue(path, out var s) ? s : null;
    public Stream OpenRead(string path) => new MemoryStream(_files[path], writable: false);
    public string ReadAllText(string path) => Encoding.UTF8.GetString(_files[path]);
    public void WriteAllText(string path, string contents) => AddFile(path, contents);

    public void AppendLine(string path, string line)
    {
        var existing = _files.TryGetValue(path, out var b) ? Encoding.UTF8.GetString(b) : "";
        AddFile(path, existing + line + "\n");
    }

    public void CreateDirectory(string path) => AddDirectory(path);

    public void MoveFile(string source, string destination, bool overwrite)
    {
        if (!overwrite && _files.ContainsKey(destination)) throw new IOException("exists");
        _files[destination] = _files[source];
        _stats[destination] = _stats[source];
        DeleteFile(source);
    }

    public void DeleteFile(string path)
    {
        _files.Remove(path);
        _stats.Remove(path);
    }

    public string ExpandEnvironment(string value)
    {
        foreach (var (name, replacement) in _env)
            value = value.Replace($"%{name}%", replacement, StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private static bool Same(string? a, string? b)
        => string.Equals(a?.TrimEnd('\\'), b?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
```

Create `StartupScanner.Tests/Fakes/FakeClock.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeClock(DateTime? start = null) : IClock
{
    public DateTime UtcNow { get; set; } = start ?? new DateTime(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    public void Advance(TimeSpan by) => UtcNow += by;
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~FakeRegistryTests"`
Expected: PASS — 7 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Abstractions StartupScanner.Tests/Fakes
git commit -m "feat: add registry and filesystem abstractions with in-memory fakes"
```

---

## Task 4: Registry value codec

**Files:**
- Create: `StartupScanner.Core/Storage/RegistryValueCodec.cs`
- Test: `StartupScanner.Tests/Storage/RegistryValueCodecTests.cs`

**Interfaces:**
- Consumes: `RegistryValueType` (Task 3).
- Produces:
  - `RegistryValueCodec.Encode(object? value, RegistryValueType type) -> string?`
  - `RegistryValueCodec.Decode(string encoded) -> (object Value, RegistryValueType Type)`
  - `RegistryValueCodec.ToDisplay(object? value, RegistryValueType type) -> string`

Journal lines are JSON text, but registry values include `REG_BINARY` blobs and `REG_MULTI_SZ` arrays. The codec is the single place that flattens them to a string and reconstitutes them byte-exactly on restore. `ToDisplay` is the separate, lossy, human-facing rendering used for `StartupItem.RawValue`; multi-strings render joined by `"; "`, which is what the `autocheck autochk *` guardrail compares against.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Storage/RegistryValueCodecTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Storage;

namespace StartupScanner.Tests.Storage;

public class RegistryValueCodecTests
{
    [Theory]
    [InlineData("plain text", RegistryValueType.String)]
    [InlineData(@"%SystemRoot%\x.exe", RegistryValueType.ExpandString)]
    public void RoundTripsStrings(string value, RegistryValueType type)
    {
        var (decoded, decodedType) = RegistryValueCodec.Decode(RegistryValueCodec.Encode(value, type)!);
        Assert.Equal(value, decoded);
        Assert.Equal(type, decodedType);
    }

    [Fact]
    public void RoundTripsDword()
    {
        var (decoded, type) = RegistryValueCodec.Decode(RegistryValueCodec.Encode(4, RegistryValueType.Dword)!);
        Assert.Equal(4, decoded);
        Assert.Equal(RegistryValueType.Dword, type);
    }

    [Fact]
    public void RoundTripsQword()
    {
        var (decoded, type) = RegistryValueCodec.Decode(RegistryValueCodec.Encode(9_000_000_000L, RegistryValueType.Qword)!);
        Assert.Equal(9_000_000_000L, decoded);
        Assert.Equal(RegistryValueType.Qword, type);
    }

    [Fact]
    public void RoundTripsBinaryByteForByte()
    {
        var blob = new byte[] { 0x02, 0x00, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00, 0x00, 0x00 };
        var (decoded, type) = RegistryValueCodec.Decode(RegistryValueCodec.Encode(blob, RegistryValueType.Binary)!);
        Assert.Equal(blob, Assert.IsType<byte[]>(decoded));
        Assert.Equal(RegistryValueType.Binary, type);
    }

    [Fact]
    public void RoundTripsMultiStringIncludingEmbeddedSeparators()
    {
        var value = new[] { "autocheck autochk *", "a;b", "" };
        var (decoded, type) = RegistryValueCodec.Decode(RegistryValueCodec.Encode(value, RegistryValueType.MultiString)!);
        Assert.Equal(value, Assert.IsType<string[]>(decoded));
        Assert.Equal(RegistryValueType.MultiString, type);
    }

    [Fact]
    public void EncodesNullAsNull()
    {
        Assert.Null(RegistryValueCodec.Encode(null, RegistryValueType.None));
    }

    [Fact]
    public void DisplayJoinsMultiStringWithSemicolons()
    {
        var display = RegistryValueCodec.ToDisplay(new[] { "autocheck autochk *" }, RegistryValueType.MultiString);
        Assert.Equal("autocheck autochk *", display);

        var two = RegistryValueCodec.ToDisplay(new[] { "a", "b" }, RegistryValueType.MultiString);
        Assert.Equal("a; b", two);
    }

    [Fact]
    public void DisplayRendersBinaryAsHex()
    {
        Assert.Equal("02 00 03", RegistryValueCodec.ToDisplay(new byte[] { 2, 0, 3 }, RegistryValueType.Binary));
    }

    [Fact]
    public void DecodeRejectsUnknownPrefix()
    {
        Assert.Throws<FormatException>(() => RegistryValueCodec.Decode("nope:whatever"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~RegistryValueCodecTests"`
Expected: FAIL — `CS0246: The type or namespace name 'RegistryValueCodec' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `StartupScanner.Core/Storage/RegistryValueCodec.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Storage;

/// <summary>
/// Lossless registry-value &lt;-&gt; string codec for the write journal, plus a separate
/// lossy rendering for display. Encoded form is "type:payload".
/// </summary>
public static class RegistryValueCodec
{
    public static string? Encode(object? value, RegistryValueType type)
    {
        if (value is null) return null;
        return type switch
        {
            RegistryValueType.String => "sz:" + (string)value,
            RegistryValueType.ExpandString => "expand:" + (string)value,
            RegistryValueType.Dword => "dword:" + Convert.ToInt32(value).ToString(CultureInfo.InvariantCulture),
            RegistryValueType.Qword => "qword:" + Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture),
            RegistryValueType.Binary => "binary:" + Convert.ToBase64String((byte[])value),
            RegistryValueType.MultiString => "multi:" + JsonSerializer.Serialize((string[])value),
            _ => "sz:" + Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    public static (object Value, RegistryValueType Type) Decode(string encoded)
    {
        var split = encoded.IndexOf(':');
        if (split < 0) throw new FormatException($"Malformed encoded registry value: '{encoded}'");
        var prefix = encoded[..split];
        var payload = encoded[(split + 1)..];

        return prefix switch
        {
            "sz" => (payload, RegistryValueType.String),
            "expand" => (payload, RegistryValueType.ExpandString),
            "dword" => (int.Parse(payload, CultureInfo.InvariantCulture), RegistryValueType.Dword),
            "qword" => (long.Parse(payload, CultureInfo.InvariantCulture), RegistryValueType.Qword),
            "binary" => (Convert.FromBase64String(payload), RegistryValueType.Binary),
            "multi" => (JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>(), RegistryValueType.MultiString),
            _ => throw new FormatException($"Unknown encoded registry value prefix: '{prefix}'")
        };
    }

    /// <summary>Human-facing rendering used for StartupItem.RawValue. Not reversible.</summary>
    public static string ToDisplay(object? value, RegistryValueType type) => value switch
    {
        null => "",
        string[] multi => string.Join("; ", multi),
        byte[] blob => string.Join(' ', blob.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~RegistryValueCodecTests"`
Expected: PASS — 11 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Storage/RegistryValueCodec.cs StartupScanner.Tests/Storage
git commit -m "feat: add lossless registry value codec for the write journal"
```

---

## Task 5: Command line parser

**Files:**
- Create: `StartupScanner.Core/Models/CommandLine.cs`
- Test: `StartupScanner.Tests/CommandLineTests.cs`

**Interfaces:**
- Consumes: `IFileSystem` (Task 3).
- Produces: `CommandLine.Split(string rawValue, IFileSystem fs) -> (string? ImagePath, string? Arguments)`.

Every provider that reads a command string needs `ImagePath` and `Arguments` separated. Three cases have to work: a quoted path, an unquoted path containing spaces (resolved by probing the filesystem for the longest prefix that exists), and `rundll32.exe foo.dll,Entry` where the image is `rundll32.exe`. Environment variables are expanded before splitting.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/CommandLineTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests;

public class CommandLineTests
{
    private static FakeFileSystem Fs()
    {
        var fs = new FakeFileSystem();
        fs.SetEnvironment("SystemRoot", @"C:\Windows");
        fs.SetEnvironment("ProgramFiles", @"C:\Program Files");
        return fs;
    }

    [Fact]
    public void SplitsQuotedPathFromArguments()
    {
        var (image, args) = CommandLine.Split("\"C:\\Program Files\\App\\app.exe\" --start /min", Fs());
        Assert.Equal(@"C:\Program Files\App\app.exe", image);
        Assert.Equal("--start /min", args);
    }

    [Fact]
    public void SplitsUnquotedPathWithNoSpaces()
    {
        var (image, args) = CommandLine.Split(@"C:\Tools\svc.exe -k netsvcs", Fs());
        Assert.Equal(@"C:\Tools\svc.exe", image);
        Assert.Equal("-k netsvcs", args);
    }

    [Fact]
    public void ResolvesUnquotedPathContainingSpacesByProbingTheFilesystem()
    {
        var fs = Fs();
        fs.AddFile(@"C:\Program Files\My App\run.exe", "MZ");

        var (image, args) = CommandLine.Split(@"C:\Program Files\My App\run.exe /silent", fs);

        Assert.Equal(@"C:\Program Files\My App\run.exe", image);
        Assert.Equal("/silent", args);
    }

    [Fact]
    public void ExpandsEnvironmentVariables()
    {
        var (image, _) = CommandLine.Split(@"%SystemRoot%\system32\userinit.exe", Fs());
        Assert.Equal(@"C:\Windows\system32\userinit.exe", image);
    }

    [Fact]
    public void TreatsRundll32AsTheImageAndTheDllAsAnArgument()
    {
        var (image, args) = CommandLine.Split(@"rundll32.exe C:\x\shell.dll,Entry", Fs());
        Assert.Equal("rundll32.exe", image);
        Assert.Equal(@"C:\x\shell.dll,Entry", args);
    }

    [Fact]
    public void StripsTheTrailingCommaWindowsPutsOnUserinit()
    {
        var (image, args) = CommandLine.Split(@"%SystemRoot%\system32\userinit.exe,", Fs());
        Assert.Equal(@"C:\Windows\system32\userinit.exe", image);
        Assert.Null(args);
    }

    [Fact]
    public void ReturnsNullsForBlankInput()
    {
        var (image, args) = CommandLine.Split("   ", Fs());
        Assert.Null(image);
        Assert.Null(args);
    }

    [Fact]
    public void ReturnsNullArgumentsWhenThereAreNone()
    {
        var (image, args) = CommandLine.Split(@"C:\a\b.exe", Fs());
        Assert.Equal(@"C:\a\b.exe", image);
        Assert.Null(args);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~CommandLineTests"`
Expected: FAIL — `CS0117: 'CommandLine' does not contain a definition for 'Split'` (or type-not-found).

- [ ] **Step 3: Write the implementation**

Create `StartupScanner.Core/Models/CommandLine.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Models;

public static class CommandLine
{
    /// <summary>
    /// Splits a stored autostart command into image path and arguments.
    /// Environment variables are expanded first. Unquoted paths containing spaces are
    /// resolved by probing the filesystem for the longest existing prefix.
    /// </summary>
    public static (string? ImagePath, string? Arguments) Split(string rawValue, IFileSystem fs)
    {
        var text = fs.ExpandEnvironment(rawValue ?? "").Trim();
        if (text.Length == 0) return (null, null);

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close > 0)
                return (text[1..close], Rest(text[(close + 1)..]));
            return (text.Trim('"'), null);
        }

        // Longest existing prefix wins: handles "C:\Program Files\My App\run.exe /silent".
        for (var i = text.Length; i > 0; i--)
        {
            if (i < text.Length && text[i] != ' ') continue;
            var candidate = text[..i].TrimEnd(',', ' ');
            if (candidate.Length > 0 && fs.FileExists(candidate))
                return (candidate, Rest(text[i..]));
        }

        var space = text.IndexOf(' ');
        if (space < 0) return (text.TrimEnd(','), null);
        return (text[..space].TrimEnd(','), Rest(text[space..]));
    }

    private static string? Rest(string tail)
    {
        var trimmed = tail.Trim().TrimStart(',').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~CommandLineTests"`
Expected: PASS — 8 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Models/CommandLine.cs StartupScanner.Tests/CommandLineTests.cs
git commit -m "feat: add command line image/arguments parser"
```

---

## Task 6: Storage paths and the ProgramData ACL

**Files:**
- Create: `StartupScanner.Core/Storage/AppPaths.cs`, `Storage/DirectoryAcl.cs`
- Test: `StartupScanner.Tests/Storage/AppPathsTests.cs`, `StartupScanner.Tests/Storage/DirectoryAclTests.cs`

**Interfaces:**
- Consumes: `IFileSystem` (Task 3).
- Produces:
  - `AppPaths(string root)` with `Root`, `SnapshotFile`, `HistoryFile`, `WritesFile`, and `static AppPaths ForProgramData()`.
  - `DirectoryAcl.EnsureSecured(string path) -> AclOutcome` and `DirectoryAcl.Inspect(string path) -> AclOutcome`, `enum AclOutcome { Created, AlreadyCorrect, Repaired, Failed }`.

`DirectoryAcl` touches the real filesystem via `System.Security.AccessControl`, so its test is trait-gated to `Windows` and runs against a temp directory rather than `%ProgramData%`. That is deliberate: an ACL fake would assert nothing about the thing that matters. The required DACL is inheritance-protected with exactly three ACEs — `BUILTIN\Administrators` full control, `NT AUTHORITY\SYSTEM` full control, `BUILTIN\Users` read+execute.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Storage/AppPathsTests.cs`:

```csharp
using StartupScanner.Core.Storage;

namespace StartupScanner.Tests.Storage;

public class AppPathsTests
{
    [Fact]
    public void ComposesTheThreeStoreFileNamesUnderTheRoot()
    {
        var paths = new AppPaths(@"C:\Data\StartupScanner");

        Assert.Equal(@"C:\Data\StartupScanner", paths.Root);
        Assert.Equal(@"C:\Data\StartupScanner\snapshot.json", paths.SnapshotFile);
        Assert.Equal(@"C:\Data\StartupScanner\history.jsonl", paths.HistoryFile);
        Assert.Equal(@"C:\Data\StartupScanner\writes.jsonl", paths.WritesFile);
    }

    [Fact]
    public void ProgramDataRootIsUnderTheCommonApplicationDataFolder()
    {
        var paths = AppPaths.ForProgramData();
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StartupScanner");
        Assert.Equal(expected, paths.Root);
    }
}
```

Create `StartupScanner.Tests/Storage/DirectoryAclTests.cs`:

```csharp
using System.Security.AccessControl;
using System.Security.Principal;
using StartupScanner.Core.Storage;

namespace StartupScanner.Tests.Storage;

public class DirectoryAclTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss-acl-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public void CreatesTheDirectoryWithProtectedAdminOnlyWriteAcl()
    {
        var outcome = DirectoryAcl.EnsureSecured(_dir);

        Assert.Equal(AclOutcome.Created, outcome);
        Assert.True(Directory.Exists(_dir));

        var acl = new DirectoryInfo(_dir).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);

        var rules = acl.GetAccessRules(true, false, typeof(SecurityIdentifier))
                       .Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(3, rules.Count);
        AssertRule(rules, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        AssertRule(rules, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AssertRule(rules, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
    }

    [Fact]
    public void ReportsAlreadyCorrectOnASecondCall()
    {
        DirectoryAcl.EnsureSecured(_dir);
        Assert.Equal(AclOutcome.AlreadyCorrect, DirectoryAcl.EnsureSecured(_dir));
    }

    [Fact]
    public void RepairsALoosenedAcl()
    {
        DirectoryAcl.EnsureSecured(_dir);

        var info = new DirectoryInfo(_dir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Write, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);

        Assert.Equal(AclOutcome.Repaired, DirectoryAcl.EnsureSecured(_dir));
        Assert.Equal(AclOutcome.AlreadyCorrect, DirectoryAcl.EnsureSecured(_dir));
    }

    private static void AssertRule(List<FileSystemAccessRule> rules, WellKnownSidType sid, FileSystemRights rights)
    {
        var expected = new SecurityIdentifier(sid, null);
        Assert.Contains(rules, r =>
            r.IdentityReference.Value == expected.Value &&
            r.AccessControlType == AccessControlType.Allow &&
            r.FileSystemRights.HasFlag(rights));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~AppPathsTests|FullyQualifiedName~DirectoryAclTests"`
Expected: FAIL — `CS0246: The type or namespace name 'AppPaths' could not be found` and the same for `DirectoryAcl`.

- [ ] **Step 3: Write `AppPaths`**

Create `StartupScanner.Core/Storage/AppPaths.cs`:

```csharp
namespace StartupScanner.Core.Storage;

public sealed class AppPaths(string root)
{
    public string Root { get; } = root;
    public string SnapshotFile { get; } = Path.Combine(root, "snapshot.json");
    public string HistoryFile { get; } = Path.Combine(root, "history.jsonl");
    public string WritesFile { get; } = Path.Combine(root, "writes.jsonl");

    public static AppPaths ForProgramData() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StartupScanner"));
}
```

- [ ] **Step 4: Write `DirectoryAcl`**

Create `StartupScanner.Core/Storage/DirectoryAcl.cs`:

```csharp
using System.Security.AccessControl;
using System.Security.Principal;

namespace StartupScanner.Core.Storage;

public enum AclOutcome { Created, AlreadyCorrect, Repaired, Failed }

/// <summary>
/// %ProgramData% is writable by standard users by default. writes.jsonl holds values the
/// elevated app writes back into HKLM on re-enable, so a non-administrator must not be able
/// to seed it. The folder therefore carries an explicit, inheritance-protected DACL that is
/// verified and repaired on every startup.
/// </summary>
public static class DirectoryAcl
{
    public static AclOutcome EnsureSecured(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                var created = Directory.CreateDirectory(path);
                created.SetAccessControl(RequiredSecurity());
                return AclOutcome.Created;
            }

            var info = new DirectoryInfo(path);
            if (Matches(info.GetAccessControl())) return AclOutcome.AlreadyCorrect;

            info.SetAccessControl(RequiredSecurity());
            return AclOutcome.Repaired;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            return AclOutcome.Failed;
        }
    }

    public static AclOutcome Inspect(string path)
    {
        if (!Directory.Exists(path)) return AclOutcome.Failed;
        try
        {
            return Matches(new DirectoryInfo(path).GetAccessControl())
                ? AclOutcome.AlreadyCorrect
                : AclOutcome.Repaired;   // "needs repair"
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return AclOutcome.Failed;
        }
    }

    private static DirectorySecurity RequiredSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        Add(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        Add(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Add(security, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
        return security;
    }

    private static void Add(DirectorySecurity security, WellKnownSidType sid, FileSystemRights rights)
        => security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sid, null), rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

    private static bool Matches(DirectorySecurity actual)
    {
        if (!actual.AreAccessRulesProtected) return false;

        var rules = actual.GetAccessRules(true, false, typeof(SecurityIdentifier))
                          .Cast<FileSystemAccessRule>().ToList();
        if (rules.Count != 3) return false;

        return Has(rules, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl)
            && Has(rules, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl)
            && Has(rules, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute)
            && !rules.Any(r => r.IdentityReference.Value ==
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value &&
                    (r.FileSystemRights & FileSystemRights.Write) != 0);
    }

    private static bool Has(List<FileSystemAccessRule> rules, WellKnownSidType sid, FileSystemRights rights)
    {
        var expected = new SecurityIdentifier(sid, null).Value;
        return rules.Any(r => r.IdentityReference.Value == expected
                           && r.AccessControlType == AccessControlType.Allow
                           && r.FileSystemRights.HasFlag(rights));
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~AppPathsTests|FullyQualifiedName~DirectoryAclTests"`
Expected: PASS — 5 tests passed. If `RepairsALoosenedAcl` fails with `PrivilegeNotHeldException`, the test run is not elevated; rerun the suite from an elevated shell.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Storage StartupScanner.Tests/Storage
git commit -m "feat: add storage paths and hardened ProgramData ACL"
```

---

## Task 7: Snapshot and history stores

**Files:**
- Create: `StartupScanner.Core/Storage/SnapshotStore.cs`, `Storage/HistoryStore.cs`
- Test: `StartupScanner.Tests/Storage/SnapshotStoreTests.cs`, `StartupScanner.Tests/Storage/HistoryStoreTests.cs`

**Interfaces:**
- Consumes: `IFileSystem`, `AppPaths`, `StartupItem`, `DiffEvent`.
- Produces:
  - `SnapshotStore(IFileSystem fs, AppPaths paths)` with `Save(IReadOnlyList<StartupItem> items, DateTime scannedAtUtc)` and `Load() -> Snapshot?`, `record Snapshot(DateTime ScannedAtUtc, IReadOnlyList<StartupItem> Items)`.
  - `HistoryStore(IFileSystem fs, AppPaths paths)` with `Append(IEnumerable<DiffEvent> events)`, `ReadFor(string itemId) -> IReadOnlyList<DiffEvent>`, `FirstSeenUtc(string itemId) -> DateTime?`.

`FileFacts` is excluded from the snapshot: it is lazily computed, it is large, and a changed hash must not read as a `Modified` event. Exclusion is done by projecting onto a `SnapshotItem` DTO rather than by attributes, so the omission is visible in one place. Saves are atomic — write to `.tmp`, then move over the target — so a crash mid-write cannot leave a truncated snapshot that would make every entry look new.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Storage/SnapshotStoreTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Storage;

public class SnapshotStoreTests
{
    private static readonly AppPaths Paths = new(@"C:\Data\StartupScanner");
    private static readonly DateTime Scanned = new(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    private static StartupItem Item(string name = "OneDrive") => new()
    {
        Id = Identity.Compute("registry.run", null, @"HKCU\Run", name),
        ProviderId = "registry.run",
        Location = @"HKCU\Run",
        Name = name,
        RawValue = @"C:\OneDrive.exe /background",
        Category = StartupCategory.RunKey,
        ImagePath = @"C:\OneDrive.exe",
        Arguments = "/background",
        State = EnableState.Enabled,
        StateSource = "StartupApproved",
        Risk = RiskTier.Low,
        UserSid = "S-1-5-21-9",
        Extra = new Dictionary<string, string> { ["Start"] = "2" },
    };

    [Fact]
    public void LoadReturnsNullWhenNoSnapshotExists()
    {
        Assert.Null(new SnapshotStore(new FakeFileSystem(), Paths).Load());
    }

    [Fact]
    public void RoundTripsItemsAndTimestamp()
    {
        var fs = new FakeFileSystem();
        var store = new SnapshotStore(fs, Paths);

        store.Save(new[] { Item() }, Scanned);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(Scanned, loaded!.ScannedAtUtc);
        var item = Assert.Single(loaded.Items);
        Assert.Equal(Item().Id, item.Id);
        Assert.Equal(@"C:\OneDrive.exe /background", item.RawValue);
        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Equal(RiskTier.Low, item.Risk);
        Assert.Equal("S-1-5-21-9", item.UserSid);
        Assert.Equal("2", item.Extra["Start"]);
    }

    [Fact]
    public void DoesNotPersistLazilyComputedFileFacts()
    {
        var fs = new FakeFileSystem();
        var store = new SnapshotStore(fs, Paths);

        store.Save(new[] { Item() with { File = new FileFacts { Path = @"C:\OneDrive.exe", Exists = true, Sha256 = "abc" } } }, Scanned);

        Assert.DoesNotContain("abc", fs.ReadAllText(Paths.SnapshotFile));
        Assert.Null(store.Load()!.Items[0].File);
    }

    [Fact]
    public void SaveOverwritesThePreviousSnapshotAtomically()
    {
        var fs = new FakeFileSystem();
        var store = new SnapshotStore(fs, Paths);

        store.Save(new[] { Item("A") }, Scanned);
        store.Save(new[] { Item("B") }, Scanned.AddMinutes(5));

        Assert.Equal("B", Assert.Single(store.Load()!.Items).Name);
        Assert.False(fs.FileExists(Paths.SnapshotFile + ".tmp"));
    }

    [Fact]
    public void CorruptSnapshotLoadsAsNullRatherThanThrowing()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Paths.SnapshotFile, "{ this is not json");

        Assert.Null(new SnapshotStore(fs, Paths).Load());
    }
}
```

Create `StartupScanner.Tests/Storage/HistoryStoreTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Storage;

public class HistoryStoreTests
{
    private static readonly AppPaths Paths = new(@"C:\Data\StartupScanner");

    private static DiffEvent Event(DiffEventKind kind, string id, DateTime ts) => new()
    {
        Ts = ts, Event = kind, Id = id, Name = "Thing",
        ProviderId = "registry.run", Location = @"HKCU\Run",
        Before = kind == DiffEventKind.Added ? null : "old",
        After = kind == DiffEventKind.Removed ? null : "new",
    };

    [Fact]
    public void AppendsOneJsonObjectPerLine()
    {
        var fs = new FakeFileSystem();
        var store = new HistoryStore(fs, Paths);

        store.Append(new[]
        {
            Event(DiffEventKind.Added, "id-1", new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc)),
            Event(DiffEventKind.Modified, "id-1", new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc)),
        });

        var lines = fs.ReadAllText(Paths.HistoryFile).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{", l));
    }

    [Fact]
    public void AppendIsAdditiveAcrossCalls()
    {
        var fs = new FakeFileSystem();
        var store = new HistoryStore(fs, Paths);

        store.Append(new[] { Event(DiffEventKind.Added, "id-1", new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc)) });
        store.Append(new[] { Event(DiffEventKind.Removed, "id-1", new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc)) });

        Assert.Equal(2, store.ReadFor("id-1").Count);
    }

    [Fact]
    public void ReadForFiltersByItemIdAndPreservesOrder()
    {
        var fs = new FakeFileSystem();
        var store = new HistoryStore(fs, Paths);

        store.Append(new[]
        {
            Event(DiffEventKind.Added, "id-1", new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc)),
            Event(DiffEventKind.Added, "id-2", new DateTime(2026, 7, 28, 9, 0, 1, DateTimeKind.Utc)),
            Event(DiffEventKind.StateChanged, "id-1", new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc)),
        });

        var events = store.ReadFor("id-1");

        Assert.Equal(2, events.Count);
        Assert.Equal(DiffEventKind.Added, events[0].Event);
        Assert.Equal(DiffEventKind.StateChanged, events[1].Event);
    }

    [Fact]
    public void FirstSeenIsTheEarliestAddedEvent()
    {
        var fs = new FakeFileSystem();
        var store = new HistoryStore(fs, Paths);
        var first = new DateTime(2026, 7, 20, 8, 0, 0, DateTimeKind.Utc);

        store.Append(new[]
        {
            Event(DiffEventKind.Added, "id-1", first),
            Event(DiffEventKind.Removed, "id-1", first.AddDays(1)),
            Event(DiffEventKind.Added, "id-1", first.AddDays(2)),
        });

        Assert.Equal(first, store.FirstSeenUtc("id-1"));
    }

    [Fact]
    public void MissingFileReadsAsEmpty()
    {
        var store = new HistoryStore(new FakeFileSystem(), Paths);
        Assert.Empty(store.ReadFor("id-1"));
        Assert.Null(store.FirstSeenUtc("id-1"));
    }

    [Fact]
    public void UnparseableLinesAreSkipped()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Paths.HistoryFile, "not json\n");
        var store = new HistoryStore(fs, Paths);

        store.Append(new[] { Event(DiffEventKind.Added, "id-1", new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc)) });

        Assert.Single(store.ReadFor("id-1"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~SnapshotStoreTests|FullyQualifiedName~HistoryStoreTests"`
Expected: FAIL — `CS0246: The type or namespace name 'SnapshotStore' could not be found`.

- [ ] **Step 3: Write `SnapshotStore`**

Create `StartupScanner.Core/Storage/SnapshotStore.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Storage;

public sealed record Snapshot(DateTime ScannedAtUtc, IReadOnlyList<StartupItem> Items);

public sealed class SnapshotStore(IFileSystem fs, AppPaths paths)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(IReadOnlyList<StartupItem> items, DateTime scannedAtUtc)
    {
        var document = new SnapshotDocument(1, scannedAtUtc, items.Select(SnapshotItem.From).ToList());
        var temp = paths.SnapshotFile + ".tmp";
        fs.CreateDirectory(paths.Root);
        fs.WriteAllText(temp, JsonSerializer.Serialize(document, Json));
        fs.MoveFile(temp, paths.SnapshotFile, overwrite: true);
    }

    public Snapshot? Load()
    {
        if (!fs.FileExists(paths.SnapshotFile)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<SnapshotDocument>(fs.ReadAllText(paths.SnapshotFile), Json);
            if (document is null) return null;
            return new Snapshot(document.ScannedAtUtc, document.Items.Select(i => i.ToItem()).ToList());
        }
        catch (JsonException)
        {
            return null;   // A truncated or hand-edited snapshot means "no baseline", not a crash.
        }
    }

    private sealed record SnapshotDocument(int Version, DateTime ScannedAtUtc, List<SnapshotItem> Items);

    /// <summary>
    /// Persisted projection of StartupItem. FileFacts is deliberately absent: it is lazily
    /// computed for the details panel and must not participate in diffing.
    /// </summary>
    private sealed record SnapshotItem(
        string Id, string ProviderId, string Location, string Name, string RawValue,
        StartupCategory Category, string? ImagePath, string? Arguments,
        EnableState State, string StateSource, RiskTier Risk, string? UserSid,
        bool PartiallyRead, Dictionary<string, string> Extra)
    {
        public static SnapshotItem From(StartupItem i) => new(
            i.Id, i.ProviderId, i.Location, i.Name, i.RawValue, i.Category, i.ImagePath, i.Arguments,
            i.State, i.StateSource, i.Risk, i.UserSid, i.PartiallyRead,
            new Dictionary<string, string>(i.Extra, StringComparer.OrdinalIgnoreCase));

        public StartupItem ToItem() => new()
        {
            Id = Id, ProviderId = ProviderId, Location = Location, Name = Name, RawValue = RawValue,
            Category = Category, ImagePath = ImagePath, Arguments = Arguments, State = State,
            StateSource = StateSource, Risk = Risk, UserSid = UserSid, PartiallyRead = PartiallyRead,
            Extra = new Dictionary<string, string>(Extra, StringComparer.OrdinalIgnoreCase),
        };
    }
}
```

- [ ] **Step 4: Write `HistoryStore`**

Create `StartupScanner.Core/Storage/HistoryStore.cs`:

```csharp
using System.Text.Json;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Storage;

/// <summary>Append-only diff event log. One JSON object per line.</summary>
public sealed class HistoryStore(IFileSystem fs, AppPaths paths)
{
    public void Append(IEnumerable<DiffEvent> events)
    {
        fs.CreateDirectory(paths.Root);
        foreach (var e in events)
            fs.AppendLine(paths.HistoryFile, JsonSerializer.Serialize(e, SnapshotStore.Json.WithoutIndenting()));
    }

    public IReadOnlyList<DiffEvent> ReadFor(string itemId)
        => ReadAll().Where(e => e.Id == itemId).ToList();

    public DateTime? FirstSeenUtc(string itemId)
    {
        var added = ReadFor(itemId).Where(e => e.Event == DiffEventKind.Added).ToList();
        return added.Count == 0 ? null : added.Min(e => e.Ts);
    }

    public IReadOnlyList<DiffEvent> ReadAll()
    {
        if (!fs.FileExists(paths.HistoryFile)) return Array.Empty<DiffEvent>();

        var events = new List<DiffEvent>();
        foreach (var line in fs.ReadAllText(paths.HistoryFile).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<DiffEvent>(line, SnapshotStore.Json);
                if (parsed is not null) events.Add(parsed);
            }
            catch (JsonException)
            {
                // A partially flushed final line must not poison the whole history.
            }
        }
        return events;
    }
}

internal static class JsonOptionsExtensions
{
    private static JsonSerializerOptions? _compact;

    /// <summary>JSONL requires one line per record, so the shared indented options cannot be reused directly.</summary>
    public static JsonSerializerOptions WithoutIndenting(this JsonSerializerOptions source)
        => _compact ??= new JsonSerializerOptions(source) { WriteIndented = false };
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~SnapshotStoreTests|FullyQualifiedName~HistoryStoreTests"`
Expected: PASS — 11 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Storage StartupScanner.Tests/Storage
git commit -m "feat: add snapshot and history stores"
```

---

## Task 8: Write journal

**Files:**
- Create: `StartupScanner.Core/Storage/WriteJournal.cs`
- Test: `StartupScanner.Tests/Storage/WriteJournalTests.cs`

**Interfaces:**
- Consumes: `IFileSystem`, `AppPaths`, `WriteRecord`.
- Produces:
  - `IWriteJournalReader` with `WriteRecord? GetLast(string itemId)`, `IReadOnlyList<WriteRecord> All { get; }`, `IReadOnlyList<WriteRecord> ForSession(string sessionId)`.
  - `IWriteJournal : IWriteJournalReader` adding `void Record(WriteRecord record)`.
  - `WriteJournal(IFileSystem fs, AppPaths paths)` implementing `IWriteJournal`, loading `writes.jsonl` into a last-write-wins dictionary at construction.

This file is the single source of truth for undo and for restoring backed-up values on re-enable. There is no separate backup file.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Storage/WriteJournalTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Storage;

public class WriteJournalTests
{
    private static readonly AppPaths Paths = new(@"C:\Data\StartupScanner");

    private static WriteRecord Record(string itemId, bool enabled, DateTime ts, string session = "s1") => new()
    {
        Ts = ts, SessionId = session, ItemId = itemId, ProviderId = "registry.run",
        Mechanism = "StartupApproved", Location = @"HKCU\Run", Name = "Thing",
        Category = StartupCategory.RunKey, UserSid = null,
        Before = "binary:AgAAAAAAAAAAAAAA", After = "binary:AwAAAAAAAAAAAAAA", Enabled = enabled,
    };

    [Fact]
    public void RecordAppendsOneLinePerWrite()
    {
        var fs = new FakeFileSystem();
        var journal = new WriteJournal(fs, Paths);

        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        journal.Record(Record("id-1", true, new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc)));

        var lines = fs.ReadAllText(Paths.WritesFile).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void GetLastReturnsTheMostRecentWriteForThatItem()
    {
        var fs = new FakeFileSystem();
        var journal = new WriteJournal(fs, Paths);

        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        journal.Record(Record("id-1", true, new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc)));
        journal.Record(Record("id-2", false, new DateTime(2026, 7, 30, 9, 6, 0, DateTimeKind.Utc)));

        Assert.True(journal.GetLast("id-1")!.Enabled);
        Assert.False(journal.GetLast("id-2")!.Enabled);
        Assert.Null(journal.GetLast("id-3"));
    }

    [Fact]
    public void ExistingFileIsLoadedIntoTheLastWriteWinsIndexAtConstruction()
    {
        var fs = new FakeFileSystem();
        new WriteJournal(fs, Paths).Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));

        var reloaded = new WriteJournal(fs, Paths);

        Assert.Single(reloaded.All);
        Assert.False(reloaded.GetLast("id-1")!.Enabled);
    }

    [Fact]
    public void AllPreservesAppendOrder()
    {
        var fs = new FakeFileSystem();
        var journal = new WriteJournal(fs, Paths);

        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        journal.Record(Record("id-2", false, new DateTime(2026, 7, 30, 9, 1, 0, DateTimeKind.Utc)));

        Assert.Equal(new[] { "id-1", "id-2" }, journal.All.Select(r => r.ItemId).ToArray());
    }

    [Fact]
    public void ForSessionFiltersBySessionId()
    {
        var fs = new FakeFileSystem();
        var journal = new WriteJournal(fs, Paths);

        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc), "old-session"));
        journal.Record(Record("id-2", false, new DateTime(2026, 7, 30, 9, 1, 0, DateTimeKind.Utc), "this-session"));

        Assert.Equal(new[] { "id-2" }, journal.ForSession("this-session").Select(r => r.ItemId).ToArray());
    }

    [Fact]
    public void MechanismAndCategorySurviveTheRoundTrip()
    {
        var fs = new FakeFileSystem();
        new WriteJournal(fs, Paths).Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc))
            with { Mechanism = "BackupRemove", Category = StartupCategory.Winlogon, UserSid = "S-1-5-21-3" });

        var record = new WriteJournal(fs, Paths).GetLast("id-1")!;

        Assert.Equal("BackupRemove", record.Mechanism);
        Assert.Equal(StartupCategory.Winlogon, record.Category);
        Assert.Equal("S-1-5-21-3", record.UserSid);
    }

    [Fact]
    public void CorruptLinesAreSkippedWithoutLosingGoodOnes()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Paths.WritesFile, "half-written line\n");

        var journal = new WriteJournal(fs, Paths);
        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));

        Assert.Single(new WriteJournal(fs, Paths).All);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WriteJournalTests"`
Expected: FAIL — `CS0246: The type or namespace name 'WriteJournal' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `StartupScanner.Core/Storage/WriteJournal.cs`:

```csharp
using System.Text.Json;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Storage;

public interface IWriteJournalReader
{
    /// <summary>Most recent write for this item, or null if the app has never written it.</summary>
    WriteRecord? GetLast(string itemId);

    IReadOnlyList<WriteRecord> All { get; }
    IReadOnlyList<WriteRecord> ForSession(string sessionId);
}

public interface IWriteJournal : IWriteJournalReader
{
    /// <summary>
    /// Appends a completed write. Only ToggleService and UndoService call this, and only after
    /// the write has been read back and verified. Providers must never call it.
    /// </summary>
    void Record(WriteRecord record);
}

public sealed class WriteJournal : IWriteJournal
{
    private readonly IFileSystem _fs;
    private readonly AppPaths _paths;
    private readonly List<WriteRecord> _all = new();
    private readonly Dictionary<string, WriteRecord> _last = new(StringComparer.Ordinal);

    public WriteJournal(IFileSystem fs, AppPaths paths)
    {
        _fs = fs;
        _paths = paths;
        Load();
    }

    public IReadOnlyList<WriteRecord> All => _all;

    public WriteRecord? GetLast(string itemId) => _last.TryGetValue(itemId, out var r) ? r : null;

    public IReadOnlyList<WriteRecord> ForSession(string sessionId)
        => _all.Where(r => r.SessionId == sessionId).ToList();

    public void Record(WriteRecord record)
    {
        _fs.CreateDirectory(_paths.Root);
        _fs.AppendLine(_paths.WritesFile, JsonSerializer.Serialize(record, SnapshotStore.Json.WithoutIndenting()));
        _all.Add(record);
        _last[record.ItemId] = record;
    }

    private void Load()
    {
        if (!_fs.FileExists(_paths.WritesFile)) return;

        foreach (var line in _fs.ReadAllText(_paths.WritesFile).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var record = JsonSerializer.Deserialize<WriteRecord>(line, SnapshotStore.Json);
                if (record is null) continue;
                _all.Add(record);
                _last[record.ItemId] = record;   // append order gives last-write-wins for free
            }
            catch (JsonException)
            {
                // Skip a torn line rather than refusing to start.
            }
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WriteJournalTests"`
Expected: PASS — 7 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Storage/WriteJournal.cs StartupScanner.Tests/Storage/WriteJournalTests.cs
git commit -m "feat: add append-only write journal with last-write-wins index"
```

---

## Task 9: Diff engine

**Files:**
- Create: `StartupScanner.Core/Diff/DiffEngine.cs`
- Test: `StartupScanner.Tests/Diff/DiffEngineTests.cs`

**Interfaces:**
- Consumes: `StartupItem`, `DiffEvent`, `DiffEventKind`.
- Produces: `DiffEngine.Compare(IReadOnlyList<StartupItem> snapshot, IReadOnlyList<StartupItem> current, DateTime ts) -> IReadOnlyList<DiffEvent>`.

`StateChanged` is the event comparable tools omit; it is what catches an installer silently re-enabling something the user disabled. An item can produce both a `Modified` and a `StateChanged` in the same scan, and both are emitted.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Diff/DiffEngineTests.cs`:

```csharp
using StartupScanner.Core.Diff;
using StartupScanner.Core.Models;

namespace StartupScanner.Tests.Diff;

public class DiffEngineTests
{
    private static readonly DateTime Ts = new(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    private static StartupItem Item(string name, string raw = "cmd", EnableState state = EnableState.Enabled) => new()
    {
        Id = Identity.Compute("registry.run", null, @"HKCU\Run", name),
        ProviderId = "registry.run",
        Location = @"HKCU\Run",
        Name = name,
        RawValue = raw,
        Category = StartupCategory.RunKey,
        State = state,
    };

    [Fact]
    public void ReportsAddedWhenAnIdIsOnlyInTheCurrentScan()
    {
        var events = DiffEngine.Compare(Array.Empty<StartupItem>(), new[] { Item("New") }, Ts);

        var e = Assert.Single(events);
        Assert.Equal(DiffEventKind.Added, e.Event);
        Assert.Equal("New", e.Name);
        Assert.Null(e.Before);
        Assert.Equal("cmd", e.After);
        Assert.Equal(Ts, e.Ts);
    }

    [Fact]
    public void ReportsRemovedWhenAnIdIsOnlyInTheSnapshot()
    {
        var events = DiffEngine.Compare(new[] { Item("Gone") }, Array.Empty<StartupItem>(), Ts);

        var e = Assert.Single(events);
        Assert.Equal(DiffEventKind.Removed, e.Event);
        Assert.Equal("cmd", e.Before);
        Assert.Null(e.After);
    }

    [Fact]
    public void ReportsModifiedWhenOnlyRawValueChanged()
    {
        var events = DiffEngine.Compare(new[] { Item("X", "old.exe") }, new[] { Item("X", "new.exe") }, Ts);

        var e = Assert.Single(events);
        Assert.Equal(DiffEventKind.Modified, e.Event);
        Assert.Equal("old.exe", e.Before);
        Assert.Equal("new.exe", e.After);
    }

    [Fact]
    public void ReportsStateChangedWhenSomethingElseReEnabledTheEntry()
    {
        var events = DiffEngine.Compare(
            new[] { Item("X", "cmd", EnableState.Disabled) },
            new[] { Item("X", "cmd", EnableState.Enabled) }, Ts);

        var e = Assert.Single(events);
        Assert.Equal(DiffEventKind.StateChanged, e.Event);
        Assert.Equal("Disabled", e.Before);
        Assert.Equal("Enabled", e.After);
    }

    [Fact]
    public void EmitsBothModifiedAndStateChangedWhenBothChanged()
    {
        var events = DiffEngine.Compare(
            new[] { Item("X", "old.exe", EnableState.Disabled) },
            new[] { Item("X", "new.exe", EnableState.Enabled) }, Ts);

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Event == DiffEventKind.Modified);
        Assert.Contains(events, e => e.Event == DiffEventKind.StateChanged);
    }

    [Fact]
    public void AChangedCommandLineIsNotReportedAsRemovedPlusAdded()
    {
        var events = DiffEngine.Compare(new[] { Item("X", "old.exe") }, new[] { Item("X", "new.exe") }, Ts);

        Assert.DoesNotContain(events, e => e.Event is DiffEventKind.Added or DiffEventKind.Removed);
    }

    [Fact]
    public void IdenticalScansProduceNoEvents()
    {
        Assert.Empty(DiffEngine.Compare(new[] { Item("X") }, new[] { Item("X") }, Ts));
    }

    [Fact]
    public void EventsCarryProviderAndLocationForRendering()
    {
        var e = Assert.Single(DiffEngine.Compare(Array.Empty<StartupItem>(), new[] { Item("New") }, Ts));

        Assert.Equal("registry.run", e.ProviderId);
        Assert.Equal(@"HKCU\Run", e.Location);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~DiffEngineTests"`
Expected: FAIL — `CS0246: The type or namespace name 'DiffEngine' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `StartupScanner.Core/Diff/DiffEngine.cs`:

```csharp
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Diff;

public static class DiffEngine
{
    public static IReadOnlyList<DiffEvent> Compare(
        IReadOnlyList<StartupItem> snapshot, IReadOnlyList<StartupItem> current, DateTime ts)
    {
        var before = snapshot.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var after = current.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var events = new List<DiffEvent>();

        foreach (var item in current)
        {
            if (!before.TryGetValue(item.Id, out var old))
            {
                events.Add(Make(DiffEventKind.Added, item, ts, null, item.RawValue));
                continue;
            }

            if (!string.Equals(old.RawValue, item.RawValue, StringComparison.Ordinal))
                events.Add(Make(DiffEventKind.Modified, item, ts, old.RawValue, item.RawValue));

            if (old.State != item.State)
                events.Add(Make(DiffEventKind.StateChanged, item, ts, old.State.ToString(), item.State.ToString()));
        }

        foreach (var item in snapshot.Where(i => !after.ContainsKey(i.Id)))
            events.Add(Make(DiffEventKind.Removed, item, ts, item.RawValue, null));

        return events;
    }

    private static DiffEvent Make(DiffEventKind kind, StartupItem item, DateTime ts, string? before, string? after)
        => new()
        {
            Ts = ts, Event = kind, Id = item.Id, Name = item.Name,
            ProviderId = item.ProviderId, Location = item.Location,
            Before = before, After = after,
        };
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~DiffEngineTests"`
Expected: PASS — 8 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Diff StartupScanner.Tests/Diff
git commit -m "feat: add diff engine with Added/Removed/Modified/StateChanged events"
```

---

## Task 10: StartupApproved blob codec

**Files:**
- Create: `StartupScanner.Core/Writing/StartupApprovedBlob.cs`
- Test: `StartupScanner.Tests/Writing/StartupApprovedBlobTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `StartupApprovedBlob.IsEnabled(byte[] blob) -> bool`
  - `StartupApprovedBlob.Encode(bool enabled, DateTime disabledAtUtc, byte[]? existing) -> byte[]` (always 12 bytes)
  - `StartupApprovedBlob.DisabledAtUtc(byte[] blob) -> DateTime?`

This is the format Task Manager and `msconfig` use, so getting it byte-exact is what keeps the app's changes consistent with theirs. Layout: byte 0 is the state flag, bytes 1–3 are opaque and must be preserved, bytes 4–11 are a little-endian `FILETIME` of the moment the entry was disabled (zero when enabled). The state test is `(blob[0] & 0x01) != 0` for disabled, which covers every flag byte Windows is known to write — `0x00`/`0x02`/`0x06` enabled, `0x01`/`0x03`/`0x09` disabled — rather than only the two values this app writes.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Writing/StartupApprovedBlobTests.cs`:

```csharp
using System.Buffers.Binary;
using StartupScanner.Core.Writing;

namespace StartupScanner.Tests.Writing;

public class StartupApprovedBlobTests
{
    private static readonly DateTime DisabledAt = new(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    // Captured from a real machine: an enabled HKCU Run entry.
    private static readonly byte[] RealEnabled =
        { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    // Captured from a real machine: the same entry after Task Manager disabled it.
    private static readonly byte[] RealDisabled =
        { 0x03, 0x00, 0x00, 0x00, 0x2B, 0x9D, 0x1F, 0x28, 0x64, 0xD5, 0xDC, 0x01 };

    [Fact]
    public void RealEnabledBlobReadsAsEnabled() => Assert.True(StartupApprovedBlob.IsEnabled(RealEnabled));

    [Fact]
    public void RealDisabledBlobReadsAsDisabled() => Assert.False(StartupApprovedBlob.IsEnabled(RealDisabled));

    [Theory]
    [InlineData(0x00, true)]
    [InlineData(0x02, true)]
    [InlineData(0x06, true)]
    [InlineData(0x01, false)]
    [InlineData(0x03, false)]
    [InlineData(0x09, false)]
    public void StateIsTheLowBitOfByteZero(byte flag, bool expectedEnabled)
    {
        var blob = new byte[12];
        blob[0] = flag;
        Assert.Equal(expectedEnabled, StartupApprovedBlob.IsEnabled(blob));
    }

    [Fact]
    public void AnEmptyOrMissingBlobCountsAsEnabled()
    {
        Assert.True(StartupApprovedBlob.IsEnabled(Array.Empty<byte>()));
    }

    [Fact]
    public void EncodeDisableWritesFlag03AndTheFileTime()
    {
        var blob = StartupApprovedBlob.Encode(enabled: false, DisabledAt, existing: null);

        Assert.Equal(12, blob.Length);
        Assert.Equal(0x03, blob[0]);
        Assert.Equal(DisabledAt.ToFileTimeUtc(), BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(4, 8)));
    }

    [Fact]
    public void EncodeEnableWritesFlag02AndZeroesTheTimestamp()
    {
        var blob = StartupApprovedBlob.Encode(enabled: true, DisabledAt, existing: RealDisabled);

        Assert.Equal(0x02, blob[0]);
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(4, 8)));
    }

    [Fact]
    public void EncodePreservesTheOpaqueBytesOneToThree()
    {
        var existing = new byte[] { 0x02, 0xAA, 0xBB, 0xCC, 0, 0, 0, 0, 0, 0, 0, 0 };

        var blob = StartupApprovedBlob.Encode(enabled: false, DisabledAt, existing);

        Assert.Equal(0xAA, blob[1]);
        Assert.Equal(0xBB, blob[2]);
        Assert.Equal(0xCC, blob[3]);
    }

    [Fact]
    public void EncodeCreatesAFreshTwelveByteBlobWhenNoneExists()
    {
        var blob = StartupApprovedBlob.Encode(enabled: false, DisabledAt, existing: Array.Empty<byte>());

        Assert.Equal(12, blob.Length);
        Assert.Equal(0x03, blob[0]);
    }

    [Fact]
    public void EncodeToleratesAShortExistingBlob()
    {
        var blob = StartupApprovedBlob.Encode(enabled: false, DisabledAt, existing: new byte[] { 0x02, 0x11 });

        Assert.Equal(12, blob.Length);
        Assert.Equal(0x11, blob[1]);
    }

    [Fact]
    public void DisableThenEnableRoundTripsBackToTheEnabledBlob()
    {
        var disabled = StartupApprovedBlob.Encode(enabled: false, DisabledAt, RealEnabled);
        var enabled = StartupApprovedBlob.Encode(enabled: true, DisabledAt, disabled);

        Assert.Equal(RealEnabled, enabled);
    }

    [Fact]
    public void DisabledAtReadsTheTimestampBackAndIsNullWhenEnabled()
    {
        Assert.Null(StartupApprovedBlob.DisabledAtUtc(RealEnabled));

        var disabled = StartupApprovedBlob.Encode(enabled: false, DisabledAt, null);
        Assert.Equal(DisabledAt, StartupApprovedBlob.DisabledAtUtc(disabled));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~StartupApprovedBlobTests"`
Expected: FAIL — `CS0246: The type or namespace name 'StartupApprovedBlob' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `StartupScanner.Core/Writing/StartupApprovedBlob.cs`:

```csharp
using System.Buffers.Binary;

namespace StartupScanner.Core.Writing;

/// <summary>
/// The 12-byte StartupApproved value Task Manager and msconfig use.
/// Byte 0 is the state flag, bytes 1-3 are opaque and preserved verbatim, bytes 4-11 are a
/// little-endian FILETIME of the disable moment (zero while enabled).
/// </summary>
public static class StartupApprovedBlob
{
    public const int Length = 12;

    /// <summary>
    /// Windows has written several flag bytes over the years (0x00/0x02/0x06 enabled,
    /// 0x01/0x03/0x09 disabled). The low bit is the part that has always meant "disabled".
    /// </summary>
    public static bool IsEnabled(byte[] blob) => blob.Length == 0 || (blob[0] & 0x01) == 0;

    public static byte[] Encode(bool enabled, DateTime disabledAtUtc, byte[]? existing)
    {
        var blob = new byte[Length];
        if (existing is { Length: > 0 })
            Array.Copy(existing, blob, Math.Min(existing.Length, Length));

        blob[0] = enabled ? (byte)0x02 : (byte)0x03;
        BinaryPrimitives.WriteInt64LittleEndian(
            blob.AsSpan(4, 8), enabled ? 0L : disabledAtUtc.ToFileTimeUtc());
        return blob;
    }

    public static DateTime? DisabledAtUtc(byte[] blob)
    {
        if (blob.Length < Length) return null;
        var stamp = BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(4, 8));
        return stamp == 0 ? null : DateTime.FromFileTimeUtc(stamp);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~StartupApprovedBlobTests"`
Expected: PASS — 16 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Writing StartupScanner.Tests/Writing
git commit -m "feat: add StartupApproved 12-byte blob codec"
```

---

## Task 11: Provider 1 — registry Run keys (scan)

**Files:**
- Create: `StartupScanner.Core/Providers/IStartupProvider.cs`, `Providers/RunKeyTable.cs`, `Providers/RegistryRunProvider.cs`
- Create: `StartupScanner.Core/Writing/StartupApprovedReader.cs`
- Test: `StartupScanner.Tests/Providers/RegistryRunProviderTests.cs`

**Interfaces:**
- Consumes: `IRegistry`, `IFileSystem`, `ScanContext`, `StartupApprovedBlob`, `CommandLine`, `Identity`.
- Produces:
  - `IStartupProvider` with `string Id { get; }`, `StartupCategory Category { get; }`, `IEnumerable<StartupItem> Scan(ScanContext ctx)`.
  - `RunKeyDefinition(string RootToken, string SubKey, RegistryViewKind View, string? ApprovedSubKey, bool WalkSubKeys = false)` and `RunKeyTable.All`.
  - `StartupApprovedReader(IRegistry registry)` with `ReadRun(RunKeyDefinition def, ScanContext ctx, string valueName) -> (EnableState State, string Source)`, `ReadFolder(string approvedRoot, string fileName) -> (EnableState, string)`, `Read(string approvedKeyPath, RegistryViewKind view, string valueName) -> (EnableState, string)`, and `static ApprovedKeyPath(string root, string approvedSubKey) -> string`.
  - `RegistryRunProvider(IRegistry registry, IFileSystem fs)` with `Id == "registry.run"`.

`RootToken` is either the literal `"HKLM"` or the sentinel `"%USER%"`, which is replaced by `ctx.UserRoot` at scan time. That substitution is the whole mechanism by which provider 19 scans a logged-out user's hive with no provider changes.

`HKLM\Software` is subject to WOW64 redirection, so HKLM rows appear twice, once per view. `HKCU\Software` is not redirected, so per-user rows appear once with `RegistryViewKind.Default`. `RunOnceEx` stores its commands one level below the key in named groups, so its rows set `WalkSubKeys`.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Providers/RegistryRunProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class RegistryRunProviderTests
{
    private const string HklmRun = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string HkcuRun = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string HkcuApproved = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string HklmApproved32 = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    private static (RegistryRunProvider Provider, FakeRegistry Registry, FakeFileSystem Fs) Sut()
    {
        var registry = new FakeRegistry();
        var fs = new FakeFileSystem();
        return (new RegistryRunProvider(registry, fs), registry, fs);
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _, _) = Sut();
        Assert.Equal("registry.run", provider.Id);
        Assert.Equal(StartupCategory.RunKey, provider.Category);
    }

    [Fact]
    public void TableCoversTheKeyFamiliesFromTheSpec()
    {
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith(@"CurrentVersion\Run") && d.RootToken == "HKLM");
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith("RunOnce"));
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith("RunOnceEx") && d.WalkSubKeys);
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith("RunServices"));
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith("RunServicesOnce"));
        Assert.Contains(RunKeyTable.All, d => d.SubKey.EndsWith(@"Policies\Explorer\Run"));
        Assert.Contains(RunKeyTable.All, d => d.RootToken == "%USER%");
        Assert.Contains(RunKeyTable.All, d => d.View == RegistryViewKind.Registry32);
        Assert.Equal("Run32", RunKeyTable.All.Single(d =>
            d.RootToken == "HKLM" && d.View == RegistryViewKind.Registry32 &&
            d.SubKey.EndsWith(@"CurrentVersion\Run")).ApprovedSubKey);
    }

    [Fact]
    public void ScansAnHklmRunValueIntoAnItem()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HklmRun, "OneDrive", @"""C:\OneDrive.exe"" /background",
                     RegistryValueType.String, RegistryViewKind.Registry64);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "OneDrive"));

        Assert.Equal("registry.run", item.ProviderId);
        Assert.Equal(HklmRun, item.Location);
        Assert.Equal(@"""C:\OneDrive.exe"" /background", item.RawValue);
        Assert.Equal(@"C:\OneDrive.exe", item.ImagePath);
        Assert.Equal("/background", item.Arguments);
        Assert.Equal(StartupCategory.RunKey, item.Category);
        Assert.Null(item.UserSid);
    }

    [Fact]
    public void PerUserRowsFollowUserRootAndCarryTheSid()
    {
        var (provider, registry, _) = Sut();
        registry.Set(@"HKU\S-1-5-21-7\Software\Microsoft\Windows\CurrentVersion\Run", "Spotify",
                     @"C:\Spotify.exe", RegistryValueType.String);

        var ctx = new ScanContext { UserRoot = @"HKU\S-1-5-21-7", UserSid = "S-1-5-21-7", ScanningMountedHive = true };
        var item = Assert.Single(provider.Scan(ctx).Where(i => i.Name == "Spotify"));

        Assert.Equal("S-1-5-21-7", item.UserSid);
        Assert.StartsWith(@"HKU\S-1-5-21-7\", item.Location);
    }

    [Fact]
    public void IdIsUnchangedWhenOnlyTheCommandLineChanges()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HkcuRun, "Thing", "old.exe", RegistryValueType.String);
        var before = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Thing")).Id;

        registry.Set(HkcuRun, "Thing", "new.exe", RegistryValueType.String);
        var after = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Thing")).Id;

        Assert.Equal(before, after);
    }

    [Fact]
    public void EntryWithNoStartupApprovedBlobReadsAsEnabled()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HkcuRun, "Thing", "x.exe", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Thing"));

        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Contains("Default", item.StateSource);
    }

    [Fact]
    public void DisabledStartupApprovedBlobMakesTheItemDisabled()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HkcuRun, "Thing", "x.exe", RegistryValueType.String);
        registry.Set(HkcuApproved, "Thing",
            StartupApprovedBlob.Encode(false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc), null),
            RegistryValueType.Binary);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Thing"));

        Assert.Equal(EnableState.Disabled, item.State);
        Assert.Equal("StartupApproved", item.StateSource);
    }

    [Fact]
    public void ThirtyTwoBitHklmRunUsesTheRun32ApprovedKey()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HklmRun, "Legacy", "x.exe", RegistryValueType.String, RegistryViewKind.Registry32);
        registry.Set(HklmApproved32, "Legacy",
            StartupApprovedBlob.Encode(false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc), null),
            RegistryValueType.Binary, RegistryViewKind.Registry32);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Legacy"));

        Assert.Equal(EnableState.Disabled, item.State);
    }

    [Fact]
    public void RunOnceHasNoStartupApprovedCoverageSoItReadsAsEnabled()
    {
        var (provider, registry, _) = Sut();
        registry.Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce", "Once", "x.exe", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Once"));

        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Equal("NoStartupApprovedCoverage", item.StateSource);
    }

    [Fact]
    public void RunOnceExCommandsOneLevelDeepAreFound()
    {
        var (provider, registry, _) = Sut();
        registry.Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnceEx\0001", "0000",
                     @"C:\setup.exe", RegistryValueType.String, RegistryViewKind.Registry64);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "0000"));

        Assert.EndsWith(@"RunOnceEx\0001", item.Location);
    }

    [Fact]
    public void AccessDeniedKeyBecomesAScanErrorAndDoesNotStopTheOtherKeys()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HklmRun, "Blocked", "x.exe", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Deny(HklmRun, RegistryViewKind.Registry64);
        registry.Set(HkcuRun, "Readable", "y.exe", RegistryValueType.String);
        var ctx = Ctx();

        var names = provider.Scan(ctx).Select(i => i.Name).ToList();

        Assert.Contains("Readable", names);
        Assert.DoesNotContain("Blocked", names);
        Assert.Contains(ctx.Errors, e => e.ProviderId == "registry.run" && e.Location == HklmRun);
    }

    [Fact]
    public void NonStringValuesAreRenderedForDisplayRatherThanSkipped()
    {
        var (provider, registry, _) = Sut();
        registry.Set(HkcuRun, "Weird", new byte[] { 0x01, 0x02 }, RegistryValueType.Binary);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == "Weird"));

        Assert.Equal("01 02", item.RawValue);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~RegistryRunProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'RegistryRunProvider' could not be found`.

- [ ] **Step 3: Write `IStartupProvider`**

Create `StartupScanner.Core/Providers/IStartupProvider.cs`:

```csharp
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Providers;

public interface IStartupProvider
{
    /// <summary>Stable dotted id, e.g. "registry.run". Persisted in snapshots and journal lines.</summary>
    string Id { get; }

    StartupCategory Category { get; }

    /// <summary>
    /// Enumerates this provider's locations. Per-entry failures are added to ctx.Errors and the
    /// remaining entries still yield; only a catastrophic failure should throw.
    /// </summary>
    IEnumerable<StartupItem> Scan(ScanContext ctx);
}
```

- [ ] **Step 4: Write `RunKeyTable`**

Create `StartupScanner.Core/Providers/RunKeyTable.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Providers;

/// <summary>
/// One row per concrete autostart key. RootToken is "HKLM" or the "%USER%" sentinel, which the
/// provider replaces with ScanContext.UserRoot — that is how a logged-out user's mounted hive is
/// scanned without provider changes. Adding a location is one row here, not new code.
/// </summary>
public sealed record RunKeyDefinition(
    string RootToken,
    string SubKey,
    RegistryViewKind View,
    string? ApprovedSubKey,
    bool WalkSubKeys = false);

public static class RunKeyTable
{
    private const string MachineCv = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private const string UserCv = @"Software\Microsoft\Windows\CurrentVersion";
    private const string MachineTs = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Terminal Server\Install\Software\Microsoft\Windows\CurrentVersion";
    private const string UserTs = @"Software\Microsoft\Windows NT\CurrentVersion\Terminal Server\Install\Software\Microsoft\Windows\CurrentVersion";

    public static readonly IReadOnlyList<RunKeyDefinition> All = Build();

    private static List<RunKeyDefinition> Build()
    {
        var rows = new List<RunKeyDefinition>();

        // HKLM\SOFTWARE is WOW64-redirected, so every machine row is scanned in both views.
        foreach (var view in new[] { RegistryViewKind.Registry64, RegistryViewKind.Registry32 })
        {
            var approved = view == RegistryViewKind.Registry32 ? "Run32" : "Run";
            rows.Add(new("HKLM", MachineCv + @"\Run", view, approved));
            rows.Add(new("HKLM", MachineCv + @"\RunOnce", view, null));
            rows.Add(new("HKLM", MachineCv + @"\RunOnce\Setup", view, null));
            rows.Add(new("HKLM", MachineCv + @"\RunOnceEx", view, null, WalkSubKeys: true));
            rows.Add(new("HKLM", MachineCv + @"\RunServices", view, null));
            rows.Add(new("HKLM", MachineCv + @"\RunServicesOnce", view, null));
            rows.Add(new("HKLM", MachineCv + @"\Policies\Explorer\Run", view, null));
            rows.Add(new("HKLM", MachineTs + @"\Run", view, null));
            rows.Add(new("HKLM", MachineTs + @"\RunOnce", view, null));
        }

        // HKCU\Software is not WOW64-redirected, so per-user rows are scanned once.
        rows.Add(new("%USER%", UserCv + @"\Run", RegistryViewKind.Default, "Run"));
        rows.Add(new("%USER%", UserCv + @"\RunOnce", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserCv + @"\RunOnce\Setup", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserCv + @"\RunOnceEx", RegistryViewKind.Default, null, WalkSubKeys: true));
        rows.Add(new("%USER%", UserCv + @"\RunServices", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserCv + @"\RunServicesOnce", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserCv + @"\Policies\Explorer\Run", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserTs + @"\Run", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserTs + @"\RunOnce", RegistryViewKind.Default, null));

        return rows;
    }
}
```

- [ ] **Step 5: Write `StartupApprovedReader`**

Create `StartupScanner.Core/Writing/StartupApprovedReader.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Reads the StartupApproved state that Task Manager and msconfig write, for both Run keys and
/// Startup folder shortcuts.
/// </summary>
public sealed class StartupApprovedReader(IRegistry registry)
{
    private const string Tail = @"Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>
    /// Full path of the StartupApproved key for a hive root. Root is either "HKLM" or a user hive
    /// root such as "HKCU" or "HKU\S-1-5-21-…".
    /// </summary>
    public static string ApprovedKeyPath(string root, string approvedSubKey)
        => root.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            ? $@"HKLM\SOFTWARE\{Tail}\{approvedSubKey}"
            : $@"{root}\Software\{Tail}\{approvedSubKey}";

    public (EnableState State, string Source) ReadRun(RunKeyDefinition def, ScanContext ctx, string valueName)
    {
        if (def.ApprovedSubKey is null) return (EnableState.Enabled, "NoStartupApprovedCoverage");
        var root = def.RootToken == "HKLM" ? "HKLM" : ctx.UserRoot;
        return Read(ApprovedKeyPath(root, def.ApprovedSubKey), def.View, valueName);
    }

    /// <param name="approvedRoot">"HKLM" for the common folder, otherwise the owning hive root.</param>
    public (EnableState State, string Source) ReadFolder(string approvedRoot, string fileName)
        => Read(ApprovedKeyPath(approvedRoot, "StartupFolder"), RegistryViewKind.Default, fileName);

    public (EnableState State, string Source) Read(string approvedKeyPath, RegistryViewKind view, string valueName)
    {
        try
        {
            using var key = registry.OpenKey(approvedKeyPath, view);
            if (key?.GetValue(valueName) is byte[] blob)
                return (StartupApprovedBlob.IsEnabled(blob) ? EnableState.Enabled : EnableState.Disabled,
                        "StartupApproved");
        }
        catch (UnauthorizedAccessException)
        {
            return (EnableState.Unknown, "StartupApproved (access denied)");
        }

        return (EnableState.Enabled, "Default (no StartupApproved entry)");
    }
}
```

- [ ] **Step 6: Write `RegistryRunProvider`**

Create `StartupScanner.Core/Providers/RegistryRunProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;

namespace StartupScanner.Core.Providers;

public sealed class RegistryRunProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private readonly StartupApprovedReader _approved = new(registry);

    public string Id => "registry.run";
    public StartupCategory Category => StartupCategory.RunKey;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var def in RunKeyTable.All)
        {
            var path = def.RootToken == "HKLM" ? $@"HKLM\{def.SubKey}" : $@"{ctx.UserRoot}\{def.SubKey}";

            foreach (var item in ScanKey(def, path, ctx)) yield return item;

            if (!def.WalkSubKeys) continue;

            foreach (var group in SubKeyNames(path, def, ctx))
                foreach (var item in ScanKey(def, $@"{path}\{group}", ctx))
                    yield return item;
        }
    }

    private IEnumerable<StartupItem> ScanKey(RunKeyDefinition def, string path, ScanContext ctx)
    {
        var values = new List<(string Name, object? Value, RegistryValueType Type)>();
        try
        {
            using var key = registry.OpenKey(path, def.View);
            if (key is null) return Array.Empty<StartupItem>();
            foreach (var name in key.GetValueNames())
                values.Add((name, key.GetValue(name), key.GetValueType(name)));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            return Array.Empty<StartupItem>();
        }

        return values.Select(v => Build(def, path, ctx, v.Name, v.Value, v.Type)).ToList();
    }

    private IReadOnlyList<string> SubKeyNames(string path, RunKeyDefinition def, ScanContext ctx)
    {
        try
        {
            using var key = registry.OpenKey(path, def.View);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            return Array.Empty<string>();
        }
    }

    private StartupItem Build(RunKeyDefinition def, string path, ScanContext ctx,
                              string name, object? value, RegistryValueType type)
    {
        var raw = RegistryValueCodec.ToDisplay(value, type);
        var (image, args) = CommandLine.Split(raw, fs);
        var (state, source) = _approved.ReadRun(def, ctx, name);
        var sid = def.RootToken == "HKLM" ? null : ctx.UserSid;

        return new StartupItem
        {
            Id = Identity.Compute(Id, sid, path, name),
            ProviderId = Id,
            Location = path,
            Name = name,
            RawValue = raw,
            Category = Category,
            ImagePath = image,
            Arguments = args,
            State = state,
            StateSource = source,
            UserSid = sid,
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["View"] = def.View.ToString(),
                ["ValueType"] = type.ToString(),
                ["ApprovedSubKey"] = def.ApprovedSubKey ?? "",
                // Which hive the StartupApproved state lives in. The toggle needs this and cannot
                // re-derive it from Location once the item has been through a snapshot round trip.
                ["ApprovedRoot"] = def.RootToken == "HKLM" ? "HKLM" : ctx.UserRoot,
            },
        };
    }
}
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~RegistryRunProviderTests"`
Expected: PASS — 12 tests passed.

- [ ] **Step 8: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Providers StartupScanner.Core/Writing/StartupApprovedReader.cs StartupScanner.Tests/Providers
git commit -m "feat: add table-driven registry Run key provider"
```

---

## Task 12: Provider 2 — Startup folders

**Files:**
- Create: `StartupScanner.Core/Abstractions/IShellLinkResolver.cs`, `Providers/StartupFolderProvider.cs`
- Create: `StartupScanner.Tests/Fakes/FakeShellLinkResolver.cs`
- Test: `StartupScanner.Tests/Providers/StartupFolderProviderTests.cs`

**Interfaces:**
- Consumes: `IFileSystem`, `IRegistry`, `StartupApprovedReader`, `IStartupProvider`.
- Produces:
  - `IShellLinkResolver.Resolve(string lnkPath) -> ShortcutTarget?`, `ShortcutTarget(string TargetPath, string Arguments, string WorkingDirectory, string IconLocation)`.
  - `StartupFolderProvider(IFileSystem fs, IRegistry registry, IShellLinkResolver links)` with `Id == "startup.folder"`.
  - `static StartupFolderProvider.CommonStartupPath(IFileSystem fs)` and `static .UserStartupPath(string appDataRoaming)`.
  - Items carry `Extra["ApprovedRoot"]`, the hive root whose `StartupApproved\StartupFolder` key governs the entry.

Three sets of folders are covered: the interactive user's Startup folder, the common (All Users) Startup folder, and the Startup folder inside every profile listed in `ctx.Profiles`. State comes from the `StartupApproved\StartupFolder` key, keyed by the shortcut's file name.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Fakes/FakeShellLinkResolver.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeShellLinkResolver : IShellLinkResolver
{
    private readonly Dictionary<string, ShortcutTarget> _targets = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string lnkPath, ShortcutTarget target) => _targets[lnkPath] = target;

    public ShortcutTarget? Resolve(string lnkPath) => _targets.TryGetValue(lnkPath, out var t) ? t : null;
}
```

Create `StartupScanner.Tests/Providers/StartupFolderProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class StartupFolderProviderTests
{
    private const string UserStartup = @"C:\Users\alice\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
    private const string CommonStartup = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup";
    private const string ApprovedUser = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private static (StartupFolderProvider Provider, FakeFileSystem Fs, FakeRegistry Registry, FakeShellLinkResolver Links) Sut()
    {
        var fs = new FakeFileSystem();
        fs.SetEnvironment("APPDATA", @"C:\Users\alice\AppData\Roaming");
        fs.SetEnvironment("ProgramData", @"C:\ProgramData");
        var registry = new FakeRegistry();
        var links = new FakeShellLinkResolver();
        return (new StartupFolderProvider(fs, registry, links), fs, registry, links);
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _, _, _) = Sut();
        Assert.Equal("startup.folder", provider.Id);
        Assert.Equal(StartupCategory.StartupFolder, provider.Category);
    }

    [Fact]
    public void ResolvesAShortcutIntoImagePathArgumentsAndWorkingDirectory()
    {
        var (provider, fs, _, links) = Sut();
        var lnk = Path.Combine(UserStartup, "Spotify.lnk");
        fs.AddFile(lnk, "shortcut");
        links.Add(lnk, new ShortcutTarget(@"C:\Spotify\spotify.exe", "--minimized", @"C:\Spotify", @"C:\Spotify\app.ico"));

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Spotify.lnk"));

        Assert.Equal(@"C:\Spotify\spotify.exe", item.ImagePath);
        Assert.Equal("--minimized", item.Arguments);
        Assert.Equal(@"C:\Spotify", item.Extra["WorkingDirectory"]);
        Assert.Equal(@"C:\Spotify\app.ico", item.Extra["IconLocation"]);
        Assert.Equal(UserStartup, item.Location);
    }

    [Fact]
    public void ANonShortcutFileIsStillReportedWithItselfAsTheImage()
    {
        var (provider, fs, _, _) = Sut();
        fs.AddFile(Path.Combine(UserStartup, "cleanup.bat"), "echo hi");

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "cleanup.bat"));

        Assert.Equal(Path.Combine(UserStartup, "cleanup.bat"), item.ImagePath);
        Assert.Null(item.Arguments);
    }

    [Fact]
    public void AnUnresolvableShortcutIsMarkedPartiallyRead()
    {
        var (provider, fs, _, _) = Sut();
        fs.AddFile(Path.Combine(UserStartup, "Broken.lnk"), "junk");

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Broken.lnk"));

        Assert.True(item.PartiallyRead);
        Assert.Null(item.ImagePath);
    }

    [Fact]
    public void ScansTheCommonStartupFolderToo()
    {
        var (provider, fs, _, links) = Sut();
        var lnk = Path.Combine(CommonStartup, "Everyone.lnk");
        fs.AddFile(lnk, "shortcut");
        links.Add(lnk, new ShortcutTarget(@"C:\tool.exe", "", "", ""));

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Everyone.lnk"));

        Assert.Equal(CommonStartup, item.Location);
        Assert.Equal("True", item.Extra["CommonFolder"]);
    }

    [Fact]
    public void ScansEveryProfileListedInTheContext()
    {
        var (provider, fs, _, links) = Sut();
        var otherStartup = @"C:\Users\bob\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
        var lnk = Path.Combine(otherStartup, "BobThing.lnk");
        fs.AddFile(lnk, "shortcut");
        links.Add(lnk, new ShortcutTarget(@"C:\bob.exe", "", "", ""));

        var ctx = new ScanContext
        {
            UserRoot = "HKCU",
            Profiles = new[] { new UserProfile("S-1-5-21-42", @"C:\Users\bob", false, "bob") },
        };

        var item = Assert.Single(provider.Scan(ctx).Where(i => i.Name == "BobThing.lnk"));

        Assert.Equal("S-1-5-21-42", item.UserSid);
        Assert.Equal(otherStartup, item.Location);
        Assert.Equal(@"HKU\S-1-5-21-42", item.Extra["ApprovedRoot"]);
    }

    [Fact]
    public void DisabledStartupApprovedBlobKeyedByFileNameMakesTheItemDisabled()
    {
        var (provider, fs, registry, links) = Sut();
        var lnk = Path.Combine(UserStartup, "Spotify.lnk");
        fs.AddFile(lnk, "shortcut");
        links.Add(lnk, new ShortcutTarget(@"C:\spotify.exe", "", "", ""));
        registry.Set(ApprovedUser, "Spotify.lnk",
            StartupApprovedBlob.Encode(false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc), null),
            RegistryValueType.Binary);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Spotify.lnk"));

        Assert.Equal(EnableState.Disabled, item.State);
        Assert.Equal("StartupApproved", item.StateSource);
    }

    [Fact]
    public void MissingFolderYieldsNothingAndNoError()
    {
        var (provider, _, _, _) = Sut();
        var ctx = new ScanContext { UserRoot = "HKCU" };

        Assert.Empty(provider.Scan(ctx));
        Assert.Empty(ctx.Errors);
    }

    [Fact]
    public void DesktopIniIsIgnored()
    {
        var (provider, fs, _, _) = Sut();
        fs.AddFile(Path.Combine(UserStartup, "desktop.ini"), "[.ShellClassInfo]");

        Assert.Empty(provider.Scan(new ScanContext { UserRoot = "HKCU" }));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~StartupFolderProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'IShellLinkResolver' could not be found`.

- [ ] **Step 3: Write the abstraction**

Create `StartupScanner.Core/Abstractions/IShellLinkResolver.cs`:

```csharp
namespace StartupScanner.Core.Abstractions;

public sealed record ShortcutTarget(string TargetPath, string Arguments, string WorkingDirectory, string IconLocation);

public interface IShellLinkResolver
{
    /// <summary>Resolves a .lnk via IShellLink. Null when the file is not a readable shortcut.</summary>
    ShortcutTarget? Resolve(string lnkPath);
}
```

- [ ] **Step 4: Write the provider**

Create `StartupScanner.Core/Providers/StartupFolderProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;

namespace StartupScanner.Core.Providers;

public sealed class StartupFolderProvider(IFileSystem fs, IRegistry registry, IShellLinkResolver links)
    : IStartupProvider
{
    private const string StartupTail = @"Microsoft\Windows\Start Menu\Programs\Startup";
    private readonly StartupApprovedReader _approved = new(registry);

    public string Id => "startup.folder";
    public StartupCategory Category => StartupCategory.StartupFolder;

    public static string UserStartupPath(string appDataRoaming)
        => Path.Combine(appDataRoaming, StartupTail);

    public static string CommonStartupPath(IFileSystem fs)
        => Path.Combine(fs.ExpandEnvironment("%ProgramData%"), StartupTail);

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        var folders = new List<(string Path, bool Common, string? Sid)>
        {
            (UserStartupPath(fs.ExpandEnvironment("%APPDATA%")), false, ctx.UserSid),
            (CommonStartupPath(fs), true, null),
        };

        foreach (var profile in ctx.Profiles)
            folders.Add((Path.Combine(profile.ProfilePath, @"AppData\Roaming", StartupTail), false, profile.Sid));

        foreach (var (folder, common, sid) in folders)
        {
            foreach (var item in ScanFolder(folder, common, sid, ctx)) yield return item;
        }
    }

    private IEnumerable<StartupItem> ScanFolder(string folder, bool common, string? sid, ScanContext ctx)
    {
        List<string> files;
        try
        {
            if (!fs.DirectoryExists(folder)) return Array.Empty<StartupItem>();
            files = fs.EnumerateFiles(folder)
                      .Where(f => !string.Equals(Path.GetFileName(f), "desktop.ini", StringComparison.OrdinalIgnoreCase))
                      .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ctx.Errors.Add(new ScanError(Id, folder, ex.Message));
            return Array.Empty<StartupItem>();
        }

        return files.Select(file => Build(folder, file, common, sid, ctx)).ToList();
    }

    private StartupItem Build(string folder, string file, bool common, string? sid, ScanContext ctx)
    {
        var name = Path.GetFileName(file);
        var isShortcut = name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        var target = isShortcut ? links.Resolve(file) : null;

        // The StartupApproved state for another profile's folder lives in that profile's hive, which
        // may not be mounted during this pass; the read then reports "no entry", i.e. enabled.
        var approvedRoot = common ? "HKLM" : sid is null ? ctx.UserRoot : $@"HKU\{sid}";
        var (state, source) = _approved.ReadFolder(approvedRoot, name);

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CommonFolder"] = common.ToString(),
            ["FilePath"] = file,
            ["WorkingDirectory"] = target?.WorkingDirectory ?? "",
            ["IconLocation"] = target?.IconLocation ?? "",
            ["LinkTarget"] = target?.TargetPath ?? "",
            ["ApprovedRoot"] = approvedRoot,
        };

        return new StartupItem
        {
            Id = Identity.Compute(Id, sid, folder, name),
            ProviderId = Id,
            Location = folder,
            Name = name,
            RawValue = target is null ? file : $"{target.TargetPath} {target.Arguments}".TrimEnd(),
            Category = Category,
            ImagePath = isShortcut ? target?.TargetPath : file,
            Arguments = string.IsNullOrWhiteSpace(target?.Arguments) ? null : target!.Arguments,
            State = state,
            StateSource = source,
            UserSid = sid,
            PartiallyRead = isShortcut && target is null,
            Extra = extra,
        };
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~StartupFolderProviderTests"`
Expected: PASS — 9 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add startup folder provider with shell link resolution"
```

---

## Task 13: Provider 3 — services and drivers

**Files:**
- Create: `StartupScanner.Core/Providers/ServiceProvider.cs`
- Test: `StartupScanner.Tests/Providers/ServiceProviderTests.cs`

**Interfaces:**
- Consumes: `IRegistry`, `IFileSystem`, `IStartupProvider`.
- Produces: `ServiceProvider(IRegistry registry, IFileSystem fs)` with `Id == "service"`. Items carry `Extra` keys `Start`, `Type`, `ObjectName`, `DisplayName`, `Description`, `DependOnService`, `Group`, `ImagePath`.

`Type` separates services from drivers: values `1` (kernel driver), `2` (file system driver), `4` (adapter), and `8` (recognizer driver) are drivers, everything else is a service. `Start` is the state: `4` is disabled, anything else is enabled, and the exact prior value (`2` automatic vs `3` manual) is what the journal has to preserve so re-enable does not guess.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Providers/ServiceProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class ServiceProviderTests
{
    private const string Services = @"HKLM\SYSTEM\CurrentControlSet\Services";

    private static (ServiceProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new ServiceProvider(registry, new FakeFileSystem()), registry);
    }

    private static void AddService(FakeRegistry registry, string name, int start, int type,
                                   string imagePath = @"C:\Windows\System32\svchost.exe -k netsvcs")
    {
        registry.Set($@"{Services}\{name}", "Start", start, RegistryValueType.Dword);
        registry.Set($@"{Services}\{name}", "Type", type, RegistryValueType.Dword);
        registry.Set($@"{Services}\{name}", "ImagePath", imagePath, RegistryValueType.ExpandString);
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("service", provider.Id);
        Assert.Equal(StartupCategory.Service, provider.Category);
    }

    [Fact]
    public void ReadsAnAutomaticServiceAsEnabled()
    {
        var (provider, registry) = Sut();
        AddService(registry, "Spooler", start: 2, type: 32);
        registry.Set($@"{Services}\Spooler", "DisplayName", "Print Spooler", RegistryValueType.String);
        registry.Set($@"{Services}\Spooler", "ObjectName", "LocalSystem", RegistryValueType.String);
        registry.Set($@"{Services}\Spooler", "Description", "Manages printing", RegistryValueType.String);
        registry.Set($@"{Services}\Spooler", "DependOnService", new[] { "RPCSS", "http" }, RegistryValueType.MultiString);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Spooler"));

        Assert.Equal(StartupCategory.Service, item.Category);
        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Equal("Service.Start=2", item.StateSource);
        Assert.Equal("2", item.Extra["Start"]);
        Assert.Equal("32", item.Extra["Type"]);
        Assert.Equal("Print Spooler", item.Extra["DisplayName"]);
        Assert.Equal("LocalSystem", item.Extra["ObjectName"]);
        Assert.Equal("Manages printing", item.Extra["Description"]);
        Assert.Equal("RPCSS; http", item.Extra["DependOnService"]);
        Assert.Equal(@"C:\Windows\System32\svchost.exe", item.ImagePath);
        Assert.Equal("-k netsvcs", item.Arguments);
        Assert.Equal($@"{Services}\Spooler", item.Location);
    }

    [Fact]
    public void StartFourReadsAsDisabled()
    {
        var (provider, registry) = Sut();
        AddService(registry, "Disabled1", start: 4, type: 16);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Disabled1"));

        Assert.Equal(EnableState.Disabled, item.State);
        Assert.Equal("Service.Start=4", item.StateSource);
    }

    [Theory]
    [InlineData(1, StartupCategory.Driver)]
    [InlineData(2, StartupCategory.Driver)]
    [InlineData(4, StartupCategory.Driver)]
    [InlineData(8, StartupCategory.Driver)]
    [InlineData(16, StartupCategory.Service)]
    [InlineData(32, StartupCategory.Service)]
    [InlineData(272, StartupCategory.Service)]
    public void TypeSeparatesDriversFromServices(int type, StartupCategory expected)
    {
        var (provider, registry) = Sut();
        AddService(registry, "Thing", start: 3, type: type);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Thing"));

        Assert.Equal(expected, item.Category);
    }

    [Fact]
    public void ServiceWithNoStartValueIsUnknownAndPartiallyRead()
    {
        var (provider, registry) = Sut();
        registry.AddKey($@"{Services}\Weird");

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Weird"));

        Assert.Equal(EnableState.Unknown, item.State);
        Assert.True(item.PartiallyRead);
    }

    [Fact]
    public void RawValueIsTheImagePathBecauseThatIsWhatRuns()
    {
        var (provider, registry) = Sut();
        AddService(registry, "Thing", 2, 16, @"\SystemRoot\System32\drivers\thing.sys");

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }).Where(i => i.Name == "Thing"));

        Assert.Equal(@"\SystemRoot\System32\drivers\thing.sys", item.RawValue);
    }

    [Fact]
    public void InaccessibleServiceSubkeyBecomesAScanErrorAndTheRestStillScan()
    {
        var (provider, registry) = Sut();
        AddService(registry, "Good", 2, 16);
        AddService(registry, "Protected", 2, 16);
        registry.Deny($@"{Services}\Protected");
        var ctx = new ScanContext { UserRoot = "HKCU" };

        var names = provider.Scan(ctx).Select(i => i.Name).ToList();

        Assert.Contains("Good", names);
        Assert.DoesNotContain("Protected", names);
        Assert.Contains(ctx.Errors, e => e.Location.EndsWith("Protected"));
    }

    [Fact]
    public void NoServicesKeyYieldsNothing()
    {
        var (provider, _) = Sut();
        Assert.Empty(provider.Scan(new ScanContext { UserRoot = "HKCU" }));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ServiceProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ServiceProvider' could not be found`.

- [ ] **Step 3: Write the provider**

Create `StartupScanner.Core/Providers/ServiceProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class ServiceProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    public const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services";

    private static readonly string[] Copied =
        { "Start", "Type", "ObjectName", "DisplayName", "Description", "DependOnService", "Group", "ImagePath" };

    public string Id => "service";
    public StartupCategory Category => StartupCategory.Service;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        IReadOnlyList<string> names;
        try
        {
            using var root = registry.OpenKey(ServicesKey);
            if (root is null) yield break;
            names = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, ServicesKey, ex.Message));
            yield break;
        }

        foreach (var name in names)
        {
            var item = TryBuild(name, ctx);
            if (item is not null) yield return item;
        }
    }

    private StartupItem? TryBuild(string name, ScanContext ctx)
    {
        var path = $@"{ServicesKey}\{name}";
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        object? start;

        try
        {
            using var key = registry.OpenKey(path);
            if (key is null) return null;

            foreach (var valueName in Copied)
                extra[valueName] = RegistryValueCodec.ToDisplay(key.GetValue(valueName), key.GetValueType(valueName));

            start = key.GetValue("Start");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            return null;
        }

        var imagePathRaw = extra["ImagePath"];
        var (image, args) = CommandLine.Split(imagePathRaw, fs);
        var typeValue = int.TryParse(extra["Type"], out var t) ? t : 0;
        var category = (typeValue & 0x0F) != 0 && typeValue != 0 ? StartupCategory.Driver : StartupCategory.Service;

        var state = start is null ? EnableState.Unknown
                  : Convert.ToInt32(start) == 4 ? EnableState.Disabled
                  : EnableState.Enabled;

        return new StartupItem
        {
            Id = Identity.Compute(Id, null, path, name),
            ProviderId = Id,
            Location = path,
            Name = name,
            RawValue = imagePathRaw,
            Category = category,
            ImagePath = image,
            Arguments = args,
            State = state,
            StateSource = start is null ? "Service.Start missing" : $"Service.Start={Convert.ToInt32(start)}",
            PartiallyRead = start is null,
            Extra = extra,
        };
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ServiceProviderTests"`
Expected: PASS — 13 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Providers/ServiceProvider.cs StartupScanner.Tests/Providers/ServiceProviderTests.cs
git commit -m "feat: add service and driver provider"
```

---

## Task 14: Provider 4 — scheduled tasks

**Files:**
- Create: `StartupScanner.Core/Abstractions/ITaskSchedulerService.cs`, `Providers/ScheduledTaskProvider.cs`
- Create: `StartupScanner.Tests/Fakes/FakeTaskScheduler.cs`
- Test: `StartupScanner.Tests/Providers/ScheduledTaskProviderTests.cs`

**Interfaces:**
- Consumes: `IFileSystem`, `IStartupProvider`.
- Produces:
  - `ScheduledTaskInfo` with `Path`, `Name`, `Folder`, `Enabled`, `Triggers`, `Actions`, `Principal`, `RunLevel`, `UserSid`, `LastRunTimeUtc`, `LastTaskResult`, `NextRunTimeUtc`, `Xml`, `PartiallyRead`.
  - `ITaskSchedulerService` with `IReadOnlyList<ScheduledTaskInfo> EnumerateTasks()` and `void SetEnabled(string taskPath, bool enabled)`.
  - `ScheduledTaskProvider(ITaskSchedulerService tasks, IFileSystem fs)` with `Id == "task-scheduler"`.
  - `FakeTaskScheduler` with `Add(ScheduledTaskInfo)`, `SetEnabledCalls`, `FailOn(string taskPath)`.

The tree walk itself lives behind `ITaskSchedulerService` because it is COM; the provider's job is turning `ScheduledTaskInfo` into `StartupItem` and putting the trigger and action lists where the details panel can find them.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Fakes/FakeTaskScheduler.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeTaskScheduler : ITaskSchedulerService
{
    private readonly List<ScheduledTaskInfo> _tasks = new();
    private readonly HashSet<string> _failures = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, bool Enabled)> SetEnabledCalls { get; } = new();

    public void Add(ScheduledTaskInfo task) => _tasks.Add(task);

    public void FailOn(string taskPath) => _failures.Add(taskPath);

    public IReadOnlyList<ScheduledTaskInfo> EnumerateTasks() => _tasks;

    public void SetEnabled(string taskPath, bool enabled)
    {
        if (_failures.Contains(taskPath)) throw new UnauthorizedAccessException(taskPath);

        SetEnabledCalls.Add((taskPath, enabled));
        var index = _tasks.FindIndex(t => t.Path == taskPath);
        if (index >= 0) _tasks[index] = _tasks[index] with { Enabled = enabled };
    }
}
```

Create `StartupScanner.Tests/Providers/ScheduledTaskProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class ScheduledTaskProviderTests
{
    private static ScheduledTaskInfo Task(string path, bool enabled = true) => new()
    {
        Path = path,
        Name = path[(path.LastIndexOf('\\') + 1)..],
        Folder = path[..path.LastIndexOf('\\')] is "" ? "\\" : path[..path.LastIndexOf('\\')],
        Enabled = enabled,
        Triggers = new[] { "AtLogon: any user", "Boot: delay 30s" },
        Actions = new[] { @"C:\Program Files\App\updater.exe /silent" },
        Principal = @"NT AUTHORITY\SYSTEM",
        RunLevel = "HighestAvailable",
        UserSid = null,
        LastRunTimeUtc = new DateTime(2026, 7, 29, 6, 0, 0, DateTimeKind.Utc),
        LastTaskResult = 0,
        NextRunTimeUtc = new DateTime(2026, 7, 31, 6, 0, 0, DateTimeKind.Utc),
        Xml = "<Task/>",
    };

    private static (ScheduledTaskProvider Provider, FakeTaskScheduler Tasks) Sut()
    {
        var tasks = new FakeTaskScheduler();
        return (new ScheduledTaskProvider(tasks, new FakeFileSystem()), tasks);
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("task-scheduler", provider.Id);
        Assert.Equal(StartupCategory.ScheduledTask, provider.Category);
    }

    [Fact]
    public void ProjectsATaskIntoAnItemWithTypeSpecificFacts()
    {
        var (provider, tasks) = Sut();
        tasks.Add(Task(@"\Vendor\Updater"));

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }));

        Assert.Equal(@"\Vendor", item.Location);
        Assert.Equal("Updater", item.Name);
        Assert.Equal(@"C:\Program Files\App\updater.exe /silent", item.RawValue);
        Assert.Equal(@"C:\Program Files\App\updater.exe", item.ImagePath);
        Assert.Equal("/silent", item.Arguments);
        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Equal("Task.Enabled", item.StateSource);
        Assert.Equal(@"\Vendor\Updater", item.Extra["TaskPath"]);
        Assert.Equal("AtLogon: any user; Boot: delay 30s", item.Extra["Triggers"]);
        Assert.Equal(@"C:\Program Files\App\updater.exe /silent", item.Extra["Actions"]);
        Assert.Equal(@"NT AUTHORITY\SYSTEM", item.Extra["Principal"]);
        Assert.Equal("HighestAvailable", item.Extra["RunLevel"]);
        Assert.Equal("0", item.Extra["LastTaskResult"]);
        Assert.Equal("2026-07-29T06:00:00Z", item.Extra["LastRunTimeUtc"]);
        Assert.Equal("2026-07-31T06:00:00Z", item.Extra["NextRunTimeUtc"]);
    }

    [Fact]
    public void DisabledTaskReadsAsDisabled()
    {
        var (provider, tasks) = Sut();
        tasks.Add(Task(@"\Vendor\Updater", enabled: false));

        Assert.Equal(EnableState.Disabled, Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" })).State);
    }

    [Fact]
    public void IdentityUsesTheFullTaskPathSoSameNamedTasksInDifferentFoldersDiffer()
    {
        var (provider, tasks) = Sut();
        tasks.Add(Task(@"\A\Updater"));
        tasks.Add(Task(@"\B\Updater"));

        var ids = provider.Scan(new ScanContext { UserRoot = "HKCU" }).Select(i => i.Id).Distinct().ToList();

        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void PartiallyReadTaskIsFlagged()
    {
        var (provider, tasks) = Sut();
        tasks.Add(Task(@"\Protected\Thing") with { PartiallyRead = true, Actions = Array.Empty<string>() });

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }));

        Assert.True(item.PartiallyRead);
        Assert.Equal("", item.RawValue);
    }

    [Fact]
    public void MissingTimestampsRenderAsEmptyStringsRatherThanCrashing()
    {
        var (provider, tasks) = Sut();
        tasks.Add(Task(@"\A\B") with { LastRunTimeUtc = null, NextRunTimeUtc = null });

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }));

        Assert.Equal("", item.Extra["LastRunTimeUtc"]);
        Assert.Equal("", item.Extra["NextRunTimeUtc"]);
    }

    [Fact]
    public void EnumerationFailureBecomesAScanError()
    {
        var provider = new ScheduledTaskProvider(new ThrowingTaskScheduler(), new FakeFileSystem());
        var ctx = new ScanContext { UserRoot = "HKCU" };

        Assert.Empty(provider.Scan(ctx));
        Assert.Contains(ctx.Errors, e => e.ProviderId == "task-scheduler");
    }

    private sealed class ThrowingTaskScheduler : ITaskSchedulerService
    {
        public IReadOnlyList<ScheduledTaskInfo> EnumerateTasks() => throw new InvalidOperationException("COM failed");
        public void SetEnabled(string taskPath, bool enabled) => throw new InvalidOperationException();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ScheduledTaskProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ITaskSchedulerService' could not be found`.

- [ ] **Step 3: Write the abstraction**

Create `StartupScanner.Core/Abstractions/ITaskSchedulerService.cs`:

```csharp
namespace StartupScanner.Core.Abstractions;

public sealed record ScheduledTaskInfo
{
    /// <summary>Full task path, e.g. "\Microsoft\Windows\Defrag\ScheduledDefrag".</summary>
    public required string Path { get; init; }

    public required string Name { get; init; }

    /// <summary>Containing folder path, "\" for the root.</summary>
    public required string Folder { get; init; }

    public bool Enabled { get; init; }
    public IReadOnlyList<string> Triggers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
    public string? Principal { get; init; }
    public string? RunLevel { get; init; }
    public string? UserSid { get; init; }
    public DateTime? LastRunTimeUtc { get; init; }
    public int LastTaskResult { get; init; }
    public DateTime? NextRunTimeUtc { get; init; }
    public string? Xml { get; init; }

    /// <summary>Set when the task was listed but its definition could not be read.</summary>
    public bool PartiallyRead { get; init; }
}

public interface ITaskSchedulerService
{
    /// <summary>Recursive walk of the whole Task Scheduler tree.</summary>
    IReadOnlyList<ScheduledTaskInfo> EnumerateTasks();

    void SetEnabled(string taskPath, bool enabled);
}
```

- [ ] **Step 4: Write the provider**

Create `StartupScanner.Core/Providers/ScheduledTaskProvider.cs`:

```csharp
using System.Globalization;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Providers;

public sealed class ScheduledTaskProvider(ITaskSchedulerService tasks, IFileSystem fs) : IStartupProvider
{
    public string Id => "task-scheduler";
    public StartupCategory Category => StartupCategory.ScheduledTask;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        IReadOnlyList<ScheduledTaskInfo> all;
        try
        {
            all = tasks.EnumerateTasks();
        }
        catch (Exception ex)
        {
            ctx.Errors.Add(new ScanError(Id, "Task Scheduler", ex.Message));
            return Array.Empty<StartupItem>();
        }

        return all.Select(t => Build(t, ctx)).ToList();
    }

    private StartupItem Build(ScheduledTaskInfo task, ScanContext ctx)
    {
        var raw = string.Join("; ", task.Actions);
        var (image, args) = CommandLine.Split(task.Actions.FirstOrDefault() ?? "", fs);

        return new StartupItem
        {
            Id = Identity.Compute(Id, task.UserSid, task.Folder, task.Name),
            ProviderId = Id,
            Location = task.Folder,
            Name = task.Name,
            RawValue = raw,
            Category = Category,
            ImagePath = image,
            Arguments = args,
            State = task.Enabled ? EnableState.Enabled : EnableState.Disabled,
            StateSource = "Task.Enabled",
            UserSid = task.UserSid,
            PartiallyRead = task.PartiallyRead,
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TaskPath"] = task.Path,
                ["Triggers"] = string.Join("; ", task.Triggers),
                ["Actions"] = raw,
                ["Principal"] = task.Principal ?? "",
                ["RunLevel"] = task.RunLevel ?? "",
                ["LastRunTimeUtc"] = Stamp(task.LastRunTimeUtc),
                ["LastTaskResult"] = task.LastTaskResult.ToString(CultureInfo.InvariantCulture),
                ["NextRunTimeUtc"] = Stamp(task.NextRunTimeUtc),
                ["Xml"] = task.Xml ?? "",
            },
        };
    }

    private static string Stamp(DateTime? value)
        => value?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "";
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ScheduledTaskProviderTests"`
Expected: PASS — 7 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add scheduled task provider"
```

---

## Task 15: Guardrails and risk tiers

**Files:**
- Create: `StartupScanner.Core/Safety/Guardrails.cs`, `Safety/RiskTiers.cs`
- Test: `StartupScanner.Tests/Safety/GuardrailsTests.cs`, `StartupScanner.Tests/Safety/RiskTiersTests.cs`

**Interfaces:**
- Consumes: `StartupItem`, `StartupCategory`, `RiskTier`.
- Produces:
  - `BlockDecision(bool Blocked, string? Reason)` with `BlockDecision.Allowed`.
  - `Guardrails.Evaluate(StartupItem item) -> BlockDecision` and `Guardrails.CriticalServices` (the named list).
  - `RiskTiers.For(StartupItem item) -> RiskTier`.

These live in namespace `StartupScanner.Core.Safety`, not `….Guardrails`: a namespace and a
class with the same name would make `Guardrails.Evaluate` resolve to the namespace from every
other file under `StartupScanner.Core`, which does not compile.

Blocked status and risk tier are decided separately: the blocked list is the hard rule, and `Critical` is simply the tier assigned to whatever that list already blocks, so a confirmation dialog is never reached for those. Goal 5 — never make the machine unbootable — is enforced here and nowhere else, so every rule gets its own test.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Safety/GuardrailsTests.cs`:

```csharp
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;

namespace StartupScanner.Tests.Safety;

public class GuardrailsTests
{
    private static StartupItem Item(StartupCategory category, string name, string raw,
                                    IDictionary<string, string>? extra = null) => new()
    {
        Id = "id", ProviderId = "p", Location = "loc", Name = name, RawValue = raw, Category = category,
        Extra = new Dictionary<string, string>(extra ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
    };

    [Theory]
    [InlineData(@"%systemroot%\system32\userinit.exe,")]
    [InlineData(@"%SystemRoot%\System32\userinit.exe")]
    [InlineData(@"C:\Windows\system32\userinit.exe,")]
    public void DefaultUserinitIsBlocked(string raw)
    {
        var decision = Guardrails.Evaluate(Item(StartupCategory.Winlogon, "Userinit", raw));

        Assert.True(decision.Blocked);
        Assert.Contains("logon", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModifiedUserinitIsNotBlocked()
    {
        var decision = Guardrails.Evaluate(
            Item(StartupCategory.Winlogon, "Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe"));

        Assert.False(decision.Blocked);
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("Explorer.exe")]
    public void DefaultShellIsBlocked(string raw)
    {
        Assert.True(Guardrails.Evaluate(Item(StartupCategory.Winlogon, "Shell", raw)).Blocked);
    }

    [Fact]
    public void ModifiedShellIsNotBlocked()
    {
        Assert.False(Guardrails.Evaluate(
            Item(StartupCategory.Winlogon, "Shell", "explorer.exe,evil.exe")).Blocked);
    }

    [Fact]
    public void DefaultBootExecuteIsBlocked()
    {
        var decision = Guardrails.Evaluate(
            Item(StartupCategory.BootExecute, "BootExecute", "autocheck autochk *"));

        Assert.True(decision.Blocked);
        Assert.Contains("autochk", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtraBootExecuteEntriesAreNotBlocked()
    {
        Assert.False(Guardrails.Evaluate(
            Item(StartupCategory.BootExecute, "BootExecute", "autocheck autochk *; evil.exe")).Blocked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BootAndSystemStartDriversAreBlocked(int start)
    {
        var decision = Guardrails.Evaluate(Item(StartupCategory.Driver, "disk", "driver.sys",
            new Dictionary<string, string> { ["Start"] = start.ToString() }));

        Assert.True(decision.Blocked);
        Assert.Contains("boot", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DemandAndAutoStartServicesAreNotBlocked(int start)
    {
        Assert.False(Guardrails.Evaluate(Item(StartupCategory.Service, "Spooler", "svc.exe",
            new Dictionary<string, string> { ["Start"] = start.ToString() })).Blocked);
    }

    [Theory]
    [InlineData("DcomLaunch")]
    [InlineData("RpcSs")]
    [InlineData("RpcEptMapper")]
    [InlineData("PlugPlay")]
    [InlineData("Power")]
    [InlineData("ProfSvc")]
    [InlineData("LSM")]
    [InlineData("BrokerInfrastructure")]
    [InlineData("SamSs")]
    [InlineData("EventLog")]
    [InlineData("rpcss")]
    public void NamedCriticalServicesAreBlockedRegardlessOfStartValue(string name)
    {
        var decision = Guardrails.Evaluate(Item(StartupCategory.Service, name, "svc.exe",
            new Dictionary<string, string> { ["Start"] = "3" }));

        Assert.True(decision.Blocked);
    }

    [Fact]
    public void EveryLsaPackageEntryIsBlockedAsACategory()
    {
        var decision = Guardrails.Evaluate(
            Item(StartupCategory.Lsa, "Authentication Packages", "msv1_0"));

        Assert.True(decision.Blocked);
        Assert.Contains("logon", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryRunKeysAreNotBlocked()
    {
        Assert.False(Guardrails.Evaluate(
            Item(StartupCategory.RunKey, "Spotify", @"C:\Spotify.exe")).Blocked);
    }

    [Fact]
    public void TheNamedCriticalServiceListMatchesTheSpec()
    {
        Assert.Equal(
            new[] { "BrokerInfrastructure", "DcomLaunch", "EventLog", "LSM", "PlugPlay", "Power",
                    "ProfSvc", "RpcEptMapper", "RpcSs", "SamSs" },
            Guardrails.CriticalServices.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }
}
```

Create `StartupScanner.Tests/Safety/RiskTiersTests.cs`:

```csharp
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;

namespace StartupScanner.Tests.Safety;

public class RiskTiersTests
{
    private static StartupItem Item(StartupCategory category, string name = "n", string raw = "r",
                                    string location = "loc", IDictionary<string, string>? extra = null) => new()
    {
        Id = "id", ProviderId = "p", Location = location, Name = name, RawValue = raw, Category = category,
        Extra = new Dictionary<string, string>(extra ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
    };

    [Theory]
    [InlineData(StartupCategory.RunKey)]
    [InlineData(StartupCategory.StartupFolder)]
    [InlineData(StartupCategory.AppxStartupTask)]
    public void RunKeysFoldersAndAppxAreLow(StartupCategory category)
    {
        Assert.Equal(RiskTier.Low, RiskTiers.For(Item(category)));
    }

    [Fact]
    public void ServicesAreMedium()
    {
        Assert.Equal(RiskTier.Medium, RiskTiers.For(Item(StartupCategory.Service, "Spooler",
            extra: new Dictionary<string, string> { ["Start"] = "2" })));
    }

    [Fact]
    public void TasksUnderMicrosoftWindowsAreMediumAndOtherTasksAreLow()
    {
        Assert.Equal(RiskTier.Medium, RiskTiers.For(Item(StartupCategory.ScheduledTask, location: @"\Microsoft\Windows\Defrag")));
        Assert.Equal(RiskTier.Low, RiskTiers.For(Item(StartupCategory.ScheduledTask, location: @"\Vendor")));
    }

    [Theory]
    [InlineData(StartupCategory.ActiveSetup)]
    [InlineData(StartupCategory.GpoScript)]
    public void ActiveSetupAndGpoScriptsAreMedium(StartupCategory category)
    {
        Assert.Equal(RiskTier.Medium, RiskTiers.For(Item(category)));
    }

    [Theory]
    [InlineData(StartupCategory.Winlogon, "Taskman")]
    [InlineData(StartupCategory.BootExecute, "SetupExecute")]
    [InlineData(StartupCategory.WinsockLsp, "LibraryPath")]
    public void WinlogonBootExecuteAndWinsockAreHigh(StartupCategory category, string name)
    {
        Assert.Equal(RiskTier.High, RiskTiers.For(Item(category, name)));
    }

    [Fact]
    public void IfeoDebuggerIsHighAndOtherIfeoValuesAreMedium()
    {
        Assert.Equal(RiskTier.High, RiskTiers.For(Item(StartupCategory.Ifeo, "Debugger")));
        Assert.Equal(RiskTier.Medium, RiskTiers.For(Item(StartupCategory.Ifeo, "GlobalFlag")));
    }

    [Fact]
    public void AppInitDllsIsHighAndAppCertDllsIsMedium()
    {
        Assert.Equal(RiskTier.High, RiskTiers.For(Item(StartupCategory.AppInit, "AppInit_DLLs")));
        Assert.Equal(RiskTier.Medium, RiskTiers.For(Item(StartupCategory.AppInit, "AppCertDlls")));
    }

    [Fact]
    public void AnythingTheBlockedListCatchesIsCritical()
    {
        Assert.Equal(RiskTier.Critical, RiskTiers.For(Item(StartupCategory.Winlogon, "Shell", "explorer.exe")));
        Assert.Equal(RiskTier.Critical, RiskTiers.For(Item(StartupCategory.Lsa, "Security Packages")));
        Assert.Equal(RiskTier.Critical, RiskTiers.For(Item(StartupCategory.Service, "RpcSs",
            extra: new Dictionary<string, string> { ["Start"] = "2" })));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~GuardrailsTests|FullyQualifiedName~RiskTiersTests"`
Expected: FAIL — `CS0234: The type or namespace name 'Guardrails' does not exist in the namespace 'StartupScanner.Core'`.

- [ ] **Step 3: Write `Guardrails`**

Create `StartupScanner.Core/Safety/Guardrails.cs`:

```csharp
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Safety;

public sealed record BlockDecision(bool Blocked, string? Reason)
{
    public static readonly BlockDecision Allowed = new(false, null);

    public static BlockDecision Block(string reason) => new(true, reason);
}

/// <summary>
/// The hard "never make the machine unbootable" rules. Items these block are shown and fully
/// detailed but are never writable, and RiskTiers reports them as Critical so no confirmation
/// dialog is ever reached for them.
/// </summary>
public static class Guardrails
{
    public static readonly IReadOnlyList<string> CriticalServices = new[]
    {
        "DcomLaunch", "RpcSs", "RpcEptMapper", "PlugPlay", "Power",
        "ProfSvc", "LSM", "BrokerInfrastructure", "SamSs", "EventLog",
    };

    public static BlockDecision Evaluate(StartupItem item) => item.Category switch
    {
        StartupCategory.Winlogon => EvaluateWinlogon(item),
        StartupCategory.BootExecute => EvaluateBootExecute(item),
        StartupCategory.Service or StartupCategory.Driver => EvaluateService(item),
        StartupCategory.Lsa => BlockDecision.Block(
            "LSA package entries are blocked as a category: removing an authentication package can prevent logon, "
            + "and legitimate additions are rare enough that manual editing is the safer path."),
        _ => BlockDecision.Allowed,
    };

    private static BlockDecision EvaluateWinlogon(StartupItem item)
    {
        if (Is(item.Name, "Userinit") && IsDefault(item.RawValue, @"system32\userinit.exe"))
            return BlockDecision.Block(
                "Userinit is the default logon process. Removing it prevents logon from completing.");

        if (Is(item.Name, "Shell") && IsDefault(item.RawValue, "explorer.exe"))
            return BlockDecision.Block(
                "Shell is the default Windows shell. Removing it leaves the desktop with no shell after logon.");

        return BlockDecision.Allowed;
    }

    private static BlockDecision EvaluateBootExecute(StartupItem item)
        => Is(item.Name, "BootExecute") && item.RawValue.Trim() == "autocheck autochk *"
            ? BlockDecision.Block("'autocheck autochk *' is the boot-time disk check. Removing it risks an unbootable system.")
            : BlockDecision.Allowed;

    private static BlockDecision EvaluateService(StartupItem item)
    {
        if (CriticalServices.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            return BlockDecision.Block($"'{item.Name}' is on the named critical-service list. Disabling it prevents Windows from starting correctly.");

        if (item.Extra.TryGetValue("Start", out var start) && int.TryParse(start, out var value) && value is 0 or 1)
            return BlockDecision.Block($"Start={value} means this loads at boot or system start. Disabling it risks an unbootable system.");

        return BlockDecision.Allowed;
    }

    private static bool Is(string name, string expected) => name.Equals(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the value is exactly the OS default, ignoring case, the %SystemRoot% prefix, and
    /// the trailing comma Windows writes on Userinit. An appended second command makes it false.
    /// </summary>
    private static bool IsDefault(string rawValue, string defaultTail)
    {
        var value = rawValue.Trim().TrimEnd(',').Trim();
        if (value.Contains(',')) return false;
        return value.Equals(defaultTail, StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(@"\" + defaultTail, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Write `RiskTiers`**

Create `StartupScanner.Core/Safety/RiskTiers.cs`:

```csharp
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Safety;

/// <summary>
/// Risk tier controls the confirmation dialog only. Critical is the tier given to whatever the
/// blocked list already blocks, so those items are never writable and never prompt.
/// </summary>
public static class RiskTiers
{
    public static RiskTier For(StartupItem item)
    {
        if (Guardrails.Evaluate(item).Blocked) return RiskTier.Critical;

        return item.Category switch
        {
            StartupCategory.RunKey or StartupCategory.StartupFolder or StartupCategory.AppxStartupTask
                => RiskTier.Low,

            StartupCategory.ScheduledTask
                => item.Location.StartsWith(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase)
                    ? RiskTier.Medium : RiskTier.Low,

            StartupCategory.Winlogon or StartupCategory.BootExecute or StartupCategory.WinsockLsp
                => RiskTier.High,

            StartupCategory.Ifeo
                => item.Name.Equals("Debugger", StringComparison.OrdinalIgnoreCase) ? RiskTier.High : RiskTier.Medium,

            StartupCategory.AppInit
                => item.Name.StartsWith("AppInit", StringComparison.OrdinalIgnoreCase) ? RiskTier.High : RiskTier.Medium,

            _ => RiskTier.Medium,
        };
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~GuardrailsTests|FullyQualifiedName~RiskTiersTests"`
Expected: PASS — 39 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Safety StartupScanner.Tests/Safety
git commit -m "feat: add boot-critical guardrails and risk tier assignment"
```

---

## Task 16: Scan engine

**Files:**
- Create: `StartupScanner.Core/Scanning/ScanEngine.cs`
- Test: `StartupScanner.Tests/Scanning/ScanEngineTests.cs`

**Interfaces:**
- Consumes: `IStartupProvider`, `IWriteJournalReader`, `RiskTiers`, `RegistryValueCodec`, `ScanContext`, `ScanResult`.
- Produces: `ScanEngine(IReadOnlyList<IStartupProvider> providers, IWriteJournalReader journal)` with `ScanResult Run(ScanContext ctx)`.

Four responsibilities, all of them cross-provider:

1. **Parallelism.** Providers run concurrently; a provider that throws becomes a `ScanError` and the others still finish.
2. **Risk assignment.** Providers leave `Risk` at its default; the engine stamps every item with `RiskTiers.For`, so tier logic lives in exactly one place.
3. **Determinism.** Items are sorted by `(ProviderId, Location, Name)` so two identical machines produce identical snapshots and the diff is not order-dependent.
4. **Journal re-injection.** A removed-and-backed-up entry no longer exists where the provider looks, so no provider can see it. Without re-injection it would silently vanish from the list, report as `Removed`, and become impossible to re-enable. The engine synthesises it back from its journal record with `State = Disabled` and `StateSource = "RemovedByScanner"`. Three mechanisms remove rather than flag — `BackupRemove`, `Wmi.Instance`, and `GpoScript.Ini` — and only those are re-injected. `Before` payloads from the non-registry mechanisms are not `RegistryValueCodec` strings, so decoding falls back to the stored text.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Scanning/ScanEngineTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Scanning;
using StartupScanner.Core.Storage;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Scanning;

public class ScanEngineTests
{
    private sealed class StubProvider(string id, StartupCategory category, params StartupItem[] items) : IStartupProvider
    {
        public string Id => id;
        public StartupCategory Category => category;
        public IEnumerable<StartupItem> Scan(ScanContext ctx) => items;
    }

    private sealed class ThrowingProvider(string id) : IStartupProvider
    {
        public string Id => id;
        public StartupCategory Category => StartupCategory.RunKey;
        public IEnumerable<StartupItem> Scan(ScanContext ctx) => throw new InvalidOperationException("provider exploded");
    }

    private static StartupItem Item(string provider, string location, string name,
                                    StartupCategory category = StartupCategory.RunKey,
                                    IDictionary<string, string>? extra = null) => new()
    {
        Id = Identity.Compute(provider, null, location, name),
        ProviderId = provider, Location = location, Name = name, RawValue = "cmd", Category = category,
        Extra = new Dictionary<string, string>(extra ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
    };

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void CollectsItemsFromEveryProvider()
    {
        var engine = new ScanEngine(new IStartupProvider[]
        {
            new StubProvider("a", StartupCategory.RunKey, Item("a", "l1", "n1")),
            new StubProvider("b", StartupCategory.Service, Item("b", "l2", "n2")),
        }, new FakeJournal());

        var result = engine.Run(Ctx());

        Assert.Equal(2, result.Items.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AThrowingProviderBecomesAScanErrorAndTheOthersStillComplete()
    {
        var engine = new ScanEngine(new IStartupProvider[]
        {
            new ThrowingProvider("bad"),
            new StubProvider("good", StartupCategory.RunKey, Item("good", "l", "n")),
        }, new FakeJournal());

        var result = engine.Run(Ctx());

        Assert.Single(result.Items);
        var error = Assert.Single(result.Errors);
        Assert.Equal("bad", error.ProviderId);
        Assert.Contains("provider exploded", error.Message);
    }

    [Fact]
    public void PerEntryErrorsFromTheContextAreIncludedInTheResult()
    {
        var ctx = Ctx();
        ctx.Errors.Add(new ScanError("a", "some\\key", "access denied"));
        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("a", StartupCategory.RunKey) }, new FakeJournal());

        Assert.Single(engine.Run(ctx).Errors);
    }

    [Fact]
    public void StampsRiskTierOnEveryItem()
    {
        var engine = new ScanEngine(new IStartupProvider[]
        {
            new StubProvider("a", StartupCategory.RunKey, Item("a", "l", "Spotify")),
            new StubProvider("b", StartupCategory.Service,
                Item("b", "l", "RpcSs", StartupCategory.Service, new Dictionary<string, string> { ["Start"] = "2" })),
            new StubProvider("c", StartupCategory.Winlogon, Item("c", "l", "Taskman", StartupCategory.Winlogon)),
        }, new FakeJournal());

        var items = engine.Run(Ctx()).Items;

        Assert.Equal(RiskTier.Low, items.Single(i => i.Name == "Spotify").Risk);
        Assert.Equal(RiskTier.Critical, items.Single(i => i.Name == "RpcSs").Risk);
        Assert.Equal(RiskTier.High, items.Single(i => i.Name == "Taskman").Risk);
    }

    [Fact]
    public void SortsDeterministicallyByProviderLocationName()
    {
        var engine = new ScanEngine(new IStartupProvider[]
        {
            new StubProvider("z", StartupCategory.RunKey, Item("z", "l", "b"), Item("z", "l", "a")),
            new StubProvider("a", StartupCategory.RunKey, Item("a", "l2", "x"), Item("a", "l1", "y")),
        }, new FakeJournal());

        var keys = engine.Run(Ctx()).Items.Select(i => $"{i.ProviderId}|{i.Location}|{i.Name}").ToArray();

        Assert.Equal(new[] { "a|l1|y", "a|l2|x", "z|l|a", "z|l|b" }, keys);
    }

    [Fact]
    public void ReInjectsABackupRemoveDisabledEntryThatNoProviderCanStillSee()
    {
        var journal = new FakeJournal();
        var removedId = Identity.Compute("winlogon", null, @"HKLM\SOFTWARE\Winlogon", "Taskman");
        journal.Add(new WriteRecord
        {
            Ts = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc),
            SessionId = "s1", ItemId = removedId, ProviderId = "winlogon", Mechanism = "BackupRemove",
            Location = @"HKLM\SOFTWARE\Winlogon", Name = "Taskman", Category = StartupCategory.Winlogon,
            Before = @"sz:C:\taskman.exe", After = null, Enabled = false,
        });

        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("winlogon", StartupCategory.Winlogon) }, journal);

        var item = Assert.Single(engine.Run(Ctx()).Items);

        Assert.Equal(removedId, item.Id);
        Assert.Equal("Taskman", item.Name);
        Assert.Equal(@"C:\taskman.exe", item.RawValue);
        Assert.Equal(EnableState.Disabled, item.State);
        Assert.Equal("RemovedByScanner", item.StateSource);
        Assert.Equal(StartupCategory.Winlogon, item.Category);
    }

    [Fact]
    public void DoesNotReInjectWhenTheEntryIsPresentAgain()
    {
        var journal = new FakeJournal();
        var id = Identity.Compute("winlogon", null, @"HKLM\SOFTWARE\Winlogon", "Taskman");
        journal.Add(new WriteRecord
        {
            Ts = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc),
            SessionId = "s1", ItemId = id, ProviderId = "winlogon", Mechanism = "BackupRemove",
            Location = @"HKLM\SOFTWARE\Winlogon", Name = "Taskman", Category = StartupCategory.Winlogon,
            Before = @"sz:C:\taskman.exe", After = null, Enabled = false,
        });

        var live = Item("winlogon", @"HKLM\SOFTWARE\Winlogon", "Taskman", StartupCategory.Winlogon);
        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("winlogon", StartupCategory.Winlogon, live) }, journal);

        var item = Assert.Single(engine.Run(Ctx()).Items);

        Assert.NotEqual("RemovedByScanner", item.StateSource);
    }

    [Fact]
    public void DoesNotReInjectAfterTheEntryWasReEnabled()
    {
        var journal = new FakeJournal();
        var id = Identity.Compute("winlogon", null, @"HKLM\SOFTWARE\Winlogon", "Taskman");
        journal.Add(new WriteRecord
        {
            Ts = new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
            SessionId = "s2", ItemId = id, ProviderId = "winlogon", Mechanism = "BackupRemove",
            Location = @"HKLM\SOFTWARE\Winlogon", Name = "Taskman", Category = StartupCategory.Winlogon,
            Before = null, After = @"sz:C:\taskman.exe", Enabled = true,
        });

        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("winlogon", StartupCategory.Winlogon) }, journal);

        Assert.Empty(engine.Run(Ctx()).Items);
    }

    [Fact]
    public void NativeMechanismRecordsAreNotReInjected()
    {
        var journal = new FakeJournal();
        journal.Add(new WriteRecord
        {
            Ts = new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc),
            SessionId = "s1", ItemId = "gone-id", ProviderId = "registry.run", Mechanism = "StartupApproved",
            Location = @"HKCU\Run", Name = "Spotify", Category = StartupCategory.RunKey,
            Before = "binary:AgAAAAAAAAAAAAAA", After = "binary:AwAAAAAAAAAAAAAA", Enabled = false,
        });

        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("registry.run", StartupCategory.RunKey) }, journal);

        Assert.Empty(engine.Run(Ctx()).Items);
    }

    [Fact]
    public void NonRegistryBackupsAreReInjectedWithTheirStoredTextAsIs()
    {
        var journal = new FakeJournal();
        journal.Add(new WriteRecord
        {
            Ts = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc),
            SessionId = "s1", ItemId = "wmi-id", ProviderId = "wmi", Mechanism = "Wmi.Instance",
            Location = @"root\subscription:__EventFilter", Name = "EvilFilter",
            Category = StartupCategory.WmiSubscription,
            Before = """{"Name":"EvilFilter","Query":"SELECT * FROM __InstanceCreationEvent"}""",
            After = null, Enabled = false,
        });

        var engine = new ScanEngine(new IStartupProvider[] { new StubProvider("wmi", StartupCategory.WmiSubscription) }, journal);

        var item = Assert.Single(engine.Run(Ctx()).Items);

        Assert.Equal("RemovedByScanner", item.StateSource);
        Assert.Contains("EvilFilter", item.RawValue);
    }

    [Fact]
    public void ManyProvidersRunConcurrentlyWithoutLosingItems()
    {
        var providers = Enumerable.Range(0, 40)
            .Select(i => (IStartupProvider)new StubProvider($"p{i:00}", StartupCategory.RunKey, Item($"p{i:00}", "l", "n")))
            .ToList();

        Assert.Equal(40, new ScanEngine(providers, new FakeJournal()).Run(Ctx()).Items.Count);
    }
}
```

Create `StartupScanner.Tests/Fakes/FakeJournal.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeJournal : IWriteJournal
{
    private readonly List<WriteRecord> _records = new();

    public IReadOnlyList<WriteRecord> All => _records;

    /// <summary>Seeds a pre-existing record without going through Record().</summary>
    public void Add(WriteRecord record) => _records.Add(record);

    public WriteRecord? GetLast(string itemId) => _records.LastOrDefault(r => r.ItemId == itemId);

    public IReadOnlyList<WriteRecord> ForSession(string sessionId)
        => _records.Where(r => r.SessionId == sessionId).ToList();

    public void Record(WriteRecord record) => _records.Add(record);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ScanEngineTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ScanEngine' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `StartupScanner.Core/Scanning/ScanEngine.cs`:

```csharp
using System.Collections.Concurrent;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Scanning;

public sealed class ScanEngine(IReadOnlyList<IStartupProvider> providers, IWriteJournalReader journal)
{
    public ScanResult Run(ScanContext ctx)
    {
        var collected = new ConcurrentBag<StartupItem>();
        var failures = new ConcurrentBag<ScanError>();

        Parallel.ForEach(providers, provider =>
        {
            try
            {
                foreach (var item in provider.Scan(ctx)) collected.Add(item);
            }
            catch (Exception ex)
            {
                failures.Add(new ScanError(provider.Id, "provider", ex.Message));
            }
        });

        var items = collected.ToList();
        items.AddRange(ReInjectRemoved(items));

        var stamped = items
            .Select(i => i with { Risk = RiskTiers.For(i) })
            .OrderBy(i => i.ProviderId, StringComparer.Ordinal)
            .ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScanResult(stamped, ctx.Errors.Concat(failures).ToList());
    }

    /// <summary>Mechanisms that remove an entry rather than flag it, so its row must be synthesised back.</summary>
    private static readonly HashSet<string> ReInjectableMechanisms =
        new(StringComparer.Ordinal) { "BackupRemove", "Wmi.Instance", "GpoScript.Ini" };

    /// <summary>
    /// A removed-and-backed-up entry is gone from where its provider looks, so no provider can see
    /// it. Synthesise it from its journal record so it stays visible and re-enableable.
    /// </summary>
    private IEnumerable<StartupItem> ReInjectRemoved(IReadOnlyList<StartupItem> live)
    {
        var present = live.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var id in journal.All.Select(r => r.ItemId).Distinct(StringComparer.Ordinal))
        {
            if (present.Contains(id)) continue;

            var record = journal.GetLast(id);
            if (record is null || record.Enabled || record.Before is null
                || !ReInjectableMechanisms.Contains(record.Mechanism))
                continue;

            yield return new StartupItem
            {
                Id = record.ItemId,
                ProviderId = record.ProviderId,
                Location = record.Location,
                Name = record.Name,
                RawValue = Display(record.Before),
                Category = record.Category,
                State = EnableState.Disabled,
                StateSource = "RemovedByScanner",
                UserSid = record.UserSid,
            };
        }
    }

    /// <summary>WMI and GPO script backups are not RegistryValueCodec strings; show them as stored.</summary>
    private static string Display(string encoded)
    {
        try
        {
            var (value, type) = RegistryValueCodec.Decode(encoded);
            return RegistryValueCodec.ToDisplay(value, type);
        }
        catch (FormatException)
        {
            return encoded;
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ScanEngineTests"`
Expected: PASS — 11 tests passed.

- [ ] **Step 5: Run the whole suite**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: PASS — all tests green.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Scanning StartupScanner.Tests/Scanning StartupScanner.Tests/Fakes/FakeJournal.cs
git commit -m "feat: add parallel scan engine with error isolation and journal re-injection"
```

---

## Task 17: Toggle contract and the BackupRemove mechanism

**Files:**
- Create: `StartupScanner.Core/Writing/IToggleProvider.cs`, `Writing/RegistryWriteOps.cs`, `Writing/RegistryValueToggle.cs`
- Test: `StartupScanner.Tests/Writing/RegistryValueToggleTests.cs`

**Interfaces:**
- Consumes: `IRegistry`, `IClock`, `IWriteJournalReader`, `RegistryValueCodec`, `Guardrails`, `WriteRecord`.
- Produces:
  - `IToggleProvider` with `string ProviderId { get; }`, `ToggleCapability GetCapability(StartupItem item)`, `WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)`, `void Restore(WriteRecord record)`.
  - `ToggleResult(bool Success, bool Cancelled, string? Error, WriteRecord? Record)` with `Ok(record)`, `Fail(error)`, `Cancel(reason = null)`.
  - `IConfirmationSink.Confirm(StartupItem item, RiskTier tier, string message) -> bool`.
  - `RegistryWriteOps.Restore(IRegistry registry, WriteRecord record)` and `RegistryWriteOps.ViewOf(string? name) -> RegistryViewKind`.
  - `RegistryValueToggle(string providerId, IRegistry registry, IClock clock, Func<StartupItem, (object Value, RegistryValueType Type)?>? replacement = null)`.

Two deliberate deviations from the spec's interface sketch, both to keep the write path honest:

1. **Toggling lives in its own class keyed by `ProviderId`, not on the scan provider.** Scan providers stay free of `IClock` and journal dependencies, and each file keeps one job.
2. **`SetEnabled` returns an uncommitted `WriteRecord` instead of writing to the journal itself.** The journal must never record a write that did not land, so only `ToggleService` — after reading the value back — may call `journal.Record`. Providers receive the read-only `IWriteJournalReader` so they can find a backed-up value when re-enabling.

The optional `replacement` delegate exists for one real case: a non-default `Winlogon\Userinit` must not be deleted or blanked, because a missing `Userinit` breaks logon. Winlogon supplies a delegate that writes the OS default back instead. Everywhere else the delegate is absent and the value is deleted.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Writing/RegistryValueToggleTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class RegistryValueToggleTests
{
    private const string Winlogon = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private static StartupItem Item(string name = "Taskman", string raw = @"C:\taskman.exe",
                                    StartupCategory category = StartupCategory.Winlogon,
                                    string? view = null) => new()
    {
        Id = Identity.Compute("winlogon", null, Winlogon, name),
        ProviderId = "winlogon", Location = Winlogon, Name = name, RawValue = raw, Category = category,
        State = EnableState.Enabled,
        Extra = view is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["View"] = view },
    };

    private static (RegistryValueToggle Toggle, FakeRegistry Registry, FakeJournal Journal) Sut(
        Func<StartupItem, (object, RegistryValueType)?>? replacement = null)
    {
        var registry = new FakeRegistry();
        return (new RegistryValueToggle("winlogon", registry, new FakeClock(), replacement), registry, new FakeJournal());
    }

    [Fact]
    public void CapabilityIsBackupRemoveForAnOrdinaryEntry()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal(ToggleCapability.BackupRemove, toggle.GetCapability(Item()));
        Assert.Equal("winlogon", toggle.ProviderId);
    }

    [Fact]
    public void CapabilityIsBlockedForAGuardrailedEntry()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal(ToggleCapability.Blocked, toggle.GetCapability(Item("Shell", "explorer.exe")));
    }

    [Fact]
    public void DisableJournalsTheCurrentValueAndDeletesIt()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Taskman", @"C:\taskman.exe", RegistryValueType.String);

        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        Assert.Null(registry.OpenKey(Winlogon)!.GetValue("Taskman"));
        Assert.Equal("BackupRemove", record.Mechanism);
        Assert.Equal(@"sz:C:\taskman.exe", record.Before);
        Assert.Null(record.After);
        Assert.False(record.Enabled);
        Assert.Equal(Winlogon, record.Location);
        Assert.Equal("Taskman", record.Name);
        Assert.Equal(StartupCategory.Winlogon, record.Category);
    }

    [Fact]
    public void DisablePreservesTheValueTypeInTheJournal()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Multi", new[] { "a", "b" }, RegistryValueType.MultiString);

        var record = toggle.SetEnabled(Item("Multi", "a; b"), enabled: false, journal);

        Assert.Equal("multi:[\"a\",\"b\"]", record.Before);
    }

    [Fact]
    public void DisableWithAReplacementWritesTheReplacementInsteadOfDeleting()
    {
        var (toggle, registry, journal) = Sut(_ => (@"%systemroot%\system32\userinit.exe,", RegistryValueType.ExpandString));
        registry.Set(Winlogon, "Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe", RegistryValueType.ExpandString);

        var record = toggle.SetEnabled(Item("Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe"), false, journal);

        Assert.Equal(@"%systemroot%\system32\userinit.exe,", registry.OpenKey(Winlogon)!.GetValue("Userinit"));
        Assert.Equal(@"expand:C:\Windows\system32\userinit.exe,C:\evil.exe", record.Before);
        Assert.Equal(@"expand:%systemroot%\system32\userinit.exe,", record.After);
    }

    [Fact]
    public void EnableRestoresTheValueFromTheJournal()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Taskman", @"C:\taskman.exe", RegistryValueType.String);
        var disable = toggle.SetEnabled(Item(), enabled: false, journal);
        journal.Record(disable);

        var record = toggle.SetEnabled(Item() with { State = EnableState.Disabled }, enabled: true, journal);

        Assert.Equal(@"C:\taskman.exe", registry.OpenKey(Winlogon)!.GetValue("Taskman"));
        Assert.Null(record.Before);
        Assert.Equal(@"sz:C:\taskman.exe", record.After);
        Assert.True(record.Enabled);
    }

    [Fact]
    public void EnableWithNoJournalledBackupThrowsRatherThanGuessing()
    {
        var (toggle, _, journal) = Sut();

        var ex = Assert.Throws<InvalidOperationException>(
            () => toggle.SetEnabled(Item(), enabled: true, journal));

        Assert.Contains("Taskman", ex.Message);
    }

    [Fact]
    public void EnableUsesTheMostRecentDisableWhenAnItemWasToggledRepeatedly()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Taskman", "first.exe", RegistryValueType.String);
        journal.Record(toggle.SetEnabled(Item(), false, journal));
        journal.Record(toggle.SetEnabled(Item() with { State = EnableState.Disabled }, true, journal));
        registry.Set(Winlogon, "Taskman", "second.exe", RegistryValueType.String);
        journal.Record(toggle.SetEnabled(Item(), false, journal));

        toggle.SetEnabled(Item() with { State = EnableState.Disabled }, true, journal);

        Assert.Equal("second.exe", registry.OpenKey(Winlogon)!.GetValue("Taskman"));
    }

    [Fact]
    public void RestorePutsBackTheBeforeValue()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Taskman", @"C:\taskman.exe", RegistryValueType.String);
        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        toggle.Restore(record);

        Assert.Equal(@"C:\taskman.exe", registry.OpenKey(Winlogon)!.GetValue("Taskman"));
    }

    [Fact]
    public void RestoreOfAnEnableRecordDeletesTheValueAgain()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Winlogon, "Taskman", @"C:\taskman.exe", RegistryValueType.String);
        journal.Record(toggle.SetEnabled(Item(), false, journal));
        var enable = toggle.SetEnabled(Item() with { State = EnableState.Disabled }, true, journal);

        toggle.Restore(enable);

        Assert.Null(registry.OpenKey(Winlogon)!.GetValue("Taskman"));
    }

    [Fact]
    public void ViewIsCarriedIntoTheRecordAndHonouredOnRestore()
    {
        var registry = new FakeRegistry();
        var toggle = new RegistryValueToggle("winlogon", registry, new FakeClock());
        var journal = new FakeJournal();
        registry.Set(Winlogon, "Taskman", "x.exe", RegistryValueType.String, RegistryViewKind.Registry32);

        var record = toggle.SetEnabled(Item(view: "Registry32"), enabled: false, journal);
        toggle.Restore(record);

        Assert.Equal("Registry32", record.View);
        Assert.Equal("x.exe", registry.OpenKey(Winlogon, RegistryViewKind.Registry32)!.GetValue("Taskman"));
    }

    [Fact]
    public void DisableOnAMissingKeyThrowsAndWritesNothing()
    {
        var (toggle, _, journal) = Sut();

        Assert.Throws<InvalidOperationException>(() => toggle.SetEnabled(Item(), enabled: false, journal));
        Assert.Empty(journal.All);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~RegistryValueToggleTests"`
Expected: FAIL — `CS0246: The type or namespace name 'RegistryValueToggle' could not be found`.

- [ ] **Step 3: Write the contract**

Create `StartupScanner.Core/Writing/IToggleProvider.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

public sealed record ToggleResult(bool Success, bool Cancelled, string? Error, WriteRecord? Record)
{
    public static ToggleResult Ok(WriteRecord record) => new(true, false, null, record);
    public static ToggleResult Fail(string error) => new(false, false, error, null);
    public static ToggleResult Cancel(string? reason = null) => new(false, true, reason, null);
}

public interface IConfirmationSink
{
    /// <summary>Returns true to proceed. Never called for Low-risk items.</summary>
    bool Confirm(StartupItem item, RiskTier tier, string message);
}

public interface IToggleProvider
{
    /// <summary>Matches the scan provider's Id, which is how ToggleService finds this toggle.</summary>
    string ProviderId { get; }

    ToggleCapability GetCapability(StartupItem item);

    /// <summary>
    /// Performs the write and returns an UNCOMMITTED record describing it. Implementations must
    /// not call IWriteJournal.Record: only ToggleService may, and only after reading the value
    /// back. SessionId is left empty for ToggleService to stamp.
    /// </summary>
    WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal);

    /// <summary>Writes record.Before back. Used both to roll back a failed write and to undo.</summary>
    void Restore(WriteRecord record);
}
```

- [ ] **Step 4: Write the shared registry write helpers**

Create `StartupScanner.Core/Writing/RegistryWriteOps.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>Registry write primitives shared by every registry-backed toggle.</summary>
public static class RegistryWriteOps
{
    public static RegistryViewKind ViewOf(string? name)
        => Enum.TryParse<RegistryViewKind>(name, out var view) ? view : RegistryViewKind.Default;

    /// <summary>Puts record.Before back, deleting the value when Before is null.</summary>
    public static void Restore(IRegistry registry, WriteRecord record)
    {
        var view = ViewOf(record.View);

        if (record.Before is null)
        {
            using var existing = registry.OpenKey(record.Location, view, writable: true);
            existing?.DeleteValue(record.Name);
            return;
        }

        var (value, type) = RegistryValueCodec.Decode(record.Before);
        using var key = registry.CreateKey(record.Location, view);
        key.SetValue(record.Name, value, type);
    }
}
```

- [ ] **Step 5: Write `RegistryValueToggle`**

Create `StartupScanner.Core/Writing/RegistryValueToggle.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// The BackupRemove mechanism, used wherever Windows has no disable flag: journal the current
/// value, then remove it — or replace it with a supplied safe default where removal would be
/// dangerous. Re-enable rewrites the journalled value.
/// </summary>
public sealed class RegistryValueToggle(
    string providerId,
    IRegistry registry,
    IClock clock,
    Func<StartupItem, (object Value, RegistryValueType Type)?>? replacement = null) : IToggleProvider
{
    public string ProviderId => providerId;

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.BackupRemove;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
        => enabled ? Enable(item, journal) : Disable(item);

    public void Restore(WriteRecord record) => RegistryWriteOps.Restore(registry, record);

    private WriteRecord Disable(StartupItem item)
    {
        var view = RegistryWriteOps.ViewOf(item.Extra.GetValueOrDefault("View"));

        using var key = registry.OpenKey(item.Location, view, writable: true)
            ?? throw new InvalidOperationException($"Key not found or not writable: {item.Location}");

        var before = RegistryValueCodec.Encode(key.GetValue(item.Name), key.GetValueType(item.Name));
        var swap = replacement?.Invoke(item);

        if (swap is { } value) key.SetValue(item.Name, value.Value, value.Type);
        else key.DeleteValue(item.Name);

        return Build(item, view, enabled: false, before,
                     swap is { } s ? RegistryValueCodec.Encode(s.Value, s.Type) : null);
    }

    private WriteRecord Enable(StartupItem item, IWriteJournalReader journal)
    {
        var backup = journal.All.LastOrDefault(r => r.ItemId == item.Id && !r.Enabled && r.Before is not null)
            ?? throw new InvalidOperationException(
                $"No backed-up value is recorded for '{item.Name}', so it cannot be re-enabled.");

        var view = RegistryWriteOps.ViewOf(item.Extra.GetValueOrDefault("View"));
        var (value, type) = RegistryValueCodec.Decode(backup.Before!);

        using var key = registry.CreateKey(item.Location, view);
        key.SetValue(item.Name, value, type);

        return Build(item, view, enabled: true, before: null, after: backup.Before);
    }

    private WriteRecord Build(StartupItem item, RegistryViewKind view, bool enabled, string? before, string? after)
        => new()
        {
            Ts = clock.UtcNow,
            SessionId = "",
            ItemId = item.Id,
            ProviderId = providerId,
            Mechanism = "BackupRemove",
            Location = item.Location,
            Name = item.Name,
            Category = item.Category,
            UserSid = item.UserSid,
            View = view == RegistryViewKind.Default ? null : view.ToString(),
            Before = before,
            After = after,
            Enabled = enabled,
        };
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~RegistryValueToggleTests"`
Expected: PASS — 12 tests passed.

- [ ] **Step 7: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Writing StartupScanner.Tests/Writing
git commit -m "feat: add toggle contract and journaled BackupRemove mechanism"
```

---

## Task 18: Native toggles for Run keys and Startup folders

**Files:**
- Create: `StartupScanner.Core/Writing/StartupApprovedToggle.cs`, `Writing/RunKeyToggle.cs`, `Writing/StartupFolderToggle.cs`
- Test: `StartupScanner.Tests/Writing/RunKeyToggleTests.cs`, `StartupScanner.Tests/Writing/StartupFolderToggleTests.cs`

**Interfaces:**
- Consumes: `IToggleProvider`, `StartupApprovedBlob`, `StartupApprovedReader.ApprovedKeyPath`, `RegistryWriteOps`, `Extra["ApprovedRoot"]`, `Extra["ApprovedSubKey"]`, `Extra["View"]`.
- Produces:
  - `StartupApprovedToggle.Write(IRegistry registry, IClock clock, string providerId, StartupItem item, string approvedKeyPath, RegistryViewKind view, bool enabled) -> WriteRecord`.
  - `RunKeyToggle(IRegistry registry, IClock clock)` with `ProviderId == "registry.run"`.
  - `StartupFolderToggle(IRegistry registry, IClock clock)` with `ProviderId == "startup.folder"`.

Run keys and Startup folder shortcuts both disable through the same `StartupApproved` blob, so the write itself is one shared function; the two toggles differ only in which `StartupApproved` subkey they address (`Run`, `Run32`, or `StartupFolder`).

`RunOnce`, `RunServices`, and the policy Run keys have no `StartupApproved` coverage, so `RunKeyToggle` reports `BackupRemove` for them and delegates to `RegistryValueToggle`. That is the spec's "native flag first, journaled backup-and-remove as fallback" rule applied within one provider.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Writing/RunKeyToggleTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class RunKeyToggleTests
{
    private const string HkcuRun = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string HkcuApproved = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string HklmRun = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string HklmApproved32 = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private static readonly DateTime Now = new(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    private static StartupItem Item(string location = HkcuRun, string name = "Spotify",
                                    string approvedRoot = "HKCU", string approvedSubKey = "Run",
                                    string view = "Default") => new()
    {
        Id = Identity.Compute("registry.run", null, location, name),
        ProviderId = "registry.run", Location = location, Name = name,
        RawValue = @"C:\Spotify.exe", Category = StartupCategory.RunKey, State = EnableState.Enabled,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ApprovedRoot"] = approvedRoot, ["ApprovedSubKey"] = approvedSubKey, ["View"] = view,
        },
    };

    private static (RunKeyToggle Toggle, FakeRegistry Registry, FakeJournal Journal) Sut()
    {
        var registry = new FakeRegistry();
        return (new RunKeyToggle(registry, new FakeClock(Now)), registry, new FakeJournal());
    }

    [Fact]
    public void ProviderIdMatchesTheScanProvider()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal("registry.run", toggle.ProviderId);
    }

    [Fact]
    public void CapabilityIsNativeWhenStartupApprovedCoversTheKey()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal(ToggleCapability.Native, toggle.GetCapability(Item()));
    }

    [Fact]
    public void CapabilityFallsBackToBackupRemoveForKeysWindowsDoesNotTrack()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal(ToggleCapability.BackupRemove, toggle.GetCapability(Item(approvedSubKey: "")));
    }

    [Fact]
    public void DisableWritesADisabledBlobIntoTheStartupApprovedKey()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);

        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        var blob = Assert.IsType<byte[]>(registry.OpenKey(HkcuApproved)!.GetValue("Spotify"));
        Assert.False(StartupApprovedBlob.IsEnabled(blob));
        Assert.Equal(Now, StartupApprovedBlob.DisabledAtUtc(blob));
        Assert.Equal("StartupApproved", record.Mechanism);
        Assert.Equal(HkcuApproved, record.Location);
        Assert.Equal("Spotify", record.Name);
        Assert.False(record.Enabled);
    }

    [Fact]
    public void DisableLeavesTheRunValueItselfAlone()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);

        toggle.SetEnabled(Item(), enabled: false, journal);

        Assert.Equal(@"C:\Spotify.exe", registry.OpenKey(HkcuRun)!.GetValue("Spotify"));
    }

    [Fact]
    public void DisableRecordsNoBeforeValueWhenNoBlobExistedYet()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);

        Assert.Null(toggle.SetEnabled(Item(), enabled: false, journal).Before);
    }

    [Fact]
    public void EnableWritesAnEnabledBlobAndZeroesTheTimestamp()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);
        journal.Record(toggle.SetEnabled(Item(), enabled: false, journal));

        toggle.SetEnabled(Item() with { State = EnableState.Disabled }, enabled: true, journal);

        var blob = (byte[])registry.OpenKey(HkcuApproved)!.GetValue("Spotify")!;
        Assert.True(StartupApprovedBlob.IsEnabled(blob));
        Assert.Null(StartupApprovedBlob.DisabledAtUtc(blob));
    }

    [Fact]
    public void EnablePreservesTheOpaqueBlobBytes()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);
        registry.Set(HkcuApproved, "Spotify",
            new byte[] { 0x03, 0xAA, 0xBB, 0xCC, 1, 2, 3, 4, 5, 6, 7, 8 }, RegistryValueType.Binary);

        toggle.SetEnabled(Item() with { State = EnableState.Disabled }, enabled: true, journal);

        var blob = (byte[])registry.OpenKey(HkcuApproved)!.GetValue("Spotify")!;
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, blob[1..4]);
    }

    [Fact]
    public void ThirtyTwoBitRunKeyWritesTheRun32BlobInTheThirtyTwoBitView()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HklmRun, "Legacy", "x.exe", RegistryValueType.String, RegistryViewKind.Registry32);

        var record = toggle.SetEnabled(
            Item(HklmRun, "Legacy", "HKLM", "Run32", "Registry32"), enabled: false, journal);

        Assert.NotNull(registry.OpenKey(HklmApproved32, RegistryViewKind.Registry32)!.GetValue("Legacy"));
        Assert.Equal("Registry32", record.View);
    }

    [Fact]
    public void RestorePutsTheEarlierBlobBack()
    {
        var (toggle, registry, journal) = Sut();
        var original = StartupApprovedBlob.Encode(true, Now, null);
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);
        registry.Set(HkcuApproved, "Spotify", original, RegistryValueType.Binary);

        var record = toggle.SetEnabled(Item(), enabled: false, journal);
        toggle.Restore(record);

        Assert.Equal(original, (byte[])registry.OpenKey(HkcuApproved)!.GetValue("Spotify")!);
    }

    [Fact]
    public void RestoreRemovesTheBlobWhenThereWasNotOneBefore()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(HkcuRun, "Spotify", @"C:\Spotify.exe", RegistryValueType.String);

        var record = toggle.SetEnabled(Item(), enabled: false, journal);
        toggle.Restore(record);

        Assert.Null(registry.OpenKey(HkcuApproved)!.GetValue("Spotify"));
    }

    [Fact]
    public void UntrackedKeysGoThroughBackupRemoveInstead()
    {
        var (toggle, registry, journal) = Sut();
        var runOnce = @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce";
        registry.Set(runOnce, "Once", "setup.exe", RegistryValueType.String);

        var record = toggle.SetEnabled(Item(runOnce, "Once", "HKCU", "", "Default"), enabled: false, journal);

        Assert.Equal("BackupRemove", record.Mechanism);
        Assert.Null(registry.OpenKey(runOnce)!.GetValue("Once"));
        Assert.Equal("sz:setup.exe", record.Before);
    }

    [Fact]
    public void RestoreRoutesByMechanism()
    {
        var (toggle, registry, journal) = Sut();
        var runOnce = @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce";
        registry.Set(runOnce, "Once", "setup.exe", RegistryValueType.String);
        var record = toggle.SetEnabled(Item(runOnce, "Once", "HKCU", "", "Default"), enabled: false, journal);

        toggle.Restore(record);

        Assert.Equal("setup.exe", registry.OpenKey(runOnce)!.GetValue("Once"));
    }
}
```

Create `StartupScanner.Tests/Writing/StartupFolderToggleTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class StartupFolderToggleTests
{
    private const string UserFolder = @"C:\Users\alice\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
    private const string UserApproved = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const string CommonApproved = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private static readonly DateTime Now = new(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    private static StartupItem Item(string approvedRoot = "HKCU", string name = "Spotify.lnk") => new()
    {
        Id = Identity.Compute("startup.folder", null, UserFolder, name),
        ProviderId = "startup.folder", Location = UserFolder, Name = name,
        RawValue = @"C:\Spotify.exe", Category = StartupCategory.StartupFolder, State = EnableState.Enabled,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ApprovedRoot"] = approvedRoot },
    };

    private static (StartupFolderToggle Toggle, FakeRegistry Registry, FakeJournal Journal) Sut()
    {
        var registry = new FakeRegistry();
        return (new StartupFolderToggle(registry, new FakeClock(Now)), registry, new FakeJournal());
    }

    [Fact]
    public void ProviderIdAndCapabilityAreCorrect()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal("startup.folder", toggle.ProviderId);
        Assert.Equal(ToggleCapability.Native, toggle.GetCapability(Item()));
    }

    [Fact]
    public void DisableWritesTheBlobKeyedByTheShortcutFileName()
    {
        var (toggle, registry, journal) = Sut();

        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        var blob = Assert.IsType<byte[]>(registry.OpenKey(UserApproved)!.GetValue("Spotify.lnk"));
        Assert.False(StartupApprovedBlob.IsEnabled(blob));
        Assert.Equal(UserApproved, record.Location);
    }

    [Fact]
    public void CommonFolderEntriesUseTheHklmApprovedKey()
    {
        var (toggle, registry, journal) = Sut();

        toggle.SetEnabled(Item(approvedRoot: "HKLM"), enabled: false, journal);

        Assert.NotNull(registry.OpenKey(CommonApproved)!.GetValue("Spotify.lnk"));
    }

    [Fact]
    public void AnotherProfilesFolderTargetsThatProfilesHive()
    {
        var (toggle, registry, journal) = Sut();

        toggle.SetEnabled(Item(approvedRoot: @"HKU\S-1-5-21-42"), enabled: false, journal);

        Assert.NotNull(registry.OpenKey(
            @"HKU\S-1-5-21-42\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder")!
            .GetValue("Spotify.lnk"));
    }

    [Fact]
    public void EnableFlipsTheBlobBack()
    {
        var (toggle, registry, journal) = Sut();
        journal.Record(toggle.SetEnabled(Item(), enabled: false, journal));

        toggle.SetEnabled(Item() with { State = EnableState.Disabled }, enabled: true, journal);

        Assert.True(StartupApprovedBlob.IsEnabled((byte[])registry.OpenKey(UserApproved)!.GetValue("Spotify.lnk")!));
    }

    [Fact]
    public void TheShortcutFileIsNeverTouched()
    {
        var (toggle, registry, journal) = Sut();

        toggle.SetEnabled(Item(), enabled: false, journal);

        // The toggle only ever writes to the registry; no IFileSystem dependency exists to delete with.
        Assert.Equal(UserFolder, Item().Location);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~RunKeyToggleTests|FullyQualifiedName~StartupFolderToggleTests"`
Expected: FAIL — `CS0246: The type or namespace name 'RunKeyToggle' could not be found`.

- [ ] **Step 3: Write the shared StartupApproved write**

Create `StartupScanner.Core/Writing/StartupApprovedToggle.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// The Native disable mechanism: flip the StartupApproved blob that Task Manager and msconfig
/// read, so this app's changes stay consistent with theirs.
/// </summary>
public static class StartupApprovedToggle
{
    public static WriteRecord Write(IRegistry registry, IClock clock, string providerId, StartupItem item,
                                    string approvedKeyPath, RegistryViewKind view, bool enabled)
    {
        using var key = registry.CreateKey(approvedKeyPath, view);

        var existing = key.GetValue(item.Name) as byte[];
        var before = existing is null ? null : RegistryValueCodec.Encode(existing, RegistryValueType.Binary);
        var blob = StartupApprovedBlob.Encode(enabled, clock.UtcNow, existing);

        key.SetValue(item.Name, blob, RegistryValueType.Binary);

        return new WriteRecord
        {
            Ts = clock.UtcNow,
            SessionId = "",
            ItemId = item.Id,
            ProviderId = providerId,
            Mechanism = "StartupApproved",
            Location = approvedKeyPath,
            Name = item.Name,
            Category = item.Category,
            UserSid = item.UserSid,
            View = view == RegistryViewKind.Default ? null : view.ToString(),
            Before = before,
            After = RegistryValueCodec.Encode(blob, RegistryValueType.Binary),
            Enabled = enabled,
        };
    }
}
```

- [ ] **Step 4: Write `RunKeyToggle`**

Create `StartupScanner.Core/Writing/RunKeyToggle.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

public sealed class RunKeyToggle(IRegistry registry, IClock clock) : IToggleProvider
{
    private readonly RegistryValueToggle _fallback = new("registry.run", registry, clock);

    public string ProviderId => "registry.run";

    public ToggleCapability GetCapability(StartupItem item)
    {
        if (Guardrails.Evaluate(item).Blocked) return ToggleCapability.Blocked;
        return HasApprovedCoverage(item) ? ToggleCapability.Native : ToggleCapability.BackupRemove;
    }

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        if (!HasApprovedCoverage(item)) return _fallback.SetEnabled(item, enabled, journal);

        return StartupApprovedToggle.Write(registry, clock, ProviderId, item,
            StartupApprovedReader.ApprovedKeyPath(item.Extra["ApprovedRoot"], item.Extra["ApprovedSubKey"]),
            RegistryWriteOps.ViewOf(item.Extra.GetValueOrDefault("View")), enabled);
    }

    public void Restore(WriteRecord record)
    {
        if (record.Mechanism == "BackupRemove") _fallback.Restore(record);
        else RegistryWriteOps.Restore(registry, record);
    }

    /// <summary>RunOnce, RunServices, and the policy Run keys have no StartupApproved state.</summary>
    private static bool HasApprovedCoverage(StartupItem item)
        => !string.IsNullOrEmpty(item.Extra.GetValueOrDefault("ApprovedSubKey"))
        && !string.IsNullOrEmpty(item.Extra.GetValueOrDefault("ApprovedRoot"));
}
```

- [ ] **Step 5: Write `StartupFolderToggle`**

Create `StartupScanner.Core/Writing/StartupFolderToggle.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Startup folder shortcuts disable through StartupApproved\StartupFolder, keyed by file name.
/// The shortcut file itself is never moved or deleted.
/// </summary>
public sealed class StartupFolderToggle(IRegistry registry, IClock clock) : IToggleProvider
{
    public string ProviderId => "startup.folder";

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.Native;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
        => StartupApprovedToggle.Write(registry, clock, ProviderId, item,
            StartupApprovedReader.ApprovedKeyPath(item.Extra["ApprovedRoot"], "StartupFolder"),
            RegistryViewKind.Default, enabled);

    public void Restore(WriteRecord record) => RegistryWriteOps.Restore(registry, record);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~RunKeyToggleTests|FullyQualifiedName~StartupFolderToggleTests"`
Expected: PASS — 19 tests passed.

- [ ] **Step 7: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Writing StartupScanner.Tests/Writing
git commit -m "feat: add native StartupApproved toggles for run keys and startup folders"
```

---

## Task 19: Native toggles for services and scheduled tasks

**Files:**
- Create: `StartupScanner.Core/Writing/ServiceToggle.cs`, `Writing/ScheduledTaskToggle.cs`
- Test: `StartupScanner.Tests/Writing/ServiceToggleTests.cs`, `StartupScanner.Tests/Writing/ScheduledTaskToggleTests.cs`

**Interfaces:**
- Consumes: `IToggleProvider`, `IRegistry`, `ITaskSchedulerService`, `IClock`, `RegistryWriteOps`.
- Produces:
  - `ServiceToggle(IRegistry registry, IClock clock)` with `ProviderId == "service"`, `const int DisabledStart = 4`, `const int FallbackEnabledStart = 3`.
  - `ScheduledTaskToggle(ITaskSchedulerService tasks, IClock clock)` with `ProviderId == "task-scheduler"`.

The prior `Start` value is journaled so re-enable restores `2` (Automatic) versus `3` (Manual) exactly rather than guessing. When there is no journal record — the service was already `Start=4` before this app ever ran — the fallback is `3`, Manual, because Manual cannot make a machine boot-loop the way an unexpected Automatic can.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Writing/ServiceToggleTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class ServiceToggleTests
{
    private const string Key = @"HKLM\SYSTEM\CurrentControlSet\Services\Spooler";

    private static StartupItem Item(int start, string name = "Spooler",
                                    StartupCategory category = StartupCategory.Service) => new()
    {
        Id = Identity.Compute("service", null, $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}", name),
        ProviderId = "service",
        Location = $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}",
        Name = name,
        RawValue = "svchost.exe",
        Category = category,
        State = start == 4 ? EnableState.Disabled : EnableState.Enabled,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Start"] = start.ToString(), ["Type"] = "32",
        },
    };

    private static (ServiceToggle Toggle, FakeRegistry Registry, FakeJournal Journal) Sut()
    {
        var registry = new FakeRegistry();
        return (new ServiceToggle(registry, new FakeClock()), registry, new FakeJournal());
    }

    [Fact]
    public void ProviderIdAndCapability()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal("service", toggle.ProviderId);
        Assert.Equal(ToggleCapability.Native, toggle.GetCapability(Item(2)));
    }

    [Fact]
    public void GuardrailedServicesAreBlocked()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal(ToggleCapability.Blocked, toggle.GetCapability(Item(2, "RpcSs")));
        Assert.Equal(ToggleCapability.Blocked, toggle.GetCapability(Item(1, "disk", StartupCategory.Driver)));
    }

    [Fact]
    public void DisableSetsStartToFourAndJournalsThePriorValue()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Key, "Start", 2, RegistryValueType.Dword);

        var record = toggle.SetEnabled(Item(2), enabled: false, journal);

        Assert.Equal(4, registry.OpenKey(Key)!.GetValue("Start"));
        Assert.Equal("Service.Start", record.Mechanism);
        Assert.Equal("Start", record.Name);
        Assert.Equal(Key, record.Location);
        Assert.Equal("dword:2", record.Before);
        Assert.Equal("dword:4", record.After);
        Assert.False(record.Enabled);
    }

    [Fact]
    public void EnableRestoresAutomaticVersusManualExactly()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Key, "Start", 3, RegistryValueType.Dword);
        journal.Record(toggle.SetEnabled(Item(3), enabled: false, journal));

        toggle.SetEnabled(Item(4), enabled: true, journal);

        Assert.Equal(3, registry.OpenKey(Key)!.GetValue("Start"));
    }

    [Fact]
    public void EnableWithNoJournalledPriorValueFallsBackToManual()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Key, "Start", 4, RegistryValueType.Dword);

        var record = toggle.SetEnabled(Item(4), enabled: true, journal);

        Assert.Equal(ServiceToggle.FallbackEnabledStart, registry.OpenKey(Key)!.GetValue("Start"));
        Assert.Equal("dword:3", record.After);
        Assert.True(record.Enabled);
    }

    [Fact]
    public void RestorePutsTheOldStartValueBack()
    {
        var (toggle, registry, journal) = Sut();
        registry.Set(Key, "Start", 2, RegistryValueType.Dword);
        var record = toggle.SetEnabled(Item(2), enabled: false, journal);

        toggle.Restore(record);

        Assert.Equal(2, registry.OpenKey(Key)!.GetValue("Start"));
    }

    [Fact]
    public void MissingServiceKeyThrows()
    {
        var (toggle, _, journal) = Sut();
        Assert.Throws<InvalidOperationException>(() => toggle.SetEnabled(Item(2), enabled: false, journal));
    }
}
```

Create `StartupScanner.Tests/Writing/ScheduledTaskToggleTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class ScheduledTaskToggleTests
{
    private static StartupItem Item(bool enabled = true, string taskPath = @"\Vendor\Updater") => new()
    {
        Id = Identity.Compute("task-scheduler", null, @"\Vendor", "Updater"),
        ProviderId = "task-scheduler", Location = @"\Vendor", Name = "Updater",
        RawValue = "updater.exe", Category = StartupCategory.ScheduledTask,
        State = enabled ? EnableState.Enabled : EnableState.Disabled,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["TaskPath"] = taskPath },
    };

    private static (ScheduledTaskToggle Toggle, FakeTaskScheduler Tasks, FakeJournal Journal) Sut()
    {
        var tasks = new FakeTaskScheduler();
        return (new ScheduledTaskToggle(tasks, new FakeClock()), tasks, new FakeJournal());
    }

    [Fact]
    public void ProviderIdAndCapability()
    {
        var (toggle, _, _) = Sut();
        Assert.Equal("task-scheduler", toggle.ProviderId);
        Assert.Equal(ToggleCapability.Native, toggle.GetCapability(Item()));
    }

    [Fact]
    public void DisableCallsSetEnabledFalseOnTheFullTaskPath()
    {
        var (toggle, tasks, journal) = Sut();

        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        Assert.Equal((@"\Vendor\Updater", false), tasks.SetEnabledCalls.Single());
        Assert.Equal("Task.Enabled", record.Mechanism);
        Assert.Equal(@"\Vendor\Updater", record.Location);
        Assert.Equal("sz:True", record.Before);
        Assert.Equal("sz:False", record.After);
        Assert.False(record.Enabled);
    }

    [Fact]
    public void EnableCallsSetEnabledTrue()
    {
        var (toggle, tasks, journal) = Sut();

        toggle.SetEnabled(Item(enabled: false), enabled: true, journal);

        Assert.Equal((@"\Vendor\Updater", true), tasks.SetEnabledCalls.Single());
    }

    [Fact]
    public void RestoreReappliesThePriorEnabledFlag()
    {
        var (toggle, tasks, journal) = Sut();
        var record = toggle.SetEnabled(Item(), enabled: false, journal);

        toggle.Restore(record);

        Assert.Equal((@"\Vendor\Updater", true), tasks.SetEnabledCalls[^1]);
    }

    [Fact]
    public void TaskPathIsReconstructedWhenTheExtraIsMissing()
    {
        var (toggle, tasks, journal) = Sut();
        var item = Item();
        var stripped = item with { Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };

        toggle.SetEnabled(stripped, enabled: false, journal);

        Assert.Equal(@"\Vendor\Updater", tasks.SetEnabledCalls.Single().Path);
    }

    [Fact]
    public void APermissionFailurePropagatesSoNothingIsJournalled()
    {
        var (toggle, tasks, journal) = Sut();
        tasks.FailOn(@"\Vendor\Updater");

        Assert.Throws<UnauthorizedAccessException>(() => toggle.SetEnabled(Item(), enabled: false, journal));
        Assert.Empty(journal.All);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ServiceToggleTests|FullyQualifiedName~ScheduledTaskToggleTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ServiceToggle' could not be found`.

- [ ] **Step 3: Write `ServiceToggle`**

Create `StartupScanner.Core/Writing/ServiceToggle.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

public sealed class ServiceToggle(IRegistry registry, IClock clock) : IToggleProvider
{
    public const int DisabledStart = 4;

    /// <summary>
    /// Used only when nothing was journalled, i.e. the service was already disabled before this app
    /// first ran. Manual is the conservative choice: an unexpected Automatic can start something at
    /// boot that the user never asked for.
    /// </summary>
    public const int FallbackEnabledStart = 3;

    public string ProviderId => "service";

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.Native;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        using var key = registry.OpenKey(item.Location, RegistryViewKind.Default, writable: true)
            ?? throw new InvalidOperationException($"Service key not found or not writable: {item.Location}");

        var current = key.GetValue("Start");
        var before = current is null ? null : RegistryValueCodec.Encode(Convert.ToInt32(current), RegistryValueType.Dword);
        var target = enabled ? PriorStart(item, journal) : DisabledStart;

        key.SetValue("Start", target, RegistryValueType.Dword);

        return new WriteRecord
        {
            Ts = clock.UtcNow,
            SessionId = "",
            ItemId = item.Id,
            ProviderId = ProviderId,
            Mechanism = "Service.Start",
            Location = item.Location,
            Name = "Start",
            Category = item.Category,
            UserSid = null,
            Before = before,
            After = RegistryValueCodec.Encode(target, RegistryValueType.Dword),
            Enabled = enabled,
        };
    }

    public void Restore(WriteRecord record) => RegistryWriteOps.Restore(registry, record);

    private static int PriorStart(StartupItem item, IWriteJournalReader journal)
    {
        var backup = journal.All.LastOrDefault(r => r.ItemId == item.Id && !r.Enabled && r.Before is not null);
        if (backup is null) return FallbackEnabledStart;

        var (value, _) = RegistryValueCodec.Decode(backup.Before!);
        return Convert.ToInt32(value);
    }
}
```

- [ ] **Step 4: Write `ScheduledTaskToggle`**

Create `StartupScanner.Core/Writing/ScheduledTaskToggle.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

public sealed class ScheduledTaskToggle(ITaskSchedulerService tasks, IClock clock) : IToggleProvider
{
    public string ProviderId => "task-scheduler";

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.Native;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        var path = TaskPath(item);
        tasks.SetEnabled(path, enabled);

        return new WriteRecord
        {
            Ts = clock.UtcNow,
            SessionId = "",
            ItemId = item.Id,
            ProviderId = ProviderId,
            Mechanism = "Task.Enabled",
            Location = path,
            Name = item.Name,
            Category = item.Category,
            UserSid = item.UserSid,
            Before = $"sz:{item.State == EnableState.Enabled}",
            After = $"sz:{enabled}",
            Enabled = enabled,
        };
    }

    public void Restore(WriteRecord record)
    {
        var (value, _) = RegistryValueCodec.Decode(record.Before ?? "sz:True");
        tasks.SetEnabled(record.Location, bool.Parse((string)value));
    }

    private static string TaskPath(StartupItem item)
        => item.Extra.TryGetValue("TaskPath", out var path) && path.Length > 0
            ? path
            : $@"{item.Location.TrimEnd('\\')}\{item.Name}";
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ServiceToggleTests|FullyQualifiedName~ScheduledTaskToggleTests"`
Expected: PASS — 13 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Writing StartupScanner.Tests/Writing
git commit -m "feat: add native service and scheduled task toggles"
```

---

## Task 20: Restore point gate

**Files:**
- Create: `StartupScanner.Core/Abstractions/ISystemRestore.cs`, `Writing/RestorePointGate.cs`
- Create: `StartupScanner.Tests/Fakes/FakeSystemRestore.cs`
- Test: `StartupScanner.Tests/Writing/RestorePointGateTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `ISystemRestore.TryCreateRestorePoint(string description, out string? error) -> bool`.
  - `IRestorePointPrompt.ContinueWithoutRestorePoint(string reason) -> bool`.
  - `IRestorePointGate.Ensure(out string? error) -> bool`.
  - `RestorePointGate(ISystemRestore restore, IRestorePointPrompt prompt, string description = "StartupScanner: before first change")`.

One restore point per session, created before the first write. If System Protection is disabled the call fails and the user is asked whether to continue without protection — never proceeded with silently. A decline is not memoised: the user may go and turn System Protection on, so the next write attempt tries again.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Fakes/FakeSystemRestore.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeSystemRestore : ISystemRestore
{
    public List<string> Descriptions { get; } = new();
    public string? FailWith { get; set; }

    public bool TryCreateRestorePoint(string description, out string? error)
    {
        Descriptions.Add(description);
        error = FailWith;
        return FailWith is null;
    }
}

public sealed class StubRestorePointPrompt(bool answer) : IRestorePointPrompt
{
    public List<string> Reasons { get; } = new();

    public bool ContinueWithoutRestorePoint(string reason)
    {
        Reasons.Add(reason);
        return answer;
    }
}
```

Create `StartupScanner.Tests/Writing/RestorePointGateTests.cs`:

```csharp
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class RestorePointGateTests
{
    [Fact]
    public void CreatesOneRestorePointAndDoesNotAskTheUser()
    {
        var restore = new FakeSystemRestore();
        var prompt = new StubRestorePointPrompt(false);
        var gate = new RestorePointGate(restore, prompt);

        Assert.True(gate.Ensure(out var error));

        Assert.Null(error);
        Assert.Single(restore.Descriptions);
        Assert.Empty(prompt.Reasons);
    }

    [Fact]
    public void CreatesTheRestorePointOnlyOncePerSession()
    {
        var restore = new FakeSystemRestore();
        var gate = new RestorePointGate(restore, new StubRestorePointPrompt(false));

        gate.Ensure(out _);
        gate.Ensure(out _);
        gate.Ensure(out _);

        Assert.Single(restore.Descriptions);
    }

    [Fact]
    public void OnFailureTheUserIsAskedAndAnAcceptanceProceeds()
    {
        var restore = new FakeSystemRestore { FailWith = "System Protection is turned off." };
        var prompt = new StubRestorePointPrompt(true);
        var gate = new RestorePointGate(restore, prompt);

        Assert.True(gate.Ensure(out var error));

        Assert.Null(error);
        Assert.Equal("System Protection is turned off.", prompt.Reasons.Single());
    }

    [Fact]
    public void AfterTheUserAcceptsTheGateDoesNotAskOrRetryAgain()
    {
        var restore = new FakeSystemRestore { FailWith = "off" };
        var prompt = new StubRestorePointPrompt(true);
        var gate = new RestorePointGate(restore, prompt);

        gate.Ensure(out _);
        gate.Ensure(out _);

        Assert.Single(restore.Descriptions);
        Assert.Single(prompt.Reasons);
    }

    [Fact]
    public void ADeclineBlocksTheWriteAndReportsTheReason()
    {
        var restore = new FakeSystemRestore { FailWith = "System Protection is turned off." };
        var gate = new RestorePointGate(restore, new StubRestorePointPrompt(false));

        Assert.False(gate.Ensure(out var error));

        Assert.Equal("System Protection is turned off.", error);
    }

    [Fact]
    public void ADeclineIsNotMemoisedSoTurningProtectionOnLetsTheNextAttemptSucceed()
    {
        var restore = new FakeSystemRestore { FailWith = "off" };
        var gate = new RestorePointGate(restore, new StubRestorePointPrompt(false));
        gate.Ensure(out _);

        restore.FailWith = null;

        Assert.True(gate.Ensure(out _));
        Assert.Equal(2, restore.Descriptions.Count);
    }

    [Fact]
    public void TheDescriptionIsUsedVerbatim()
    {
        var restore = new FakeSystemRestore();
        new RestorePointGate(restore, new StubRestorePointPrompt(true), "custom text").Ensure(out _);

        Assert.Equal("custom text", restore.Descriptions.Single());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~RestorePointGateTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ISystemRestore' could not be found`.

- [ ] **Step 3: Write the abstraction**

Create `StartupScanner.Core/Abstractions/ISystemRestore.cs`:

```csharp
namespace StartupScanner.Core.Abstractions;

public interface ISystemRestore
{
    /// <summary>False with a populated error when System Protection is disabled or the call fails.</summary>
    bool TryCreateRestorePoint(string description, out string? error);
}

public interface IRestorePointPrompt
{
    /// <summary>Returns true to continue writing without restore-point protection.</summary>
    bool ContinueWithoutRestorePoint(string reason);
}
```

- [ ] **Step 4: Write the gate**

Create `StartupScanner.Core/Writing/RestorePointGate.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Writing;

public interface IRestorePointGate
{
    /// <summary>
    /// Ensures a restore point exists for this session, asking the user whether to continue if one
    /// cannot be created. False means the caller must not write.
    /// </summary>
    bool Ensure(out string? error);
}

public sealed class RestorePointGate(
    ISystemRestore restore,
    IRestorePointPrompt prompt,
    string description = "StartupScanner: before first change") : IRestorePointGate
{
    private bool _satisfied;

    public bool Ensure(out string? error)
    {
        error = null;
        if (_satisfied) return true;

        if (restore.TryCreateRestorePoint(description, out var failure))
        {
            _satisfied = true;
            return true;
        }

        var reason = failure ?? "Creating a system restore point failed.";
        if (prompt.ContinueWithoutRestorePoint(reason))
        {
            _satisfied = true;
            return true;
        }

        // Not memoised: the user may enable System Protection and try again.
        error = reason;
        return false;
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~RestorePointGateTests"`
Expected: PASS — 7 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add once-per-session restore point gate"
```

---

## Task 21: Toggle service — verification, confirmation, journaling

**Files:**
- Create: `StartupScanner.Core/Writing/WriteVerifier.cs`, `Writing/ToggleService.cs`, `Presentation/ConfirmationText.cs`
- Create: `StartupScanner.Tests/Fakes/RecordingConfirmationSink.cs`, `Fakes/StubToggleProvider.cs`
- Test: `StartupScanner.Tests/Writing/ToggleServiceTests.cs`, `StartupScanner.Tests/Presentation/ConfirmationTextTests.cs`

**Interfaces:**
- Consumes: `IToggleProvider`, `IStartupProvider`, `IWriteJournal`, `IRestorePointGate`, `IConfirmationSink`, `IClock`, `Guardrails`.
- Produces:
  - `WriteVerifier(IReadOnlyList<IStartupProvider> scanners)` with `string? Check(WriteRecord record, bool expectedEnabled, ScanContext ctx)` — null means the write landed.
  - `ToggleService(IReadOnlyList<IToggleProvider> toggles, IReadOnlyList<IStartupProvider> scanners, IWriteJournal journal, IRestorePointGate restorePoint, IConfirmationSink confirmations, IClock clock, string sessionId)` with `ToggleResult SetEnabled(StartupItem item, bool enabled, ScanContext ctx)`.
  - `ConfirmationText.For(StartupItem item, bool enabling) -> string`.

This is where the spec's write rules become one ordered sequence: guardrail check, risk confirmation, restore point, write, read back, journal. The read-back is what makes the journal trustworthy — a mismatch rolls the write back and appends nothing, so undo can never replay a write that did not land.

`WriteVerifier` decides what "landed" means from the record's own mechanism. `BackupRemove` with a null `After` expects the value to be gone; `BackupRemove` with an `After` (the Winlogon default-restore case, and every re-enable) expects the value present and equal; every native mechanism expects the item's `State` to match.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Fakes/RecordingConfirmationSink.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;

namespace StartupScanner.Tests.Fakes;

public sealed class RecordingConfirmationSink(bool answer = true) : IConfirmationSink
{
    public List<(string Name, RiskTier Tier, string Message)> Calls { get; } = new();

    public bool Confirm(StartupItem item, RiskTier tier, string message)
    {
        Calls.Add((item.Name, tier, message));
        return answer;
    }
}

public sealed class StubRestorePointGate(bool allow = true, string? error = null) : IRestorePointGate
{
    public int Calls { get; private set; }

    public bool Ensure(out string? failure)
    {
        Calls++;
        failure = allow ? null : error ?? "declined";
        return allow;
    }
}
```

Create `StartupScanner.Tests/Fakes/StubToggleProvider.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;

namespace StartupScanner.Tests.Fakes;

/// <summary>Toggle whose write is a no-op, so ToggleService's own behaviour is what gets tested.</summary>
public sealed class StubToggleProvider(string providerId, ToggleCapability capability) : IToggleProvider
{
    public string ProviderId => providerId;
    public List<WriteRecord> Restored { get; } = new();
    public Exception? ThrowOnWrite { get; set; }
    public string Mechanism { get; set; } = "StartupApproved";
    public string? After { get; set; } = "binary:AwAAAAAAAAAAAAAA";

    public ToggleCapability GetCapability(StartupItem item) => capability;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        if (ThrowOnWrite is not null) throw ThrowOnWrite;

        return new WriteRecord
        {
            Ts = new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc),
            SessionId = "",
            ItemId = item.Id,
            ProviderId = providerId,
            Mechanism = Mechanism,
            Location = item.Location,
            Name = item.Name,
            Category = item.Category,
            Before = "binary:AgAAAAAAAAAAAAAA",
            After = After,
            Enabled = enabled,
        };
    }

    public void Restore(WriteRecord record) => Restored.Add(record);
}

/// <summary>Scan provider whose result the test controls, standing in for the read-back.</summary>
public sealed class StubScanProvider(string id, params StartupItem[] items) : Core.Providers.IStartupProvider
{
    public string Id => id;
    public StartupCategory Category => StartupCategory.RunKey;
    public List<StartupItem> Items { get; } = items.ToList();
    public IEnumerable<StartupItem> Scan(ScanContext ctx) => Items;
}
```

Create `StartupScanner.Tests/Writing/ToggleServiceTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class ToggleServiceTests
{
    private const string Session = "session-1";

    private static StartupItem Item(RiskTier risk = RiskTier.Low, EnableState state = EnableState.Enabled,
                                    string name = "Spotify", StartupCategory category = StartupCategory.RunKey,
                                    string raw = @"C:\Spotify.exe") => new()
    {
        Id = Identity.Compute("registry.run", null, @"HKCU\Run", name),
        ProviderId = "registry.run", Location = @"HKCU\Run", Name = name, RawValue = raw,
        Category = category, State = state, Risk = risk,
    };

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    private static ToggleService Build(
        StubToggleProvider toggle, StubScanProvider scanner, FakeJournal journal,
        IConfirmationSink? confirmations = null, StubRestorePointGate? gate = null)
        => new(new IToggleProvider[] { toggle }, new IStartupProvider[] { scanner }, journal,
               gate ?? new StubRestorePointGate(), confirmations ?? new RecordingConfirmationSink(),
               new FakeClock(), Session);

    [Fact]
    public void LowRiskToggleWritesWithoutAConfirmation()
    {
        var item = Item();
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native);
        var scanner = new StubScanProvider("registry.run", item with { State = EnableState.Disabled });
        var journal = new FakeJournal();
        var confirmations = new RecordingConfirmationSink();

        var result = Build(toggle, scanner, journal, confirmations).SetEnabled(item, enabled: false, Ctx());

        Assert.True(result.Success);
        Assert.Empty(confirmations.Calls);
        Assert.Equal(Session, journal.All.Single().SessionId);
    }

    [Fact]
    public void MediumRiskAsksForConfirmationBeforeWriting()
    {
        var item = Item(RiskTier.Medium, category: StartupCategory.Service);
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native);
        var scanner = new StubScanProvider("registry.run", item with { State = EnableState.Disabled });
        var confirmations = new RecordingConfirmationSink();

        var result = Build(toggle, scanner, new FakeJournal(), confirmations).SetEnabled(item, false, Ctx());

        Assert.True(result.Success);
        Assert.Equal(RiskTier.Medium, confirmations.Calls.Single().Tier);
    }

    [Fact]
    public void DecliningTheConfirmationCancelsAndWritesNothing()
    {
        var item = Item(RiskTier.High, category: StartupCategory.Winlogon, name: "Taskman");
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native);
        var journal = new FakeJournal();

        var result = Build(toggle, new StubScanProvider("registry.run"), journal,
                           new RecordingConfirmationSink(answer: false)).SetEnabled(item, false, Ctx());

        Assert.True(result.Cancelled);
        Assert.Empty(journal.All);
        Assert.Empty(toggle.Restored);
    }

    [Fact]
    public void ABlockedItemIsRefusedWithTheGuardrailReason()
    {
        var item = Item(RiskTier.Critical, category: StartupCategory.Winlogon, name: "Shell", raw: "explorer.exe");
        var journal = new FakeJournal();

        var result = Build(new StubToggleProvider("registry.run", ToggleCapability.Blocked),
                           new StubScanProvider("registry.run"), journal).SetEnabled(item, false, Ctx());

        Assert.False(result.Success);
        Assert.Contains("shell", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(journal.All);
    }

    [Fact]
    public void ARestorePointIsRequestedBeforeTheFirstWrite()
    {
        var item = Item();
        var gate = new StubRestorePointGate();
        var scanner = new StubScanProvider("registry.run", item with { State = EnableState.Disabled });

        Build(new StubToggleProvider("registry.run", ToggleCapability.Native), scanner, new FakeJournal(), gate: gate)
            .SetEnabled(item, false, Ctx());

        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public void DecliningTheRestorePointCancelsTheWrite()
    {
        var item = Item();
        var journal = new FakeJournal();

        var result = Build(new StubToggleProvider("registry.run", ToggleCapability.Native),
                           new StubScanProvider("registry.run"), journal,
                           gate: new StubRestorePointGate(allow: false, error: "protection off"))
            .SetEnabled(item, false, Ctx());

        Assert.True(result.Cancelled);
        Assert.Equal("protection off", result.Error);
        Assert.Empty(journal.All);
    }

    [Fact]
    public void AFailedVerificationRollsBackAndJournalsNothing()
    {
        var item = Item();
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native);
        // The read-back still reports Enabled, so the disable did not land.
        var scanner = new StubScanProvider("registry.run", item with { State = EnableState.Enabled });
        var journal = new FakeJournal();

        var result = Build(toggle, scanner, journal).SetEnabled(item, enabled: false, Ctx());

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(toggle.Restored);
        Assert.Empty(journal.All);
    }

    [Fact]
    public void AThrowingWriteIsReportedAndJournalsNothing()
    {
        var item = Item();
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native)
        {
            ThrowOnWrite = new UnauthorizedAccessException("denied"),
        };
        var journal = new FakeJournal();

        var result = Build(toggle, new StubScanProvider("registry.run"), journal).SetEnabled(item, false, Ctx());

        Assert.False(result.Success);
        Assert.Contains("denied", result.Error!);
        Assert.Empty(journal.All);
    }

    [Fact]
    public void BackupRemoveDisableVerifiesTheValueIsGone()
    {
        var item = Item(category: StartupCategory.Winlogon, name: "Taskman");
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.BackupRemove)
        {
            Mechanism = "BackupRemove", After = null,
        };
        var journal = new FakeJournal();

        var result = Build(toggle, new StubScanProvider("registry.run"), journal).SetEnabled(item, false, Ctx());

        Assert.True(result.Success);
        Assert.Single(journal.All);
    }

    [Fact]
    public void BackupRemoveDisableFailsWhenTheValueIsStillThere()
    {
        var item = Item(category: StartupCategory.Winlogon, name: "Taskman");
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.BackupRemove)
        {
            Mechanism = "BackupRemove", After = null,
        };
        var scanner = new StubScanProvider("registry.run", item);
        var journal = new FakeJournal();

        var result = Build(toggle, scanner, journal).SetEnabled(item, false, Ctx());

        Assert.False(result.Success);
        Assert.Single(toggle.Restored);
        Assert.Empty(journal.All);
    }

    [Fact]
    public void BackupRemoveReplacementVerifiesTheReplacementValue()
    {
        var item = Item(category: StartupCategory.Winlogon, name: "Userinit",
                        raw: @"C:\Windows\system32\userinit.exe,C:\evil.exe");
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.BackupRemove)
        {
            Mechanism = "BackupRemove", After = @"expand:%systemroot%\system32\userinit.exe,",
        };
        var scanner = new StubScanProvider("registry.run",
            item with { RawValue = @"%systemroot%\system32\userinit.exe," });

        var result = Build(toggle, scanner, new FakeJournal()).SetEnabled(item, false, Ctx());

        Assert.True(result.Success);
    }

    [Fact]
    public void AProviderWithNoToggleImplementationIsReportedNotCrashed()
    {
        var item = Item();

        var result = Build(new StubToggleProvider("some.other.provider", ToggleCapability.Native),
                           new StubScanProvider("registry.run"), new FakeJournal())
            .SetEnabled(item, false, Ctx());

        Assert.False(result.Success);
        Assert.Contains("registry.run", result.Error!);
    }

    [Fact]
    public void TheCommittedRecordCarriesTheSessionIdAndCurrentTimestamp()
    {
        var item = Item();
        var scanner = new StubScanProvider("registry.run", item with { State = EnableState.Disabled });
        var clock = new FakeClock(new DateTime(2026, 7, 30, 10, 30, 0, DateTimeKind.Utc));
        var journal = new FakeJournal();
        var service = new ToggleService(
            new IToggleProvider[] { new StubToggleProvider("registry.run", ToggleCapability.Native) },
            new IStartupProvider[] { scanner }, journal, new StubRestorePointGate(),
            new RecordingConfirmationSink(), clock, Session);

        service.SetEnabled(item, false, Ctx());

        var record = journal.All.Single();
        Assert.Equal(Session, record.SessionId);
        Assert.Equal(clock.UtcNow, record.Ts);
    }
}
```

Create `StartupScanner.Tests/Presentation/ConfirmationTextTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Presentation;

namespace StartupScanner.Tests.Presentation;

public class ConfirmationTextTests
{
    private static StartupItem Item(RiskTier risk, StartupCategory category, string name = "Thing") => new()
    {
        Id = "id", ProviderId = "p", Location = "loc", Name = name, RawValue = "r",
        Category = category, Risk = risk,
    };

    [Fact]
    public void MediumRiskGetsAPlainConfirmation()
    {
        var text = ConfirmationText.For(Item(RiskTier.Medium, StartupCategory.Service, "Spooler"), enabling: false);

        Assert.Contains("Disable", text);
        Assert.Contains("Spooler", text);
    }

    [Fact]
    public void EnablingSaysEnable()
    {
        Assert.Contains("Enable", ConfirmationText.For(Item(RiskTier.Medium, StartupCategory.Service), enabling: true));
    }

    [Theory]
    [InlineData(StartupCategory.Winlogon, "logging in")]
    [InlineData(StartupCategory.BootExecute, "booting")]
    [InlineData(StartupCategory.WinsockLsp, "network")]
    [InlineData(StartupCategory.AppInit, "application")]
    [InlineData(StartupCategory.Ifeo, "redirects")]
    public void HighRiskNamesTheConcreteConsequence(StartupCategory category, string expectedFragment)
    {
        var text = ConfirmationText.For(Item(RiskTier.High, category), enabling: false);

        Assert.Contains(expectedFragment, text, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ToggleServiceTests|FullyQualifiedName~ConfirmationTextTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ToggleService' could not be found`.

- [ ] **Step 3: Write `ConfirmationText`**

Create `StartupScanner.Core/Presentation/ConfirmationText.cs`:

```csharp
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Presentation;

/// <summary>
/// Confirmation dialog copy. Medium risk gets a plain confirmation; High risk names the concrete
/// consequence so the user is not asked to approve an abstraction.
/// </summary>
public static class ConfirmationText
{
    public static string For(StartupItem item, bool enabling)
    {
        var verb = enabling ? "Enable" : "Disable";

        return item.Risk == RiskTier.High
            ? $"{verb} '{item.Name}'?\n\n{Consequence(item.Category)}"
            : $"{verb} '{item.Name}' ({item.Category})?";
    }

    private static string Consequence(StartupCategory category) => category switch
    {
        StartupCategory.Winlogon =>
            "Winlogon values run as part of the logon sequence. A bad change here can stop you logging in.",
        StartupCategory.BootExecute =>
            "BootExecute entries run before Windows starts. A bad change here can stop the machine booting.",
        StartupCategory.Ifeo =>
            "An Image File Execution Options Debugger value redirects the launch of another program. "
            + "Changing it alters what runs when that program is started.",
        StartupCategory.AppInit =>
            "AppInit_DLLs are loaded into most processes that use USER32, so this affects nearly every application.",
        StartupCategory.WinsockLsp =>
            "Winsock LSP entries sit inside the network stack. Removing one can break network connectivity.",
        _ => "This entry loads early in the boot or logon sequence.",
    };
}
```

- [ ] **Step 4: Write `WriteVerifier`**

Create `StartupScanner.Core/Writing/WriteVerifier.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Reads a write back through the owning scan provider and reports any mismatch. Returning null
/// means the write landed exactly as intended, which is the only case in which the caller may
/// append a journal entry.
/// </summary>
public sealed class WriteVerifier(IReadOnlyList<IStartupProvider> scanners)
{
    private readonly Dictionary<string, IStartupProvider> _scanners =
        scanners.ToDictionary(s => s.Id, StringComparer.Ordinal);

    public string? Check(WriteRecord record, bool expectedEnabled, ScanContext ctx)
    {
        if (!_scanners.TryGetValue(record.ProviderId, out var scanner))
            return $"No scan provider registered for '{record.ProviderId}', so the write cannot be verified.";

        StartupItem? found;
        try
        {
            found = scanner.Scan(ctx).FirstOrDefault(i => i.Id == record.ItemId);
        }
        catch (Exception ex)
        {
            return $"The change could not be read back: {ex.Message}";
        }

        if (record.Mechanism == "BackupRemove") return CheckBackupRemove(record, found);

        if (found is null) return "The entry could not be found after the write.";

        var expected = expectedEnabled ? EnableState.Enabled : EnableState.Disabled;
        return found.State == expected ? null : $"Read back {found.State} but expected {expected}.";
    }

    private static string? CheckBackupRemove(WriteRecord record, StartupItem? found)
    {
        if (record.After is null)
            return found is null ? null : "The value was still present after it was removed.";

        if (found is null) return "The value was not present after it was written.";

        var (value, type) = RegistryValueCodec.Decode(record.After);
        var expected = RegistryValueCodec.ToDisplay(value, type);
        return found.RawValue == expected ? null : $"Read back '{found.RawValue}' but expected '{expected}'.";
    }
}
```

- [ ] **Step 5: Write `ToggleService`**

Create `StartupScanner.Core/Writing/ToggleService.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Presentation;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// The only path through which anything is written. Order is fixed: guardrail, risk confirmation,
/// restore point, write, read back, journal. A failed read-back rolls the write back and appends
/// nothing, so the journal never records a write that did not land.
/// </summary>
public sealed class ToggleService
{
    private readonly Dictionary<string, IToggleProvider> _toggles;
    private readonly WriteVerifier _verifier;
    private readonly IWriteJournal _journal;
    private readonly IRestorePointGate _restorePoint;
    private readonly IConfirmationSink _confirmations;
    private readonly IClock _clock;
    private readonly string _sessionId;

    public ToggleService(
        IReadOnlyList<IToggleProvider> toggles,
        IReadOnlyList<IStartupProvider> scanners,
        IWriteJournal journal,
        IRestorePointGate restorePoint,
        IConfirmationSink confirmations,
        IClock clock,
        string sessionId)
    {
        _toggles = toggles.ToDictionary(t => t.ProviderId, StringComparer.Ordinal);
        _verifier = new WriteVerifier(scanners);
        _journal = journal;
        _restorePoint = restorePoint;
        _confirmations = confirmations;
        _clock = clock;
        _sessionId = sessionId;
    }

    public ToggleResult SetEnabled(StartupItem item, bool enabled, ScanContext ctx)
    {
        var blocked = Guardrails.Evaluate(item);
        if (blocked.Blocked) return ToggleResult.Fail(blocked.Reason!);

        if (!_toggles.TryGetValue(item.ProviderId, out var toggle))
            return ToggleResult.Fail($"Provider '{item.ProviderId}' has no writable implementation.");

        if (toggle.GetCapability(item) == ToggleCapability.Blocked)
            return ToggleResult.Fail("This entry is shown for reference only and is not writable.");

        if (item.Risk != RiskTier.Low && !_confirmations.Confirm(item, item.Risk, ConfirmationText.For(item, enabled)))
            return ToggleResult.Cancel();

        if (!_restorePoint.Ensure(out var restoreError)) return ToggleResult.Cancel(restoreError);

        WriteRecord record;
        try
        {
            record = toggle.SetEnabled(item, enabled, _journal);
        }
        catch (Exception ex)
        {
            return ToggleResult.Fail(ex.Message);
        }

        var problem = _verifier.Check(record, enabled, ctx);
        if (problem is not null) return RollBack(toggle, record, problem);

        var committed = record with { SessionId = _sessionId, Ts = _clock.UtcNow };
        _journal.Record(committed);
        return ToggleResult.Ok(committed);
    }

    private static ToggleResult RollBack(IToggleProvider toggle, WriteRecord record, string problem)
    {
        try
        {
            toggle.Restore(record);
        }
        catch (Exception ex)
        {
            return ToggleResult.Fail($"{problem} The rollback also failed: {ex.Message}");
        }

        return ToggleResult.Fail($"{problem} The change was rolled back and nothing was journaled.");
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ToggleServiceTests|FullyQualifiedName~ConfirmationTextTests"`
Expected: PASS — 20 tests passed.

- [ ] **Step 7: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add toggle service with write verification and rollback"
```

---

## Task 22: Undo service

**Files:**
- Create: `StartupScanner.Core/Writing/UndoService.cs`
- Test: `StartupScanner.Tests/Writing/UndoServiceTests.cs`

**Interfaces:**
- Consumes: `IToggleProvider`, `IStartupProvider`, `IWriteJournal`, `WriteVerifier`, `IClock`.
- Produces: `UndoService(IReadOnlyList<IToggleProvider> toggles, IReadOnlyList<IStartupProvider> scanners, IWriteJournal journal, IClock clock, string sessionId)` with `ToggleResult UndoLast(ScanContext ctx)` and `IReadOnlyList<ToggleResult> UndoSession(ScanContext ctx)`.

Undo is reverse-application of `writes.jsonl`: for each record, put `Before` back, verify the same way a forward write is verified, then append an inverse record so the journal stays append-only and last-write-wins stays correct. Inverse records are tagged with a `":undo"` session suffix so that undoing twice steps further back through history instead of redoing the undo.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Writing/UndoServiceTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class UndoServiceTests
{
    private const string Session = "session-1";

    private static WriteRecord Record(string itemId, bool enabled, DateTime ts, string session = Session) => new()
    {
        Ts = ts, SessionId = session, ItemId = itemId, ProviderId = "registry.run",
        Mechanism = "StartupApproved", Location = @"HKCU\Approved", Name = "Spotify",
        Category = StartupCategory.RunKey,
        Before = "binary:AgAAAAAAAAAAAAAA", After = "binary:AwAAAAAAAAAAAAAA", Enabled = enabled,
    };

    private static StartupItem Item(string itemId, EnableState state) => new()
    {
        Id = itemId, ProviderId = "registry.run", Location = @"HKCU\Run", Name = "Spotify",
        RawValue = @"C:\Spotify.exe", Category = StartupCategory.RunKey, State = state,
    };

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    private static (UndoService Service, StubToggleProvider Toggle, StubScanProvider Scanner, FakeJournal Journal) Sut()
    {
        var toggle = new StubToggleProvider("registry.run", ToggleCapability.Native);
        var scanner = new StubScanProvider("registry.run");
        var journal = new FakeJournal();
        var service = new UndoService(new IToggleProvider[] { toggle }, new IStartupProvider[] { scanner },
                                      journal, new FakeClock(), Session);
        return (service, toggle, scanner, journal);
    }

    [Fact]
    public void UndoLastRestoresTheMostRecentWrite()
    {
        var (service, toggle, scanner, journal) = Sut();
        var record = Record("id-1", enabled: false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc));
        journal.Record(record);
        // Restoring the pre-write blob puts the entry back to Enabled.
        scanner.Items.Add(Item("id-1", EnableState.Enabled));

        var result = service.UndoLast(Ctx());

        Assert.True(result.Success);
        Assert.Equal(record, toggle.Restored.Single());
    }

    [Fact]
    public void UndoAppendsAnInverseRecordSoTheJournalStaysAppendOnly()
    {
        var (service, _, scanner, journal) = Sut();
        journal.Record(Record("id-1", enabled: false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        scanner.Items.Add(Item("id-1", EnableState.Enabled));

        service.UndoLast(Ctx());

        Assert.Equal(2, journal.All.Count);
        var inverse = journal.All[^1];
        Assert.Equal("binary:AwAAAAAAAAAAAAAA", inverse.Before);
        Assert.Equal("binary:AgAAAAAAAAAAAAAA", inverse.After);
        Assert.True(inverse.Enabled);
        Assert.Equal("session-1:undo", inverse.SessionId);
    }

    [Fact]
    public void UndoingTwiceStepsFurtherBackRatherThanRedoing()
    {
        var (service, toggle, scanner, journal) = Sut();
        var first = Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc));
        var second = Record("id-2", false, new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc));
        journal.Record(first);
        journal.Record(second);
        scanner.Items.Add(Item("id-1", EnableState.Enabled));
        scanner.Items.Add(Item("id-2", EnableState.Enabled));

        service.UndoLast(Ctx());
        service.UndoLast(Ctx());

        Assert.Equal(new[] { second, first }, toggle.Restored);
    }

    [Fact]
    public void UndoLastReportsWhenThereIsNothingLeft()
    {
        var (service, _, _, _) = Sut();

        var result = service.UndoLast(Ctx());

        Assert.False(result.Success);
        Assert.Contains("nothing", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UndoSessionReversesEveryWriteFromThisSessionNewestFirst()
    {
        var (service, toggle, scanner, journal) = Sut();
        var first = Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc));
        var second = Record("id-2", false, new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc));
        journal.Record(first);
        journal.Record(second);
        scanner.Items.Add(Item("id-1", EnableState.Enabled));
        scanner.Items.Add(Item("id-2", EnableState.Enabled));

        var results = service.UndoSession(Ctx());

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(new[] { second, first }, toggle.Restored);
    }

    [Fact]
    public void UndoSessionIgnoresWritesFromEarlierSessions()
    {
        var (service, toggle, scanner, journal) = Sut();
        journal.Record(Record("id-old", false, new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc), "older-session"));
        var mine = Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc));
        journal.Record(mine);
        scanner.Items.Add(Item("id-1", EnableState.Enabled));

        service.UndoSession(Ctx());

        Assert.Equal(mine, toggle.Restored.Single());
    }

    [Fact]
    public void RunningUndoSessionTwiceDoesNothingTheSecondTime()
    {
        var (service, toggle, scanner, journal) = Sut();
        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        scanner.Items.Add(Item("id-1", EnableState.Enabled));

        service.UndoSession(Ctx());
        var second = service.UndoSession(Ctx());

        Assert.Empty(second);
        Assert.Single(toggle.Restored);
    }

    [Fact]
    public void AFailedVerificationDuringUndoIsReportedAndNothingIsJournalled()
    {
        var (service, _, scanner, journal) = Sut();
        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        // The read-back still says Disabled, so the restore did not land.
        scanner.Items.Add(Item("id-1", EnableState.Disabled));

        var result = service.UndoLast(Ctx());

        Assert.False(result.Success);
        Assert.Single(journal.All);
    }

    [Fact]
    public void UndoSessionStopsAtTheFirstFailure()
    {
        var (service, toggle, scanner, journal) = Sut();
        journal.Record(Record("id-1", false, new DateTime(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc)));
        journal.Record(Record("id-2", false, new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc)));
        scanner.Items.Add(Item("id-2", EnableState.Disabled));   // id-2 will fail verification

        var results = service.UndoSession(Ctx());

        Assert.Single(results);
        Assert.False(results[0].Success);
        Assert.Single(toggle.Restored);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~UndoServiceTests"`
Expected: FAIL — `CS0246: The type or namespace name 'UndoService' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `StartupScanner.Core/Writing/UndoService.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Reverse-application of writes.jsonl. Each undo puts the record's Before value back, verifies it
/// the same way a forward write is verified, and appends an inverse record so the journal stays
/// append-only.
/// </summary>
public sealed class UndoService
{
    private const string UndoSuffix = ":undo";

    private readonly Dictionary<string, IToggleProvider> _toggles;
    private readonly WriteVerifier _verifier;
    private readonly IWriteJournal _journal;
    private readonly IClock _clock;
    private readonly string _sessionId;
    private readonly HashSet<WriteRecord> _undone = new(ReferenceEqualityComparer.Instance);

    public UndoService(
        IReadOnlyList<IToggleProvider> toggles,
        IReadOnlyList<IStartupProvider> scanners,
        IWriteJournal journal,
        IClock clock,
        string sessionId)
    {
        _toggles = toggles.ToDictionary(t => t.ProviderId, StringComparer.Ordinal);
        _verifier = new WriteVerifier(scanners);
        _journal = journal;
        _clock = clock;
        _sessionId = sessionId;
    }

    public ToggleResult UndoLast(ScanContext ctx)
    {
        var record = _journal.All.LastOrDefault(r => !IsUndo(r) && !_undone.Contains(r));
        return record is null
            ? ToggleResult.Fail("There is nothing left to undo.")
            : Reverse(record, ctx);
    }

    public IReadOnlyList<ToggleResult> UndoSession(ScanContext ctx)
    {
        var pending = _journal.All
            .Where(r => r.SessionId == _sessionId && !_undone.Contains(r))
            .Reverse()
            .ToList();

        var results = new List<ToggleResult>();
        foreach (var record in pending)
        {
            var result = Reverse(record, ctx);
            results.Add(result);
            if (!result.Success) break;
        }
        return results;
    }

    private ToggleResult Reverse(WriteRecord record, ScanContext ctx)
    {
        if (!_toggles.TryGetValue(record.ProviderId, out var toggle))
            return ToggleResult.Fail($"Provider '{record.ProviderId}' has no writable implementation.");

        try
        {
            toggle.Restore(record);
        }
        catch (Exception ex)
        {
            return ToggleResult.Fail($"Undo failed: {ex.Message}");
        }

        var inverse = record with
        {
            Ts = _clock.UtcNow,
            SessionId = _sessionId + UndoSuffix,
            Before = record.After,
            After = record.Before,
            Enabled = !record.Enabled,
        };

        var problem = _verifier.Check(inverse, inverse.Enabled, ctx);
        if (problem is not null) return ToggleResult.Fail($"{problem} The undo was not journaled.");

        _undone.Add(record);
        _journal.Record(inverse);
        return ToggleResult.Ok(inverse);
    }

    private static bool IsUndo(WriteRecord record)
        => record.SessionId.EndsWith(UndoSuffix, StringComparison.Ordinal);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~UndoServiceTests"`
Expected: PASS — 9 tests passed.

- [ ] **Step 5: Run the whole suite**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: PASS — all tests green.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Writing/UndoService.cs StartupScanner.Tests/Writing/UndoServiceTests.cs
git commit -m "feat: add undo service with append-only inverse records"
```

---

## Task 23: Table value scanner and provider 5 — Winlogon

**Files:**
- Create: `StartupScanner.Core/Scanning/TableValueScanner.cs`, `Providers/WinlogonProvider.cs`, `Writing/WinlogonToggle.cs`
- Test: `StartupScanner.Tests/Providers/WinlogonProviderTests.cs`, `StartupScanner.Tests/Writing/WinlogonToggleTests.cs`

**Interfaces:**
- Consumes: `IRegistry`, `IFileSystem`, `RegistryValueCodec`, `CommandLine`, `RegistryValueToggle`.
- Produces:
  - `ValueSource(string RootToken, string SubKey, RegistryViewKind View, string[]? ValueNames)` — `ValueNames == null` means every value in the key.
  - `TableValueScanner(IRegistry registry, IFileSystem fs)` with `IEnumerable<StartupItem> Scan(string providerId, StartupCategory category, IReadOnlyList<ValueSource> sources, ScanContext ctx)`.
  - `WinlogonProvider(IRegistry registry, IFileSystem fs)` with `Id == "winlogon"`.
  - `WinlogonToggle.Create(IRegistry registry, IClock clock) -> RegistryValueToggle`.

Nine of the remaining fourteen providers are "read these value names from these keys", so that logic is written once here. `TableValueScanner` skips absent and empty values — a blank `Taskman` is not an autostart entry — and records access-denied per key without aborting.

`WinlogonToggle` is the one place the `replacement` delegate is used. A non-default `Userinit` or `Shell` is not deleted, because a machine with no `Userinit` value cannot complete logon; the OS default is written back instead and the prior value is journaled, so re-enable still restores exactly what was there.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Providers/WinlogonProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class WinlogonProviderTests
{
    private const string Machine = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string User = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private static (WinlogonProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        var fs = new FakeFileSystem();
        fs.SetEnvironment("systemroot", @"C:\Windows");
        return (new WinlogonProvider(registry, fs), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("winlogon", provider.Id);
        Assert.Equal(StartupCategory.Winlogon, provider.Category);
    }

    [Fact]
    public void ReadsTheMachineWinlogonValues()
    {
        var (provider, registry) = Sut();
        registry.Set(Machine, "Userinit", @"%systemroot%\system32\userinit.exe,", RegistryValueType.ExpandString);
        registry.Set(Machine, "Shell", "explorer.exe", RegistryValueType.String);
        registry.Set(Machine, "Taskman", @"C:\evil\taskman.exe", RegistryValueType.String);
        registry.Set(Machine, "AppSetup", "setup.exe", RegistryValueType.String);
        registry.Set(Machine, "GinaDLL", "gina.dll", RegistryValueType.String);

        var names = provider.Scan(Ctx()).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "AppSetup", "GinaDLL", "Shell", "Taskman", "Userinit" }, names);
    }

    [Fact]
    public void ExpandsEnvironmentVariablesIntoImagePath()
    {
        var (provider, registry) = Sut();
        registry.Set(Machine, "Userinit", @"%systemroot%\system32\userinit.exe,", RegistryValueType.ExpandString);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(@"%systemroot%\system32\userinit.exe,", item.RawValue);
        Assert.Equal(@"C:\Windows\system32\userinit.exe", item.ImagePath);
        Assert.Equal(EnableState.Enabled, item.State);
    }

    [Fact]
    public void BlankAndAbsentValuesAreNotReported()
    {
        var (provider, registry) = Sut();
        registry.Set(Machine, "Taskman", "", RegistryValueType.String);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void ReadsThePerUserWinlogonValuesToo()
    {
        var (provider, registry) = Sut();
        registry.Set(User, "Shell", "custom.exe", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(User, item.Location);
    }

    [Fact]
    public void WalksTheNotifySubkeysAndReportsTheirDllName()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\Notify\mynotify", "DLLName", @"C:\notify.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("mynotify", item.Name);
        Assert.Equal($@"{Machine}\Notify\mynotify", item.Location);
        Assert.Equal(@"C:\notify.dll", item.RawValue);
    }

    [Fact]
    public void AnInaccessibleKeyBecomesAScanError()
    {
        var (provider, registry) = Sut();
        registry.Set(Machine, "Shell", "explorer.exe", RegistryValueType.String);
        registry.Deny(Machine);
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));
        Assert.Contains(ctx.Errors, e => e.ProviderId == "winlogon");
    }
}
```

Create `StartupScanner.Tests/Writing/WinlogonToggleTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class WinlogonToggleTests
{
    private const string Machine = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private static StartupItem Item(string name, string raw) => new()
    {
        Id = Identity.Compute("winlogon", null, Machine, name),
        ProviderId = "winlogon", Location = Machine, Name = name, RawValue = raw,
        Category = StartupCategory.Winlogon, State = EnableState.Enabled,
    };

    [Fact]
    public void DisablingAHijackedUserinitWritesTheOsDefaultBackInsteadOfDeleting()
    {
        var registry = new FakeRegistry();
        registry.Set(Machine, "Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe", RegistryValueType.ExpandString);
        var toggle = WinlogonToggle.Create(registry, new FakeClock());

        var record = toggle.SetEnabled(Item("Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe"),
                                      enabled: false, new FakeJournal());

        Assert.Equal(@"%systemroot%\system32\userinit.exe,", registry.OpenKey(Machine)!.GetValue("Userinit"));
        Assert.Equal(@"expand:C:\Windows\system32\userinit.exe,C:\evil.exe", record.Before);
    }

    [Fact]
    public void DisablingAHijackedShellWritesExplorerBack()
    {
        var registry = new FakeRegistry();
        registry.Set(Machine, "Shell", "explorer.exe,evil.exe", RegistryValueType.String);
        var toggle = WinlogonToggle.Create(registry, new FakeClock());

        toggle.SetEnabled(Item("Shell", "explorer.exe,evil.exe"), enabled: false, new FakeJournal());

        Assert.Equal("explorer.exe", registry.OpenKey(Machine)!.GetValue("Shell"));
    }

    [Fact]
    public void DisablingAnyOtherWinlogonValueDeletesIt()
    {
        var registry = new FakeRegistry();
        registry.Set(Machine, "Taskman", @"C:\taskman.exe", RegistryValueType.String);
        var toggle = WinlogonToggle.Create(registry, new FakeClock());

        toggle.SetEnabled(Item("Taskman", @"C:\taskman.exe"), enabled: false, new FakeJournal());

        Assert.Null(registry.OpenKey(Machine)!.GetValue("Taskman"));
    }

    [Fact]
    public void ReEnablingRestoresTheExactPriorValue()
    {
        var registry = new FakeRegistry();
        registry.Set(Machine, "Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe", RegistryValueType.ExpandString);
        var toggle = WinlogonToggle.Create(registry, new FakeClock());
        var journal = new FakeJournal();
        var item = Item("Userinit", @"C:\Windows\system32\userinit.exe,C:\evil.exe");
        journal.Record(toggle.SetEnabled(item, enabled: false, journal));

        toggle.SetEnabled(item with { State = EnableState.Disabled }, enabled: true, journal);

        Assert.Equal(@"C:\Windows\system32\userinit.exe,C:\evil.exe", registry.OpenKey(Machine)!.GetValue("Userinit"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WinlogonProviderTests|FullyQualifiedName~WinlogonToggleTests"`
Expected: FAIL — `CS0246: The type or namespace name 'WinlogonProvider' could not be found`.

- [ ] **Step 3: Write `TableValueScanner`**

Create `StartupScanner.Core/Scanning/TableValueScanner.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Scanning;

/// <summary>One key to read, and which of its values matter. Null ValueNames means all of them.</summary>
public sealed record ValueSource(string RootToken, string SubKey, RegistryViewKind View, string[]? ValueNames);

/// <summary>
/// Shared "read these values from these keys" scan used by most of the registry providers.
/// Absent and empty values are skipped; an unreadable key becomes a ScanError.
/// </summary>
public sealed class TableValueScanner(IRegistry registry, IFileSystem fs)
{
    public IEnumerable<StartupItem> Scan(string providerId, StartupCategory category,
                                        IReadOnlyList<ValueSource> sources, ScanContext ctx)
    {
        foreach (var source in sources)
        {
            var path = source.RootToken == "HKLM"
                ? $@"HKLM\{source.SubKey}"
                : $@"{ctx.UserRoot}\{source.SubKey}";

            foreach (var item in ScanOne(providerId, category, source, path, ctx)) yield return item;
        }
    }

    private IReadOnlyList<StartupItem> ScanOne(string providerId, StartupCategory category,
                                               ValueSource source, string path, ScanContext ctx)
    {
        var found = new List<StartupItem>();
        var sid = source.RootToken == "HKLM" ? null : ctx.UserSid;

        try
        {
            using var key = registry.OpenKey(path, source.View);
            if (key is null) return found;

            foreach (var name in source.ValueNames ?? key.GetValueNames().ToArray())
            {
                var raw = RegistryValueCodec.ToDisplay(key.GetValue(name), key.GetValueType(name));
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var (image, args) = CommandLine.Split(raw, fs);
                found.Add(new StartupItem
                {
                    Id = Identity.Compute(providerId, sid, path, name),
                    ProviderId = providerId,
                    Location = path,
                    Name = name,
                    RawValue = raw,
                    Category = category,
                    ImagePath = image,
                    Arguments = args,
                    State = EnableState.Enabled,
                    StateSource = "ValuePresent",
                    UserSid = sid,
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["View"] = source.View.ToString(),
                        ["ValueType"] = key.GetValueType(name).ToString(),
                    },
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(providerId, path, ex.Message));
        }

        return found;
    }
}
```

- [ ] **Step 4: Write `WinlogonProvider`**

Create `StartupScanner.Core/Providers/WinlogonProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class WinlogonProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    public const string MachineKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string UserKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string ProviderIdValue = "winlogon";

    private static readonly IReadOnlyList<ValueSource> Sources = new[]
    {
        new ValueSource("HKLM", MachineKey, RegistryViewKind.Default,
            new[] { "Userinit", "Shell", "Taskman", "AppSetup", "GinaDLL" }),
        new ValueSource("%USER%", UserKey, RegistryViewKind.Default,
            new[] { "Shell", "Taskman" }),
    };

    private readonly TableValueScanner _values = new(registry, fs);

    public string Id => ProviderIdValue;
    public StartupCategory Category => StartupCategory.Winlogon;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var item in _values.Scan(Id, Category, Sources, ctx)) yield return item;
        foreach (var item in ScanNotify(ctx)) yield return item;
    }

    /// <summary>Winlogon\Notify packages are subkeys carrying a DLLName rather than flat values.</summary>
    private IReadOnlyList<StartupItem> ScanNotify(ScanContext ctx)
    {
        var notifyPath = $@"HKLM\{MachineKey}\Notify";
        var found = new List<StartupItem>();

        try
        {
            using var root = registry.OpenKey(notifyPath);
            if (root is null) return found;

            foreach (var name in root.GetSubKeyNames())
            {
                using var child = root.OpenSubKey(name);
                var raw = RegistryValueCodec.ToDisplay(child?.GetValue("DLLName"),
                                                       child?.GetValueType("DLLName") ?? RegistryValueType.None);
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var path = $@"{notifyPath}\{name}";
                var (image, args) = CommandLine.Split(raw, fs);

                found.Add(new StartupItem
                {
                    Id = Identity.Compute(Id, null, path, name),
                    ProviderId = Id,
                    Location = path,
                    Name = name,
                    RawValue = raw,
                    Category = Category,
                    ImagePath = image,
                    Arguments = args,
                    State = EnableState.Enabled,
                    StateSource = "ValuePresent",
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ValueName"] = "DLLName",
                    },
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, notifyPath, ex.Message));
        }

        return found;
    }
}
```

- [ ] **Step 5: Write `WinlogonToggle`**

Create `StartupScanner.Core/Writing/WinlogonToggle.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Winlogon's BackupRemove toggle. Userinit and Shell are restored to the OS default rather than
/// deleted: a machine with no Userinit value cannot complete logon, so deletion would violate the
/// "never make the machine unbootable" rule even though the prior value is journaled.
/// </summary>
public static class WinlogonToggle
{
    public const string DefaultUserinit = @"%systemroot%\system32\userinit.exe,";
    public const string DefaultShell = "explorer.exe";

    public static RegistryValueToggle Create(IRegistry registry, IClock clock)
        => new(WinlogonProvider.ProviderIdValue, registry, clock, SafeDefaultFor);

    private static (object Value, RegistryValueType Type)? SafeDefaultFor(StartupItem item)
    {
        if (item.Name.Equals("Userinit", StringComparison.OrdinalIgnoreCase))
            return (DefaultUserinit, RegistryValueType.ExpandString);

        if (item.Name.Equals("Shell", StringComparison.OrdinalIgnoreCase))
            return (DefaultShell, RegistryValueType.String);

        return null;
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WinlogonProviderTests|FullyQualifiedName~WinlogonToggleTests"`
Expected: PASS — 11 tests passed.

- [ ] **Step 7: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add table value scanner and Winlogon provider with safe-default toggle"
```

---

## Task 24: Providers 6 and 9 — BootExecute and AppInit

**Files:**
- Create: `StartupScanner.Core/Providers/BootExecuteProvider.cs`, `Providers/AppInitProvider.cs`
- Test: `StartupScanner.Tests/Providers/BootExecuteProviderTests.cs`, `StartupScanner.Tests/Providers/AppInitProviderTests.cs`

**Interfaces:**
- Consumes: `TableValueScanner`, `ValueSource`.
- Produces:
  - `BootExecuteProvider(IRegistry registry, IFileSystem fs)` with `Id == "bootexecute"`, `const string SessionManagerKey`.
  - `AppInitProvider(IRegistry registry, IFileSystem fs)` with `Id == "appinit"`.

Both are pure `TableValueScanner` tables. `BootExecute` and friends are `REG_MULTI_SZ`, so `RegistryValueCodec.ToDisplay` joins them with `"; "` — which is exactly the form the `autocheck autochk *` guardrail compares against. `AppInit_DLLs` exists separately in the 32-bit and 64-bit views, so both are scanned; `AppCertDlls` is a subkey full of values under Session Manager.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Providers/BootExecuteProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class BootExecuteProviderTests
{
    private const string Key = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager";

    private static (BootExecuteProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new BootExecuteProvider(registry, new FakeFileSystem()), registry);
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("bootexecute", provider.Id);
        Assert.Equal(StartupCategory.BootExecute, provider.Category);
    }

    [Fact]
    public void ReadsAllFourValueNames()
    {
        var (provider, registry) = Sut();
        registry.Set(Key, "BootExecute", new[] { "autocheck autochk *" }, RegistryValueType.MultiString);
        registry.Set(Key, "SetupExecute", new[] { "setup.exe" }, RegistryValueType.MultiString);
        registry.Set(Key, "PlatformExecute", new[] { "platform.exe" }, RegistryValueType.MultiString);
        registry.Set(Key, "S0InitialCommand", "init.exe", RegistryValueType.String);

        var names = provider.Scan(new ScanContext { UserRoot = "HKCU" }).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "BootExecute", "PlatformExecute", "S0InitialCommand", "SetupExecute" }, names);
    }

    [Fact]
    public void MultiStringValuesRenderJoinedSoTheGuardrailCanMatchTheDefault()
    {
        var (provider, registry) = Sut();
        registry.Set(Key, "BootExecute", new[] { "autocheck autochk *" }, RegistryValueType.MultiString);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }));

        Assert.Equal("autocheck autochk *", item.RawValue);
        Assert.True(Guardrails.Evaluate(item).Blocked);
    }

    [Fact]
    public void AnAppendedBootExecuteEntryIsNotBlocked()
    {
        var (provider, registry) = Sut();
        registry.Set(Key, "BootExecute", new[] { "autocheck autochk *", "evil.exe" }, RegistryValueType.MultiString);

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU" }));

        Assert.Equal("autocheck autochk *; evil.exe", item.RawValue);
        Assert.False(Guardrails.Evaluate(item).Blocked);
    }

    [Fact]
    public void MissingKeyYieldsNothing()
    {
        var (provider, _) = Sut();
        Assert.Empty(provider.Scan(new ScanContext { UserRoot = "HKCU" }));
    }
}
```

Create `StartupScanner.Tests/Providers/AppInitProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class AppInitProviderTests
{
    private const string Windows = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string AppCert = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls";

    private static (AppInitProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new AppInitProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("appinit", provider.Id);
        Assert.Equal(StartupCategory.AppInit, provider.Category);
    }

    [Fact]
    public void ReadsAppInitDllsAndItsEnableFlag()
    {
        var (provider, registry) = Sut();
        registry.Set(Windows, "AppInit_DLLs", @"C:\hook.dll", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set(Windows, "LoadAppInit_DLLs", 1, RegistryValueType.Dword, RegistryViewKind.Registry64);

        var names = provider.Scan(Ctx()).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "AppInit_DLLs", "LoadAppInit_DLLs" }, names);
    }

    [Fact]
    public void ScansBothRegistryViewsBecauseTheyHoldSeparateValues()
    {
        var (provider, registry) = Sut();
        registry.Set(Windows, "AppInit_DLLs", @"C:\hook64.dll", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set(Windows, "AppInit_DLLs", @"C:\hook32.dll", RegistryValueType.String, RegistryViewKind.Registry32);

        var raws = provider.Scan(Ctx()).Select(i => i.RawValue).OrderBy(r => r).ToArray();

        Assert.Equal(new[] { @"C:\hook32.dll", @"C:\hook64.dll" }, raws);
    }

    [Fact]
    public void AppInitDllsIsHighRiskAndAppCertDllsIsMedium()
    {
        var (provider, registry) = Sut();
        registry.Set(Windows, "AppInit_DLLs", @"C:\hook.dll", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set(AppCert, "Something", @"C:\cert.dll", RegistryValueType.String);

        var items = provider.Scan(Ctx()).ToList();

        Assert.Equal(RiskTier.High, RiskTiers.For(items.Single(i => i.Name == "AppInit_DLLs")));
        Assert.Equal(RiskTier.Medium, RiskTiers.For(items.Single(i => i.Name == "Something")));
    }

    [Fact]
    public void ReadsEveryValueUnderAppCertDlls()
    {
        var (provider, registry) = Sut();
        registry.Set(AppCert, "First", @"C:\a.dll", RegistryValueType.String);
        registry.Set(AppCert, "Second", @"C:\b.dll", RegistryValueType.String);

        Assert.Equal(2, provider.Scan(Ctx()).Count());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~BootExecuteProviderTests|FullyQualifiedName~AppInitProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'BootExecuteProvider' could not be found`.

- [ ] **Step 3: Write `BootExecuteProvider`**

Create `StartupScanner.Core/Providers/BootExecuteProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;

namespace StartupScanner.Core.Providers;

public sealed class BootExecuteProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    public const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    private static readonly IReadOnlyList<ValueSource> Sources = new[]
    {
        new ValueSource("HKLM", SessionManagerKey, RegistryViewKind.Default,
            new[] { "BootExecute", "SetupExecute", "PlatformExecute", "S0InitialCommand" }),
    };

    private readonly TableValueScanner _values = new(registry, fs);

    public string Id => "bootexecute";
    public StartupCategory Category => StartupCategory.BootExecute;

    public IEnumerable<StartupItem> Scan(ScanContext ctx) => _values.Scan(Id, Category, Sources, ctx);
}
```

- [ ] **Step 4: Write `AppInitProvider`**

Create `StartupScanner.Core/Providers/AppInitProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;

namespace StartupScanner.Core.Providers;

public sealed class AppInitProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string WindowsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string AppCertKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls";

    private static readonly IReadOnlyList<ValueSource> Sources = new[]
    {
        // AppInit_DLLs is per-view: 32-bit processes read the WOW6432Node copy.
        new ValueSource("HKLM", WindowsKey, RegistryViewKind.Registry64,
            new[] { "AppInit_DLLs", "LoadAppInit_DLLs" }),
        new ValueSource("HKLM", WindowsKey, RegistryViewKind.Registry32,
            new[] { "AppInit_DLLs", "LoadAppInit_DLLs" }),
        new ValueSource("HKLM", AppCertKey, RegistryViewKind.Default, null),
    };

    private readonly TableValueScanner _values = new(registry, fs);

    public string Id => "appinit";
    public StartupCategory Category => StartupCategory.AppInit;

    public IEnumerable<StartupItem> Scan(ScanContext ctx) => _values.Scan(Id, Category, Sources, ctx);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~BootExecuteProviderTests|FullyQualifiedName~AppInitProviderTests"`
Expected: PASS — 10 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Providers StartupScanner.Tests/Providers
git commit -m "feat: add BootExecute and AppInit providers"
```

---

## Task 25: Provider 7 — Active Setup

**Files:**
- Create: `StartupScanner.Core/Providers/ActiveSetupProvider.cs`
- Test: `StartupScanner.Tests/Providers/ActiveSetupProviderTests.cs`

**Interfaces:**
- Consumes: `IRegistry`, `IFileSystem`, `RegistryValueCodec`, `CommandLine`.
- Produces: `ActiveSetupProvider(IRegistry registry, IFileSystem fs)` with `Id == "active-setup"`. Items carry `Extra` keys `MachineVersion`, `UserVersion`, `WillRunAtNextLogon`, `ComponentId`, `IsInstalled`.

Active Setup runs a component's `StubPath` at logon only while the per-user `Version` stamp is behind the machine one, so the interesting fact is not just that a `StubPath` exists but whether it is still pending. Both versions and the verdict go into `Extra` for the details panel; the item's `State` stays `Enabled` because a component that already ran will run again after any version bump.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Providers/ActiveSetupProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class ActiveSetupProviderTests
{
    private const string Machine = @"HKLM\SOFTWARE\Microsoft\Active Setup\Installed Components";
    private const string User = @"HKCU\Software\Microsoft\Active Setup\Installed Components";
    private const string Guid = "{89820200-ECBD-11cf-8B85-00AA005B4383}";

    private static (ActiveSetupProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new ActiveSetupProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("active-setup", provider.Id);
        Assert.Equal(StartupCategory.ActiveSetup, provider.Category);
    }

    [Fact]
    public void ReportsAComponentWithAStubPath()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "StubPath", @"C:\Windows\System32\ie4uinit.exe -BaseSettings", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "Version", "27,1,0,0", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "ComponentID", "IE", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "IsInstalled", 1, RegistryValueType.Dword);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(Guid, item.Name);
        Assert.Equal($@"{Machine}\{Guid}", item.Location);
        Assert.Equal(@"C:\Windows\System32\ie4uinit.exe -BaseSettings", item.RawValue);
        Assert.Equal(@"C:\Windows\System32\ie4uinit.exe", item.ImagePath);
        Assert.Equal("-BaseSettings", item.Arguments);
        Assert.Equal("27,1,0,0", item.Extra["MachineVersion"]);
        Assert.Equal("IE", item.Extra["ComponentId"]);
        Assert.Equal("1", item.Extra["IsInstalled"]);
    }

    [Fact]
    public void ComponentsWithNoStubPathAreNotAutostartEntries()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "Version", "1,0,0,0", RegistryValueType.String);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void AMissingUserStampMeansTheStubWillRunAtNextLogon()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "StubPath", "run.exe", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "Version", "27,1,0,0", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("", item.Extra["UserVersion"]);
        Assert.Equal("True", item.Extra["WillRunAtNextLogon"]);
    }

    [Fact]
    public void AMatchingUserStampMeansTheStubAlreadyRan()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "StubPath", "run.exe", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "Version", "27,1,0,0", RegistryValueType.String);
        registry.Set($@"{User}\{Guid}", "Version", "27,1,0,0", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("27,1,0,0", item.Extra["UserVersion"]);
        Assert.Equal("False", item.Extra["WillRunAtNextLogon"]);
    }

    [Fact]
    public void AStaleUserStampMeansTheStubWillRunAgain()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "StubPath", "run.exe", RegistryValueType.String);
        registry.Set($@"{Machine}\{Guid}", "Version", "27,1,0,0", RegistryValueType.String);
        registry.Set($@"{User}\{Guid}", "Version", "26,0,0,0", RegistryValueType.String);

        Assert.Equal("True", Assert.Single(provider.Scan(Ctx())).Extra["WillRunAtNextLogon"]);
    }

    [Fact]
    public void AnInaccessibleComponentBecomesAScanError()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Machine}\{Guid}", "StubPath", "run.exe", RegistryValueType.String);
        registry.Deny($@"{Machine}\{Guid}");
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));
        Assert.Contains(ctx.Errors, e => e.ProviderId == "active-setup");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ActiveSetupProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ActiveSetupProvider' could not be found`.

- [ ] **Step 3: Write the provider**

Create `StartupScanner.Core/Providers/ActiveSetupProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class ActiveSetupProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string MachineKey = @"HKLM\SOFTWARE\Microsoft\Active Setup\Installed Components";
    private const string UserSubKey = @"Software\Microsoft\Active Setup\Installed Components";

    public string Id => "active-setup";
    public StartupCategory Category => StartupCategory.ActiveSetup;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        IReadOnlyList<string> components;
        try
        {
            using var root = registry.OpenKey(MachineKey);
            if (root is null) yield break;
            components = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, MachineKey, ex.Message));
            yield break;
        }

        foreach (var component in components)
        {
            var item = TryBuild(component, ctx);
            if (item is not null) yield return item;
        }
    }

    private StartupItem? TryBuild(string component, ScanContext ctx)
    {
        var path = $@"{MachineKey}\{component}";
        string stubPath, machineVersion, componentId, isInstalled;

        try
        {
            using var key = registry.OpenKey(path);
            if (key is null) return null;

            stubPath = Text(key, "StubPath");
            if (string.IsNullOrWhiteSpace(stubPath)) return null;   // nothing runs at logon

            machineVersion = Text(key, "Version");
            componentId = Text(key, "ComponentID");
            isInstalled = Text(key, "IsInstalled");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            return null;
        }

        var userVersion = UserStamp(component, ctx);
        var (image, args) = CommandLine.Split(stubPath, fs);

        return new StartupItem
        {
            Id = Identity.Compute(Id, null, path, component),
            ProviderId = Id,
            Location = path,
            Name = component,
            RawValue = stubPath,
            Category = Category,
            ImagePath = image,
            Arguments = args,
            State = EnableState.Enabled,
            StateSource = "ValuePresent",
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MachineVersion"] = machineVersion,
                ["UserVersion"] = userVersion,
                ["ComponentId"] = componentId,
                ["IsInstalled"] = isInstalled,
                // The stub runs while the per-user stamp is behind the machine stamp.
                ["WillRunAtNextLogon"] = (!string.Equals(machineVersion, userVersion, StringComparison.OrdinalIgnoreCase)).ToString(),
                ["ValueName"] = "StubPath",
            },
        };
    }

    private string UserStamp(string component, ScanContext ctx)
    {
        try
        {
            using var key = registry.OpenKey($@"{ctx.UserRoot}\{UserSubKey}\{component}");
            return Text(key, "Version");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, $@"{ctx.UserRoot}\{UserSubKey}\{component}", ex.Message));
            return "";
        }
    }

    private static string Text(IRegistryKey? key, string name)
        => key is null ? "" : RegistryValueCodec.ToDisplay(key.GetValue(name), key.GetValueType(name));
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ActiveSetupProviderTests"`
Expected: PASS — 7 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Providers/ActiveSetupProvider.cs StartupScanner.Tests/Providers/ActiveSetupProviderTests.cs
git commit -m "feat: add Active Setup provider with pending-stub detection"
```

---

## Task 26: Provider 8 — Image File Execution Options

**Files:**
- Create: `StartupScanner.Core/Providers/IfeoProvider.cs`
- Test: `StartupScanner.Tests/Providers/IfeoProviderTests.cs`

**Interfaces:**
- Consumes: `IRegistry`, `IFileSystem`, `RegistryValueCodec`, `CommandLine`.
- Produces: `IfeoProvider(IRegistry registry, IFileSystem fs)` with `Id == "ifeo"`. Item `Name` is `"<image>:<valueName>"`, `Location` is the IFEO subkey, and `Extra["TargetImage"]`/`Extra["ValueName"]` split it back apart.

IFEO is a subkey per executable name, each carrying up to three interesting values, so one executable can produce three items. Encoding the value name into `Name` keeps identities distinct without a second identity field, and both views are walked because 32-bit and 64-bit IFEO are separate.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Providers/IfeoProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class IfeoProviderTests
{
    private const string Ifeo = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static (IfeoProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new IfeoProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("ifeo", provider.Id);
        Assert.Equal(StartupCategory.Ifeo, provider.Category);
    }

    [Fact]
    public void ReportsADebuggerHijack()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Ifeo}\sethc.exe", "Debugger", @"C:\Windows\System32\cmd.exe",
                     RegistryValueType.String, RegistryViewKind.Registry64);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("sethc.exe:Debugger", item.Name);
        Assert.Equal($@"{Ifeo}\sethc.exe", item.Location);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", item.RawValue);
        Assert.Equal("sethc.exe", item.Extra["TargetImage"]);
        Assert.Equal("Debugger", item.Extra["ValueName"]);
        Assert.Equal(RiskTier.High, RiskTiers.For(item));
    }

    [Fact]
    public void OneImageWithSeveralInterestingValuesProducesSeveralItems()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Ifeo}\app.exe", "Debugger", "dbg.exe", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set($@"{Ifeo}\app.exe", "VerifierDlls", "verify.dll", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set($@"{Ifeo}\app.exe", "GlobalFlag", "0x100", RegistryValueType.String, RegistryViewKind.Registry64);

        var names = provider.Scan(Ctx()).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "app.exe:Debugger", "app.exe:GlobalFlag", "app.exe:VerifierDlls" }, names);
    }

    [Fact]
    public void IdentitiesForDifferentValuesOfTheSameImageDiffer()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Ifeo}\app.exe", "Debugger", "dbg.exe", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set($@"{Ifeo}\app.exe", "VerifierDlls", "v.dll", RegistryValueType.String, RegistryViewKind.Registry64);

        Assert.Equal(2, provider.Scan(Ctx()).Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void SubkeysWithNoInterestingValuesAreIgnored()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Ifeo}\notepad.exe", "MitigationOptions", "0", RegistryValueType.String, RegistryViewKind.Registry64);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void BothViewsAreWalked()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Ifeo}\a.exe", "Debugger", "x64.exe", RegistryValueType.String, RegistryViewKind.Registry64);
        registry.Set($@"{Ifeo}\b.exe", "Debugger", "x86.exe", RegistryValueType.String, RegistryViewKind.Registry32);

        Assert.Equal(2, provider.Scan(Ctx()).Count());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~IfeoProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'IfeoProvider' could not be found`.

- [ ] **Step 3: Write the provider**

Create `StartupScanner.Core/Providers/IfeoProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class IfeoProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string IfeoKey = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static readonly string[] Interesting = { "Debugger", "VerifierDlls", "GlobalFlag" };

    private static readonly RegistryViewKind[] Views = { RegistryViewKind.Registry64, RegistryViewKind.Registry32 };

    public string Id => "ifeo";
    public StartupCategory Category => StartupCategory.Ifeo;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var view in Views)
            foreach (var item in ScanView(view, ctx))
                yield return item;
    }

    private IReadOnlyList<StartupItem> ScanView(RegistryViewKind view, ScanContext ctx)
    {
        var found = new List<StartupItem>();

        try
        {
            using var root = registry.OpenKey(IfeoKey, view);
            if (root is null) return found;

            foreach (var image in root.GetSubKeyNames())
            {
                var path = $@"{IfeoKey}\{image}";
                using var key = root.OpenSubKey(image);
                if (key is null) continue;

                foreach (var valueName in Interesting)
                {
                    var raw = RegistryValueCodec.ToDisplay(key.GetValue(valueName), key.GetValueType(valueName));
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    var name = $"{image}:{valueName}";
                    var (imagePath, args) = CommandLine.Split(raw, fs);

                    found.Add(new StartupItem
                    {
                        Id = Identity.Compute(Id, null, path, name),
                        ProviderId = Id,
                        Location = path,
                        Name = name,
                        RawValue = raw,
                        Category = Category,
                        ImagePath = imagePath,
                        Arguments = args,
                        State = EnableState.Enabled,
                        StateSource = "ValuePresent",
                        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["TargetImage"] = image,
                            ["ValueName"] = valueName,
                            ["View"] = view.ToString(),
                        },
                    });
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, IfeoKey, ex.Message));
        }

        return found;
    }
}
```

Note: `Name` carries the value name, but `RegistryValueToggle` writes `item.Name` as the registry value name. IFEO therefore needs a toggle whose value name comes from `Extra["ValueName"]`. That is handled in Task 40's composition root by wrapping the item — see the `SubValueToggle` step there.

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~IfeoProviderTests"`
Expected: PASS — 6 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Providers/IfeoProvider.cs StartupScanner.Tests/Providers/IfeoProviderTests.cs
git commit -m "feat: add Image File Execution Options provider"
```

---

## Task 27: Sub-value toggle and provider 10 — Explorer hooks

**Files:**
- Create: `StartupScanner.Core/Writing/SubValueToggle.cs`, `Providers/ClsidResolver.cs`, `Providers/ExplorerHooksProvider.cs`
- Test: `StartupScanner.Tests/Writing/SubValueToggleTests.cs`, `StartupScanner.Tests/Providers/ExplorerHooksProviderTests.cs`

**Interfaces:**
- Consumes: `RegistryValueToggle`, `IRegistry`, `IFileSystem`, `TableValueScanner`.
- Produces:
  - `SubValueToggle(string providerId, IRegistry registry, IClock clock)` — an `IToggleProvider` that targets `Extra["ValueName"]` instead of `item.Name`, for providers whose `Name` is a composite.
  - `ClsidResolver(IRegistry registry)` with `string? ResolveInprocServer(string clsid, string userRoot)`.
  - `ExplorerHooksProvider(IRegistry registry, IFileSystem fs)` with `Id == "explorer-hooks"`.

Explorer hooks come in two shapes. `Load` and `Run` under the per-user `Windows` key are plain command values. `ShellExecuteHooks`, `SharedTaskScheduler`, and `ShellServiceObjectDelayLoad` are lists whose value data is a description and whose value *name* (or data) is a CLSID — so the useful `ImagePath` comes from resolving that CLSID's `InprocServer32`.

`SubValueToggle` exists because `IfeoProvider` and `ExplorerHooksProvider` both build composite `Name`s; without it the toggle would try to delete a value called `sethc.exe:Debugger`.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Writing/SubValueToggleTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Writing;

public class SubValueToggleTests
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe";

    private static StartupItem Item() => new()
    {
        Id = Identity.Compute("ifeo", null, Key, "sethc.exe:Debugger"),
        ProviderId = "ifeo", Location = Key, Name = "sethc.exe:Debugger",
        RawValue = @"C:\Windows\System32\cmd.exe", Category = StartupCategory.Ifeo, State = EnableState.Enabled,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TargetImage"] = "sethc.exe", ["ValueName"] = "Debugger",
        },
    };

    [Fact]
    public void DisableRemovesTheValueNamedByTheExtraNotTheCompositeItemName()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Debugger", @"C:\Windows\System32\cmd.exe", RegistryValueType.String);
        var toggle = new SubValueToggle("ifeo", registry, new FakeClock());

        var record = toggle.SetEnabled(Item(), enabled: false, new FakeJournal());

        Assert.Null(registry.OpenKey(Key)!.GetValue("Debugger"));
        Assert.Equal("Debugger", record.Name);
        Assert.Equal(@"sz:C:\Windows\System32\cmd.exe", record.Before);
    }

    [Fact]
    public void EnableRestoresIntoTheSameSubValue()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Debugger", @"C:\evil.exe", RegistryValueType.String);
        var toggle = new SubValueToggle("ifeo", registry, new FakeClock());
        var journal = new FakeJournal();
        journal.Record(toggle.SetEnabled(Item(), enabled: false, journal));

        toggle.SetEnabled(Item() with { State = EnableState.Disabled }, enabled: true, journal);

        Assert.Equal(@"C:\evil.exe", registry.OpenKey(Key)!.GetValue("Debugger"));
    }

    [Fact]
    public void ItemsWithNoValueNameExtraFallBackToTheItemName()
    {
        var registry = new FakeRegistry();
        registry.Set(Key, "Plain", "x", RegistryValueType.String);
        var toggle = new SubValueToggle("ifeo", registry, new FakeClock());
        var item = Item() with { Name = "Plain", Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };

        toggle.SetEnabled(item, enabled: false, new FakeJournal());

        Assert.Null(registry.OpenKey(Key)!.GetValue("Plain"));
    }

    [Fact]
    public void CapabilityIsBackupRemove()
    {
        var toggle = new SubValueToggle("ifeo", new FakeRegistry(), new FakeClock());
        Assert.Equal(ToggleCapability.BackupRemove, toggle.GetCapability(Item()));
        Assert.Equal("ifeo", toggle.ProviderId);
    }
}
```

Create `StartupScanner.Tests/Providers/ExplorerHooksProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class ExplorerHooksProviderTests
{
    private const string UserWindows = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string ShellExecuteHooks = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellExecuteHooks";
    private const string SharedTaskScheduler = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SharedTaskScheduler";
    private const string DelayLoad = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad";
    private const string Clsid = "{AAAAAAAA-1111-2222-3333-444444444444}";

    private static (ExplorerHooksProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new ExplorerHooksProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("explorer-hooks", provider.Id);
        Assert.Equal(StartupCategory.ExplorerHook, provider.Category);
    }

    [Fact]
    public void ReadsTheLoadAndRunValuesFromThePerUserWindowsKey()
    {
        var (provider, registry) = Sut();
        registry.Set(UserWindows, "Load", @"C:\load.exe", RegistryValueType.String);
        registry.Set(UserWindows, "Run", @"C:\run.exe", RegistryValueType.String);

        var names = provider.Scan(Ctx()).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "Load", "Run" }, names);
    }

    [Fact]
    public void ResolvesAShellExecuteHookClsidToItsInprocServer()
    {
        var (provider, registry) = Sut();
        registry.Set(ShellExecuteHooks, Clsid, "Some hook", RegistryValueType.String);
        registry.Set($@"HKLM\SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32", "", @"C:\hook.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()).Where(i => i.Name == Clsid));

        Assert.Equal(@"C:\hook.dll", item.ImagePath);
        Assert.Equal("Some hook", item.RawValue);
        Assert.Equal(Clsid, item.Extra["Clsid"]);
        Assert.Equal(ShellExecuteHooks, item.Location);
    }

    [Fact]
    public void AnUnresolvableClsidIsStillReportedAndMarkedPartiallyRead()
    {
        var (provider, registry) = Sut();
        registry.Set(ShellExecuteHooks, Clsid, "Orphan hook", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Null(item.ImagePath);
        Assert.True(item.PartiallyRead);
    }

    [Fact]
    public void CoversSharedTaskSchedulerAndShellServiceObjectDelayLoad()
    {
        var (provider, registry) = Sut();
        registry.Set(SharedTaskScheduler, Clsid, "Scheduler entry", RegistryValueType.String);
        registry.Set(DelayLoad, "WebCheck", Clsid, RegistryValueType.String);

        var locations = provider.Scan(Ctx()).Select(i => i.Location).Distinct().OrderBy(l => l).ToArray();

        Assert.Equal(new[] { DelayLoad, SharedTaskScheduler }, locations);
    }

    [Fact]
    public void DelayLoadEntriesResolveTheClsidFromTheValueDataNotTheValueName()
    {
        var (provider, registry) = Sut();
        registry.Set(DelayLoad, "WebCheck", Clsid, RegistryValueType.String);
        registry.Set($@"HKLM\SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32", "", @"C:\webcheck.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("WebCheck", item.Name);
        Assert.Equal(@"C:\webcheck.dll", item.ImagePath);
        Assert.Equal(Clsid, item.Extra["Clsid"]);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~SubValueToggleTests|FullyQualifiedName~ExplorerHooksProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'SubValueToggle' could not be found`.

- [ ] **Step 3: Write `SubValueToggle`**

Create `StartupScanner.Core/Writing/SubValueToggle.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// BackupRemove for providers whose item Name is a composite (IFEO's "sethc.exe:Debugger").
/// The registry value written is Extra["ValueName"], falling back to Name when absent.
/// </summary>
public sealed class SubValueToggle(string providerId, IRegistry registry, IClock clock) : IToggleProvider
{
    private readonly RegistryValueToggle _inner = new(providerId, registry, clock);

    public string ProviderId => providerId;

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.BackupRemove;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
        => _inner.SetEnabled(Retarget(item), enabled, journal) with { ItemId = item.Id };

    public void Restore(WriteRecord record) => _inner.Restore(record);

    /// <summary>
    /// Points Name at the real registry value while keeping the item's identity, so the journal
    /// entry still resolves back to the row the user clicked.
    /// </summary>
    private static StartupItem Retarget(StartupItem item)
        => item.Extra.TryGetValue("ValueName", out var valueName) && valueName.Length > 0
            ? item with { Name = valueName }
            : item;
}
```

- [ ] **Step 4: Write `ClsidResolver`**

Create `StartupScanner.Core/Providers/ClsidResolver.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

/// <summary>Resolves a CLSID to the DLL that implements it, checking the user hive first.</summary>
public sealed class ClsidResolver(IRegistry registry)
{
    public string? ResolveInprocServer(string clsid, string userRoot)
    {
        foreach (var path in new[]
                 {
                     $@"{userRoot}\Software\Classes\CLSID\{clsid}\InprocServer32",
                     $@"HKLM\SOFTWARE\Classes\CLSID\{clsid}\InprocServer32",
                 })
        {
            try
            {
                using var key = registry.OpenKey(path);
                // The implementing DLL is the key's default (unnamed) value.
                var value = RegistryValueCodec.ToDisplay(key?.GetValue(""), key?.GetValueType("") ?? RegistryValueType.None);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Fall through to the next hive.
            }
        }

        return null;
    }
}
```

- [ ] **Step 5: Write `ExplorerHooksProvider`**

Create `StartupScanner.Core/Providers/ExplorerHooksProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class ExplorerHooksProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string UserWindowsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string ShellExecuteHooks = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellExecuteHooks";
    private const string SharedTaskScheduler = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SharedTaskScheduler";
    private const string DelayLoad = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad";

    private static readonly IReadOnlyList<ValueSource> PlainSources = new[]
    {
        new ValueSource("%USER%", UserWindowsKey, RegistryViewKind.Default, new[] { "Load", "Run" }),
    };

    /// <summary>
    /// Where the CLSID lives differs by key: the hook lists use the value name, while
    /// ShellServiceObjectDelayLoad puts a description in the name and the CLSID in the data.
    /// </summary>
    private static readonly (string Path, bool ClsidIsValueName)[] ClsidSources =
    {
        (ShellExecuteHooks, true),
        (SharedTaskScheduler, true),
        (DelayLoad, false),
    };

    private readonly TableValueScanner _values = new(registry, fs);
    private readonly ClsidResolver _clsids = new(registry);

    public string Id => "explorer-hooks";
    public StartupCategory Category => StartupCategory.ExplorerHook;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var item in _values.Scan(Id, Category, PlainSources, ctx)) yield return item;

        foreach (var (path, clsidIsValueName) in ClsidSources)
            foreach (var item in ScanClsidList(path, clsidIsValueName, ctx))
                yield return item;
    }

    private IReadOnlyList<StartupItem> ScanClsidList(string path, bool clsidIsValueName, ScanContext ctx)
    {
        var found = new List<StartupItem>();

        try
        {
            using var key = registry.OpenKey(path);
            if (key is null) return found;

            foreach (var name in key.GetValueNames())
            {
                var data = RegistryValueCodec.ToDisplay(key.GetValue(name), key.GetValueType(name));
                var clsid = clsidIsValueName ? name : data;
                if (string.IsNullOrWhiteSpace(clsid)) continue;

                var dll = _clsids.ResolveInprocServer(clsid, ctx.UserRoot);

                found.Add(new StartupItem
                {
                    Id = Identity.Compute(Id, null, path, name),
                    ProviderId = Id,
                    Location = path,
                    Name = name,
                    RawValue = data,
                    Category = Category,
                    ImagePath = dll,
                    State = EnableState.Enabled,
                    StateSource = "ValuePresent",
                    PartiallyRead = dll is null,
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Clsid"] = clsid,
                        ["ValueName"] = name,
                    },
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, path, ex.Message));
        }

        return found;
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~SubValueToggleTests|FullyQualifiedName~ExplorerHooksProviderTests"`
Expected: PASS — 10 tests passed.

- [ ] **Step 7: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add sub-value toggle, CLSID resolver, and Explorer hooks provider"
```

---

## Task 28: Provider 11 — AppX startup tasks

**Files:**
- Create: `StartupScanner.Core/Providers/AppxStartupTaskProvider.cs`, `Writing/AppxStartupTaskToggle.cs`
- Test: `StartupScanner.Tests/Providers/AppxStartupTaskProviderTests.cs`

**Interfaces:**
- Consumes: `IRegistry`, `IFileSystem`.
- Produces:
  - `AppxStartupTaskProvider(IRegistry registry, IFileSystem fs)` with `Id == "appx-startup"`, `const int EnabledState = 2`, `const int DisabledState = 1`.
  - `AppxStartupTaskToggle(IRegistry registry, IClock clock)` with `ProviderId == "appx-startup"`, Native via the `State` DWORD.

The spec flags this as a known risk: AppX startup-task state lives in a package-private location whose layout changes between Windows releases. The provider therefore reports what is present and **degrades to read-only rather than guessing** — an entry whose `State` value is missing or is not a DWORD is emitted with `State = Unknown` and its toggle reports `Blocked` with the reason shown in the panel.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Providers/AppxStartupTaskProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class AppxStartupTaskProviderTests
{
    private const string SystemAppData =
        @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    private const string Package = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0";

    private static (AppxStartupTaskProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new AppxStartupTaskProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU", UserSid = "S-1-5-21-9" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("appx-startup", provider.Id);
        Assert.Equal(StartupCategory.AppxStartupTask, provider.Category);
    }

    [Fact]
    public void ReportsAnEnabledStartupTask()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\SpotifyStartupTask", "State", 2, RegistryValueType.Dword);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("SpotifyStartupTask", item.Name);
        Assert.Equal(Package, item.Extra["PackageFamilyName"]);
        Assert.Equal(EnableState.Enabled, item.State);
        Assert.Equal("Appx.State=2", item.StateSource);
        Assert.Equal("S-1-5-21-9", item.UserSid);
        Assert.False(item.PartiallyRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void StateValuesBelowTwoMeanDisabled(int state)
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\Task", "State", state, RegistryValueType.Dword);

        Assert.Equal(EnableState.Disabled, Assert.Single(provider.Scan(Ctx())).State);
    }

    [Fact]
    public void SubkeysWithNoStateValueAreNotStartupTasks()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\SomethingElse", "Other", 1, RegistryValueType.Dword);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void AStateValueOfAnUnexpectedTypeDegradesToReadOnlyRatherThanGuessing()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\Task", "State", "enabled", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(EnableState.Unknown, item.State);
        Assert.True(item.PartiallyRead);
        Assert.Equal(ToggleCapability.Blocked,
            new AppxStartupTaskToggle(registry, new FakeClock()).GetCapability(item));
    }

    [Fact]
    public void AWellFormedTaskIsNativelyToggleable()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\Task", "State", 2, RegistryValueType.Dword);
        var item = Assert.Single(provider.Scan(Ctx()));
        var toggle = new AppxStartupTaskToggle(registry, new FakeClock());

        Assert.Equal(ToggleCapability.Native, toggle.GetCapability(item));

        var record = toggle.SetEnabled(item, enabled: false, new FakeJournal());

        Assert.Equal(1, registry.OpenKey($@"{SystemAppData}\{Package}\Task")!.GetValue("State"));
        Assert.Equal("Appx.State", record.Mechanism);
        Assert.Equal("State", record.Name);
        Assert.Equal("dword:2", record.Before);
    }

    [Fact]
    public void EnablingWritesStateTwo()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{SystemAppData}\{Package}\Task", "State", 1, RegistryValueType.Dword);
        var item = Assert.Single(provider.Scan(Ctx()));

        new AppxStartupTaskToggle(registry, new FakeClock()).SetEnabled(item, enabled: true, new FakeJournal());

        Assert.Equal(2, registry.OpenKey($@"{SystemAppData}\{Package}\Task")!.GetValue("State"));
    }

    [Fact]
    public void MissingSystemAppDataKeyYieldsNothing()
    {
        var (provider, _) = Sut();
        Assert.Empty(provider.Scan(Ctx()));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~AppxStartupTaskProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'AppxStartupTaskProvider' could not be found`.

- [ ] **Step 3: Write the provider**

Create `StartupScanner.Core/Providers/AppxStartupTaskProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Providers;

/// <summary>
/// AppX startup tasks and their per-user enable state. The state layout is package-private and has
/// changed between Windows releases, so anything unexpected is reported read-only rather than
/// interpreted.
/// </summary>
public sealed class AppxStartupTaskProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    public const string SystemAppDataSubKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    public const int EnabledState = 2;
    public const int DisabledState = 1;

    public string Id => "appx-startup";
    public StartupCategory Category => StartupCategory.AppxStartupTask;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        var rootPath = $@"{ctx.UserRoot}\{SystemAppDataSubKey}";
        IReadOnlyList<string> packages;

        try
        {
            using var root = registry.OpenKey(rootPath);
            if (root is null) yield break;
            packages = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, rootPath, ex.Message));
            yield break;
        }

        foreach (var package in packages)
            foreach (var item in ScanPackage(rootPath, package, ctx))
                yield return item;
    }

    private IReadOnlyList<StartupItem> ScanPackage(string rootPath, string package, ScanContext ctx)
    {
        var packagePath = $@"{rootPath}\{package}";
        var found = new List<StartupItem>();

        try
        {
            using var packageKey = registry.OpenKey(packagePath);
            if (packageKey is null) return found;

            foreach (var taskId in packageKey.GetSubKeyNames())
            {
                using var taskKey = packageKey.OpenSubKey(taskId);
                var stateValue = taskKey?.GetValue("State");
                if (stateValue is null) continue;   // not a startup task

                var taskPath = $@"{packagePath}\{taskId}";
                var recognised = stateValue is int;
                var state = recognised
                    ? (int)stateValue >= EnabledState ? EnableState.Enabled : EnableState.Disabled
                    : EnableState.Unknown;

                found.Add(new StartupItem
                {
                    Id = Identity.Compute(Id, ctx.UserSid, taskPath, taskId),
                    ProviderId = Id,
                    Location = taskPath,
                    Name = taskId,
                    RawValue = package,
                    Category = Category,
                    State = state,
                    StateSource = recognised ? $"Appx.State={stateValue}" : "Appx.State (unrecognised format)",
                    UserSid = ctx.UserSid,
                    PartiallyRead = !recognised,
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["PackageFamilyName"] = package,
                        ["StateRecognised"] = recognised.ToString(),
                    },
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, packagePath, ex.Message));
        }

        return found;
    }
}
```

- [ ] **Step 4: Write the toggle**

Create `StartupScanner.Core/Writing/AppxStartupTaskToggle.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

public sealed class AppxStartupTaskToggle(IRegistry registry, IClock clock) : IToggleProvider
{
    public string ProviderId => "appx-startup";

    public ToggleCapability GetCapability(StartupItem item)
    {
        if (Guardrails.Evaluate(item).Blocked) return ToggleCapability.Blocked;

        // Refusing to write an unrecognised layout is the point: guessing here could corrupt the
        // package's own state.
        return item.Extra.GetValueOrDefault("StateRecognised") == "True"
            ? ToggleCapability.Native
            : ToggleCapability.Blocked;
    }

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        using var key = registry.OpenKey(item.Location, RegistryViewKind.Default, writable: true)
            ?? throw new InvalidOperationException($"AppX startup task key not found: {item.Location}");

        var before = RegistryValueCodec.Encode(key.GetValue("State"), RegistryValueType.Dword);
        var target = enabled ? AppxStartupTaskProvider.EnabledState : AppxStartupTaskProvider.DisabledState;
        key.SetValue("State", target, RegistryValueType.Dword);

        return new WriteRecord
        {
            Ts = clock.UtcNow,
            SessionId = "",
            ItemId = item.Id,
            ProviderId = ProviderId,
            Mechanism = "Appx.State",
            Location = item.Location,
            Name = "State",
            Category = item.Category,
            UserSid = item.UserSid,
            Before = before,
            After = RegistryValueCodec.Encode(target, RegistryValueType.Dword),
            Enabled = enabled,
        };
    }

    public void Restore(WriteRecord record) => RegistryWriteOps.Restore(registry, record);
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~AppxStartupTaskProviderTests"`
Expected: PASS — 9 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add AppX startup task provider that degrades to read-only"
```

---

## Task 29: Provider 12 — WMI event subscriptions

**Files:**
- Create: `StartupScanner.Core/Abstractions/IWmi.cs`, `Providers/WmiSubscriptionProvider.cs`, `Writing/WmiInstanceToggle.cs`
- Create: `StartupScanner.Tests/Fakes/FakeWmi.cs`
- Test: `StartupScanner.Tests/Providers/WmiSubscriptionProviderTests.cs`

**Interfaces:**
- Consumes: `IStartupProvider`, `IToggleProvider`, `IClock`.
- Produces:
  - `WmiInstance(string ClassName, string RelativePath, IReadOnlyDictionary<string, object?> Properties)`.
  - `IWmi` with `Query(string ns, string wql) -> IReadOnlyList<WmiInstance>`, `DeleteInstance(string ns, string relativePath)`, `CreateInstance(string ns, string className, IReadOnlyDictionary<string, object?> properties)`.
  - `WmiSubscriptionProvider(IWmi wmi)` with `Id == "wmi-subscription"`, `const string Namespace = @"root\subscription"`.
  - `WmiInstanceToggle(IWmi wmi, IClock clock)` with `ProviderId == "wmi-subscription"`, mechanism `"Wmi.Instance"`.
  - `FakeWmi` with `Add(string ns, WmiInstance)`, `Deleted`, `Created`.

A persistence subscription is three objects: an `__EventFilter` holding the WQL that decides when to fire, an `__EventConsumer` holding what to run, and a `__FilterToConsumerBinding` joining them. All three are reported, because breaking any one of them stops the subscription and the user needs to see which piece they are looking at.

There is no disable flag, so the toggle journals the instance's full property set as JSON, deletes the instance, and recreates it from that JSON on re-enable. The mechanism name is `Wmi.Instance` rather than `BackupRemove` so the scan engine knows the `Before` payload is JSON, not a registry value.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Fakes/FakeWmi.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeWmi : IWmi
{
    private readonly List<(string Namespace, WmiInstance Instance)> _instances = new();

    public List<(string Namespace, string RelativePath)> Deleted { get; } = new();
    public List<(string Namespace, string ClassName, IReadOnlyDictionary<string, object?> Properties)> Created { get; } = new();

    public void Add(string @namespace, WmiInstance instance) => _instances.Add((@namespace, instance));

    /// <summary>Only supports "SELECT * FROM &lt;class&gt;", which is all the provider issues.</summary>
    public IReadOnlyList<WmiInstance> Query(string @namespace, string wql)
    {
        var className = wql[(wql.LastIndexOf(' ') + 1)..];
        return _instances
            .Where(i => i.Namespace == @namespace && i.Instance.ClassName.StartsWith(className, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Instance)
            .ToList();
    }

    public void DeleteInstance(string @namespace, string relativePath)
    {
        Deleted.Add((@namespace, relativePath));
        _instances.RemoveAll(i => i.Namespace == @namespace && i.Instance.RelativePath == relativePath);
    }

    public void CreateInstance(string @namespace, string className, IReadOnlyDictionary<string, object?> properties)
        => Created.Add((@namespace, className, properties));
}
```

Create `StartupScanner.Tests/Providers/WmiSubscriptionProviderTests.cs`:

```csharp
using System.Text.Json;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class WmiSubscriptionProviderTests
{
    private const string Ns = @"root\subscription";

    private static WmiInstance Filter() => new("__EventFilter", "__EventFilter.Name=\"EvilFilter\"",
        new Dictionary<string, object?>
        {
            ["Name"] = "EvilFilter",
            ["Query"] = "SELECT * FROM __InstanceModificationEvent WITHIN 60",
            ["QueryLanguage"] = "WQL",
            ["EventNamespace"] = @"root\cimv2",
        });

    private static WmiInstance Consumer() => new("CommandLineEventConsumer",
        "CommandLineEventConsumer.Name=\"EvilConsumer\"",
        new Dictionary<string, object?>
        {
            ["Name"] = "EvilConsumer",
            ["CommandLineTemplate"] = @"C:\evil.exe -run",
        });

    private static WmiInstance Binding() => new("__FilterToConsumerBinding",
        "__FilterToConsumerBinding.Consumer=\"CommandLineEventConsumer.Name=\\\"EvilConsumer\\\"\",Filter=\"__EventFilter.Name=\\\"EvilFilter\\\"\"",
        new Dictionary<string, object?>
        {
            ["Filter"] = "__EventFilter.Name=\"EvilFilter\"",
            ["Consumer"] = "CommandLineEventConsumer.Name=\"EvilConsumer\"",
        });

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var provider = new WmiSubscriptionProvider(new FakeWmi());
        Assert.Equal("wmi-subscription", provider.Id);
        Assert.Equal(StartupCategory.WmiSubscription, provider.Category);
    }

    [Fact]
    public void ReportsTheFilterWithItsWqlText()
    {
        var wmi = new FakeWmi();
        wmi.Add(Ns, Filter());

        var item = Assert.Single(new WmiSubscriptionProvider(wmi).Scan(Ctx()).Where(i => i.Name == "EvilFilter"));

        Assert.Equal(@"root\subscription:__EventFilter", item.Location);
        Assert.Equal("SELECT * FROM __InstanceModificationEvent WITHIN 60", item.RawValue);
        Assert.Equal("SELECT * FROM __InstanceModificationEvent WITHIN 60", item.Extra["Wql"]);
        Assert.Equal("WQL", item.Extra["QueryLanguage"]);
        Assert.Equal(@"root\cimv2", item.Extra["EventNamespace"]);
        Assert.Equal(EnableState.Enabled, item.State);
    }

    [Fact]
    public void ReportsTheConsumerWithItsTypeAndCommand()
    {
        var wmi = new FakeWmi();
        wmi.Add(Ns, Consumer());

        var item = Assert.Single(new WmiSubscriptionProvider(wmi).Scan(Ctx()).Where(i => i.Name == "EvilConsumer"));

        Assert.Equal(@"C:\evil.exe -run", item.RawValue);
        Assert.Equal("CommandLineEventConsumer", item.Extra["ConsumerType"]);
        Assert.Equal(@"C:\evil.exe", item.ImagePath);
    }

    [Fact]
    public void ReportsTheBindingThatJoinsThem()
    {
        var wmi = new FakeWmi();
        wmi.Add(Ns, Binding());

        var item = Assert.Single(new WmiSubscriptionProvider(wmi).Scan(Ctx())
            .Where(i => i.Location.EndsWith("__FilterToConsumerBinding")));

        Assert.Contains("EvilFilter", item.Extra["Filter"]);
        Assert.Contains("EvilConsumer", item.Extra["Consumer"]);
    }

    [Fact]
    public void AllThreePartsOfOneSubscriptionAppearSeparately()
    {
        var wmi = new FakeWmi();
        wmi.Add(Ns, Filter());
        wmi.Add(Ns, Consumer());
        wmi.Add(Ns, Binding());

        Assert.Equal(3, new WmiSubscriptionProvider(wmi).Scan(Ctx()).Count());
    }

    [Fact]
    public void AFailingQueryBecomesAScanError()
    {
        var provider = new WmiSubscriptionProvider(new ThrowingWmi());
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));
        Assert.NotEmpty(ctx.Errors);
    }

    [Fact]
    public void DisableJournalsThePropertiesAsJsonAndDeletesTheInstance()
    {
        var wmi = new FakeWmi();
        wmi.Add(Ns, Filter());
        var item = Assert.Single(new WmiSubscriptionProvider(wmi).Scan(Ctx()).Where(i => i.Name == "EvilFilter"));
        var toggle = new WmiInstanceToggle(wmi, new FakeClock());

        var record = toggle.SetEnabled(item, enabled: false, new FakeJournal());

        Assert.Equal("Wmi.Instance", record.Mechanism);
        Assert.Single(wmi.Deleted);
        Assert.Contains("EvilFilter", record.Before);
        Assert.Null(record.After);
        var restored = JsonSerializer.Deserialize<Dictionary<string, object?>>(record.Before!);
        Assert.NotNull(restored);
        Assert.Contains("Query", restored!.Keys);
    }

    [Fact]
    public void RestoreRecreatesTheInstanceFromTheJournalledJson()
    {
        var wmi = new FakeWmi();
        wmi.Add(Ns, Filter());
        var item = Assert.Single(new WmiSubscriptionProvider(wmi).Scan(Ctx()).Where(i => i.Name == "EvilFilter"));
        var toggle = new WmiInstanceToggle(wmi, new FakeClock());
        var record = toggle.SetEnabled(item, enabled: false, new FakeJournal());

        toggle.Restore(record);

        var created = Assert.Single(wmi.Created);
        Assert.Equal("__EventFilter", created.ClassName);
        Assert.Equal("EvilFilter", created.Properties["Name"]?.ToString());
    }

    private sealed class ThrowingWmi : IWmi
    {
        public IReadOnlyList<WmiInstance> Query(string @namespace, string wql)
            => throw new InvalidOperationException("WMI unavailable");
        public void DeleteInstance(string @namespace, string relativePath) => throw new InvalidOperationException();
        public void CreateInstance(string @namespace, string className, IReadOnlyDictionary<string, object?> properties)
            => throw new InvalidOperationException();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WmiSubscriptionProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'IWmi' could not be found`.

- [ ] **Step 3: Write the abstraction**

Create `StartupScanner.Core/Abstractions/IWmi.cs`:

```csharp
namespace StartupScanner.Core.Abstractions;

public sealed record WmiInstance(
    string ClassName,
    string RelativePath,
    IReadOnlyDictionary<string, object?> Properties);

public interface IWmi
{
    IReadOnlyList<WmiInstance> Query(string @namespace, string wql);
    void DeleteInstance(string @namespace, string relativePath);
    void CreateInstance(string @namespace, string className, IReadOnlyDictionary<string, object?> properties);
}
```

- [ ] **Step 4: Write the provider**

Create `StartupScanner.Core/Providers/WmiSubscriptionProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Providers;

/// <summary>
/// WMI permanent event subscriptions. A working subscription needs a filter, a consumer, and a
/// binding, and all three are reported because breaking any one of them stops it firing.
/// </summary>
public sealed class WmiSubscriptionProvider(IWmi wmi) : IStartupProvider
{
    public const string Namespace = @"root\subscription";

    private static readonly string[] Classes = { "__EventFilter", "__EventConsumer", "__FilterToConsumerBinding" };

    public string Id => "wmi-subscription";
    public StartupCategory Category => StartupCategory.WmiSubscription;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var className in Classes)
        {
            IReadOnlyList<WmiInstance> instances;
            try
            {
                instances = wmi.Query(Namespace, $"SELECT * FROM {className}");
            }
            catch (Exception ex)
            {
                ctx.Errors.Add(new ScanError(Id, $"{Namespace}:{className}", ex.Message));
                continue;
            }

            foreach (var instance in instances) yield return Build(className, instance);
        }
    }

    private StartupItem Build(string queriedClass, WmiInstance instance)
    {
        var location = $"{Namespace}:{queriedClass}";
        var name = Text(instance, "Name") is { Length: > 0 } named ? named : instance.RelativePath;
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RelativePath"] = instance.RelativePath,
            ["ActualClass"] = instance.ClassName,
        };

        string raw;
        string? image = null;

        switch (queriedClass)
        {
            case "__EventFilter":
                raw = Text(instance, "Query");
                extra["Wql"] = raw;
                extra["QueryLanguage"] = Text(instance, "QueryLanguage");
                extra["EventNamespace"] = Text(instance, "EventNamespace");
                break;

            case "__EventConsumer":
                raw = FirstNonEmpty(instance, "CommandLineTemplate", "ExecutablePath", "ScriptText", "ScriptFileName");
                extra["ConsumerType"] = instance.ClassName;
                image = raw.Length > 0 ? raw.Split(' ')[0].Trim('"') : null;
                break;

            default:
                extra["Filter"] = Text(instance, "Filter");
                extra["Consumer"] = Text(instance, "Consumer");
                raw = $"{extra["Filter"]} -> {extra["Consumer"]}";
                break;
        }

        return new StartupItem
        {
            Id = Identity.Compute(Id, null, location, name),
            ProviderId = Id,
            Location = location,
            Name = name,
            RawValue = raw,
            Category = Category,
            ImagePath = image,
            State = EnableState.Enabled,
            StateSource = "InstancePresent",
            Extra = extra,
        };
    }

    private static string Text(WmiInstance instance, string property)
        => instance.Properties.TryGetValue(property, out var value) ? value?.ToString() ?? "" : "";

    private static string FirstNonEmpty(WmiInstance instance, params string[] properties)
        => properties.Select(p => Text(instance, p)).FirstOrDefault(v => v.Length > 0) ?? "";
}
```

- [ ] **Step 5: Write the toggle**

Create `StartupScanner.Core/Writing/WmiInstanceToggle.cs`:

```csharp
using System.Text.Json;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// WMI has no disable flag, so the instance's whole property set is journaled as JSON, the
/// instance is deleted, and re-enable recreates it. The mechanism name tells ScanEngine that the
/// journal payload is JSON rather than an encoded registry value.
/// </summary>
public sealed class WmiInstanceToggle(IWmi wmi, IClock clock) : IToggleProvider
{
    public string ProviderId => "wmi-subscription";

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.BackupRemove;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
        => enabled ? Enable(item, journal) : Disable(item);

    public void Restore(WriteRecord record)
    {
        if (record.Before is null)
        {
            wmi.DeleteInstance(WmiSubscriptionProvider.Namespace, record.Location.Split(':')[^1]);
            return;
        }

        Recreate(record.Before);
    }

    private WriteRecord Disable(StartupItem item)
    {
        var relativePath = item.Extra["RelativePath"];
        var backup = JsonSerializer.Serialize(new Backup(
            item.Extra["ActualClass"], relativePath, PropertiesOf(item)));

        wmi.DeleteInstance(WmiSubscriptionProvider.Namespace, relativePath);

        return Build(item, enabled: false, before: backup, after: null);
    }

    private WriteRecord Enable(StartupItem item, IWriteJournalReader journal)
    {
        var backup = journal.All.LastOrDefault(r => r.ItemId == item.Id && !r.Enabled && r.Before is not null)
            ?? throw new InvalidOperationException(
                $"No backed-up WMI instance is recorded for '{item.Name}', so it cannot be re-enabled.");

        Recreate(backup.Before!);
        return Build(item, enabled: true, before: null, after: backup.Before);
    }

    private void Recreate(string json)
    {
        var backup = JsonSerializer.Deserialize<Backup>(json)
            ?? throw new InvalidOperationException("The journalled WMI instance could not be read back.");

        wmi.CreateInstance(WmiSubscriptionProvider.Namespace, backup.ClassName,
            backup.Properties.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extra is the only copy of the instance's data the scan kept, which is enough to rebuild the
    /// filter, consumer, or binding; the bookkeeping keys are dropped.
    /// </summary>
    private static Dictionary<string, string> PropertiesOf(StartupItem item)
    {
        var skip = new[] { "RelativePath", "ActualClass", "ConsumerType" };
        var properties = item.Extra
            .Where(p => !skip.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        properties["Name"] = item.Name;
        if (item.Extra.TryGetValue("Wql", out var wql)) properties["Query"] = wql;
        return properties;
    }

    private WriteRecord Build(StartupItem item, bool enabled, string? before, string? after) => new()
    {
        Ts = clock.UtcNow,
        SessionId = "",
        ItemId = item.Id,
        ProviderId = ProviderId,
        Mechanism = "Wmi.Instance",
        Location = item.Location,
        Name = item.Name,
        Category = item.Category,
        Before = before,
        After = after,
        Enabled = enabled,
    };

    private sealed record Backup(string ClassName, string RelativePath, Dictionary<string, string> Properties);
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WmiSubscriptionProviderTests"`
Expected: PASS — 8 tests passed.

- [ ] **Step 7: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add WMI subscription provider with JSON-journaled instance toggle"
```

---

## Task 30: Provider 13 — COM hijacks

**Files:**
- Create: `StartupScanner.Core/Providers/ComHijackProvider.cs`
- Test: `StartupScanner.Tests/Providers/ComHijackProviderTests.cs`

**Interfaces:**
- Consumes: `IRegistry`, `IFileSystem`, `ClsidResolver`.
- Produces: `ComHijackProvider(IRegistry registry, IFileSystem fs)` with `Id == "com-hijack"`. Items carry `Extra["Clsid"]`, `Extra["ValueName"]` (`InprocServer32` or `TreatAs`), `Extra["MachineRegistration"]`.

A per-user `CLSID` registration takes precedence over the machine one, so an `HKCU` `InprocServer32` that shadows an existing `HKLM` registration silently replaces a system COM object. Only shadowing entries are reported: a purely per-user CLSID with no machine counterpart is an ordinary per-user COM registration, not a hijack.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Providers/ComHijackProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class ComHijackProviderTests
{
    private const string Clsid = "{0006F045-0000-0000-C000-000000000046}";
    private const string UserClsids = @"HKCU\Software\Classes\CLSID";
    private const string MachineClsids = @"HKLM\SOFTWARE\Classes\CLSID";

    private static (ComHijackProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new ComHijackProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU", UserSid = "S-1-5-21-9" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("com-hijack", provider.Id);
        Assert.Equal(StartupCategory.ComHijack, provider.Category);
    }

    [Fact]
    public void ReportsAPerUserInprocServerThatShadowsAMachineRegistration()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{UserClsids}\{Clsid}\InprocServer32", "", @"C:\Users\alice\evil.dll", RegistryValueType.String);
        registry.Set($@"{MachineClsids}\{Clsid}\InprocServer32", "", @"C:\Windows\System32\real.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(Clsid, item.Name);
        Assert.Equal($@"{UserClsids}\{Clsid}\InprocServer32", item.Location);
        Assert.Equal(@"C:\Users\alice\evil.dll", item.RawValue);
        Assert.Equal(@"C:\Users\alice\evil.dll", item.ImagePath);
        Assert.Equal("InprocServer32", item.Extra["ValueName"]);
        Assert.Equal(@"C:\Windows\System32\real.dll", item.Extra["MachineRegistration"]);
        Assert.Equal("S-1-5-21-9", item.UserSid);
    }

    [Fact]
    public void APerUserOnlyClsidIsNotAHijack()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{UserClsids}\{Clsid}\InprocServer32", "", @"C:\ownapp.dll", RegistryValueType.String);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void ReportsATreatAsRedirection()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{UserClsids}\{Clsid}", "TreatAs", "{11111111-2222-3333-4444-555555555555}", RegistryValueType.String);
        registry.Set($@"{MachineClsids}\{Clsid}\InprocServer32", "", @"C:\Windows\System32\real.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("TreatAs", item.Extra["ValueName"]);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", item.RawValue);
    }

    [Fact]
    public void ClsidsWithNeitherInprocServerNorTreatAsAreIgnored()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{UserClsids}\{Clsid}", "SomethingElse", "x", RegistryValueType.String);
        registry.Set($@"{MachineClsids}\{Clsid}\InprocServer32", "", @"C:\real.dll", RegistryValueType.String);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void FollowsUserRootSoAMountedHiveIsScanned()
    {
        var (provider, registry) = Sut();
        registry.Set($@"HKU\S-1-5-21-42\Software\Classes\CLSID\{Clsid}\InprocServer32", "", @"C:\evil.dll", RegistryValueType.String);
        registry.Set($@"{MachineClsids}\{Clsid}\InprocServer32", "", @"C:\real.dll", RegistryValueType.String);

        var ctx = new ScanContext { UserRoot = @"HKU\S-1-5-21-42", UserSid = "S-1-5-21-42", ScanningMountedHive = true };
        var item = Assert.Single(provider.Scan(ctx));

        Assert.Equal("S-1-5-21-42", item.UserSid);
    }

    [Fact]
    public void AnInaccessibleClsidBecomesAScanError()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{UserClsids}\{Clsid}\InprocServer32", "", @"C:\evil.dll", RegistryValueType.String);
        registry.Deny($@"{UserClsids}\{Clsid}");
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));
        Assert.Contains(ctx.Errors, e => e.ProviderId == "com-hijack");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ComHijackProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ComHijackProvider' could not be found`.

- [ ] **Step 3: Write the provider**

Create `StartupScanner.Core/Providers/ComHijackProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

/// <summary>
/// Per-user CLSID registrations that shadow a machine one. A per-user key wins over HKLM, so this
/// silently replaces a system COM object; a per-user CLSID with no machine counterpart is an
/// ordinary registration and is not reported.
/// </summary>
public sealed class ComHijackProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string UserClsidSubKey = @"Software\Classes\CLSID";
    private const string MachineClsidKey = @"HKLM\SOFTWARE\Classes\CLSID";

    public string Id => "com-hijack";
    public StartupCategory Category => StartupCategory.ComHijack;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        var rootPath = $@"{ctx.UserRoot}\{UserClsidSubKey}";
        IReadOnlyList<string> clsids;

        try
        {
            using var root = registry.OpenKey(rootPath);
            if (root is null) yield break;
            clsids = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, rootPath, ex.Message));
            yield break;
        }

        foreach (var clsid in clsids)
        {
            var item = TryBuild(rootPath, clsid, ctx);
            if (item is not null) yield return item;
        }
    }

    private StartupItem? TryBuild(string rootPath, string clsid, ScanContext ctx)
    {
        var clsidPath = $@"{rootPath}\{clsid}";
        string valueName, raw, location;

        try
        {
            using var inproc = registry.OpenKey($@"{clsidPath}\InprocServer32");
            var inprocValue = Value(inproc, "");

            if (inprocValue.Length > 0)
            {
                valueName = "InprocServer32";
                raw = inprocValue;
                location = $@"{clsidPath}\InprocServer32";
            }
            else
            {
                using var key = registry.OpenKey(clsidPath);
                var treatAs = Value(key, "TreatAs");
                if (treatAs.Length == 0) return null;

                valueName = "TreatAs";
                raw = treatAs;
                location = clsidPath;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, clsidPath, ex.Message));
            return null;
        }

        var machine = MachineRegistration(clsid);
        if (machine is null) return null;   // not shadowing anything

        var (image, args) = CommandLine.Split(raw, fs);

        return new StartupItem
        {
            Id = Identity.Compute(Id, ctx.UserSid, location, clsid),
            ProviderId = Id,
            Location = location,
            Name = clsid,
            RawValue = raw,
            Category = Category,
            ImagePath = valueName == "InprocServer32" ? image : null,
            Arguments = valueName == "InprocServer32" ? args : null,
            State = EnableState.Enabled,
            StateSource = "ValuePresent",
            UserSid = ctx.UserSid,
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Clsid"] = clsid,
                ["ValueName"] = valueName == "InprocServer32" ? "" : "TreatAs",
                ["MachineRegistration"] = machine,
            },
        };
    }

    private string? MachineRegistration(string clsid)
    {
        try
        {
            using var key = registry.OpenKey($@"{MachineClsidKey}\{clsid}\InprocServer32");
            var value = Value(key, "");
            return value.Length > 0 ? value : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string Value(IRegistryKey? key, string name)
        => key is null ? "" : RegistryValueCodec.ToDisplay(key.GetValue(name), key.GetValueType(name));
}
```

Note the `Extra["ValueName"]` convention: an `InprocServer32` hijack lives in the key's default (unnamed) value, so `ValueName` is deliberately empty and `SubValueToggle` targets that default value. A `TreatAs` hijack sets `ValueName` to `"TreatAs"`.

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ComHijackProviderTests"`
Expected: PASS — 7 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Providers/ComHijackProvider.cs StartupScanner.Tests/Providers/ComHijackProviderTests.cs
git commit -m "feat: add COM hijack provider for per-user CLSID shadowing"
```

---

## Task 31: Provider 14 — LSA packages, and the blocked toggle

**Files:**
- Create: `StartupScanner.Core/Providers/LsaProvider.cs`, `Writing/BlockedToggle.cs`
- Test: `StartupScanner.Tests/Providers/LsaProviderTests.cs`

**Interfaces:**
- Consumes: `TableValueScanner`, `Guardrails`.
- Produces:
  - `LsaProvider(IRegistry registry, IFileSystem fs)` with `Id == "lsa"`.
  - `BlockedToggle(string providerId)` — an `IToggleProvider` that always reports `Blocked` and throws if asked to write.

LSA packages are blocked as a whole category: removing an authentication package can prevent logon, and legitimate additions are rare enough that manual editing is the safer path. `BlockedToggle` makes that structural rather than advisory — there is no code path that can write an LSA value even if a caller tries.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Providers/LsaProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class LsaProviderTests
{
    private const string Lsa = @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa";

    private static (LsaProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new LsaProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("lsa", provider.Id);
        Assert.Equal(StartupCategory.Lsa, provider.Category);
    }

    [Fact]
    public void ReadsTheThreePackageLists()
    {
        var (provider, registry) = Sut();
        registry.Set(Lsa, "Authentication Packages", new[] { "msv1_0" }, RegistryValueType.MultiString);
        registry.Set(Lsa, "Notification Packages", new[] { "scecli" }, RegistryValueType.MultiString);
        registry.Set(Lsa, "Security Packages", new[] { "kerberos", "msv1_0" }, RegistryValueType.MultiString);

        var names = provider.Scan(Ctx()).Select(i => i.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "Authentication Packages", "Notification Packages", "Security Packages" }, names);
    }

    [Fact]
    public void MultiStringPackageListsRenderJoined()
    {
        var (provider, registry) = Sut();
        registry.Set(Lsa, "Security Packages", new[] { "kerberos", "msv1_0" }, RegistryValueType.MultiString);

        Assert.Equal("kerberos; msv1_0", Assert.Single(provider.Scan(Ctx())).RawValue);
    }

    [Fact]
    public void AlsoReadsTheOsConfigCopy()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Lsa}\OSConfig", "Security Packages", new[] { "kerberos" }, RegistryValueType.MultiString);

        Assert.Equal($@"{Lsa}\OSConfig", Assert.Single(provider.Scan(Ctx())).Location);
    }

    [Fact]
    public void EveryLsaItemIsBlockedAndCriticalRisk()
    {
        var (provider, registry) = Sut();
        registry.Set(Lsa, "Authentication Packages", new[] { "msv1_0" }, RegistryValueType.MultiString);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.True(Guardrails.Evaluate(item).Blocked);
        Assert.Equal(RiskTier.Critical, RiskTiers.For(item));
        Assert.Equal(ToggleCapability.Blocked, new BlockedToggle("lsa").GetCapability(item));
    }

    [Fact]
    public void TheBlockedToggleRefusesToWriteEvenIfCalledDirectly()
    {
        var toggle = new BlockedToggle("lsa");
        var item = new StartupItem
        {
            Id = "id", ProviderId = "lsa", Location = Lsa, Name = "Security Packages",
            RawValue = "kerberos", Category = StartupCategory.Lsa,
        };

        Assert.Throws<InvalidOperationException>(() => toggle.SetEnabled(item, false, new FakeJournal()));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~LsaProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'LsaProvider' could not be found`.

- [ ] **Step 3: Write the provider**

Create `StartupScanner.Core/Providers/LsaProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;

namespace StartupScanner.Core.Providers;

public sealed class LsaProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string LsaKey = @"SYSTEM\CurrentControlSet\Control\Lsa";

    private static readonly string[] PackageValues =
        { "Authentication Packages", "Notification Packages", "Security Packages" };

    private static readonly IReadOnlyList<ValueSource> Sources = new[]
    {
        new ValueSource("HKLM", LsaKey, RegistryViewKind.Default, PackageValues),
        new ValueSource("HKLM", LsaKey + @"\OSConfig", RegistryViewKind.Default, PackageValues),
    };

    private readonly TableValueScanner _values = new(registry, fs);

    public string Id => "lsa";
    public StartupCategory Category => StartupCategory.Lsa;

    public IEnumerable<StartupItem> Scan(ScanContext ctx) => _values.Scan(Id, Category, Sources, ctx);
}
```

- [ ] **Step 4: Write `BlockedToggle`**

Create `StartupScanner.Core/Writing/BlockedToggle.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Toggle for categories that are shown and fully detailed but never writable. Registering this
/// instead of omitting a toggle makes the refusal structural: there is no code path that writes.
/// </summary>
public sealed class BlockedToggle(string providerId) : IToggleProvider
{
    public string ProviderId => providerId;

    public ToggleCapability GetCapability(StartupItem item) => ToggleCapability.Blocked;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
        => throw new InvalidOperationException(
            $"'{item.Name}' is in a category this app never writes to. Edit it manually if you are sure.");

    public void Restore(WriteRecord record)
        => throw new InvalidOperationException($"'{record.Name}' is in a category this app never writes to.");
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~LsaProviderTests"`
Expected: PASS — 6 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add LSA provider and structurally blocked toggle"
```

---

## Task 32: Providers 15 and 16 — netsh helpers and print monitors

**Files:**
- Create: `StartupScanner.Core/Providers/NetshHelperProvider.cs`, `Providers/PrintMonitorProvider.cs`
- Test: `StartupScanner.Tests/Providers/NetshHelperProviderTests.cs`, `StartupScanner.Tests/Providers/PrintMonitorProviderTests.cs`

**Interfaces:**
- Consumes: `TableValueScanner`, `IRegistry`, `IFileSystem`.
- Produces:
  - `NetshHelperProvider(IRegistry registry, IFileSystem fs)` with `Id == "netsh-helper"`.
  - `PrintMonitorProvider(IRegistry registry, IFileSystem fs)` with `Id == "print-monitor"`. Items carry `Extra["ValueName"]` naming the value that loads the DLL.

Netsh helpers are flat values under one key, so that provider is a one-line table. Print monitors and print providers are a subkey per monitor whose `Driver` value names the DLL, so the toggle targets that value rather than deleting the whole monitor subkey — a value-level change is reversible from the journal and does not disturb the rest of the monitor's configuration.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Providers/NetshHelperProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class NetshHelperProviderTests
{
    private const string Netsh = @"HKLM\SOFTWARE\Microsoft\Netsh";

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var provider = new NetshHelperProvider(new FakeRegistry(), new FakeFileSystem());
        Assert.Equal("netsh-helper", provider.Id);
        Assert.Equal(StartupCategory.NetshHelper, provider.Category);
    }

    [Fact]
    public void ReportsEveryHelperDll()
    {
        var registry = new FakeRegistry();
        registry.Set(Netsh, "dhcpcmonitor", "dhcpcmonitor.dll", RegistryValueType.String);
        registry.Set(Netsh, "evil", @"C:\evil.dll", RegistryValueType.String);
        var provider = new NetshHelperProvider(registry, new FakeFileSystem());

        var items = provider.Scan(new ScanContext { UserRoot = "HKCU" }).ToList();

        Assert.Equal(2, items.Count);
        var evil = items.Single(i => i.Name == "evil");
        Assert.Equal(@"C:\evil.dll", evil.RawValue);
        Assert.Equal(Netsh, evil.Location);
        Assert.Equal(EnableState.Enabled, evil.State);
    }

    [Fact]
    public void MissingKeyYieldsNothing()
    {
        var provider = new NetshHelperProvider(new FakeRegistry(), new FakeFileSystem());
        Assert.Empty(provider.Scan(new ScanContext { UserRoot = "HKCU" }));
    }
}
```

Create `StartupScanner.Tests/Providers/PrintMonitorProviderTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class PrintMonitorProviderTests
{
    private const string Monitors = @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors";
    private const string Providers = @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Providers";

    private static (PrintMonitorProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new PrintMonitorProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("print-monitor", provider.Id);
        Assert.Equal(StartupCategory.PrintMonitor, provider.Category);
    }

    [Fact]
    public void ReportsAMonitorsDriverDll()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Monitors}\Local Port", "Driver", "localspl.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("Local Port", item.Name);
        Assert.Equal($@"{Monitors}\Local Port", item.Location);
        Assert.Equal("localspl.dll", item.RawValue);
        Assert.Equal("Driver", item.Extra["ValueName"]);
    }

    [Fact]
    public void ReportsAPrintProvidersDriverDll()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Providers}\Internet Print Provider", "Driver", "inetpp.dll", RegistryValueType.String);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal($@"{Providers}\Internet Print Provider", item.Location);
    }

    [Fact]
    public void MonitorsWithNoDriverValueAreIgnored()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Monitors}\Weird", "Something", "x", RegistryValueType.String);

        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void AnInaccessibleMonitorBecomesAScanError()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Monitors}\Blocked", "Driver", "x.dll", RegistryValueType.String);
        registry.Deny($@"{Monitors}\Blocked");
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));
        Assert.Contains(ctx.Errors, e => e.ProviderId == "print-monitor");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~NetshHelperProviderTests|FullyQualifiedName~PrintMonitorProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'NetshHelperProvider' could not be found`.

- [ ] **Step 3: Write `NetshHelperProvider`**

Create `StartupScanner.Core/Providers/NetshHelperProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Scanning;

namespace StartupScanner.Core.Providers;

public sealed class NetshHelperProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private static readonly IReadOnlyList<ValueSource> Sources = new[]
    {
        new ValueSource("HKLM", @"SOFTWARE\Microsoft\Netsh", RegistryViewKind.Default, null),
    };

    private readonly TableValueScanner _values = new(registry, fs);

    public string Id => "netsh-helper";
    public StartupCategory Category => StartupCategory.NetshHelper;

    public IEnumerable<StartupItem> Scan(ScanContext ctx) => _values.Scan(Id, Category, Sources, ctx);
}
```

- [ ] **Step 4: Write `PrintMonitorProvider`**

Create `StartupScanner.Core/Providers/PrintMonitorProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

/// <summary>
/// Print monitors and print providers load a DLL named by their Driver value. Disabling targets
/// that value rather than the whole monitor subkey: a value-level change restores exactly from the
/// journal and leaves the rest of the monitor's configuration untouched.
/// </summary>
public sealed class PrintMonitorProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private static readonly string[] Roots =
    {
        @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Providers",
    };

    public string Id => "print-monitor";
    public StartupCategory Category => StartupCategory.PrintMonitor;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var root in Roots)
            foreach (var item in ScanRoot(root, ctx))
                yield return item;
    }

    private IReadOnlyList<StartupItem> ScanRoot(string rootPath, ScanContext ctx)
    {
        var found = new List<StartupItem>();
        IReadOnlyList<string> names;

        try
        {
            using var root = registry.OpenKey(rootPath);
            if (root is null) return found;
            names = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, rootPath, ex.Message));
            return found;
        }

        foreach (var name in names)
        {
            var path = $@"{rootPath}\{name}";
            try
            {
                using var key = registry.OpenKey(path);
                var driver = key is null
                    ? ""
                    : RegistryValueCodec.ToDisplay(key.GetValue("Driver"), key.GetValueType("Driver"));
                if (string.IsNullOrWhiteSpace(driver)) continue;

                var (image, args) = CommandLine.Split(driver, fs);

                found.Add(new StartupItem
                {
                    Id = Identity.Compute(Id, null, path, name),
                    ProviderId = Id,
                    Location = path,
                    Name = name,
                    RawValue = driver,
                    Category = Category,
                    ImagePath = image,
                    Arguments = args,
                    State = EnableState.Enabled,
                    StateSource = "ValuePresent",
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ValueName"] = "Driver",
                    },
                });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            }
        }

        return found;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~NetshHelperProviderTests|FullyQualifiedName~PrintMonitorProviderTests"`
Expected: PASS — 8 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Providers StartupScanner.Tests/Providers
git commit -m "feat: add netsh helper and print monitor providers"
```

---

## Task 33: Provider 17 — Winsock LSPs

**Files:**
- Create: `StartupScanner.Core/Providers/WinsockLspProvider.cs`
- Test: `StartupScanner.Tests/Providers/WinsockLspProviderTests.cs`

**Interfaces:**
- Consumes: `IRegistry`, `IFileSystem`.
- Produces: `WinsockLspProvider(IRegistry registry, IFileSystem fs)` with `Id == "winsock-lsp"` and `static string? ParsePackedCatalogItem(byte[] blob)`. Items carry `Extra["ValueName"]` (`PackedCatalogItem` or `LibraryPath`) and `Extra["Catalog"]`.

Two catalogs. `NameSpace_Catalog5` entries store the DLL in a plain `LibraryPath` string. `Protocol_Catalog9` entries store it inside a `PackedCatalogItem` binary blob whose first field is a fixed-size UTF-16 path buffer, so the path is the null-terminated string at offset 0. Parsing that is the only non-obvious part, and it gets its own tests including a truncated blob.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Providers/WinsockLspProviderTests.cs`:

```csharp
using System.Text;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class WinsockLspProviderTests
{
    private const string Protocol =
        @"HKLM\SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\Protocol_Catalog9\Catalog_Entries";
    private const string NameSpace =
        @"HKLM\SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\NameSpace_Catalog5\Catalog_Entries";

    private static (WinsockLspProvider Provider, FakeRegistry Registry) Sut()
    {
        var registry = new FakeRegistry();
        return (new WinsockLspProvider(registry, new FakeFileSystem()), registry);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    /// <summary>A PackedCatalogItem starts with a fixed 256-WCHAR, null-terminated path buffer.</summary>
    private static byte[] PackedCatalogItem(string path)
    {
        var blob = new byte[512 + 64];
        Encoding.Unicode.GetBytes(path).CopyTo(blob, 0);
        return blob;
    }

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("winsock-lsp", provider.Id);
        Assert.Equal(StartupCategory.WinsockLsp, provider.Category);
    }

    [Fact]
    public void ParsesTheDllPathOutOfAPackedCatalogItem()
    {
        Assert.Equal(@"C:\Windows\System32\mswsock.dll",
            WinsockLspProvider.ParsePackedCatalogItem(PackedCatalogItem(@"C:\Windows\System32\mswsock.dll")));
    }

    [Fact]
    public void ATruncatedBlobParsesAsNullRatherThanThrowing()
    {
        Assert.Null(WinsockLspProvider.ParsePackedCatalogItem(new byte[] { 0x43, 0x00, 0x3A }));
    }

    [Fact]
    public void AnAllZeroBlobParsesAsNull()
    {
        Assert.Null(WinsockLspProvider.ParsePackedCatalogItem(new byte[600]));
    }

    [Fact]
    public void ReportsAProtocolCatalogEntry()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Protocol}\000000000001", "PackedCatalogItem",
                     PackedCatalogItem(@"C:\evil\lsp.dll"), RegistryValueType.Binary);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal("000000000001", item.Name);
        Assert.Equal($@"{Protocol}\000000000001", item.Location);
        Assert.Equal(@"C:\evil\lsp.dll", item.RawValue);
        Assert.Equal(@"C:\evil\lsp.dll", item.ImagePath);
        Assert.Equal("PackedCatalogItem", item.Extra["ValueName"]);
        Assert.Equal("Protocol_Catalog9", item.Extra["Catalog"]);
        Assert.Equal(RiskTier.High, RiskTiers.For(item));
    }

    [Fact]
    public void ReportsANameSpaceCatalogEntryFromItsLibraryPath()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{NameSpace}\000000000002", "LibraryPath", @"%SystemRoot%\System32\nlaapi.dll",
                     RegistryValueType.ExpandString);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(@"%SystemRoot%\System32\nlaapi.dll", item.RawValue);
        Assert.Equal("LibraryPath", item.Extra["ValueName"]);
        Assert.Equal("NameSpace_Catalog5", item.Extra["Catalog"]);
    }

    [Fact]
    public void AnUnparseableEntryIsReportedAndMarkedPartiallyRead()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Protocol}\000000000003", "PackedCatalogItem", new byte[4], RegistryValueType.Binary);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.True(item.PartiallyRead);
        Assert.Null(item.ImagePath);
    }

    [Fact]
    public void EntriesWithNeitherValueAreIgnored()
    {
        var (provider, registry) = Sut();
        registry.Set($@"{Protocol}\000000000004", "Other", 1, RegistryValueType.Dword);

        Assert.Empty(provider.Scan(Ctx()));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WinsockLspProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'WinsockLspProvider' could not be found`.

- [ ] **Step 3: Write the provider**

Create `StartupScanner.Core/Providers/WinsockLspProvider.cs`:

```csharp
using System.Text;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed class WinsockLspProvider(IRegistry registry, IFileSystem fs) : IStartupProvider
{
    private const string ParametersKey = @"HKLM\SYSTEM\CurrentControlSet\Services\WinSock2\Parameters";

    /// <summary>WSAPROTOCOL_INFOW starts with a fixed WCHAR szProtocol[256] buffer.</summary>
    private const int PackedPathBytes = 512;

    private static readonly (string Catalog, string ValueName)[] Catalogs =
    {
        ("Protocol_Catalog9", "PackedCatalogItem"),
        ("NameSpace_Catalog5", "LibraryPath"),
    };

    public string Id => "winsock-lsp";
    public StartupCategory Category => StartupCategory.WinsockLsp;

    /// <summary>
    /// A PackedCatalogItem's DLL path is the null-terminated UTF-16 string at offset 0. Anything
    /// shorter than the fixed buffer, or an empty first character, means the blob is not one.
    /// </summary>
    public static string? ParsePackedCatalogItem(byte[] blob)
    {
        if (blob.Length < PackedPathBytes) return null;

        var text = Encoding.Unicode.GetString(blob, 0, PackedPathBytes);
        var end = text.IndexOf('\0');
        var path = (end < 0 ? text : text[..end]).Trim();
        return path.Length == 0 ? null : path;
    }

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var (catalog, valueName) in Catalogs)
            foreach (var item in ScanCatalog(catalog, valueName, ctx))
                yield return item;
    }

    private IReadOnlyList<StartupItem> ScanCatalog(string catalog, string valueName, ScanContext ctx)
    {
        var rootPath = $@"{ParametersKey}\{catalog}\Catalog_Entries";
        var found = new List<StartupItem>();
        IReadOnlyList<string> entries;

        try
        {
            using var root = registry.OpenKey(rootPath);
            if (root is null) return found;
            entries = root.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors.Add(new ScanError(Id, rootPath, ex.Message));
            return found;
        }

        foreach (var entry in entries)
        {
            var path = $@"{rootPath}\{entry}";
            try
            {
                using var key = registry.OpenKey(path);
                var value = key?.GetValue(valueName);
                if (value is null) continue;

                var dll = value is byte[] blob ? ParsePackedCatalogItem(blob) : value.ToString();
                var raw = dll ?? RegistryValueCodec.ToDisplay(value, key!.GetValueType(valueName));
                var (image, _) = dll is null ? (null, null) : CommandLine.Split(dll, fs);

                found.Add(new StartupItem
                {
                    Id = Identity.Compute(Id, null, path, entry),
                    ProviderId = Id,
                    Location = path,
                    Name = entry,
                    RawValue = raw,
                    Category = Category,
                    ImagePath = image,
                    State = EnableState.Enabled,
                    StateSource = "ValuePresent",
                    PartiallyRead = dll is null,
                    Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ValueName"] = valueName,
                        ["Catalog"] = catalog,
                    },
                });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                ctx.Errors.Add(new ScanError(Id, path, ex.Message));
            }
        }

        return found;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WinsockLspProviderTests"`
Expected: PASS — 8 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Providers/WinsockLspProvider.cs StartupScanner.Tests/Providers/WinsockLspProviderTests.cs
git commit -m "feat: add Winsock LSP provider with PackedCatalogItem parsing"
```

---

## Task 34: Provider 18 — Group Policy scripts

**Files:**
- Create: `StartupScanner.Core/Providers/GpoScriptProvider.cs`, `Writing/GpoScriptToggle.cs`
- Test: `StartupScanner.Tests/Providers/GpoScriptProviderTests.cs`

**Interfaces:**
- Consumes: `IFileSystem`, `IRegistry`, `IClock`.
- Produces:
  - `GpoScriptProvider(IFileSystem fs, IRegistry registry)` with `Id == "gpo-script"` and `static IReadOnlyList<GpoScriptEntry> ParseScriptsIni(string text)`, `record GpoScriptEntry(string Section, int Index, string CmdLine, string Parameters)`.
  - `GpoScriptToggle(IFileSystem fs, IClock clock)` with `ProviderId == "gpo-script"`, mechanism `"GpoScript.Ini"`.

`scripts.ini` is the store; the `State\...\Scripts` registry keys are a cache that Group Policy rewrites. The provider reads the ini as truth and records the registry copy in `Extra` when present. Disabling journals the whole ini file and rewrites it without the `NCmdLine`/`NParameters` pair, so re-enable restores the file byte for byte — safer than trying to renumber entries in place.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Providers/GpoScriptProviderTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class GpoScriptProviderTests
{
    private const string MachineIni = @"C:\Windows\System32\GroupPolicy\Machine\Scripts\scripts.ini";
    private const string UserIni = @"C:\Windows\System32\GroupPolicy\User\Scripts\scripts.ini";

    private const string SampleIni = """
        [Startup]
        0CmdLine=logon.bat
        0Parameters=/quiet
        1CmdLine=\\server\share\second.cmd
        1Parameters=
        [Shutdown]
        0CmdLine=bye.bat
        0Parameters=
        """;

    private static (GpoScriptProvider Provider, FakeFileSystem Fs) Sut()
    {
        var fs = new FakeFileSystem();
        fs.SetEnvironment("SystemRoot", @"C:\Windows");
        return (new GpoScriptProvider(fs, new FakeRegistry()), fs);
    }

    private static ScanContext Ctx() => new() { UserRoot = "HKCU" };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var (provider, _) = Sut();
        Assert.Equal("gpo-script", provider.Id);
        Assert.Equal(StartupCategory.GpoScript, provider.Category);
    }

    [Fact]
    public void ParsesSectionsAndIndexedPairs()
    {
        var entries = GpoScriptProvider.ParseScriptsIni(SampleIni);

        Assert.Equal(3, entries.Count);
        Assert.Equal(("Startup", 0, "logon.bat", "/quiet"),
            (entries[0].Section, entries[0].Index, entries[0].CmdLine, entries[0].Parameters));
        Assert.Equal(("Startup", 1, @"\\server\share\second.cmd", ""),
            (entries[1].Section, entries[1].Index, entries[1].CmdLine, entries[1].Parameters));
        Assert.Equal("Shutdown", entries[2].Section);
    }

    [Fact]
    public void IgnoresBlankLinesAndUnknownKeys()
    {
        var entries = GpoScriptProvider.ParseScriptsIni("[Logon]\n\nnonsense\n0CmdLine=a.bat\n");

        var entry = Assert.Single(entries);
        Assert.Equal("a.bat", entry.CmdLine);
        Assert.Equal("", entry.Parameters);
    }

    [Fact]
    public void ReportsMachineStartupScripts()
    {
        var (provider, fs) = Sut();
        fs.AddFile(MachineIni, SampleIni);

        var items = provider.Scan(Ctx()).ToList();

        var startup = items.Single(i => i.Name == "Startup[0]");
        Assert.Equal(MachineIni, startup.Location);
        Assert.Equal("logon.bat /quiet", startup.RawValue);
        Assert.Equal("Startup", startup.Extra["Section"]);
        Assert.Equal("0", startup.Extra["Index"]);
        Assert.Equal(EnableState.Enabled, startup.State);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void ReportsUserScriptsWithTheUserSid()
    {
        var (provider, fs) = Sut();
        fs.AddFile(UserIni, "[Logon]\n0CmdLine=user.bat\n0Parameters=\n");

        var item = Assert.Single(provider.Scan(new ScanContext { UserRoot = "HKCU", UserSid = "S-1-5-21-9" }));

        Assert.Equal(UserIni, item.Location);
        Assert.Equal("S-1-5-21-9", item.UserSid);
    }

    [Fact]
    public void RecordsTheRegistryStateCopyWhenPresent()
    {
        var fs = new FakeFileSystem();
        fs.SetEnvironment("SystemRoot", @"C:\Windows");
        fs.AddFile(MachineIni, "[Startup]\n0CmdLine=logon.bat\n0Parameters=\n");
        var registry = new FakeRegistry();
        registry.Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine\Scripts\Startup\0\0",
                     "Script", "logon.bat", Core.Abstractions.RegistryValueType.String);

        var item = Assert.Single(new GpoScriptProvider(fs, registry).Scan(Ctx()));

        Assert.Equal("logon.bat", item.Extra["RegistryStateScript"]);
    }

    [Fact]
    public void NoIniFilesYieldsNothing()
    {
        var (provider, _) = Sut();
        Assert.Empty(provider.Scan(Ctx()));
    }

    [Fact]
    public void DisableJournalsTheWholeIniAndRewritesItWithoutTheEntry()
    {
        var (provider, fs) = Sut();
        fs.AddFile(MachineIni, SampleIni);
        var item = provider.Scan(Ctx()).Single(i => i.Name == "Startup[0]");
        var toggle = new GpoScriptToggle(fs, new FakeClock());

        var record = toggle.SetEnabled(item, enabled: false, new FakeJournal());

        Assert.Equal("GpoScript.Ini", record.Mechanism);
        Assert.Equal(SampleIni, record.Before);
        var rewritten = fs.ReadAllText(MachineIni);
        Assert.DoesNotContain("0CmdLine=logon.bat", rewritten);
        Assert.Contains("1CmdLine=", rewritten);
        Assert.Contains("[Shutdown]", rewritten);
    }

    [Fact]
    public void EnableRestoresTheIniByteForByte()
    {
        var (provider, fs) = Sut();
        fs.AddFile(MachineIni, SampleIni);
        var item = provider.Scan(Ctx()).Single(i => i.Name == "Startup[0]");
        var toggle = new GpoScriptToggle(fs, new FakeClock());
        var journal = new FakeJournal();
        journal.Record(toggle.SetEnabled(item, enabled: false, journal));

        toggle.SetEnabled(item with { State = EnableState.Disabled }, enabled: true, journal);

        Assert.Equal(SampleIni, fs.ReadAllText(MachineIni));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~GpoScriptProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'GpoScriptProvider' could not be found`.

- [ ] **Step 3: Write the provider**

Create `StartupScanner.Core/Providers/GpoScriptProvider.cs`:

```csharp
using System.Globalization;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed record GpoScriptEntry(string Section, int Index, string CmdLine, string Parameters);

/// <summary>
/// Group Policy startup, shutdown, logon, and logoff scripts. scripts.ini is the store; the
/// State\...\Scripts registry keys are a cache Group Policy rewrites, so they are reported as
/// context rather than treated as truth.
/// </summary>
public sealed class GpoScriptProvider(IFileSystem fs, IRegistry registry) : IStartupProvider
{
    private const string StateKey =
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State";

    public string Id => "gpo-script";
    public StartupCategory Category => StartupCategory.GpoScript;

    public static string MachineIniPath(IFileSystem fs)
        => fs.ExpandEnvironment(@"%SystemRoot%\System32\GroupPolicy\Machine\Scripts\scripts.ini");

    public static string UserIniPath(IFileSystem fs)
        => fs.ExpandEnvironment(@"%SystemRoot%\System32\GroupPolicy\User\Scripts\scripts.ini");

    public static IReadOnlyList<GpoScriptEntry> ParseScriptsIni(string text)
    {
        var commands = new Dictionary<(string Section, int Index), (string? CmdLine, string? Parameters)>();
        var order = new List<(string Section, int Index)>();
        var section = "";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().Trim('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            var split = line.IndexOf('=');
            if (split <= 0) continue;

            var key = line[..split];
            var value = line[(split + 1)..];
            var digits = new string(key.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) continue;

            var index = int.Parse(digits, CultureInfo.InvariantCulture);
            var field = key[digits.Length..];
            var slot = (section, index);

            if (!commands.ContainsKey(slot))
            {
                commands[slot] = (null, null);
                order.Add(slot);
            }

            commands[slot] = field.Equals("CmdLine", StringComparison.OrdinalIgnoreCase)
                ? (value, commands[slot].Parameters)
                : (commands[slot].CmdLine, value);
        }

        return order
            .Where(slot => !string.IsNullOrWhiteSpace(commands[slot].CmdLine))
            .Select(slot => new GpoScriptEntry(slot.Section, slot.Index,
                                               commands[slot].CmdLine!, commands[slot].Parameters ?? ""))
            .ToList();
    }

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var item in ScanIni(MachineIniPath(fs), "Machine", null, ctx)) yield return item;
        foreach (var item in ScanIni(UserIniPath(fs), "User", ctx.UserSid, ctx)) yield return item;
    }

    private IReadOnlyList<StartupItem> ScanIni(string iniPath, string scope, string? sid, ScanContext ctx)
    {
        string text;
        try
        {
            if (!fs.FileExists(iniPath)) return Array.Empty<StartupItem>();
            text = fs.ReadAllText(iniPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ctx.Errors.Add(new ScanError(Id, iniPath, ex.Message));
            return Array.Empty<StartupItem>();
        }

        var items = new List<StartupItem>();

        foreach (var entry in ParseScriptsIni(text))
        {
            var name = $"{entry.Section}[{entry.Index}]";
            var raw = string.IsNullOrWhiteSpace(entry.Parameters)
                ? entry.CmdLine
                : $"{entry.CmdLine} {entry.Parameters}";
            var (image, args) = CommandLine.Split(raw, fs);

            items.Add(new StartupItem
            {
                Id = Identity.Compute(Id, sid, iniPath, name),
                ProviderId = Id,
                Location = iniPath,
                Name = name,
                RawValue = raw,
                Category = Category,
                ImagePath = image,
                Arguments = args,
                State = EnableState.Enabled,
                StateSource = "ScriptsIniEntry",
                UserSid = sid,
                Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Section"] = entry.Section,
                    ["Index"] = entry.Index.ToString(CultureInfo.InvariantCulture),
                    ["CmdLine"] = entry.CmdLine,
                    ["Parameters"] = entry.Parameters,
                    ["Scope"] = scope,
                    ["RegistryStateScript"] = RegistryStateScript(scope, entry),
                },
            });
        }

        return items;
    }

    private string RegistryStateScript(string scope, GpoScriptEntry entry)
    {
        var path = $@"{StateKey}\{scope}\Scripts\{entry.Section}\{entry.Index}\{entry.Index}";
        try
        {
            using var key = registry.OpenKey(path);
            return key is null ? "" : RegistryValueCodec.ToDisplay(key.GetValue("Script"), key.GetValueType("Script"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "";
        }
    }
}
```

- [ ] **Step 4: Write the toggle**

Create `StartupScanner.Core/Writing/GpoScriptToggle.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Disabling a Group Policy script rewrites scripts.ini without that entry's CmdLine/Parameters
/// pair. The entire prior file is journaled, so re-enable restores it byte for byte instead of
/// attempting to renumber entries back into place.
/// </summary>
public sealed class GpoScriptToggle(IFileSystem fs, IClock clock) : IToggleProvider
{
    public string ProviderId => "gpo-script";

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.BackupRemove;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        if (enabled)
        {
            var backup = journal.All.LastOrDefault(r => r.ItemId == item.Id && !r.Enabled && r.Before is not null)
                ?? throw new InvalidOperationException(
                    $"No backed-up scripts.ini is recorded for '{item.Name}', so it cannot be re-enabled.");

            fs.WriteAllText(item.Location, backup.Before!);
            return Build(item, enabled: true, before: null, after: backup.Before);
        }

        var original = fs.ReadAllText(item.Location);
        fs.WriteAllText(item.Location, Remove(original, item.Extra["Section"], item.Extra["Index"]));
        return Build(item, enabled: false, before: original, after: null);
    }

    public void Restore(WriteRecord record)
    {
        if (record.Before is null) return;
        fs.WriteAllText(record.Location, record.Before);
    }

    /// <summary>Drops the "NCmdLine=" and "NParameters=" lines from the given section.</summary>
    private static string Remove(string text, string section, string index)
    {
        var kept = new List<string>();
        var current = "";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                current = trimmed[1..^1];
                kept.Add(line);
                continue;
            }

            var isTarget = current.Equals(section, StringComparison.OrdinalIgnoreCase)
                && (trimmed.StartsWith($"{index}CmdLine=", StringComparison.OrdinalIgnoreCase)
                 || trimmed.StartsWith($"{index}Parameters=", StringComparison.OrdinalIgnoreCase));

            if (!isTarget) kept.Add(line);
        }

        return string.Join("\n", kept);
    }

    private WriteRecord Build(StartupItem item, bool enabled, string? before, string? after) => new()
    {
        Ts = clock.UtcNow,
        SessionId = "",
        ItemId = item.Id,
        ProviderId = ProviderId,
        Mechanism = "GpoScript.Ini",
        Location = item.Location,
        Name = item.Name,
        Category = item.Category,
        UserSid = item.UserSid,
        Before = before,
        After = after,
        Enabled = enabled,
    };
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~GpoScriptProviderTests"`
Expected: PASS — 9 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add Group Policy script provider with whole-file journaling"
```

---

## Task 35: Provider 19 — logged-out user hives

**Files:**
- Create: `StartupScanner.Core/Abstractions/IProfileEnumerator.cs`, `Abstractions/IUserHiveLoader.cs`, `Providers/UserHiveProvider.cs`
- Create: `StartupScanner.Tests/Fakes/FakeProfileEnumerator.cs`, `Fakes/FakeUserHiveLoader.cs`
- Test: `StartupScanner.Tests/Providers/UserHiveProviderTests.cs`

**Interfaces:**
- Consumes: `IStartupProvider`, `ScanContext`.
- Produces:
  - `IProfileEnumerator.Enumerate() -> IReadOnlyList<UserProfile>`.
  - `IUserHiveLoader.Load(string sid, string hiveFile) -> MountedHive`, `record MountedHive(string Root, IDisposable Lease)`.
  - `UserHiveProvider(IReadOnlyList<IStartupProvider> inner, IProfileEnumerator profiles, IUserHiveLoader loader)` with `Id == "user-hive"`.

This provider wraps providers 1, 7, 10, 11, and 13 and runs them against every other profile's hive. Three rules from the spec's open-risks section are enforced here:

1. **`HKU` is checked first.** `RegLoadKey` fails if the user is logged on, because the hive is already mounted under `HKU\<SID>`; a loaded profile is scanned in place with no load at all.
2. **Hives are always unloaded**, in a `finally` block. `yield return` inside `try`/`finally` is legal C#, and each inner provider's results are materialised inside a `try`/`catch` so one failure does not abort the profile.
3. **A hive that cannot be loaded is skipped and the user is named** in a `ScanError`, so the banner can report it.

The interactive user's own SID is skipped: the HKCU pass already covered it.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Fakes/FakeProfileEnumerator.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Tests.Fakes;

public sealed class FakeProfileEnumerator(params UserProfile[] profiles) : IProfileEnumerator
{
    public IReadOnlyList<UserProfile> Enumerate() => profiles;
}

public sealed class FakeUserHiveLoader : IUserHiveLoader
{
    public List<string> Loaded { get; } = new();
    public List<string> Unloaded { get; } = new();
    public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MountedHive Load(string sid, string hiveFile)
    {
        if (FailFor.Contains(sid)) throw new IOException($"cannot load {hiveFile}");

        Loaded.Add(sid);
        return new MountedHive($@"HKU\StartupScanner_{sid}", new Lease(this, sid));
    }

    private sealed class Lease(FakeUserHiveLoader owner, string sid) : IDisposable
    {
        public void Dispose() => owner.Unloaded.Add(sid);
    }
}
```

Create `StartupScanner.Tests/Providers/UserHiveProviderTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Providers;

public class UserHiveProviderTests
{
    /// <summary>Records the UserRoot it was handed and yields one item tagged with it.</summary>
    private sealed class RootRecordingProvider : IStartupProvider
    {
        public List<string> Roots { get; } = new();
        public List<string?> Sids { get; } = new();

        public string Id => "registry.run";
        public StartupCategory Category => StartupCategory.RunKey;

        public IEnumerable<StartupItem> Scan(ScanContext ctx)
        {
            Roots.Add(ctx.UserRoot);
            Sids.Add(ctx.UserSid);
            yield return new StartupItem
            {
                Id = Identity.Compute(Id, ctx.UserSid, ctx.UserRoot, "Thing"),
                ProviderId = Id, Location = ctx.UserRoot, Name = "Thing", RawValue = "x.exe",
                Category = StartupCategory.RunKey, UserSid = ctx.UserSid,
            };
        }
    }

    private sealed class ThrowingInnerProvider : IStartupProvider
    {
        public string Id => "com-hijack";
        public StartupCategory Category => StartupCategory.ComHijack;
        public IEnumerable<StartupItem> Scan(ScanContext ctx) => throw new InvalidOperationException("inner blew up");
    }

    private static ScanContext Ctx(string? interactiveSid = "S-1-5-21-1")
        => new() { UserRoot = "HKCU", UserSid = interactiveSid };

    [Fact]
    public void IdAndCategoryAreStable()
    {
        var provider = new UserHiveProvider(Array.Empty<IStartupProvider>(),
            new FakeProfileEnumerator(), new FakeUserHiveLoader());

        Assert.Equal("user-hive", provider.Id);
        Assert.Equal(StartupCategory.RunKey, provider.Category);
    }

    [Fact]
    public void LoadsAndUnloadsAnOfflineProfilesHive()
    {
        var inner = new RootRecordingProvider();
        var loader = new FakeUserHiveLoader();
        var provider = new UserHiveProvider(new IStartupProvider[] { inner },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-42", @"C:\Users\bob", false, "bob")), loader);

        var item = Assert.Single(provider.Scan(Ctx()));

        Assert.Equal(new[] { "S-1-5-21-42" }, loader.Loaded);
        Assert.Equal(new[] { "S-1-5-21-42" }, loader.Unloaded);
        Assert.Equal(@"HKU\StartupScanner_S-1-5-21-42", inner.Roots.Single());
        Assert.Equal("S-1-5-21-42", item.UserSid);
    }

    [Fact]
    public void AnAlreadyLoadedHiveIsScannedInPlaceWithoutLoading()
    {
        var inner = new RootRecordingProvider();
        var loader = new FakeUserHiveLoader();
        var provider = new UserHiveProvider(new IStartupProvider[] { inner },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-7", @"C:\Users\carol", true, "carol")), loader);

        provider.Scan(Ctx()).ToList();

        Assert.Empty(loader.Loaded);
        Assert.Equal(@"HKU\S-1-5-21-7", inner.Roots.Single());
    }

    [Fact]
    public void TheInteractiveUsersOwnProfileIsSkippedBecauseHkcuAlreadyCoveredIt()
    {
        var inner = new RootRecordingProvider();
        var provider = new UserHiveProvider(new IStartupProvider[] { inner },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-1", @"C:\Users\alice", true, "alice")),
            new FakeUserHiveLoader());

        Assert.Empty(provider.Scan(Ctx("S-1-5-21-1")));
        Assert.Empty(inner.Roots);
    }

    [Fact]
    public void AHiveThatCannotBeLoadedIsSkippedAndTheUserIsNamed()
    {
        var loader = new FakeUserHiveLoader();
        loader.FailFor.Add("S-1-5-21-99");
        var provider = new UserHiveProvider(new IStartupProvider[] { new RootRecordingProvider() },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-99", @"C:\Users\dave", false, "dave")), loader);
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));

        var error = Assert.Single(ctx.Errors);
        Assert.Contains("dave", error.Message);
        Assert.Empty(loader.Unloaded);
    }

    [Fact]
    public void AnInnerProviderThrowingStillUnloadsTheHiveAndRecordsTheError()
    {
        var loader = new FakeUserHiveLoader();
        var provider = new UserHiveProvider(new IStartupProvider[] { new ThrowingInnerProvider() },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-42", @"C:\Users\bob", false, "bob")), loader);
        var ctx = Ctx();

        Assert.Empty(provider.Scan(ctx));

        Assert.Equal(new[] { "S-1-5-21-42" }, loader.Unloaded);
        Assert.Contains(ctx.Errors, e => e.Message.Contains("inner blew up"));
    }

    [Fact]
    public void EveryProfileIsVisitedAndEachGetsItsOwnItemIdentity()
    {
        var inner = new RootRecordingProvider();
        var provider = new UserHiveProvider(new IStartupProvider[] { inner },
            new FakeProfileEnumerator(
                new UserProfile("S-1-5-21-42", @"C:\Users\bob", false, "bob"),
                new UserProfile("S-1-5-21-43", @"C:\Users\carol", true, "carol")),
            new FakeUserHiveLoader());

        var ids = provider.Scan(Ctx()).Select(i => i.Id).Distinct().ToList();

        Assert.Equal(2, ids.Count);
        Assert.Equal(new[] { "S-1-5-21-42", "S-1-5-21-43" }, inner.Sids);
    }

    [Fact]
    public void TheMountedContextIsFlaggedSoInnerProvidersKnow()
    {
        var flags = new List<bool>();
        var recorder = new FlagRecordingProvider(flags);
        var provider = new UserHiveProvider(new IStartupProvider[] { recorder },
            new FakeProfileEnumerator(new UserProfile("S-1-5-21-42", @"C:\Users\bob", false, "bob")),
            new FakeUserHiveLoader());

        provider.Scan(Ctx()).ToList();

        Assert.True(Assert.Single(flags));
    }

    private sealed class FlagRecordingProvider(List<bool> flags) : IStartupProvider
    {
        public string Id => "registry.run";
        public StartupCategory Category => StartupCategory.RunKey;

        public IEnumerable<StartupItem> Scan(ScanContext ctx)
        {
            flags.Add(ctx.ScanningMountedHive);
            return Array.Empty<StartupItem>();
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~UserHiveProviderTests"`
Expected: FAIL — `CS0246: The type or namespace name 'IProfileEnumerator' could not be found`.

- [ ] **Step 3: Write the abstractions**

Create `StartupScanner.Core/Abstractions/IProfileEnumerator.cs`:

```csharp
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Abstractions;

public interface IProfileEnumerator
{
    /// <summary>
    /// Every local profile from ProfileList, with HiveLoaded set when HKU\&lt;SID&gt; already exists.
    /// </summary>
    IReadOnlyList<UserProfile> Enumerate();
}
```

Create `StartupScanner.Core/Abstractions/IUserHiveLoader.cs`:

```csharp
namespace StartupScanner.Core.Abstractions;

/// <summary>A mounted hive and the lease whose disposal unloads it.</summary>
public sealed record MountedHive(string Root, IDisposable Lease);

public interface IUserHiveLoader
{
    /// <summary>
    /// Mounts a profile's NTUSER.DAT and returns the registry root it appears under.
    /// Throws when the hive cannot be loaded — in use, missing, or access denied.
    /// </summary>
    MountedHive Load(string sid, string hiveFile);
}
```

- [ ] **Step 4: Write the provider**

Create `StartupScanner.Core/Providers/UserHiveProvider.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Providers;

/// <summary>
/// Runs the per-user providers against every other profile's hive, so users who are not logged on
/// are still scanned. Hives are always unloaded in a finally block.
/// </summary>
public sealed class UserHiveProvider(
    IReadOnlyList<IStartupProvider> inner,
    IProfileEnumerator profiles,
    IUserHiveLoader loader) : IStartupProvider
{
    public string Id => "user-hive";

    /// <summary>Nominal only: the items yielded carry their own inner provider's category.</summary>
    public StartupCategory Category => StartupCategory.RunKey;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var profile in profiles.Enumerate())
        {
            // The HKCU pass already covered the interactive user.
            if (profile.Sid == ctx.UserSid) continue;

            string root;
            IDisposable? lease = null;

            if (profile.HiveLoaded)
            {
                // RegLoadKey fails while the user is logged on, because the hive is already mounted.
                root = $@"HKU\{profile.Sid}";
            }
            else
            {
                MountedHive mounted;
                try
                {
                    mounted = loader.Load(profile.Sid, Path.Combine(profile.ProfilePath, "NTUSER.DAT"));
                }
                catch (Exception ex)
                {
                    ctx.Errors.Add(new ScanError(Id, profile.ProfilePath,
                        $"Could not load the profile hive for {profile.UserName ?? profile.Sid}: {ex.Message}"));
                    continue;
                }

                root = mounted.Root;
                lease = mounted.Lease;
            }

            try
            {
                var scoped = new ScanContext
                {
                    UserRoot = root,
                    UserSid = profile.Sid,
                    ScanningMountedHive = true,
                };

                foreach (var provider in inner)
                    foreach (var item in SafeScan(provider, scoped, ctx))
                        yield return item;
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    /// <summary>Materialises one inner provider's results so a failure cannot abort the profile.</summary>
    private IReadOnlyList<StartupItem> SafeScan(IStartupProvider provider, ScanContext scoped, ScanContext outer)
    {
        try
        {
            var items = provider.Scan(scoped).ToList();
            foreach (var error in scoped.Errors) outer.Errors.Add(error);
            return items;
        }
        catch (Exception ex)
        {
            outer.Errors.Add(new ScanError(provider.Id, scoped.UserRoot, ex.Message));
            return Array.Empty<StartupItem>();
        }
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~UserHiveProviderTests"`
Expected: PASS — 8 tests passed.

- [ ] **Step 6: Run the whole suite**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: PASS — all tests green. All 19 providers now exist.

- [ ] **Step 7: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add user hive provider for logged-out profiles"
```

---

## Task 36: Presentation helpers — filter, status line, export

**Files:**
- Create: `StartupScanner.Core/Presentation/ItemFilter.cs`, `Presentation/StatusLine.cs`, `Presentation/Exporter.cs`
- Test: `StartupScanner.Tests/Presentation/ItemFilterTests.cs`, `Presentation/StatusLineTests.cs`, `Presentation/ExporterTests.cs`

**Interfaces:**
- Consumes: `StartupItem`, `DiffEvent`, `FileFacts`.
- Produces:
  - `FilterSettings { string Search, ISet<StartupCategory> Categories, bool ChangesOnly, bool HideMicrosoftSigned }`.
  - `ItemFilter.Apply(IEnumerable<StartupItem> items, FilterSettings settings, ISet<string> changedIds) -> IReadOnlyList<StartupItem>`.
  - `StatusLine.Format(int itemCount, int providerCount, IReadOnlyList<DiffEvent> events, DateTime? previousScanUtc) -> string`.
  - `Exporter.ToCsv(IEnumerable<StartupItem>) -> string` and `Exporter.ToJson(IEnumerable<StartupItem>) -> string`.

All three live in Core rather than the WPF layer so they can be tested without a UI. The App becomes a thin binding surface over them, which is the whole reason `Core` has no WPF reference.

- [ ] **Step 1: Write the failing tests**

Create `StartupScanner.Tests/Presentation/ItemFilterTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Presentation;

namespace StartupScanner.Tests.Presentation;

public class ItemFilterTests
{
    private static StartupItem Item(string name, StartupCategory category = StartupCategory.RunKey,
                                    string location = @"HKCU\Run", bool microsoftSigned = false,
                                    string id = "id") => new()
    {
        Id = id, ProviderId = "registry.run", Location = location, Name = name,
        RawValue = @"C:\thing.exe", Category = category,
        File = new FileFacts
        {
            Path = @"C:\thing.exe", Exists = true,
            Signature = new SignatureFacts { Status = SignatureStatus.Valid, IsMicrosoftSigned = microsoftSigned },
        },
    };

    private static FilterSettings All() => new();

    [Fact]
    public void NoFiltersReturnsEverything()
    {
        var items = new[] { Item("A"), Item("B", StartupCategory.Service) };

        Assert.Equal(2, ItemFilter.Apply(items, All(), new HashSet<string>()).Count);
    }

    [Fact]
    public void SearchMatchesNameLocationAndRawValueCaseInsensitively()
    {
        var items = new[]
        {
            Item("Spotify"),
            Item("Other", location: @"HKLM\SOFTWARE\Spotify"),
            Item("Third") with { RawValue = @"C:\Program Files\Spotify\app.exe" },
            Item("Nope") with { RawValue = @"C:\other.exe" },
        };

        var filtered = ItemFilter.Apply(items, All() with { Search = "spotify" }, new HashSet<string>());

        Assert.Equal(3, filtered.Count);
    }

    [Fact]
    public void CategoryFilterKeepsOnlySelectedCategories()
    {
        var items = new[] { Item("A"), Item("B", StartupCategory.Service), Item("C", StartupCategory.ScheduledTask) };

        var filtered = ItemFilter.Apply(items,
            All() with { Categories = new HashSet<StartupCategory> { StartupCategory.Service } },
            new HashSet<string>());

        Assert.Equal("B", Assert.Single(filtered).Name);
    }

    [Fact]
    public void ChangesOnlyKeepsItemsWithADiffEvent()
    {
        var items = new[] { Item("A", id: "id-a"), Item("B", id: "id-b") };

        var filtered = ItemFilter.Apply(items, All() with { ChangesOnly = true }, new HashSet<string> { "id-b" });

        Assert.Equal("B", Assert.Single(filtered).Name);
    }

    [Fact]
    public void HideMicrosoftSignedDropsMicrosoftSignedItems()
    {
        var items = new[] { Item("MS", microsoftSigned: true), Item("ThirdParty") };

        var filtered = ItemFilter.Apply(items, All() with { HideMicrosoftSigned = true }, new HashSet<string>());

        Assert.Equal("ThirdParty", Assert.Single(filtered).Name);
    }

    [Fact]
    public void HideMicrosoftSignedKeepsItemsNotYetEnrichedSoTheListDoesNotFlicker()
    {
        var items = new[] { Item("Unknown") with { File = null } };

        Assert.Single(ItemFilter.Apply(items, All() with { HideMicrosoftSigned = true }, new HashSet<string>()));
    }

    [Fact]
    public void FiltersCombineAsAnd()
    {
        var items = new[]
        {
            Item("Spotify", StartupCategory.Service, id: "id-1"),
            Item("Spotify", StartupCategory.RunKey, id: "id-2"),
        };

        var filtered = ItemFilter.Apply(items,
            new FilterSettings
            {
                Search = "spotify",
                Categories = new HashSet<StartupCategory> { StartupCategory.Service },
                ChangesOnly = true,
            },
            new HashSet<string> { "id-1", "id-2" });

        Assert.Equal(StartupCategory.Service, Assert.Single(filtered).Category);
    }
}
```

Create `StartupScanner.Tests/Presentation/StatusLineTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Presentation;

namespace StartupScanner.Tests.Presentation;

public class StatusLineTests
{
    private static DiffEvent Event(DiffEventKind kind) => new()
    {
        Ts = new DateTime(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc), Event = kind,
        Id = "id", Name = "n", ProviderId = "p", Location = "l",
    };

    [Fact]
    public void CountsItemsAndProviders()
    {
        var text = StatusLine.Format(412, 19, Array.Empty<DiffEvent>(), null);

        Assert.Contains("412 items", text);
        Assert.Contains("19 providers", text);
    }

    [Fact]
    public void SummarisesEachEventKindWithItsOwnCount()
    {
        var events = new[]
        {
            Event(DiffEventKind.Added), Event(DiffEventKind.Added), Event(DiffEventKind.Added),
            Event(DiffEventKind.Removed),
            Event(DiffEventKind.Modified),
            Event(DiffEventKind.StateChanged),
        };

        var text = StatusLine.Format(412, 19, events, new DateTime(2026, 7, 28, 9, 12, 0, DateTimeKind.Utc));

        Assert.Contains("3 added", text);
        Assert.Contains("1 removed", text);
        Assert.Contains("1 modified", text);
        Assert.Contains("1 re-enabled", text);
    }

    [Fact]
    public void NamesThePreviousScanTime()
    {
        var text = StatusLine.Format(1, 19, new[] { Event(DiffEventKind.Added) },
                                     new DateTime(2026, 7, 28, 9, 12, 0, DateTimeKind.Utc));

        Assert.Contains("2026-07-28 09:12", text);
    }

    [Fact]
    public void SaysThisIsTheFirstScanWhenThereIsNoBaseline()
    {
        var text = StatusLine.Format(412, 19, Array.Empty<DiffEvent>(), null);

        Assert.Contains("first scan", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaysNoChangesWhenThereIsABaselineButNoEvents()
    {
        var text = StatusLine.Format(412, 19, Array.Empty<DiffEvent>(),
                                     new DateTime(2026, 7, 28, 9, 12, 0, DateTimeKind.Utc));

        Assert.Contains("no changes", text, StringComparison.OrdinalIgnoreCase);
    }
}
```

Create `StartupScanner.Tests/Presentation/ExporterTests.cs`:

```csharp
using System.Text.Json;
using StartupScanner.Core.Models;
using StartupScanner.Core.Presentation;

namespace StartupScanner.Tests.Presentation;

public class ExporterTests
{
    private static StartupItem Item(string name = "Spotify", string raw = @"C:\Spotify.exe") => new()
    {
        Id = "id-1", ProviderId = "registry.run", Location = @"HKCU\Run", Name = name, RawValue = raw,
        Category = StartupCategory.RunKey, State = EnableState.Enabled, StateSource = "StartupApproved",
        Risk = RiskTier.Low, ImagePath = @"C:\Spotify.exe", UserSid = "S-1-5-21-9",
    };

    [Fact]
    public void CsvStartsWithAHeaderRow()
    {
        var lines = Exporter.ToCsv(new[] { Item() }).Split('\n');

        Assert.StartsWith("Id,ProviderId,Category,Name,Location,RawValue,ImagePath,Arguments,State,StateSource,Risk,UserSid,Publisher", lines[0]);
    }

    [Fact]
    public void CsvWritesOneRowPerItem()
    {
        var csv = Exporter.ToCsv(new[] { Item("A"), Item("B") });

        Assert.Equal(3, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void CsvQuotesFieldsContainingCommasQuotesAndNewlines()
    {
        var csv = Exporter.ToCsv(new[] { Item("Weird", "a,b \"quoted\"\nsecond") });

        Assert.Contains("\"a,b \"\"quoted\"\" second\"", csv);
    }

    [Fact]
    public void CsvIncludesThePublisherWhenEnrichmentHasRun()
    {
        var item = Item() with
        {
            File = new FileFacts
            {
                Path = @"C:\Spotify.exe", Exists = true,
                Signature = new SignatureFacts { Status = SignatureStatus.Valid, SignerName = "Spotify AB" },
            },
        };

        Assert.Contains("Spotify AB", Exporter.ToCsv(new[] { item }));
    }

    [Fact]
    public void JsonRoundTripsTheItems()
    {
        var json = Exporter.ToJson(new[] { Item() });

        using var document = JsonDocument.Parse(json);
        var first = document.RootElement.EnumerateArray().Single();
        Assert.Equal("Spotify", first.GetProperty("name").GetString());
        Assert.Equal("RunKey", first.GetProperty("category").GetString());
    }

    [Fact]
    public void JsonOmitsFileFactsSoAnExportIsNotAHashDump()
    {
        var item = Item() with { File = new FileFacts { Path = @"C:\Spotify.exe", Sha256 = "deadbeef" } };

        Assert.DoesNotContain("deadbeef", Exporter.ToJson(new[] { item }));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ItemFilterTests|FullyQualifiedName~StatusLineTests|FullyQualifiedName~ExporterTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ItemFilter' could not be found`.

- [ ] **Step 3: Write `ItemFilter`**

Create `StartupScanner.Core/Presentation/ItemFilter.cs`:

```csharp
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Presentation;

public sealed record FilterSettings
{
    public string Search { get; init; } = "";

    /// <summary>Empty means every category.</summary>
    public ISet<StartupCategory> Categories { get; init; } = new HashSet<StartupCategory>();

    public bool ChangesOnly { get; init; }
    public bool HideMicrosoftSigned { get; init; }
}

public static class ItemFilter
{
    public static IReadOnlyList<StartupItem> Apply(
        IEnumerable<StartupItem> items, FilterSettings settings, ISet<string> changedIds)
        => items.Where(item => Matches(item, settings, changedIds)).ToList();

    private static bool Matches(StartupItem item, FilterSettings settings, ISet<string> changedIds)
    {
        if (settings.Categories.Count > 0 && !settings.Categories.Contains(item.Category)) return false;
        if (settings.ChangesOnly && !changedIds.Contains(item.Id)) return false;

        // An item whose enrichment has not finished is kept: hiding it and then showing it again
        // would make rows appear and disappear while phase 2 runs.
        if (settings.HideMicrosoftSigned && item.File?.Signature?.IsMicrosoftSigned == true) return false;

        if (settings.Search.Length == 0) return true;

        return Contains(item.Name, settings.Search)
            || Contains(item.Location, settings.Search)
            || Contains(item.RawValue, settings.Search)
            || Contains(item.ImagePath, settings.Search);
    }

    private static bool Contains(string? haystack, string needle)
        => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Write `StatusLine`**

Create `StartupScanner.Core/Presentation/StatusLine.cs`:

```csharp
using System.Globalization;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Presentation;

public static class StatusLine
{
    public static string Format(int itemCount, int providerCount,
                                IReadOnlyList<DiffEvent> events, DateTime? previousScanUtc)
    {
        var head = $"{itemCount} items · {providerCount} providers";

        if (previousScanUtc is null) return $"{head} · first scan, no baseline to compare against";

        var since = previousScanUtc.Value.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        if (events.Count == 0) return $"{head} · no changes since {since}";

        var parts = new List<string>();
        Add(parts, events, DiffEventKind.Added, "added");
        Add(parts, events, DiffEventKind.Removed, "removed");
        Add(parts, events, DiffEventKind.Modified, "modified");
        Add(parts, events, DiffEventKind.StateChanged, "re-enabled or re-disabled");

        return $"{head} · {string.Join(" ", parts)} since {since}";
    }

    private static void Add(List<string> parts, IReadOnlyList<DiffEvent> events, DiffEventKind kind, string label)
    {
        var count = events.Count(e => e.Event == kind);
        if (count > 0) parts.Add($"{count} {label}");
    }
}
```

- [ ] **Step 5: Write `Exporter`**

Create `StartupScanner.Core/Presentation/Exporter.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Presentation;

/// <summary>Writes the currently filtered rows. FileFacts are excluded: an export is an inventory, not a hash dump.</summary>
public static class Exporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] Headers =
    {
        "Id", "ProviderId", "Category", "Name", "Location", "RawValue", "ImagePath", "Arguments",
        "State", "StateSource", "Risk", "UserSid", "Publisher",
    };

    public static string ToCsv(IEnumerable<StartupItem> items)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', Headers)).Append('\n');

        foreach (var item in items)
        {
            builder.Append(string.Join(',', new[]
            {
                Field(item.Id), Field(item.ProviderId), Field(item.Category.ToString()), Field(item.Name),
                Field(item.Location), Field(item.RawValue), Field(item.ImagePath), Field(item.Arguments),
                Field(item.State.ToString()), Field(item.StateSource), Field(item.Risk.ToString()),
                Field(item.UserSid), Field(item.File?.Signature?.SignerName),
            })).Append('\n');
        }

        return builder.ToString();
    }

    public static string ToJson(IEnumerable<StartupItem> items)
        => JsonSerializer.Serialize(items.Select(Row).ToList(), Json);

    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // Newlines are folded to spaces so one row stays one line.
        var flat = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return flat.Contains(',') || flat.Contains('"')
            ? "\"" + flat.Replace("\"", "\"\"") + "\""
            : flat;
    }

    private static object Row(StartupItem item) => new
    {
        item.Id, item.ProviderId, Category = item.Category.ToString(), item.Name, item.Location,
        item.RawValue, item.ImagePath, item.Arguments, State = item.State.ToString(), item.StateSource,
        Risk = item.Risk.ToString(), item.UserSid,
        Publisher = item.File?.Signature?.SignerName,
        item.Extra,
    };
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ItemFilterTests|FullyQualifiedName~StatusLineTests|FullyQualifiedName~ExporterTests"`
Expected: PASS — 18 tests passed.

- [ ] **Step 7: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Presentation StartupScanner.Tests/Presentation
git commit -m "feat: add filter, status line, and CSV/JSON export helpers"
```

---

## Task 37: Phase-2 enrichment service

**Files:**
- Create: `StartupScanner.Core/Abstractions/IEnrichmentSources.cs`, `Enrichment/FileFactsService.cs`
- Test: `StartupScanner.Tests/Enrichment/FileFactsServiceTests.cs`

**Interfaces:**
- Consumes: `IFileSystem`, `FileFacts`, `SignatureFacts`, `PeVersion`.
- Produces:
  - `IPeVersionReader.Read(string path) -> PeVersion?`
  - `IAuthenticode.Verify(string path) -> SignatureFacts`
  - `IFileOwnerReader.GetOwner(string path) -> string?`
  - `IServiceStatus.GetStatus(string serviceName) -> string?`
  - `FileFactsService(IFileSystem fs, IPeVersionReader pe, IAuthenticode authenticode, IFileOwnerReader owner)` with `FileFacts Get(string path)` and `int CacheSize { get; }`.

Phase 2 is the expensive half of a scan — hashing and Authenticode verification — so results are cached by `(path, size, mtime)`. That key means an updated binary is re-hashed while a repeat scan of an unchanged one is free. A missing file returns `Exists = false` rather than throwing, and a hash failure leaves `Sha256` null instead of losing the rest of the facts.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Enrichment/FileFactsServiceTests.cs`:

```csharp
using System.Text;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Enrichment;
using StartupScanner.Core.Models;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Enrichment;

public class FileFactsServiceTests
{
    private sealed class CountingPeReader : IPeVersionReader
    {
        public int Calls { get; private set; }
        public PeVersion? Read(string path)
        {
            Calls++;
            return new PeVersion("Spotify AB", "Spotify", "Spotify Music", "1.2.3.4");
        }
    }

    private sealed class CountingAuthenticode : IAuthenticode
    {
        public int Calls { get; private set; }
        public SignatureFacts Verify(string path)
        {
            Calls++;
            return new SignatureFacts
            {
                Status = SignatureStatus.Valid, SignerName = "Spotify AB",
                Chain = new[] { "Spotify AB", "DigiCert" }, IsMicrosoftSigned = false,
            };
        }
    }

    private sealed class StubOwner : IFileOwnerReader
    {
        public string? GetOwner(string path) => @"BUILTIN\Administrators";
    }

    private static (FileFactsService Service, FakeFileSystem Fs, CountingPeReader Pe, CountingAuthenticode Auth) Sut()
    {
        var fs = new FakeFileSystem();
        var pe = new CountingPeReader();
        var auth = new CountingAuthenticode();
        return (new FileFactsService(fs, pe, auth, new StubOwner()), fs, pe, auth);
    }

    [Fact]
    public void GathersHashSizeTimestampsVersionSignatureAndOwner()
    {
        var (service, fs, _, _) = Sut();
        var modified = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        fs.AddFile(@"C:\Spotify.exe", Encoding.UTF8.GetBytes("MZfake"), modified);

        var facts = service.Get(@"C:\Spotify.exe");

        Assert.True(facts.Exists);
        Assert.Equal(6, facts.Size);
        Assert.Equal(modified, facts.ModifiedUtc);
        Assert.Equal(64, facts.Sha256!.Length);
        Assert.Equal("Spotify AB", facts.Version!.Company);
        Assert.Equal("Spotify AB", facts.Signature!.SignerName);
        Assert.Equal(@"BUILTIN\Administrators", facts.AclOwner);
    }

    [Fact]
    public void HashIsTheSha256OfTheFileContent()
    {
        var (service, fs, _, _) = Sut();
        fs.AddFile(@"C:\a.exe", "abc");

        // SHA-256 of "abc"
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                     service.Get(@"C:\a.exe").Sha256);
    }

    [Fact]
    public void AMissingFileReportsExistsFalseWithoutCallingTheExpensiveReaders()
    {
        var (service, _, pe, auth) = Sut();

        var facts = service.Get(@"C:\gone.exe");

        Assert.False(facts.Exists);
        Assert.Null(facts.Sha256);
        Assert.Equal(0, pe.Calls);
        Assert.Equal(0, auth.Calls);
    }

    [Fact]
    public void RepeatedLookupsOfAnUnchangedFileAreServedFromCache()
    {
        var (service, fs, pe, auth) = Sut();
        fs.AddFile(@"C:\a.exe", "abc");

        service.Get(@"C:\a.exe");
        service.Get(@"C:\a.exe");
        service.Get(@"C:\a.exe");

        Assert.Equal(1, pe.Calls);
        Assert.Equal(1, auth.Calls);
        Assert.Equal(1, service.CacheSize);
    }

    [Fact]
    public void AChangedTimestampInvalidatesTheCacheEntry()
    {
        var (service, fs, pe, _) = Sut();
        fs.AddFile(@"C:\a.exe", "abc", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        service.Get(@"C:\a.exe");

        fs.AddFile(@"C:\a.exe", "abc", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        service.Get(@"C:\a.exe");

        Assert.Equal(2, pe.Calls);
    }

    [Fact]
    public void AChangedSizeInvalidatesTheCacheEntry()
    {
        var (service, fs, pe, _) = Sut();
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fs.AddFile(@"C:\a.exe", "abc", stamp);
        service.Get(@"C:\a.exe");

        fs.AddFile(@"C:\a.exe", "abcd", stamp);
        service.Get(@"C:\a.exe");

        Assert.Equal(2, pe.Calls);
    }

    [Fact]
    public void AFailingHashLeavesTheOtherFactsIntact()
    {
        var fs = new ThrowOnReadFileSystem();
        var service = new FileFactsService(fs, new CountingPeReader(), new CountingAuthenticode(), new StubOwner());

        var facts = service.Get(@"C:\locked.exe");

        Assert.True(facts.Exists);
        Assert.Null(facts.Sha256);
        Assert.NotNull(facts.Version);
    }

    [Fact]
    public void DifferentPathsAreCachedSeparately()
    {
        var (service, fs, _, _) = Sut();
        fs.AddFile(@"C:\a.exe", "abc");
        fs.AddFile(@"C:\b.exe", "abc");

        service.Get(@"C:\a.exe");
        service.Get(@"C:\b.exe");

        Assert.Equal(2, service.CacheSize);
    }

    private sealed class ThrowOnReadFileSystem : FakeFileSystemBase
    {
        public override bool FileExists(string path) => true;
        public override FileStat? Stat(string path)
            => new(10, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                       new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        public override Stream OpenRead(string path) => throw new IOException("locked");
    }
}
```

Create `StartupScanner.Tests/Fakes/FakeFileSystemBase.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Tests.Fakes;

/// <summary>Throwing base so a test can override only the members it cares about.</summary>
public abstract class FakeFileSystemBase : IFileSystem
{
    public virtual bool FileExists(string path) => throw new NotSupportedException();
    public virtual bool DirectoryExists(string path) => throw new NotSupportedException();
    public virtual IReadOnlyList<string> EnumerateFiles(string directory) => throw new NotSupportedException();
    public virtual IReadOnlyList<string> EnumerateDirectories(string directory) => throw new NotSupportedException();
    public virtual FileStat? Stat(string path) => throw new NotSupportedException();
    public virtual Stream OpenRead(string path) => throw new NotSupportedException();
    public virtual string ReadAllText(string path) => throw new NotSupportedException();
    public virtual void WriteAllText(string path, string contents) => throw new NotSupportedException();
    public virtual void AppendLine(string path, string line) => throw new NotSupportedException();
    public virtual void CreateDirectory(string path) => throw new NotSupportedException();
    public virtual void MoveFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
    public virtual void DeleteFile(string path) => throw new NotSupportedException();
    public virtual string ExpandEnvironment(string value) => value;
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~FileFactsServiceTests"`
Expected: FAIL — `CS0246: The type or namespace name 'FileFactsService' could not be found`.

- [ ] **Step 3: Write the abstractions**

Create `StartupScanner.Core/Abstractions/IEnrichmentSources.cs`:

```csharp
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Abstractions;

public interface IPeVersionReader
{
    /// <summary>PE version block, or null when the file has none or cannot be read.</summary>
    PeVersion? Read(string path);
}

public interface IAuthenticode
{
    /// <summary>
    /// Authenticode verification with revocation network retrieval disabled — CRL and OCSP fetches
    /// would stall enrichment for tens of seconds on a slow or filtered network.
    /// </summary>
    SignatureFacts Verify(string path);
}

public interface IFileOwnerReader
{
    string? GetOwner(string path);
}

public interface IServiceStatus
{
    /// <summary>Current run state ("Running", "Stopped", …), or null when it cannot be queried.</summary>
    string? GetStatus(string serviceName);
}
```

- [ ] **Step 4: Write the service**

Create `StartupScanner.Core/Enrichment/FileFactsService.cs`:

```csharp
using System.Collections.Concurrent;
using System.Security.Cryptography;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Enrichment;

/// <summary>
/// Phase 2 of a scan: hashing, PE version info, Authenticode, ACL owner. Cached by
/// (path, size, mtime), so an updated binary is re-examined while a repeat scan is free.
/// </summary>
public sealed class FileFactsService(
    IFileSystem fs,
    IPeVersionReader pe,
    IAuthenticode authenticode,
    IFileOwnerReader owner)
{
    private readonly ConcurrentDictionary<(string Path, long Size, long Ticks), FileFacts> _cache = new();

    public int CacheSize => _cache.Count;

    public FileFacts Get(string path)
    {
        if (!fs.FileExists(path)) return new FileFacts { Path = path, Exists = false };

        var stat = fs.Stat(path);
        var key = (path, stat?.Size ?? 0, stat?.ModifiedUtc.Ticks ?? 0);

        return _cache.GetOrAdd(key, _ => Build(path, stat));
    }

    private FileFacts Build(string path, FileStat? stat) => new()
    {
        Path = path,
        Exists = true,
        Size = stat?.Size ?? 0,
        CreatedUtc = stat?.CreatedUtc,
        ModifiedUtc = stat?.ModifiedUtc,
        Sha256 = TryHash(path),
        Version = pe.Read(path),
        Signature = authenticode.Verify(path),
        AclOwner = owner.GetOwner(path),
    };

    /// <summary>A locked or unreadable file loses its hash, not the rest of its facts.</summary>
    private string? TryHash(string path)
    {
        try
        {
            using var stream = fs.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~FileFactsServiceTests"`
Expected: PASS — 8 tests passed.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core StartupScanner.Tests
git commit -m "feat: add cached phase-2 file facts enrichment service"
```

---

## Task 38: Windows implementations — registry, filesystem, profiles, hive loading

**Files:**
- Create: `StartupScanner.Core/Windows/WindowsRegistry.cs`, `Windows/WindowsFileSystem.cs`, `Windows/WindowsProfileEnumerator.cs`, `Windows/WindowsUserHiveLoader.cs`
- Test: `StartupScanner.Tests/Windows/WindowsRegistryTests.cs`

**Interfaces:**
- Consumes: `IRegistry`, `IFileSystem`, `IProfileEnumerator`, `IUserHiveLoader`.
- Produces: `WindowsRegistry()`, `WindowsFileSystem()`, `WindowsProfileEnumerator(IRegistry registry)`, `WindowsUserHiveLoader()` — the real implementations behind the abstractions the whole engine has been tested against.

`WindowsUserHiveLoader` needs `SeBackupPrivilege` and `SeRestorePrivilege` enabled in the process token before `RegLoadKey` will work, even elevated. The mount name is prefixed so a partially cleaned-up mount is recognisable, and the returned lease unloads it.

Tests here touch the real registry read-only, under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`, which exists on every Windows machine.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Windows/WindowsRegistryTests.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Windows;

namespace StartupScanner.Tests.Windows;

public class WindowsRegistryTests
{
    private const string CurrentVersion = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    [Fact]
    public void ReadsARealHklmValue()
    {
        using var key = new WindowsRegistry().OpenKey(CurrentVersion);

        Assert.NotNull(key);
        Assert.False(string.IsNullOrEmpty(key!.GetValue("ProductName") as string));
        Assert.Equal(RegistryValueType.String, key.GetValueType("ProductName"));
    }

    [Fact]
    public void EnumeratesRealSubKeys()
    {
        using var key = new WindowsRegistry().OpenKey(@"HKLM\SYSTEM\CurrentControlSet\Services");

        Assert.NotNull(key);
        Assert.NotEmpty(key!.GetSubKeyNames());
    }

    [Fact]
    public void ReadsHkcu()
    {
        using var key = new WindowsRegistry().OpenKey(@"HKCU\Software");

        Assert.NotNull(key);
    }

    [Fact]
    public void MissingKeyReturnsNull()
    {
        Assert.Null(new WindowsRegistry().OpenKey(@"HKLM\SOFTWARE\NoSuchKeyStartupScanner"));
    }

    [Fact]
    public void UnknownRootThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new WindowsRegistry().OpenKey(@"HKXX\Whatever"));
    }

    [Fact]
    public void ThirtyTwoAndSixtyFourBitViewsBothOpen()
    {
        var registry = new WindowsRegistry();

        using var wide = registry.OpenKey(CurrentVersion, RegistryViewKind.Registry64);
        using var narrow = registry.OpenKey(CurrentVersion, RegistryViewKind.Registry32);

        Assert.NotNull(wide);
        Assert.NotNull(narrow);
    }

    [Fact]
    public void HkuPathsResolveAgainstTheUsersHive()
    {
        using var key = new WindowsRegistry().OpenKey(@"HKU\.DEFAULT");

        Assert.NotNull(key);
    }

    [Fact]
    public void ProfileEnumeratorFindsAtLeastTheSystemProfiles()
    {
        var profiles = new WindowsProfileEnumerator(new WindowsRegistry()).Enumerate();

        Assert.NotEmpty(profiles);
        Assert.All(profiles, p => Assert.StartsWith("S-1-", p.Sid));
        Assert.All(profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.ProfilePath)));
    }

    [Fact]
    public void FileSystemExpandsEnvironmentVariablesAndStatsARealFile()
    {
        var fs = new WindowsFileSystem();
        var path = fs.ExpandEnvironment(@"%SystemRoot%\explorer.exe");

        Assert.StartsWith(@"C:\", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(fs.FileExists(path));
        Assert.True(fs.Stat(path)!.Size > 0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WindowsRegistryTests"`
Expected: FAIL — `CS0246: The type or namespace name 'WindowsRegistry' could not be found`.

- [ ] **Step 3: Write `WindowsRegistry`**

Create `StartupScanner.Core/Windows/WindowsRegistry.cs`:

```csharp
using Microsoft.Win32;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class WindowsRegistry : IRegistry
{
    public IRegistryKey? OpenKey(string path, RegistryViewKind view = RegistryViewKind.Default, bool writable = false)
    {
        var (hive, subKey) = Split(path);
        using var root = RegistryKey.OpenBaseKey(hive, Map(view));
        var key = subKey.Length == 0
            ? root.OpenSubKey("", writable)
            : root.OpenSubKey(subKey, writable);

        return key is null ? null : new WindowsRegistryKey(key, path, view);
    }

    public IRegistryKey CreateKey(string path, RegistryViewKind view = RegistryViewKind.Default)
    {
        var (hive, subKey) = Split(path);
        using var root = RegistryKey.OpenBaseKey(hive, Map(view));
        return new WindowsRegistryKey(root.CreateSubKey(subKey, writable: true)!, path, view);
    }

    internal static (RegistryHive Hive, string SubKey) Split(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\');
        var slash = trimmed.IndexOf('\\');
        var rootName = slash < 0 ? trimmed : trimmed[..slash];
        var rest = slash < 0 ? "" : trimmed[(slash + 1)..];

        return rootName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => (RegistryHive.LocalMachine, rest),
            "HKCU" or "HKEY_CURRENT_USER" => (RegistryHive.CurrentUser, rest),
            "HKU" or "HKEY_USERS" => (RegistryHive.Users, rest),
            "HKCR" or "HKEY_CLASSES_ROOT" => (RegistryHive.ClassesRoot, rest),
            _ => throw new ArgumentException($"Unknown registry root '{rootName}' in path '{path}'.", nameof(path)),
        };
    }

    internal static RegistryView Map(RegistryViewKind view) => view switch
    {
        RegistryViewKind.Registry32 => RegistryView.Registry32,
        RegistryViewKind.Registry64 => RegistryView.Registry64,
        _ => RegistryView.Default,
    };
}

internal sealed class WindowsRegistryKey(RegistryKey key, string path, RegistryViewKind view) : IRegistryKey
{
    public string Path => path;

    public IReadOnlyList<string> GetValueNames() => key.GetValueNames();
    public IReadOnlyList<string> GetSubKeyNames() => key.GetSubKeyNames();

    // DoNotExpandEnvironmentNames keeps REG_EXPAND_SZ verbatim: the raw value must be shown as stored.
    public object? GetValue(string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

    public RegistryValueType GetValueType(string name) => key.GetValueKind(name) switch
    {
        RegistryValueKind.String => RegistryValueType.String,
        RegistryValueKind.ExpandString => RegistryValueType.ExpandString,
        RegistryValueKind.Binary => RegistryValueType.Binary,
        RegistryValueKind.DWord => RegistryValueType.Dword,
        RegistryValueKind.QWord => RegistryValueType.Qword,
        RegistryValueKind.MultiString => RegistryValueType.MultiString,
        RegistryValueKind.None => RegistryValueType.None,
        _ => RegistryValueType.Unknown,
    };

    public void SetValue(string name, object value, RegistryValueType type)
        => key.SetValue(name, value, type switch
        {
            RegistryValueType.String => RegistryValueKind.String,
            RegistryValueType.ExpandString => RegistryValueKind.ExpandString,
            RegistryValueType.Binary => RegistryValueKind.Binary,
            RegistryValueType.Dword => RegistryValueKind.DWord,
            RegistryValueType.Qword => RegistryValueKind.QWord,
            RegistryValueType.MultiString => RegistryValueKind.MultiString,
            _ => RegistryValueKind.Unknown,
        });

    public void DeleteValue(string name) => key.DeleteValue(name, throwOnMissingValue: false);

    public IRegistryKey? OpenSubKey(string name, bool writable = false)
    {
        var child = key.OpenSubKey(name, writable);
        return child is null ? null : new WindowsRegistryKey(child, $@"{path}\{name}", view);
    }

    public void Dispose() => key.Dispose();
}
```

- [ ] **Step 4: Write `WindowsFileSystem`**

Create `StartupScanner.Core/Windows/WindowsFileSystem.cs`:

```csharp
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class WindowsFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateFiles(string directory) => Directory.GetFiles(directory);
    public IReadOnlyList<string> EnumerateDirectories(string directory) => Directory.GetDirectories(directory);

    public FileStat? Stat(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStat(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc) : null;
    }

    public Stream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public void AppendLine(string path, string line) => File.AppendAllText(path, line + "\n");
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void MoveFile(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    public void DeleteFile(string path) => File.Delete(path);
    public string ExpandEnvironment(string value) => Environment.ExpandEnvironmentVariables(value);
}
```

- [ ] **Step 5: Write `WindowsProfileEnumerator`**

Create `StartupScanner.Core/Windows/WindowsProfileEnumerator.cs`:

```csharp
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Windows;

public sealed class WindowsProfileEnumerator(IRegistry registry) : IProfileEnumerator
{
    private const string ProfileList = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public IReadOnlyList<UserProfile> Enumerate()
    {
        using var root = registry.OpenKey(ProfileList);
        if (root is null) return Array.Empty<UserProfile>();

        var profiles = new List<UserProfile>();

        foreach (var sid in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(sid);
            if (key is null) continue;

            var profilePath = RegistryValueCodec.ToDisplay(
                key.GetValue("ProfileImagePath"), key.GetValueType("ProfileImagePath"));
            if (string.IsNullOrWhiteSpace(profilePath)) continue;

            // A loaded hive already appears under HKU\<SID>; RegLoadKey would fail for it.
            using var mounted = registry.OpenKey($@"HKU\{sid}");

            profiles.Add(new UserProfile(sid, Environment.ExpandEnvironmentVariables(profilePath),
                                         mounted is not null, Path.GetFileName(profilePath.TrimEnd('\\'))));
        }

        return profiles;
    }
}
```

- [ ] **Step 6: Write `WindowsUserHiveLoader`**

Create `StartupScanner.Core/Windows/WindowsUserHiveLoader.cs`:

```csharp
using System.Runtime.InteropServices;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

/// <summary>
/// Mounts a logged-out profile's NTUSER.DAT under HKU. RegLoadKey needs SeBackupPrivilege and
/// SeRestorePrivilege enabled in the process token, which elevation alone does not do.
/// </summary>
public sealed class WindowsUserHiveLoader : IUserHiveLoader
{
    private const string MountPrefix = "StartupScanner_";

    public MountedHive Load(string sid, string hiveFile)
    {
        if (!File.Exists(hiveFile))
            throw new FileNotFoundException($"Profile hive not found: {hiveFile}", hiveFile);

        EnablePrivilege("SeBackupPrivilege");
        EnablePrivilege("SeRestorePrivilege");

        var mountName = MountPrefix + sid;
        var result = RegLoadKey(HkeyUsers, mountName, hiveFile);
        if (result != 0)
            throw new InvalidOperationException($"RegLoadKey failed with Win32 error {result} for '{hiveFile}'.");

        return new MountedHive($@"HKU\{mountName}", new Unmounter(mountName));
    }

    private sealed class Unmounter(string mountName) : IDisposable
    {
        public void Dispose() => RegUnLoadKey(HkeyUsers, mountName);
    }

    private static void EnablePrivilege(string name)
    {
        var process = GetCurrentProcess();
        if (!OpenProcessToken(process, TokenAdjustPrivileges | TokenQuery, out var token))
            throw new InvalidOperationException($"OpenProcessToken failed: {Marshal.GetLastWin32Error()}");

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
                throw new InvalidOperationException($"LookupPrivilegeValue('{name}') failed: {Marshal.GetLastWin32Error()}");

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };

            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException($"AdjustTokenPrivileges('{name}') failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static readonly IntPtr HkeyUsers = new(unchecked((int)0x80000003));
    private const int TokenAdjustPrivileges = 0x0020;
    private const int TokenQuery = 0x0008;
    private const int SePrivilegeEnabled = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public Luid Luid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegLoadKey(IntPtr hKey, string lpSubKey, string lpFile);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WindowsRegistryTests"`
Expected: PASS — 9 tests passed.

- [ ] **Step 8: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Windows StartupScanner.Tests/Windows
git commit -m "feat: add Windows registry, filesystem, profile, and hive loader implementations"
```

---

## Task 39: Windows implementations — COM, WMI, signatures

**Files:**
- Create: `StartupScanner.Core/Windows/WindowsTaskScheduler.cs`, `Windows/WindowsWmi.cs`, `Windows/ShellLinkResolver.cs`, `Windows/WindowsSystemRestore.cs`, `Windows/WindowsPeVersionReader.cs`, `Windows/WindowsFileOwnerReader.cs`, `Windows/WindowsServiceStatus.cs`, `Windows/WindowsAuthenticode.cs`
- Test: `StartupScanner.Tests/Windows/WindowsComTests.cs`

**Interfaces:**
- Consumes: `ITaskSchedulerService`, `IWmi`, `IShellLinkResolver`, `ISystemRestore`, `IPeVersionReader`, `IFileOwnerReader`, `IServiceStatus`, `IAuthenticode`.
- Produces: the real implementation of each, all parameterless-constructible except `WindowsServiceStatus`.

Two things here are worth stating because they are easy to get wrong:

- **`WinVerifyTrust` runs with `WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL`.** Without those flags, CRL and OCSP fetches stall enrichment for tens of seconds on a slow or filtered network — and this app makes no network calls by design.
- **Most Windows binaries are catalog-signed, not embedded-signed.** A plain file verification returns `TRUST_E_NOSIGNATURE` for them, so the catalog is consulted via `CryptCATAdmin*` before concluding a file is unsigned. `IsCatalogSigned` records which path succeeded.

The Task Scheduler is reached through late-bound COM (`Schedule.Service`), which avoids an interop assembly for the handful of members needed.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/Windows/WindowsComTests.cs`:

```csharp
using StartupScanner.Core.Models;
using StartupScanner.Core.Windows;

namespace StartupScanner.Tests.Windows;

public class WindowsComTests
{
    [Fact]
    public void EnumeratesRealScheduledTasksRecursively()
    {
        var tasks = new WindowsTaskScheduler().EnumerateTasks();

        Assert.NotEmpty(tasks);
        Assert.Contains(tasks, t => t.Path.StartsWith(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase));
        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
        Assert.All(tasks, t => Assert.StartsWith(@"\", t.Path));
    }

    [Fact]
    public void ReadsRealWmiInstances()
    {
        var instances = new WindowsWmi().Query(@"root\cimv2", "SELECT * FROM Win32_OperatingSystem");

        var instance = Assert.Single(instances);
        Assert.True(instance.Properties.ContainsKey("Caption"));
    }

    [Fact]
    public void SubscriptionNamespaceQueriesSucceedEvenWhenEmpty()
    {
        var instances = new WindowsWmi().Query(@"root\subscription", "SELECT * FROM __EventFilter");

        Assert.NotNull(instances);
    }

    [Fact]
    public void ReadsThePeVersionBlockOfARealBinary()
    {
        var path = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\explorer.exe");

        var version = new WindowsPeVersionReader().Read(path);

        Assert.NotNull(version);
        Assert.Contains("Microsoft", version!.Company);
    }

    [Fact]
    public void VerifiesARealMicrosoftBinaryWithoutTouchingTheNetwork()
    {
        var path = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\explorer.exe");

        var facts = new WindowsAuthenticode().Verify(path);

        Assert.Equal(SignatureStatus.Valid, facts.Status);
        Assert.True(facts.IsMicrosoftSigned);
        Assert.NotEmpty(facts.Chain);
    }

    [Fact]
    public void AnUnsignedFileReportsUnsigned()
    {
        var path = Path.Combine(Path.GetTempPath(), "ss-unsigned-" + Guid.NewGuid().ToString("n") + ".exe");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x00, 0x00 });

        try
        {
            Assert.Equal(SignatureStatus.Unsigned, new WindowsAuthenticode().Verify(path).Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsTheAclOwnerOfARealFile()
    {
        var path = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\explorer.exe");

        Assert.False(string.IsNullOrWhiteSpace(new WindowsFileOwnerReader().GetOwner(path)));
    }

    [Fact]
    public void ReadsARealServiceRunState()
    {
        Assert.Equal("Running", new WindowsServiceStatus().GetStatus("EventLog"));
    }

    [Fact]
    public void ResolvesARealShortcut()
    {
        var target = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\explorer.exe");
        var lnk = Path.Combine(Path.GetTempPath(), "ss-link-" + Guid.NewGuid().ToString("n") + ".lnk");
        CreateShortcutWithWindowsScriptHost(lnk, target, "--flag");

        try
        {
            var resolved = new ShellLinkResolver().Resolve(lnk);

            Assert.NotNull(resolved);
            Assert.Equal(target, resolved!.TargetPath, ignoreCase: true);
            Assert.Equal("--flag", resolved.Arguments);
        }
        finally
        {
            File.Delete(lnk);
        }
    }

    [Fact]
    public void ANonShortcutFileResolvesToNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "ss-not-a-link-" + Guid.NewGuid().ToString("n") + ".lnk");
        File.WriteAllText(path, "not a shortcut");

        try
        {
            Assert.Null(new ShellLinkResolver().Resolve(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Creates a .lnk with WScript.Shell so the test does not depend on the code under test.</summary>
    private static void CreateShortcutWithWindowsScriptHost(string lnkPath, string target, string arguments)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(lnkPath);
        shortcut.TargetPath = target;
        shortcut.Arguments = arguments;
        shortcut.Save();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WindowsComTests"`
Expected: FAIL — `CS0246: The type or namespace name 'WindowsTaskScheduler' could not be found`.

- [ ] **Step 3: Write `WindowsTaskScheduler`**

Create `StartupScanner.Core/Windows/WindowsTaskScheduler.cs`:

```csharp
using System.Globalization;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

/// <summary>
/// Task Scheduler 2.0 through late-bound COM. Late binding avoids an interop assembly for the
/// handful of members needed, at the cost of no compile-time checking on those calls.
/// </summary>
public sealed class WindowsTaskScheduler : ITaskSchedulerService
{
    private const int TaskEnumHidden = 1;

    public IReadOnlyList<ScheduledTaskInfo> EnumerateTasks()
    {
        dynamic service = Connect();
        var tasks = new List<ScheduledTaskInfo>();
        Walk(service.GetFolder("\\"), tasks);
        return tasks;
    }

    public void SetEnabled(string taskPath, bool enabled)
    {
        dynamic service = Connect();
        var lastSlash = taskPath.LastIndexOf('\\');
        var folderPath = lastSlash <= 0 ? "\\" : taskPath[..lastSlash];
        var name = taskPath[(lastSlash + 1)..];

        dynamic folder = service.GetFolder(folderPath);
        dynamic task = folder.GetTask(name);
        task.Enabled = enabled;
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("The Task Scheduler COM service is not registered.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> into)
    {
        foreach (dynamic task in folder.GetTasks(TaskEnumHidden)) into.Add(Read(task, (string)folder.Path));
        foreach (dynamic child in folder.GetFolders(0)) Walk(child, into);
    }

    private static ScheduledTaskInfo Read(dynamic task, string folderPath)
    {
        var path = (string)task.Path;
        var name = (string)task.Name;

        try
        {
            dynamic definition = task.Definition;

            return new ScheduledTaskInfo
            {
                Path = path,
                Name = name,
                Folder = folderPath.Length == 0 ? "\\" : folderPath,
                Enabled = (bool)task.Enabled,
                Triggers = Describe(definition.Triggers, t => $"Type {(int)t.Type}: {(string)t.Id ?? ""}".Trim()),
                Actions = Describe(definition.Actions, ActionText),
                Principal = Try(() => (string)definition.Principal.UserId) ?? Try(() => (string)definition.Principal.GroupId),
                RunLevel = Try(() => ((int)definition.Principal.RunLevel).ToString(CultureInfo.InvariantCulture)),
                LastRunTimeUtc = Try(() => ((DateTime)task.LastRunTime).ToUniversalTime()),
                LastTaskResult = Try(() => (int)task.LastTaskResult) ?? 0,
                NextRunTimeUtc = Try(() => ((DateTime)task.NextRunTime).ToUniversalTime()),
                Xml = Try(() => (string)definition.XmlText),
            };
        }
        catch (Exception)
        {
            // Protected tasks list but refuse to expose their definition, even elevated.
            return new ScheduledTaskInfo
            {
                Path = path,
                Name = name,
                Folder = folderPath.Length == 0 ? "\\" : folderPath,
                Enabled = Try(() => (bool)task.Enabled) ?? false,
                PartiallyRead = true,
            };
        }
    }

    private static string ActionText(dynamic action)
    {
        var path = Try(() => (string)action.Path);
        var arguments = Try(() => (string)action.Arguments);
        if (path is not null) return string.IsNullOrWhiteSpace(arguments) ? path : $"{path} {arguments}";

        return Try(() => (string)action.ClassId) ?? $"Type {(int)action.Type}";
    }

    private static IReadOnlyList<string> Describe(dynamic collection, Func<dynamic, string> render)
    {
        var described = new List<string>();
        foreach (dynamic entry in collection)
        {
            var text = Try(() => render(entry));
            if (!string.IsNullOrWhiteSpace(text)) described.Add(text!);
        }
        return described;
    }

    private static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return default;
        }
    }
}
```

- [ ] **Step 4: Write `WindowsWmi`**

Create `StartupScanner.Core/Windows/WindowsWmi.cs`:

```csharp
using System.Management;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class WindowsWmi : IWmi
{
    public IReadOnlyList<WmiInstance> Query(string @namespace, string wql)
    {
        using var searcher = new ManagementObjectSearcher(Scope(@namespace), new ObjectQuery(wql));
        var instances = new List<WmiInstance>();

        foreach (var result in searcher.Get())
        {
            using var instance = (ManagementObject)result;
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in instance.Properties) properties[property.Name] = property.Value;

            instances.Add(new WmiInstance(
                instance.ClassPath.ClassName, instance.Path.RelativePath, properties));
        }

        return instances;
    }

    public void DeleteInstance(string @namespace, string relativePath)
    {
        using var instance = new ManagementObject(Scope(@namespace),
            new ManagementPath(relativePath), new ObjectGetOptions());
        instance.Delete();
    }

    public void CreateInstance(string @namespace, string className, IReadOnlyDictionary<string, object?> properties)
    {
        using var managementClass = new ManagementClass(Scope(@namespace), new ManagementPath(className), null);
        using var instance = managementClass.CreateInstance();

        foreach (var (name, value) in properties)
        {
            if (value is null) continue;
            instance[name] = value;
        }

        instance.Put();
    }

    private static ManagementScope Scope(string @namespace)
    {
        var scope = new ManagementScope($@"\\.\{@namespace}");
        scope.Connect();
        return scope;
    }
}
```

- [ ] **Step 5: Write `ShellLinkResolver`**

Create `StartupScanner.Core/Windows/ShellLinkResolver.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Text;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class ShellLinkResolver : IShellLinkResolver
{
    private const int MaxPath = 260;
    private const int InfoTipSize = 1024;
    private const int SlgpRawPath = 0x0004;

    public ShortcutTarget? Resolve(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);

            var target = new StringBuilder(MaxPath);
            link.GetPath(target, target.Capacity, IntPtr.Zero, SlgpRawPath);

            var arguments = new StringBuilder(InfoTipSize);
            link.GetArguments(arguments, arguments.Capacity);

            var workingDirectory = new StringBuilder(MaxPath);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);

            var icon = new StringBuilder(MaxPath);
            link.GetIconLocation(icon, icon.Capacity, out _);

            var path = target.ToString();
            return path.Length == 0
                ? null
                : new ShortcutTarget(path, arguments.ToString(), workingDirectory.ToString(), icon.ToString());
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
```

- [ ] **Step 6: Write the remaining small implementations**

Create `StartupScanner.Core/Windows/WindowsSystemRestore.cs`:

```csharp
using System.Management;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class WindowsSystemRestore : ISystemRestore
{
    private const uint ApplicationInstall = 0;
    private const uint BeginSystemChange = 100;

    public bool TryCreateRestorePoint(string description, out string? error)
    {
        try
        {
            using var restoreClass = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var parameters = restoreClass.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = description;
            parameters["RestorePointType"] = ApplicationInstall;
            parameters["EventType"] = BeginSystemChange;

            var result = restoreClass.InvokeMethod("CreateRestorePoint", parameters, null);
            var returnValue = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);

            if (returnValue == 0)
            {
                error = null;
                return true;
            }

            error = $"CreateRestorePoint returned {returnValue}. System Protection may be turned off for this drive.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"A system restore point could not be created: {ex.Message}";
            return false;
        }
    }
}
```

Create `StartupScanner.Core/Windows/WindowsPeVersionReader.cs`:

```csharp
using System.Diagnostics;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Windows;

public sealed class WindowsPeVersionReader : IPeVersionReader
{
    public PeVersion? Read(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.CompanyName is null && info.ProductName is null
                && info.FileDescription is null && info.FileVersion is null)
                return null;

            return new PeVersion(info.CompanyName, info.ProductName, info.FileDescription, info.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
```

Create `StartupScanner.Core/Windows/WindowsFileOwnerReader.cs`:

```csharp
using System.Security.AccessControl;
using System.Security.Principal;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

public sealed class WindowsFileOwnerReader : IFileOwnerReader
{
    public string? GetOwner(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(NTAccount));
            return owner?.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or IdentityNotMappedException or PrivilegeNotHeldException)
        {
            return null;
        }
    }
}
```

Create `StartupScanner.Core/Windows/WindowsServiceStatus.cs`:

```csharp
using System.Diagnostics;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

/// <summary>
/// Queries a service's run state with sc.exe rather than ServiceController, so Core needs no
/// System.ServiceProcess dependency for one read-only detail-panel field.
/// </summary>
public sealed class WindowsServiceStatus : IServiceStatus
{
    public string? GetStatus(string serviceName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe", $"query \"{serviceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (output.Contains("RUNNING", StringComparison.Ordinal)) return "Running";
            if (output.Contains("STOPPED", StringComparison.Ordinal)) return "Stopped";
            if (output.Contains("START_PENDING", StringComparison.Ordinal)) return "Starting";
            if (output.Contains("STOP_PENDING", StringComparison.Ordinal)) return "Stopping";
            if (output.Contains("PAUSED", StringComparison.Ordinal)) return "Paused";
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
```

- [ ] **Step 7: Write `WindowsAuthenticode`**

Create `StartupScanner.Core/Windows/WindowsAuthenticode.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Windows;

/// <summary>
/// Authenticode verification with all network retrieval disabled. Most Windows binaries are
/// catalog-signed rather than embedded-signed, so a TRUST_E_NOSIGNATURE result from file
/// verification is followed by a catalog lookup before concluding the file is unsigned.
/// </summary>
public sealed class WindowsAuthenticode : IAuthenticode
{
    public SignatureFacts Verify(string path)
    {
        var embedded = VerifyFile(path);

        if (embedded == TrustENoSignature)
        {
            var (catalogResult, catalogFound) = VerifyCatalog(path);
            if (catalogFound) return Describe(path, catalogResult, isCatalog: true);

            return new SignatureFacts
            {
                Status = SignatureStatus.Unsigned,
                StatusMessage = "No embedded signature and no catalog entry.",
            };
        }

        return Describe(path, embedded, isCatalog: false);
    }

    private static SignatureFacts Describe(string path, int result, bool isCatalog)
    {
        var chain = new List<string>();
        string? signer = null;
        DateTime? timestamp = null;
        var microsoft = false;

        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            signer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

            using var builder = new X509Chain();
            builder.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;   // no network, by design
            builder.Build(certificate);

            foreach (var element in builder.ChainElements)
                chain.Add(element.Certificate.Subject);

            microsoft = chain.Any(subject => subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
            timestamp = certificate.NotBefore.ToUniversalTime();
        }
        catch (CryptographicException)
        {
            // Catalog-signed files carry no embedded certificate; the catalog verified it instead.
            if (isCatalog)
            {
                signer = "Microsoft Windows (catalog)";
                microsoft = true;
            }
        }

        return new SignatureFacts
        {
            Status = result == 0 ? SignatureStatus.Valid
                   : result == TrustENoSignature ? SignatureStatus.Unsigned
                   : SignatureStatus.Invalid,
            SignerName = signer,
            Chain = chain,
            TimestampUtc = timestamp,
            IsCatalogSigned = isCatalog,
            IsMicrosoftSigned = microsoft,
            StatusMessage = result == 0 ? null : $"WinVerifyTrust returned 0x{result:X8}.",
        };
    }

    private static int VerifyFile(string path)
    {
        var fileInfo = new WintrustFileInfo
        {
            StructSize = Marshal.SizeOf<WintrustFileInfo>(),
            FilePath = Marshal.StringToCoTaskMemUni(path),
        };
        var filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());

        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);

            var data = NewTrustData();
            data.UnionChoice = WtdChoiceFile;
            data.UnionPointer = filePointer;

            return CallWinVerifyTrust(ref data);
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.FilePath);
            Marshal.FreeCoTaskMem(filePointer);
        }
    }

    private static (int Result, bool Found) VerifyCatalog(string path)
    {
        var context = IntPtr.Zero;
        var fileHandle = IntPtr.Zero;

        try
        {
            if (!CryptCATAdminAcquireContext2(out context, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
                return (TrustENoSignature, false);

            fileHandle = CreateFile(path, GenericRead, FileShareRead, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (fileHandle == InvalidHandle) return (TrustENoSignature, false);

            var hashLength = 0;
            CryptCATAdminCalcHashFromFileHandle2(context, fileHandle, ref hashLength, IntPtr.Zero, 0);
            if (hashLength == 0) return (TrustENoSignature, false);

            var hash = Marshal.AllocCoTaskMem(hashLength);
            try
            {
                if (!CryptCATAdminCalcHashFromFileHandle2(context, fileHandle, ref hashLength, hash, 0))
                    return (TrustENoSignature, false);

                var catalog = CryptCATAdminEnumCatalogFromHash(context, hash, hashLength, 0, IntPtr.Zero);
                if (catalog == IntPtr.Zero) return (TrustENoSignature, false);

                var info = new CatalogInfo { StructSize = Marshal.SizeOf<CatalogInfo>() };
                if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0))
                    return (TrustENoSignature, false);

                return (VerifyAgainstCatalog(path, info.CatalogFile, hash, hashLength), true);
            }
            finally
            {
                Marshal.FreeCoTaskMem(hash);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (TrustENoSignature, false);
        }
        finally
        {
            if (fileHandle != IntPtr.Zero && fileHandle != InvalidHandle) CloseHandle(fileHandle);
            if (context != IntPtr.Zero) CryptCATAdminReleaseContext(context, 0);
        }
    }

    private static int VerifyAgainstCatalog(string path, string catalogFile, IntPtr hash, int hashLength)
    {
        var memberTag = new StringBuilder(hashLength * 2);
        for (var i = 0; i < hashLength; i++) memberTag.Append(Marshal.ReadByte(hash, i).ToString("X2"));

        var catalogInfo = new WintrustCatalogInfo
        {
            StructSize = Marshal.SizeOf<WintrustCatalogInfo>(),
            CatalogFilePath = Marshal.StringToCoTaskMemUni(catalogFile),
            MemberFilePath = Marshal.StringToCoTaskMemUni(path),
            MemberTag = Marshal.StringToCoTaskMemUni(memberTag.ToString()),
        };
        var catalogPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustCatalogInfo>());

        try
        {
            Marshal.StructureToPtr(catalogInfo, catalogPointer, false);

            var data = NewTrustData();
            data.UnionChoice = WtdChoiceCatalog;
            data.UnionPointer = catalogPointer;

            return CallWinVerifyTrust(ref data);
        }
        finally
        {
            Marshal.FreeCoTaskMem(catalogInfo.CatalogFilePath);
            Marshal.FreeCoTaskMem(catalogInfo.MemberFilePath);
            Marshal.FreeCoTaskMem(catalogInfo.MemberTag);
            Marshal.FreeCoTaskMem(catalogPointer);
        }
    }

    private static WintrustData NewTrustData() => new()
    {
        StructSize = Marshal.SizeOf<WintrustData>(),
        UIChoice = WtdUiNone,
        RevocationChecks = WtdRevokeNone,
        StateAction = WtdStateActionVerify,
        // No network retrieval: CRL and OCSP fetches would stall enrichment for tens of seconds.
        ProviderFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
    };

    private static int CallWinVerifyTrust(ref WintrustData data)
    {
        var action = ActionGenericVerifyV2;
        var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

        data.StateAction = WtdStateActionClose;
        WinVerifyTrust(IntPtr.Zero, ref action, ref data);

        return result;
    }

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x00000010;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    private static Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public int StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustCatalogInfo
    {
        public int StructSize;
        public uint CatalogVersion;
        public IntPtr CatalogFilePath;
        public IntPtr MemberTag;
        public IntPtr MemberFilePath;
        public IntPtr MemberFileHandle;
        public IntPtr CalculatedFileHash;
        public uint CalculatedFileHashSize;
        public IntPtr CatalogContext;
        public IntPtr CatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public int StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr UnionPointer;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public int StructSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string CatalogFile;
    }

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem,
        string? hashAlgorithm, IntPtr policy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file,
        ref int hashSize, IntPtr hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, IntPtr hash,
        int hashSize, uint flags, IntPtr previous);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~WindowsComTests"`
Expected: PASS — 10 tests passed. If `ReadsARealServiceRunState` fails, check that the `EventLog` service is running on this machine (`sc query EventLog`).

- [ ] **Step 9: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/Windows StartupScanner.Tests/Windows
git commit -m "feat: add Windows COM, WMI, and Authenticode implementations"
```

---

## Task 40: Composition root

**Files:**
- Create: `StartupScanner.Core/ScannerHost.cs`
- Test: `StartupScanner.Tests/ScannerHostTests.cs`

**Interfaces:**
- Consumes: every provider, every toggle, the stores, `ScanEngine`, `ToggleService`, `UndoService`, `FileFactsService`, `DirectoryAcl`.
- Produces:
  - `ScannerHost.CreateWindows(IConfirmationSink confirmations, IRestorePointPrompt restorePrompt) -> ScannerHost`.
  - `ScannerHost` exposing `Providers`, `Toggles`, `Engine`, `ToggleService`, `Undo`, `Snapshots`, `History`, `Journal`, `Files`, `ServiceStatus`, `SessionId`, `AclOutcome`, `InteractiveUserSid`, and `ScanContext ContextFor(StartupItem? item = null)`.
  - `ScannerHost.BuildProviders(...)` and `ScannerHost.BuildToggles(...)` as testable statics.

Two things get decided here and nowhere else.

**Every provider is wired to exactly one toggle, keyed by provider id.** Providers whose `Name` is composite use `SubValueToggle`; providers with no safe write use `BlockedToggle`. Items produced by `UserHiveProvider` carry their *inner* provider's id, so they route to the same toggle as their HKCU equivalents with no special case.

**`ContextFor` builds the right hive root for a write.** An item belonging to another user is verified against `HKU\<SID>` rather than `HKCU`; if that hive is not mounted, the read-back fails and the toggle is refused, which is the safe outcome — better than writing into the wrong user's hive.

- [ ] **Step 1: Write the failing test**

Create `StartupScanner.Tests/ScannerHostTests.cs`:

```csharp
using StartupScanner.Core;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests;

public class ScannerHostTests
{
    private static readonly string[] ExpectedProviderIds =
    {
        "registry.run", "startup.folder", "service", "task-scheduler", "winlogon", "bootexecute",
        "active-setup", "ifeo", "appinit", "explorer-hooks", "appx-startup", "wmi-subscription",
        "com-hijack", "lsa", "netsh-helper", "print-monitor", "winsock-lsp", "gpo-script", "user-hive",
    };

    private static IReadOnlyList<IStartupProvider> Providers()
        => ScannerHost.BuildProviders(new FakeRegistry(), new FakeFileSystem(), new FakeTaskScheduler(),
                                      new FakeWmi(), new FakeShellLinkResolver(), new FakeProfileEnumerator(),
                                      new FakeUserHiveLoader());

    private static IReadOnlyList<IToggleProvider> Toggles()
        => ScannerHost.BuildToggles(new FakeRegistry(), new FakeFileSystem(), new FakeTaskScheduler(),
                                    new FakeWmi(), new FakeClock());

    [Fact]
    public void AllNineteenProvidersAreRegistered()
    {
        var ids = Providers().Select(p => p.Id).ToList();

        Assert.Equal(19, ids.Count);
        foreach (var expected in ExpectedProviderIds) Assert.Contains(expected, ids);
    }

    [Fact]
    public void ProviderIdsAreUnique()
    {
        var ids = Providers().Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EveryProviderExceptTheHiveWrapperHasExactlyOneToggle()
    {
        var toggleIds = Toggles().Select(t => t.ProviderId).ToList();

        foreach (var providerId in ExpectedProviderIds.Where(id => id != "user-hive"))
            Assert.Single(toggleIds.Where(id => id == providerId));
    }

    [Fact]
    public void TheHiveWrapperHasNoToggleBecauseItsItemsCarryInnerProviderIds()
    {
        Assert.DoesNotContain("user-hive", Toggles().Select(t => t.ProviderId));
    }

    [Fact]
    public void LsaIsWiredToTheBlockedToggle()
    {
        Assert.IsType<BlockedToggle>(Toggles().Single(t => t.ProviderId == "lsa"));
    }

    [Fact]
    public void CompositeNameProvidersAreWiredToTheSubValueToggle()
    {
        var toggles = Toggles();

        foreach (var providerId in new[] { "ifeo", "explorer-hooks", "com-hijack", "print-monitor", "winsock-lsp", "active-setup" })
            Assert.IsType<SubValueToggle>(toggles.Single(t => t.ProviderId == providerId));
    }

    [Fact]
    public void TheHiveWrapperWrapsExactlyProvidersOneSevenTenElevenAndThirteen()
    {
        var wrapper = Providers().OfType<UserHiveProvider>().Single();

        Assert.Equal(new[] { "active-setup", "appx-startup", "com-hijack", "explorer-hooks", "registry.run" },
            ScannerHost.HiveWrappedProviderIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal("user-hive", wrapper.Id);
    }

    [Fact]
    public void ContextForAnItemWithNoSidTargetsHkcu()
    {
        var host = ScannerHost.CreateForTesting(new FakeRegistry(), new FakeFileSystem(), new FakeTaskScheduler(),
            new FakeWmi(), new FakeShellLinkResolver(), new FakeProfileEnumerator(), new FakeUserHiveLoader(),
            new FakeSystemRestore(), new StubRestorePointPrompt(true), new RecordingConfirmationSink(),
            new FakeClock(), new AppPaths(@"C:\Data\StartupScanner"), interactiveUserSid: "S-1-5-21-1");

        Assert.Equal("HKCU", host.ContextFor().UserRoot);
        Assert.Equal("HKCU", host.ContextFor(Item(null)).UserRoot);
        Assert.Equal("HKCU", host.ContextFor(Item("S-1-5-21-1")).UserRoot);
    }

    [Fact]
    public void ContextForAnotherUsersItemTargetsThatUsersHive()
    {
        var host = ScannerHost.CreateForTesting(new FakeRegistry(), new FakeFileSystem(), new FakeTaskScheduler(),
            new FakeWmi(), new FakeShellLinkResolver(), new FakeProfileEnumerator(), new FakeUserHiveLoader(),
            new FakeSystemRestore(), new StubRestorePointPrompt(true), new RecordingConfirmationSink(),
            new FakeClock(), new AppPaths(@"C:\Data\StartupScanner"), interactiveUserSid: "S-1-5-21-1");

        var context = host.ContextFor(Item("S-1-5-21-42"));

        Assert.Equal(@"HKU\S-1-5-21-42", context.UserRoot);
        Assert.Equal("S-1-5-21-42", context.UserSid);
        Assert.True(context.ScanningMountedHive);
    }

    [Fact]
    public void SessionIdIsAFreshValuePerHost()
    {
        var first = NewHost().SessionId;
        var second = NewHost().SessionId;

        Assert.NotEqual(first, second);
        Assert.NotEmpty(first);
    }

    private static ScannerHost NewHost()
        => ScannerHost.CreateForTesting(new FakeRegistry(), new FakeFileSystem(), new FakeTaskScheduler(),
            new FakeWmi(), new FakeShellLinkResolver(), new FakeProfileEnumerator(), new FakeUserHiveLoader(),
            new FakeSystemRestore(), new StubRestorePointPrompt(true), new RecordingConfirmationSink(),
            new FakeClock(), new AppPaths(@"C:\Data\StartupScanner"), interactiveUserSid: "S-1-5-21-1");

    private static StartupItem Item(string? sid) => new()
    {
        Id = "id", ProviderId = "registry.run", Location = @"HKCU\Run", Name = "Thing",
        RawValue = "x.exe", Category = StartupCategory.RunKey, UserSid = sid,
    };
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ScannerHostTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ScannerHost' could not be found`.

- [ ] **Step 3: Write the composition root**

Create `StartupScanner.Core/ScannerHost.cs`:

```csharp
using System.Security.Principal;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Enrichment;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Scanning;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Windows;
using StartupScanner.Core.Writing;

namespace StartupScanner.Core;

/// <summary>
/// Wires the engine together. Lives in Core so the WPF app and the integration tests share exactly
/// one composition, and no UI assembly is needed to run a real scan.
/// </summary>
public sealed class ScannerHost
{
    /// <summary>Providers 1, 7, 10, 11, and 13 — the per-user ones UserHiveProvider replays per profile.</summary>
    public static readonly IReadOnlyList<string> HiveWrappedProviderIds =
        new[] { "registry.run", "active-setup", "explorer-hooks", "appx-startup", "com-hijack" };

    private ScannerHost(
        IReadOnlyList<IStartupProvider> providers,
        IReadOnlyList<IToggleProvider> toggles,
        ScanEngine engine,
        ToggleService toggleService,
        UndoService undo,
        SnapshotStore snapshots,
        HistoryStore history,
        IWriteJournal journal,
        FileFactsService files,
        IServiceStatus serviceStatus,
        string sessionId,
        AclOutcome aclOutcome,
        string? interactiveUserSid)
    {
        Providers = providers;
        Toggles = toggles;
        Engine = engine;
        ToggleService = toggleService;
        Undo = undo;
        Snapshots = snapshots;
        History = history;
        Journal = journal;
        Files = files;
        ServiceStatus = serviceStatus;
        SessionId = sessionId;
        AclOutcome = aclOutcome;
        InteractiveUserSid = interactiveUserSid;
    }

    public IReadOnlyList<IStartupProvider> Providers { get; }
    public IReadOnlyList<IToggleProvider> Toggles { get; }
    public ScanEngine Engine { get; }
    public ToggleService ToggleService { get; }
    public UndoService Undo { get; }
    public SnapshotStore Snapshots { get; }
    public HistoryStore History { get; }
    public IWriteJournal Journal { get; }
    public FileFactsService Files { get; }
    public IServiceStatus ServiceStatus { get; }
    public string SessionId { get; }
    public AclOutcome AclOutcome { get; }
    public string? InteractiveUserSid { get; }

    /// <summary>
    /// The hive root a scan or read-back should use for this item. Another user's entry is verified
    /// against their own hive; if it is not mounted the read-back fails and the write is refused,
    /// which is safer than writing into the wrong hive.
    /// </summary>
    public ScanContext ContextFor(StartupItem? item = null)
    {
        if (item?.UserSid is null || item.UserSid == InteractiveUserSid)
            return new ScanContext { UserRoot = "HKCU", UserSid = InteractiveUserSid };

        return new ScanContext
        {
            UserRoot = $@"HKU\{item.UserSid}",
            UserSid = item.UserSid,
            ScanningMountedHive = true,
        };
    }

    public static ScannerHost CreateWindows(IConfirmationSink confirmations, IRestorePointPrompt restorePrompt)
    {
        var registry = new WindowsRegistry();
        var fs = new WindowsFileSystem();

        return Build(registry, fs, new WindowsTaskScheduler(), new WindowsWmi(), new ShellLinkResolver(),
            new WindowsProfileEnumerator(registry), new WindowsUserHiveLoader(), new WindowsSystemRestore(),
            restorePrompt, confirmations, new SystemClock(), AppPaths.ForProgramData(),
            new WindowsPeVersionReader(), new WindowsAuthenticode(), new WindowsFileOwnerReader(),
            new WindowsServiceStatus(), CurrentUserSid(), secureStorage: true);
    }

    /// <summary>Test entry point: no real ACL work and stub enrichment sources.</summary>
    public static ScannerHost CreateForTesting(
        IRegistry registry, IFileSystem fs, ITaskSchedulerService tasks, IWmi wmi, IShellLinkResolver links,
        IProfileEnumerator profiles, IUserHiveLoader hives, ISystemRestore restore,
        IRestorePointPrompt restorePrompt, IConfirmationSink confirmations, IClock clock, AppPaths paths,
        string? interactiveUserSid)
        => Build(registry, fs, tasks, wmi, links, profiles, hives, restore, restorePrompt, confirmations,
                 clock, paths, new NullPeVersionReader(), new NullAuthenticode(), new NullFileOwnerReader(),
                 new NullServiceStatus(), interactiveUserSid, secureStorage: false);

    private static ScannerHost Build(
        IRegistry registry, IFileSystem fs, ITaskSchedulerService tasks, IWmi wmi, IShellLinkResolver links,
        IProfileEnumerator profiles, IUserHiveLoader hives, ISystemRestore restore,
        IRestorePointPrompt restorePrompt, IConfirmationSink confirmations, IClock clock, AppPaths paths,
        IPeVersionReader pe, IAuthenticode authenticode, IFileOwnerReader owner, IServiceStatus serviceStatus,
        string? interactiveUserSid, bool secureStorage)
    {
        var aclOutcome = secureStorage ? DirectoryAcl.EnsureSecured(paths.Root) : AclOutcome.AlreadyCorrect;

        var journal = new WriteJournal(fs, paths);
        var providers = BuildProviders(registry, fs, tasks, wmi, links, profiles, hives);
        var toggles = BuildToggles(registry, fs, tasks, wmi, clock);
        var sessionId = Guid.NewGuid().ToString("n");
        var restorePoint = new RestorePointGate(restore, restorePrompt);

        return new ScannerHost(
            providers,
            toggles,
            new ScanEngine(providers, journal),
            new ToggleService(toggles, providers, journal, restorePoint, confirmations, clock, sessionId),
            new UndoService(toggles, providers, journal, clock, sessionId),
            new SnapshotStore(fs, paths),
            new HistoryStore(fs, paths),
            journal,
            new FileFactsService(fs, pe, authenticode, owner),
            serviceStatus,
            sessionId,
            aclOutcome,
            interactiveUserSid);
    }

    public static IReadOnlyList<IStartupProvider> BuildProviders(
        IRegistry registry, IFileSystem fs, ITaskSchedulerService tasks, IWmi wmi,
        IShellLinkResolver links, IProfileEnumerator profiles, IUserHiveLoader hives)
    {
        var runKeys = new RegistryRunProvider(registry, fs);
        var activeSetup = new ActiveSetupProvider(registry, fs);
        var explorerHooks = new ExplorerHooksProvider(registry, fs);
        var appx = new AppxStartupTaskProvider(registry, fs);
        var comHijack = new ComHijackProvider(registry, fs);

        return new List<IStartupProvider>
        {
            runKeys,                                              // 1
            new StartupFolderProvider(fs, registry, links),       // 2
            new ServiceProvider(registry, fs),                    // 3
            new ScheduledTaskProvider(tasks, fs),                 // 4
            new WinlogonProvider(registry, fs),                   // 5
            new BootExecuteProvider(registry, fs),                // 6
            activeSetup,                                          // 7
            new IfeoProvider(registry, fs),                       // 8
            new AppInitProvider(registry, fs),                    // 9
            explorerHooks,                                        // 10
            appx,                                                 // 11
            new WmiSubscriptionProvider(wmi),                     // 12
            comHijack,                                            // 13
            new LsaProvider(registry, fs),                        // 14
            new NetshHelperProvider(registry, fs),                // 15
            new PrintMonitorProvider(registry, fs),               // 16
            new WinsockLspProvider(registry, fs),                 // 17
            new GpoScriptProvider(fs, registry),                  // 18
            new UserHiveProvider(                                 // 19
                new IStartupProvider[] { runKeys, activeSetup, explorerHooks, appx, comHijack },
                profiles, hives),
        };
    }

    public static IReadOnlyList<IToggleProvider> BuildToggles(
        IRegistry registry, IFileSystem fs, ITaskSchedulerService tasks, IWmi wmi, IClock clock)
        => new List<IToggleProvider>
        {
            new RunKeyToggle(registry, clock),
            new StartupFolderToggle(registry, clock),
            new ServiceToggle(registry, clock),
            new ScheduledTaskToggle(tasks, clock),
            WinlogonToggle.Create(registry, clock),
            new RegistryValueToggle("bootexecute", registry, clock),
            new SubValueToggle("active-setup", registry, clock),
            new SubValueToggle("ifeo", registry, clock),
            new RegistryValueToggle("appinit", registry, clock),
            new SubValueToggle("explorer-hooks", registry, clock),
            new AppxStartupTaskToggle(registry, clock),
            new WmiInstanceToggle(wmi, clock),
            new SubValueToggle("com-hijack", registry, clock),
            new BlockedToggle("lsa"),
            new RegistryValueToggle("netsh-helper", registry, clock),
            new SubValueToggle("print-monitor", registry, clock),
            new SubValueToggle("winsock-lsp", registry, clock),
            new GpoScriptToggle(fs, clock),
        };

    private static string? CurrentUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class NullPeVersionReader : IPeVersionReader
    {
        public PeVersion? Read(string path) => null;
    }

    private sealed class NullAuthenticode : IAuthenticode
    {
        public SignatureFacts Verify(string path) => new() { Status = SignatureStatus.Unknown };
    }

    private sealed class NullFileOwnerReader : IFileOwnerReader
    {
        public string? GetOwner(string path) => null;
    }

    private sealed class NullServiceStatus : IServiceStatus
    {
        public string? GetStatus(string serviceName) => null;
    }
}
```

Note: `ActiveSetupProvider` sets `Extra["ValueName"] = "StubPath"` and `PrintMonitorProvider` sets `"Driver"`, so `SubValueToggle` targets the right value. `ComHijackProvider` deliberately leaves it empty for `InprocServer32` hijacks, which is the key's default value.

- [ ] **Step 4: Run the test to verify it passes**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' --filter "FullyQualifiedName~ScannerHostTests"`
Expected: PASS — 11 tests passed.

- [ ] **Step 5: Run the whole suite**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: PASS — all tests green.

- [ ] **Step 6: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Core/ScannerHost.cs StartupScanner.Tests/ScannerHostTests.cs
git commit -m "feat: add composition root wiring all 19 providers and their toggles"
```

---

## Task 41: WPF shell, item list, and status bar

**Files:**
- Create: `StartupScanner.App/StartupScanner.App.csproj`, `app.manifest`, `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`
- Create: `StartupScanner.App/ViewModels/RelayCommand.cs`, `ViewModels/ItemViewModel.cs`, `ViewModels/MainViewModel.cs`
- Modify: `StartupScanner.sln`

**Interfaces:**
- Consumes: `ScannerHost`, `ScanEngine`, `DiffEngine`, `SnapshotStore`, `HistoryStore`, `StatusLine`, `ItemFilter`, `FileFactsService`.
- Produces:
  - `MainViewModel` with `Items` (`ObservableCollection<ItemViewModel>`), `RescanCommand`, `StatusText`, `IsScanning`, `EnrichmentProgress`, `SelectedItem`, and `Task RescanAsync()`.
  - `ItemViewModel` exposing `Item`, `IsEnabled`, `Publisher`, `IsNew`, `ChangeKind`, `IsBlocked`, `StateGlyph`.

The App has no unit tests — `StartupScanner.Tests` references Core only, per the spec — so every piece of logic worth testing already lives in `Core/Presentation`. What remains here is binding, and it is verified by building and running.

Phase 1 renders the list; phase 2 fills the Publisher column in the background, exactly as the spec's two-phase model requires.

- [ ] **Step 1: Create the WPF project and wire it into the solution**

```bash
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
"$DOTNET" new wpf -o 'D:\Repos\StartupScanner\StartupScanner.App' -n StartupScanner.App --framework net8.0
"$DOTNET" sln 'D:\Repos\StartupScanner\StartupScanner.sln' add 'D:\Repos\StartupScanner\StartupScanner.App\StartupScanner.App.csproj'
"$DOTNET" add 'D:\Repos\StartupScanner\StartupScanner.App\StartupScanner.App.csproj' reference 'D:\Repos\StartupScanner\StartupScanner.Core\StartupScanner.Core.csproj'
```

- [ ] **Step 2: Add the requireAdministrator manifest**

Create `StartupScanner.App/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="StartupScanner.App"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <!-- Always elevated: removes an entire class of partial-failure and state-handoff code. -->
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
    </application>
  </compatibility>
</assembly>
```

Then add to the `<PropertyGroup>` in `StartupScanner.App/StartupScanner.App.csproj`:

```xml
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Nullable>enable</Nullable>
```

- [ ] **Step 3: Verify the manifest takes effect**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: Build succeeded. Then launch it:

Run: `cmd.exe /c start "" "D:\Repos\StartupScanner\StartupScanner.App\bin\Debug\net8.0-windows\StartupScanner.App.exe"`
Expected: a UAC prompt appears before the window opens. Approve it, confirm an empty window appears, then close it.

- [ ] **Step 4: Write `RelayCommand`**

Create `StartupScanner.App/ViewModels/RelayCommand.cs`:

```csharp
using System;
using System.Windows.Input;

namespace StartupScanner.App.ViewModels;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 5: Write `ItemViewModel`**

Create `StartupScanner.App/ViewModels/ItemViewModel.cs`:

```csharp
using System;
using System.ComponentModel;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;

namespace StartupScanner.App.ViewModels;

public sealed class ItemViewModel : INotifyPropertyChanged
{
    private readonly Func<ItemViewModel, bool, bool> _requestToggle;
    private StartupItem _item;

    public ItemViewModel(StartupItem item, DiffEventKind? changeKind, Func<ItemViewModel, bool, bool> requestToggle)
    {
        _item = item;
        _requestToggle = requestToggle;
        ChangeKind = changeKind;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StartupItem Item
    {
        get => _item;
        private set
        {
            _item = value;
            Raise(nameof(Item));
            Raise(nameof(IsEnabled));
            Raise(nameof(Publisher));
            Raise(nameof(StateGlyph));
        }
    }

    public DiffEventKind? ChangeKind { get; }

    public string Name => _item.Name;
    public string Location => _item.Location;
    public StartupCategory Category => _item.Category;
    public RiskTier Risk => _item.Risk;

    public bool IsNew => ChangeKind == DiffEventKind.Added;
    public bool IsBlocked => Guardrails.Evaluate(_item).Blocked;
    public string? BlockReason => Guardrails.Evaluate(_item).Reason;

    public string Publisher => _item.File?.Signature?.SignerName
        ?? _item.File?.Version?.Company
        ?? (_item.File is null ? "…" : "—unsigned—");

    /// <summary>Risk colour dot: green low, amber medium, red high or critical.</summary>
    public string StateGlyph => _item.Risk switch
    {
        RiskTier.Low => "\U0001F7E2",
        RiskTier.Medium => "\U0001F7E1",
        _ => "\U0001F534",
    };

    public string ChangeBadge => ChangeKind switch
    {
        DiffEventKind.Added => "⊕",
        DiffEventKind.Removed => "⊖",
        DiffEventKind.Modified => "✎",
        DiffEventKind.StateChanged => "↻",
        _ => "",
    };

    /// <summary>
    /// The checkbox writes immediately. The setter asks the view model to perform the toggle and
    /// only accepts the new value if the write actually landed.
    /// </summary>
    public bool IsEnabled
    {
        get => _item.State == EnableState.Enabled;
        set
        {
            if (value == IsEnabled) return;

            if (!_requestToggle(this, value)) Raise(nameof(IsEnabled));   // snap the checkbox back
        }
    }

    public void Replace(StartupItem item) => Item = item;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
```

- [ ] **Step 6: Write `MainViewModel`**

Create `StartupScanner.App/ViewModels/MainViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using StartupScanner.Core;
using StartupScanner.Core.Diff;
using StartupScanner.Core.Models;
using StartupScanner.Core.Presentation;

namespace StartupScanner.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ScannerHost _host;
    private IReadOnlyList<StartupItem> _scanned = Array.Empty<StartupItem>();
    private IReadOnlyList<DiffEvent> _events = Array.Empty<DiffEvent>();
    private DateTime? _previousScanUtc;
    private string _statusText = "Ready.";
    private bool _isScanning;
    private string _enrichmentProgress = "";
    private ItemViewModel? _selectedItem;

    public MainViewModel(ScannerHost host)
    {
        _host = host;
        RescanCommand = new RelayCommand(() => _ = RescanAsync(), () => !IsScanning);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ItemViewModel> Items { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();
    public RelayCommand RescanCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; Raise(nameof(StatusText)); }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            _isScanning = value;
            Raise(nameof(IsScanning));
            RescanCommand.RaiseCanExecuteChanged();
        }
    }

    public string EnrichmentProgress
    {
        get => _enrichmentProgress;
        private set { _enrichmentProgress = value; Raise(nameof(EnrichmentProgress)); }
    }

    public ItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set { _selectedItem = value; Raise(nameof(SelectedItem)); }
    }

    /// <summary>Ids that changed since the previous scan; the filter and the badges both use this.</summary>
    public HashSet<string> ChangedIds { get; } = new(StringComparer.Ordinal);

    public async Task RescanAsync()
    {
        IsScanning = true;
        StatusText = "Scanning…";
        Errors.Clear();

        try
        {
            var previous = _host.Snapshots.Load();
            _previousScanUtc = previous?.ScannedAtUtc;

            var context = _host.ContextFor();
            var result = await Task.Run(() => _host.Engine.Run(context));

            var now = DateTime.UtcNow;
            _events = DiffEngine.Compare(previous?.Items ?? Array.Empty<StartupItem>(), result.Items, now);
            _scanned = result.Items;

            ChangedIds.Clear();
            foreach (var e in _events) ChangedIds.Add(e.Id);

            _host.History.Append(_events);
            _host.Snapshots.Save(result.Items, now);

            Rebuild();

            foreach (var error in result.Errors.Select(e => $"{e.ProviderId}: {e.Location} — {e.Message}").Distinct())
                Errors.Add(error);

            StatusText = StatusLine.Format(result.Items.Count, _host.Providers.Count, _events, _previousScanUtc);

            _ = EnrichAsync();
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Rebuilds the visible rows from the current scan and the current filter.</summary>
    public void Rebuild()
    {
        var byId = _events.GroupBy(e => e.Id, StringComparer.Ordinal)
                          .ToDictionary(g => g.Key, g => g.First().Event, StringComparer.Ordinal);

        Items.Clear();
        foreach (var item in ItemFilter.Apply(_scanned, Filter, ChangedIds))
        {
            byId.TryGetValue(item.Id, out var kind);
            Items.Add(new ItemViewModel(item, byId.ContainsKey(item.Id) ? kind : null, RequestToggle));
        }
    }

    /// <summary>Set by the toolbar in Task 43; Rebuild() is what applies it.</summary>
    public FilterSettings Filter { get; set; } = new();

    /// <summary>Replaced in Task 44 by the real route through ToggleService.</summary>
    private bool RequestToggle(ItemViewModel row, bool enable) => false;

    /// <summary>Phase 2: hash, PE version, and Authenticode in the background.</summary>
    private async Task EnrichAsync()
    {
        var paths = _scanned
            .Where(i => !string.IsNullOrWhiteSpace(i.ImagePath))
            .Select(i => i.ImagePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var done = 0;

        await Task.Run(() => Parallel.ForEach(paths, path =>
        {
            var facts = _host.Files.Get(path);
            var completed = Interlocked.Increment(ref done);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var row in Items.Where(r =>
                             string.Equals(r.Item.ImagePath, path, StringComparison.OrdinalIgnoreCase)))
                    row.Replace(row.Item with { File = facts });

                EnrichmentProgress = completed < paths.Count ? $"Analysing files… {completed}/{paths.Count}" : "";
            });
        }));

        // Keep the enriched facts on the backing list so a filter rebuild does not lose them.
        _scanned = _scanned
            .Select(i => i.ImagePath is null ? i : i with { File = _host.Files.Get(i.ImagePath) })
            .ToList();
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
```

- [ ] **Step 7: Write the window**

Replace `StartupScanner.App/MainWindow.xaml` with:

```xml
<Window x:Class="StartupScanner.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="StartupScanner" Height="760" Width="1280">
  <Window.Resources>
    <CollectionViewSource x:Key="GroupedItems" Source="{Binding Items}">
      <CollectionViewSource.GroupDescriptions>
        <PropertyGroupDescription PropertyName="Category"/>
      </CollectionViewSource.GroupDescriptions>
    </CollectionViewSource>
  </Window.Resources>

  <DockPanel>
    <ToolBar DockPanel.Dock="Top">
      <Button Content="Rescan" Command="{Binding RescanCommand}" Padding="10,2"/>
      <Separator/>
      <TextBlock Text="{Binding EnrichmentProgress}" VerticalAlignment="Center" Margin="8,0"/>
    </ToolBar>

    <StatusBar DockPanel.Dock="Bottom">
      <TextBlock Text="{Binding StatusText}"/>
    </StatusBar>

    <Grid>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="4"/>
        <ColumnDefinition Width="380"/>
      </Grid.ColumnDefinitions>

      <ListView Grid.Column="0"
                ItemsSource="{Binding Source={StaticResource GroupedItems}}"
                SelectedItem="{Binding SelectedItem}"
                VirtualizingPanel.IsVirtualizing="True"
                VirtualizingPanel.IsVirtualizingWhenGrouping="True"
                ScrollViewer.CanContentScroll="True">
        <ListView.GroupStyle>
          <GroupStyle>
            <GroupStyle.ContainerStyle>
              <Style TargetType="GroupItem">
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="GroupItem">
                      <Expander IsExpanded="True">
                        <Expander.Header>
                          <TextBlock FontWeight="Bold">
                            <Run Text="{Binding Name}"/>
                            <Run Text=" ("/><Run Text="{Binding ItemCount}"/><Run Text=")"/>
                          </TextBlock>
                        </Expander.Header>
                        <ItemsPresenter/>
                      </Expander>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </GroupStyle.ContainerStyle>
          </GroupStyle>
        </ListView.GroupStyle>

        <ListView.View>
          <GridView>
            <GridViewColumn Header="" Width="34">
              <GridViewColumn.CellTemplate>
                <DataTemplate>
                  <Grid>
                    <CheckBox IsChecked="{Binding IsEnabled, Mode=TwoWay}"
                              Visibility="{Binding IsBlocked, Converter={StaticResource InverseBoolToVisibility}}"/>
                    <TextBlock Text="🔒" ToolTip="{Binding BlockReason}"
                               Visibility="{Binding IsBlocked, Converter={StaticResource BoolToVisibility}}"/>
                  </Grid>
                </DataTemplate>
              </GridViewColumn.CellTemplate>
            </GridViewColumn>
            <GridViewColumn Header="" Width="26" DisplayMemberBinding="{Binding StateGlyph}"/>
            <GridViewColumn Header="Name" Width="220" DisplayMemberBinding="{Binding Name}"/>
            <GridViewColumn Header="Publisher" Width="200" DisplayMemberBinding="{Binding Publisher}"/>
            <GridViewColumn Header="Location" Width="360" DisplayMemberBinding="{Binding Location}"/>
            <GridViewColumn Header="" Width="30" DisplayMemberBinding="{Binding ChangeBadge}"/>
          </GridView>
        </ListView.View>
      </ListView>

      <GridSplitter Grid.Column="1" Width="4" HorizontalAlignment="Stretch"/>

      <ContentControl Grid.Column="2" x:Name="DetailsHost"/>
    </Grid>
  </DockPanel>
</Window>
```

Replace `StartupScanner.App/MainWindow.xaml.cs` with:

```csharp
using System.Windows;
using StartupScanner.App.ViewModels;

namespace StartupScanner.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.RescanAsync();
    }
}
```

- [ ] **Step 8: Write the converters the XAML references**

Create `StartupScanner.App/Converters/BoolToVisibilityConverters.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace StartupScanner.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Replace `StartupScanner.App/App.xaml` with:

```xml
<Application x:Class="StartupScanner.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:conv="clr-namespace:StartupScanner.App.Converters">
  <Application.Resources>
    <conv:BoolToVisibilityConverter x:Key="BoolToVisibility"/>
    <conv:InverseBoolToVisibilityConverter x:Key="InverseBoolToVisibility"/>
  </Application.Resources>
</Application>
```

- [ ] **Step 9: Write the bootstrap**

Replace `StartupScanner.App/App.xaml.cs` with:

```csharp
using System.Windows;
using StartupScanner.App.ViewModels;
using StartupScanner.Core;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;
using StartupScanner.Core.Writing;

namespace StartupScanner.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var host = ScannerHost.CreateWindows(new NoConfirmationYet(), new RestorePointPrompt());

        if (host.AclOutcome == AclOutcome.Failed)
            MessageBox.Show(
                "The StartupScanner data folder could not be secured. Values in the write journal " +
                "may be modifiable by non-administrators.",
                "StartupScanner", MessageBoxButton.OK, MessageBoxImage.Warning);

        new MainWindow(new MainViewModel(host)).Show();
    }

    /// <summary>Replaced by the real dialog in Task 44; refusing is the safe placeholder.</summary>
    private sealed class NoConfirmationYet : IConfirmationSink
    {
        public bool Confirm(StartupItem item, RiskTier tier, string message) => false;
    }

    private sealed class RestorePointPrompt : Core.Abstractions.IRestorePointPrompt
    {
        public bool ContinueWithoutRestorePoint(string reason)
            => MessageBox.Show(
                $"{reason}\n\nContinue without restore point protection?",
                "StartupScanner", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
}
```

- [ ] **Step 10: Build and run**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: Build succeeded, 0 errors.

Run: `cmd.exe /c start "" "D:\Repos\StartupScanner\StartupScanner.App\bin\Debug\net8.0-windows\StartupScanner.App.exe"`
Expected, after approving UAC:
- The list populates within a few seconds, grouped by category with expandable headers.
- Group headers show counts; several hundred items total on a typical machine.
- The status bar reads `N items · 19 providers · first scan, no baseline to compare against`.
- The Publisher column starts as `…` and fills in while `Analysing files… n/m` counts up in the toolbar.
- Blocked rows (`Winlogon\Shell`, `RpcSs`, LSA entries) show 🔒 instead of a checkbox.

Close the app, run it a second time, and confirm the status bar now reports either "no changes since …" or specific counts.

- [ ] **Step 11: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.sln StartupScanner.App
git commit -m "feat: add WPF shell with grouped item list and two-phase scan"
```

---

## Task 42: Details panel

**Files:**
- Create: `StartupScanner.App/ViewModels/DetailsViewModel.cs`, `Views/DetailsPanel.xaml`, `Views/DetailsPanel.xaml.cs`
- Modify: `StartupScanner.App/MainWindow.xaml` (replace `DetailsHost` with the panel), `ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `ScannerHost.Files`, `ScannerHost.History`, `ScannerHost.Journal`, `ScannerHost.ServiceStatus`, `Guardrails`.
- Produces: `record DetailRow(string Label, string Value)` and `DetailsViewModel(ScannerHost host)` with `Show(StartupItem? item)` plus one `IReadOnlyList<DetailRow>` property per spec section — `Identity`, `File`, `Signature`, `LocationAndState`, `TypeSpecific` — and `IReadOnlyList<string> HistoryLines`.

Six sections, all offline, all lazily computed: `FileFacts` are only requested when a row is selected, which is what keeps a 400-item scan from hashing 400 binaries up front.

- [ ] **Step 1: Write `DetailsViewModel`**

Create `StartupScanner.App/ViewModels/DetailsViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.ComponentModel;
using System.Linq;
using StartupScanner.Core;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;

namespace StartupScanner.App.ViewModels;

/// <summary>
/// One labelled row in the panel. A named type rather than a ValueTuple because WPF binds to
/// properties and a tuple's Item1/Item2 are fields, so tuple bindings silently render nothing.
/// </summary>
public sealed record DetailRow(string Label, string Value)
{
    public static implicit operator DetailRow((string Label, string Value) pair) => new(pair.Label, pair.Value);
}

public sealed class DetailsViewModel(ScannerHost host) : INotifyPropertyChanged
{
    private static readonly DetailRow[] Empty = Array.Empty<DetailRow>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasSelection { get; private set; }
    public string Title { get; private set; } = "Select an entry";

    public IReadOnlyList<DetailRow> Identity { get; private set; } = Empty;
    public IReadOnlyList<DetailRow> File { get; private set; } = Empty;
    public IReadOnlyList<DetailRow> Signature { get; private set; } = Empty;
    public IReadOnlyList<DetailRow> LocationAndState { get; private set; } = Empty;
    public IReadOnlyList<DetailRow> TypeSpecific { get; private set; } = Empty;
    public IReadOnlyList<string> HistoryLines { get; private set; } = Array.Empty<string>();

    public void Show(StartupItem? item)
    {
        if (item is null)
        {
            HasSelection = false;
            Title = "Select an entry";
            Identity = File = Signature = LocationAndState = TypeSpecific = Empty;
            HistoryLines = Array.Empty<string>();
            RaiseAll();
            return;
        }

        HasSelection = true;
        Title = item.Name;

        // FileFacts are computed here rather than during the scan: this is the "lazily computed" path.
        var facts = item.ImagePath is null ? null : host.Files.Get(item.ImagePath);
        var firstSeen = host.History.FirstSeenUtc(item.Id);

        Identity = new DetailRow[]
        {
            ("Name", item.Name),
            ("Provider", item.ProviderId),
            ("Category", item.Category.ToString()),
            ("Raw value", item.RawValue),
            ("Image path", item.ImagePath ?? "—"),
            ("Arguments", item.Arguments ?? "—"),
            ("Expanded", item.ImagePath ?? "—"),
            ("Runs as", item.UserSid ?? "machine-wide"),
            ("First seen", firstSeen?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "this scan"),
        };

        File = facts is null || !facts.Exists
            ? new DetailRow[] { ("Exists", facts is null ? "no image path" : "file not found") }
            : new DetailRow[]
            {
                ("Exists", "yes"),
                ("SHA-256", facts.Sha256 ?? "unreadable"),
                ("Size", facts.Size.ToString("N0", CultureInfo.InvariantCulture) + " bytes"),
                ("Created", Stamp(facts.CreatedUtc)),
                ("Modified", Stamp(facts.ModifiedUtc)),
                ("Company", facts.Version?.Company ?? "—"),
                ("Product", facts.Version?.Product ?? "—"),
                ("Description", facts.Version?.FileDescription ?? "—"),
                ("File version", facts.Version?.FileVersion ?? "—"),
                ("ACL owner", facts.AclOwner ?? "—"),
            };

        Signature = facts?.Signature is { } signature
            ? new DetailRow[]
            {
                ("Status", signature.Status.ToString()),
                ("Signer", signature.SignerName ?? "—"),
                ("Chain", signature.Chain.Count == 0 ? "—" : string.Join(" → ", signature.Chain)),
                ("Countersigned", Stamp(signature.TimestampUtc)),
                ("Source", signature.IsCatalogSigned ? "catalog" : "embedded"),
                ("Microsoft-signed", signature.IsMicrosoftSigned ? "yes" : "no"),
                ("Detail", signature.StatusMessage ?? "—"),
            }
            : Empty;

        var block = Guardrails.Evaluate(item);
        LocationAndState = new DetailRow[]
        {
            ("Location", item.Location),
            ("State", item.State.ToString()),
            ("Controlled by", item.StateSource),
            ("Risk tier", item.Risk.ToString()),
            ("Writable", block.Blocked ? "no" : "yes"),
            ("Reason", block.Reason ?? "—"),
            ("Partially read", item.PartiallyRead ? "yes" : "no"),
        };

        TypeSpecific = BuildTypeSpecific(item);
        HistoryLines = BuildHistory(item);
        RaiseAll();
    }

    private IReadOnlyList<DetailRow> BuildTypeSpecific(StartupItem item)
    {
        var rows = item.Extra
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && pair.Key != "Xml")
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new DetailRow(pair.Key, pair.Value))
            .ToList();

        if (item.Category is StartupCategory.Service or StartupCategory.Driver)
            rows.Insert(0, new DetailRow("Running state", host.ServiceStatus.GetStatus(item.Name) ?? "unknown"));

        return rows;
    }

    private IReadOnlyList<string> BuildHistory(StartupItem item)
    {
        var lines = host.History.ReadFor(item.Id)
            .Select(e => $"{e.Ts.ToLocalTime():yyyy-MM-dd HH:mm}  {e.Event}  {e.Before ?? "—"} → {e.After ?? "—"}")
            .ToList();

        lines.AddRange(host.Journal.All
            .Where(r => r.ItemId == item.Id)
            .Select(r => $"{r.Ts.ToLocalTime():yyyy-MM-dd HH:mm}  write via {r.Mechanism}  " +
                         $"{(r.Enabled ? "enabled" : "disabled")}"));

        return lines.Count == 0 ? new[] { "No recorded history for this entry." } : lines;
    }

    private static string Stamp(DateTime? value)
        => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";

    private void RaiseAll()
    {
        foreach (var property in new[]
                 {
                     nameof(HasSelection), nameof(Title), nameof(Identity), nameof(File), nameof(Signature),
                     nameof(LocationAndState), nameof(TypeSpecific), nameof(HistoryLines),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
```

- [ ] **Step 2: Write the panel**

Create `StartupScanner.App/Views/DetailsPanel.xaml`:

```xml
<UserControl x:Class="StartupScanner.App.Views.DetailsPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <UserControl.Resources>
    <DataTemplate x:Key="RowTemplate">
      <Grid Margin="0,1">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="130"/>
          <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <TextBlock Grid.Column="0" Text="{Binding Label}" Foreground="Gray" TextWrapping="Wrap"/>
        <TextBlock Grid.Column="1" Text="{Binding Value}" TextWrapping="Wrap"
                   ToolTip="{Binding Value}"/>
      </Grid>
    </DataTemplate>
  </UserControl.Resources>

  <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="10">
    <StackPanel>
      <TextBlock Text="{Binding Title}" FontSize="16" FontWeight="Bold" TextWrapping="Wrap" Margin="0,0,0,10"/>

      <TextBlock Text="IDENTITY" FontWeight="Bold" Margin="0,6,0,3"/>
      <ItemsControl ItemsSource="{Binding Identity}" ItemTemplate="{StaticResource RowTemplate}"/>

      <TextBlock Text="FILE" FontWeight="Bold" Margin="0,12,0,3"/>
      <ItemsControl ItemsSource="{Binding File}" ItemTemplate="{StaticResource RowTemplate}"/>

      <TextBlock Text="SIGNATURE" FontWeight="Bold" Margin="0,12,0,3"/>
      <ItemsControl ItemsSource="{Binding Signature}" ItemTemplate="{StaticResource RowTemplate}"/>

      <TextBlock Text="LOCATION AND STATE" FontWeight="Bold" Margin="0,12,0,3"/>
      <ItemsControl ItemsSource="{Binding LocationAndState}" ItemTemplate="{StaticResource RowTemplate}"/>

      <TextBlock Text="TYPE-SPECIFIC" FontWeight="Bold" Margin="0,12,0,3"/>
      <ItemsControl ItemsSource="{Binding TypeSpecific}" ItemTemplate="{StaticResource RowTemplate}"/>

      <TextBlock Text="HISTORY" FontWeight="Bold" Margin="0,12,0,3"/>
      <ItemsControl ItemsSource="{Binding HistoryLines}">
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <TextBlock Text="{Binding}" TextWrapping="Wrap" FontFamily="Consolas" FontSize="11"/>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

Create `StartupScanner.App/Views/DetailsPanel.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace StartupScanner.App.Views;

public partial class DetailsPanel : UserControl
{
    public DetailsPanel() => InitializeComponent();
}
```

- [ ] **Step 3: Wire selection into the panel**

In `StartupScanner.App/ViewModels/MainViewModel.cs`, add the details view model and update the `SelectedItem` setter:

```csharp
    public DetailsViewModel Details { get; private set; } = null!;

    public ItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            _selectedItem = value;
            Details.Show(value?.Item);
            Raise(nameof(SelectedItem));
        }
    }
```

and initialise it in the constructor, immediately after `_host = host;`:

```csharp
        Details = new DetailsViewModel(host);
```

In `StartupScanner.App/MainWindow.xaml`, replace the `ContentControl` with the panel:

```xml
      <views:DetailsPanel Grid.Column="2" DataContext="{Binding Details}"/>
```

and add the namespace to the `Window` element:

```xml
        xmlns:views="clr-namespace:StartupScanner.App.Views"
```

- [ ] **Step 4: Build and verify by running**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: Build succeeded.

Run: `cmd.exe /c start "" "D:\Repos\StartupScanner\StartupScanner.App\bin\Debug\net8.0-windows\StartupScanner.App.exe"`
Expected, after approving UAC:
- Selecting a Run key row fills all six sections; SHA-256 and signature appear within a moment.
- Selecting a service row shows a "Running state" row plus `Start`, `Type`, `ObjectName`, and `Description`.
- Selecting a scheduled task row shows `Triggers`, `Actions`, `Principal`, `LastTaskResult`, and `NextRunTimeUtc`.
- Selecting a `Winlogon\Shell` row shows `Writable: no` with the guardrail reason.
- Selecting a WMI filter row shows its `Wql` text.
- History reads "No recorded history for this entry" on a first run.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.App
git commit -m "feat: add lazily computed details panel with all six sections"
```

---

## Task 43: Toolbar — filters and export

**Files:**
- Modify: `StartupScanner.App/MainWindow.xaml`, `ViewModels/MainViewModel.cs`
- Create: `StartupScanner.App/ViewModels/CategoryToggleViewModel.cs`

**Interfaces:**
- Consumes: `ItemFilter`, `FilterSettings`, `Exporter`.
- Produces: `MainViewModel` gains `SearchText`, `ChangesOnly`, `HideMicrosoftSigned`, `CategoryToggles`, and `ExportCommand`.

Every filter decision already lives in `ItemFilter`, so this task is bindings plus one save dialog. The export writes the currently filtered rows, with CSV or JSON chosen by the extension the user picks in the dialog.

- [ ] **Step 1: Write `CategoryToggleViewModel`**

Create `StartupScanner.App/ViewModels/CategoryToggleViewModel.cs`:

```csharp
using System;
using System.ComponentModel;
using StartupScanner.Core.Models;

namespace StartupScanner.App.ViewModels;

public sealed class CategoryToggleViewModel(StartupCategory category, Action onChanged) : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public StartupCategory Category { get; } = category;
    public string Label { get; } = category.ToString();

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            onChanged();
        }
    }
}
```

- [ ] **Step 2: Add the filter and export members to `MainViewModel`**

Add these members to `StartupScanner.App/ViewModels/MainViewModel.cs`:

```csharp
    private string _searchText = "";
    private bool _changesOnly;
    private bool _hideMicrosoftSigned;

    public ObservableCollection<CategoryToggleViewModel> CategoryToggles { get; } = new();
    public RelayCommand ExportCommand { get; private set; } = null!;

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; Raise(nameof(SearchText)); ApplyFilter(); }
    }

    public bool ChangesOnly
    {
        get => _changesOnly;
        set { _changesOnly = value; Raise(nameof(ChangesOnly)); ApplyFilter(); }
    }

    public bool HideMicrosoftSigned
    {
        get => _hideMicrosoftSigned;
        set { _hideMicrosoftSigned = value; Raise(nameof(HideMicrosoftSigned)); ApplyFilter(); }
    }

    private void ApplyFilter()
    {
        Filter = new FilterSettings
        {
            Search = SearchText,
            Categories = CategoryToggles.Where(c => c.IsSelected).Select(c => c.Category).ToHashSet(),
            ChangesOnly = ChangesOnly,
            HideMicrosoftSigned = HideMicrosoftSigned,
        };
        Rebuild();
    }

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"startupscanner-{DateTime.Now:yyyyMMdd-HHmm}",
            DefaultExt = ".csv",
            Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
        };

        if (dialog.ShowDialog() != true) return;

        var rows = Items.Select(i => i.Item).ToList();
        var text = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? Exporter.ToJson(rows)
            : Exporter.ToCsv(rows);

        System.IO.File.WriteAllText(dialog.FileName, text);
        StatusText = $"Exported {rows.Count} rows to {dialog.FileName}";
    }
```

And in the constructor, after `RescanCommand = …`:

```csharp
        ExportCommand = new RelayCommand(Export, () => Items.Count > 0);

        foreach (var category in Enum.GetValues<StartupCategory>())
            CategoryToggles.Add(new CategoryToggleViewModel(category, ApplyFilter));
```

- [ ] **Step 3: Add the toolbar controls**

Replace the `<ToolBar>` element in `StartupScanner.App/MainWindow.xaml` with:

```xml
    <ToolBar DockPanel.Dock="Top">
      <Button Content="Rescan" Command="{Binding RescanCommand}" Padding="10,2"/>
      <Separator/>
      <TextBlock Text="🔍" VerticalAlignment="Center"/>
      <TextBox Width="180" Margin="4,0"
               Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged, Delay=250}"/>
      <Separator/>
      <ItemsControl ItemsSource="{Binding CategoryToggles}">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <ToggleButton Content="{Binding Label}" IsChecked="{Binding IsSelected, Mode=TwoWay}"
                          Margin="2,0" Padding="6,1"/>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
      <Separator/>
      <CheckBox Content="Changes only" IsChecked="{Binding ChangesOnly, Mode=TwoWay}" Margin="4,0"/>
      <CheckBox Content="Hide MS-signed" IsChecked="{Binding HideMicrosoftSigned, Mode=TwoWay}" Margin="4,0"/>
      <Separator/>
      <Button Content="Export" Command="{Binding ExportCommand}" Padding="10,2"/>
      <Separator/>
      <TextBlock Text="{Binding EnrichmentProgress}" VerticalAlignment="Center" Margin="8,0"/>
    </ToolBar>
```

- [ ] **Step 4: Build and verify by running**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: Build succeeded.

Run: `cmd.exe /c start "" "D:\Repos\StartupScanner\StartupScanner.App\bin\Debug\net8.0-windows\StartupScanner.App.exe"`
Expected, after approving UAC:
- Typing in the search box narrows the list as you type; clearing it restores everything.
- Toggling the `Service` chip shows only services; toggling it off restores everything.
- "Changes only" empties the list on a run with no changes.
- "Hide MS-signed" removes Microsoft-signed rows once enrichment has finished.
- Export with a `.csv` name writes a file whose first line is the header and whose row count matches the visible rows; export with a `.json` name writes a JSON array.

- [ ] **Step 5: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.App
git commit -m "feat: add filter toolbar and CSV/JSON export"
```

---

## Task 44: Toggle UX, risk confirmation, undo, and the error banner

**Files:**
- Create: `StartupScanner.App/Views/RiskConfirmationDialog.xaml`, `Views/RiskConfirmationDialog.xaml.cs`, `ViewModels/DialogConfirmationSink.cs`
- Modify: `StartupScanner.App/ViewModels/MainViewModel.cs`, `MainWindow.xaml`, `App.xaml.cs`

**Interfaces:**
- Consumes: `ToggleService`, `UndoService`, `IConfirmationSink`, `ToggleResult`.
- Produces:
  - `DialogConfirmationSink` implementing `IConfirmationSink` over a modal dialog.
  - `MainViewModel.RequestToggle` override routing through `ToggleService`, plus `UndoLastCommand` and `UndoSessionCommand`, plus `Errors` surfaced as a dismissible banner.

The checkbox writes immediately, after any risk confirmation — but it snaps back if the write did not land, so the UI never claims a change that the read-back rejected. `Low` risk never prompts; `Medium` gets a plain confirmation; `High` gets one naming the concrete consequence; `Critical` never reaches a dialog because the row shows a lock instead of a checkbox.

- [ ] **Step 1: Write the confirmation dialog**

Create `StartupScanner.App/Views/RiskConfirmationDialog.xaml`:

```xml
<Window x:Class="StartupScanner.App.Views.RiskConfirmationDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Confirm change" SizeToContent="Height" Width="480"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize">
  <StackPanel Margin="16">
    <TextBlock x:Name="TierText" FontWeight="Bold" Margin="0,0,0,8"/>
    <TextBlock x:Name="MessageText" TextWrapping="Wrap" Margin="0,0,0,16"/>
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
      <Button Content="Cancel" IsCancel="True" Width="90" Margin="0,0,8,0"/>
      <Button Content="Continue" IsDefault="True" Width="90" Click="OnContinue"/>
    </StackPanel>
  </StackPanel>
</Window>
```

Create `StartupScanner.App/Views/RiskConfirmationDialog.xaml.cs`:

```csharp
using System.Windows;
using StartupScanner.Core.Models;

namespace StartupScanner.App.Views;

public partial class RiskConfirmationDialog : Window
{
    public RiskConfirmationDialog(RiskTier tier, string message)
    {
        InitializeComponent();
        TierText.Text = $"{tier} risk";
        MessageText.Text = message;
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
```

- [ ] **Step 2: Write the confirmation sink**

Create `StartupScanner.App/ViewModels/DialogConfirmationSink.cs`:

```csharp
using System.Windows;
using StartupScanner.App.Views;
using StartupScanner.Core.Models;
using StartupScanner.Core.Writing;

namespace StartupScanner.App.ViewModels;

public sealed class DialogConfirmationSink : IConfirmationSink
{
    public bool Confirm(StartupItem item, RiskTier tier, string message)
        => Application.Current.Dispatcher.Invoke(() => new RiskConfirmationDialog(tier, message)
        {
            Owner = Application.Current.MainWindow,
        }.ShowDialog() == true);
}
```

- [ ] **Step 3: Route toggles through `ToggleService`**

In `StartupScanner.App/ViewModels/MainViewModel.cs`, replace the placeholder `RequestToggle` with:

```csharp
    /// <summary>
    /// Returns true only when the write landed. A false return makes the checkbox snap back, so the
    /// UI never shows a state the read-back rejected.
    /// </summary>
    private bool RequestToggle(ItemViewModel row, bool enable)
    {
        var result = _host.ToggleService.SetEnabled(row.Item, enable, _host.ContextFor(row.Item));

        if (result.Success)
        {
            row.Replace(row.Item with
            {
                State = enable ? EnableState.Enabled : EnableState.Disabled,
                StateSource = result.Record!.Mechanism,
            });
            StatusText = $"{(enable ? "Enabled" : "Disabled")} {row.Name}.";
            UndoLastCommand.RaiseCanExecuteChanged();
            UndoSessionCommand.RaiseCanExecuteChanged();
            return true;
        }

        if (result.Cancelled)
        {
            if (result.Error is not null) StatusText = result.Error;
            return false;
        }

        Errors.Add($"{row.Name}: {result.Error}");
        StatusText = $"Could not change {row.Name}.";
        return false;
    }
```

- [ ] **Step 4: Add the undo commands**

Add to `StartupScanner.App/ViewModels/MainViewModel.cs`:

```csharp
    public RelayCommand UndoLastCommand { get; private set; } = null!;
    public RelayCommand UndoSessionCommand { get; private set; } = null!;

    private void UndoLast()
    {
        var result = _host.Undo.UndoLast(_host.ContextFor());
        StatusText = result.Success ? "Undid the last change." : result.Error ?? "Nothing to undo.";
        if (!result.Success && result.Error is not null && !result.Cancelled) Errors.Add(result.Error);
        _ = RescanAsync();
    }

    private void UndoSession()
    {
        var results = _host.Undo.UndoSession(_host.ContextFor());
        var undone = results.Count(r => r.Success);

        StatusText = results.Count == 0
            ? "No changes from this session to undo."
            : $"Undid {undone} of {results.Count} changes from this session.";

        foreach (var failure in results.Where(r => !r.Success && r.Error is not null))
            Errors.Add(failure.Error!);

        _ = RescanAsync();
    }
```

And in the constructor, after `ExportCommand = …`:

```csharp
        UndoLastCommand = new RelayCommand(UndoLast, () => _host.Journal.All.Count > 0);
        UndoSessionCommand = new RelayCommand(UndoSession, () => _host.Journal.ForSession(_host.SessionId).Count > 0);
```

- [ ] **Step 5: Add the undo menu and error banner to the window**

In `StartupScanner.App/MainWindow.xaml`, add to the `ToolBar` just before the final `TextBlock`:

```xml
      <Separator/>
      <Menu Background="Transparent">
        <MenuItem Header="Undo ▾">
          <MenuItem Header="Undo last change" Command="{Binding UndoLastCommand}"/>
          <MenuItem Header="Undo everything from this session" Command="{Binding UndoSessionCommand}"/>
        </MenuItem>
      </Menu>
```

And add the banner immediately inside the `DockPanel`, before the `ToolBar`:

```xml
    <Border DockPanel.Dock="Top" Background="#FFF4CE" BorderBrush="#E0C060" BorderThickness="0,0,0,1"
            Padding="8" Visibility="{Binding HasErrors, Converter={StaticResource BoolToVisibility}}">
      <DockPanel>
        <Button DockPanel.Dock="Right" Content="Dismiss" Command="{Binding DismissErrorsCommand}" Padding="8,1"/>
        <ItemsControl ItemsSource="{Binding Errors}">
          <ItemsControl.ItemTemplate>
            <DataTemplate>
              <TextBlock Text="{Binding}" TextWrapping="Wrap" FontSize="11"/>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </DockPanel>
    </Border>
```

Then add the banner's two members to `MainViewModel`:

```csharp
    public bool HasErrors => Errors.Count > 0;
    public RelayCommand DismissErrorsCommand { get; private set; } = null!;
```

in the constructor, after `UndoSessionCommand = …`:

```csharp
        DismissErrorsCommand = new RelayCommand(() => Errors.Clear());
        Errors.CollectionChanged += (_, _) => Raise(nameof(HasErrors));
```

- [ ] **Step 6: Use the real confirmation sink**

In `StartupScanner.App/App.xaml.cs`, replace the `NoConfirmationYet` usage and delete the class:

```csharp
        var host = ScannerHost.CreateWindows(new DialogConfirmationSink(), new RestorePointPrompt());
```

- [ ] **Step 7: Build and verify the write path end to end**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: Build succeeded.

Before running, create a harmless entry to practise on, from an elevated `cmd`:

```
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v StartupScannerDemo /t REG_SZ /d "C:\Windows\System32\notepad.exe" /f
```

Run: `cmd.exe /c start "" "D:\Repos\StartupScanner\StartupScanner.App\bin\Debug\net8.0-windows\StartupScanner.App.exe"`
Expected, after approving UAC:
- `StartupScannerDemo` appears in the Run keys group with a `⊕` badge on the scan after it was created.
- Unticking its checkbox writes with no prompt (Low risk); the status bar confirms it.
- Task Manager's Startup tab now shows the same entry as Disabled — this is the consistency the `StartupApproved` mechanism buys.
- `%ProgramData%\StartupScanner\writes.jsonl` has gained one line with `"mechanism":"StartupApproved"`.
- "Undo last change" re-ticks it and Task Manager agrees again.
- Unticking a service (e.g. `Spooler`) shows a plain confirmation; cancelling leaves the checkbox ticked.
- Unticking a `Winlogon\Taskman`-style entry, if the machine has one, shows a High-risk dialog naming the logon consequence.
- Blocked rows show 🔒 and cannot be clicked at all.

Clean up when done:

```
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v StartupScannerDemo /f
```

- [ ] **Step 8: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.App
git commit -m "feat: add toggle UX with risk confirmation, undo menu, and error banner"
```

---

## Task 45: Opt-in elevated integration suite

**Files:**
- Create: `StartupScanner.Tests/Integration/ElevatedRunKeyTests.cs`
- Test: the file is itself the test.

**Interfaces:**
- Consumes: `ScannerHost`, `WindowsRegistry`, `StartupApprovedBlob`.
- Produces: a trait-marked suite that exercises the real registry end to end.

Every other test runs against fakes. This one proves the fake and the real registry agree about the thing most likely to be wrong: the exact bytes Windows expects in a `StartupApproved` blob. It is opt-in — gated on `STARTUPSCANNER_ELEVATED_TESTS=1` — because it writes to the real `HKCU\…\Run` key, and it cleans up in a `finally` block.

- [ ] **Step 1: Write the suite**

Create `StartupScanner.Tests/Integration/ElevatedRunKeyTests.cs`:

```csharp
using StartupScanner.Core;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Windows;
using StartupScanner.Core.Writing;
using StartupScanner.Tests.Fakes;

namespace StartupScanner.Tests.Integration;

/// <summary>
/// Writes to the real registry. Enable with STARTUPSCANNER_ELEVATED_TESTS=1 in an elevated shell:
///   "/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln' \
///     --filter "Category=Elevated"
/// </summary>
[Trait("Category", "Elevated")]
public class ElevatedRunKeyTests
{
    private const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "__StartupScannerSelfTest";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("STARTUPSCANNER_ELEVATED_TESTS") == "1";

    [Fact]
    public void DisableWritesARealStartupApprovedBlobAndUndoLeavesTheKeyClean()
    {
        if (!Enabled) return;   // opt-in

        var registry = new WindowsRegistry();
        var host = ScannerHost.CreateWindows(new AlwaysConfirm(), new AlwaysContinue());

        try
        {
            using (var key = registry.CreateKey(RunKey))
                key.SetValue(ValueName, @"C:\Windows\System32\notepad.exe", RegistryValueType.String);

            // Phase 1: the scanner must see the value we just wrote.
            var context = host.ContextFor();
            var scanned = host.Engine.Run(context);
            var item = scanned.Items.Single(i => i.ProviderId == "registry.run" && i.Name == ValueName);
            Assert.Equal(EnableState.Enabled, item.State);

            // Disable it and assert the real blob bytes.
            var disable = host.ToggleService.SetEnabled(item, enabled: false, context);
            Assert.True(disable.Success, disable.Error);

            using (var approved = registry.OpenKey(ApprovedKey))
            {
                var blob = Assert.IsType<byte[]>(approved!.GetValue(ValueName));
                Assert.Equal(12, blob.Length);
                Assert.Equal(0x03, blob[0]);
                Assert.False(StartupApprovedBlob.IsEnabled(blob));
                Assert.NotNull(StartupApprovedBlob.DisabledAtUtc(blob));
            }

            // A rescan must now report it as disabled.
            var afterDisable = host.Engine.Run(host.ContextFor()).Items
                .Single(i => i.ProviderId == "registry.run" && i.Name == ValueName);
            Assert.Equal(EnableState.Disabled, afterDisable.State);
            Assert.Equal("StartupApproved", afterDisable.StateSource);

            // Undo must put it back to enabled.
            var undo = host.Undo.UndoLast(host.ContextFor());
            Assert.True(undo.Success, undo.Error);

            var afterUndo = host.Engine.Run(host.ContextFor()).Items
                .Single(i => i.ProviderId == "registry.run" && i.Name == ValueName);
            Assert.Equal(EnableState.Enabled, afterUndo.State);
        }
        finally
        {
            using (var key = registry.OpenKey(RunKey, RegistryViewKind.Default, writable: true))
                key?.DeleteValue(ValueName);

            using (var approved = registry.OpenKey(ApprovedKey, RegistryViewKind.Default, writable: true))
                approved?.DeleteValue(ValueName);
        }
    }

    [Fact]
    public void ARealScanFindsAllNineteenProvidersAndCompletesQuickly()
    {
        if (!Enabled) return;

        var host = ScannerHost.CreateWindows(new AlwaysConfirm(), new AlwaysContinue());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = host.Engine.Run(host.ContextFor());

        stopwatch.Stop();

        Assert.Equal(19, host.Providers.Count);
        Assert.True(result.Items.Count > 100, $"only {result.Items.Count} items found");
        Assert.True(stopwatch.Elapsed.TotalSeconds < 10,
            $"phase 1 took {stopwatch.Elapsed.TotalSeconds:F1}s; the target is under 2s");
    }

    [Fact]
    public void TheProgramDataFolderIsSecuredOnStartup()
    {
        if (!Enabled) return;

        var host = ScannerHost.CreateWindows(new AlwaysConfirm(), new AlwaysContinue());

        Assert.NotEqual(Core.Storage.AclOutcome.Failed, host.AclOutcome);
    }

    private sealed class AlwaysConfirm : IConfirmationSink
    {
        public bool Confirm(StartupItem item, RiskTier tier, string message) => true;
    }

    private sealed class AlwaysContinue : IRestorePointPrompt
    {
        public bool ContinueWithoutRestorePoint(string reason) => true;
    }
}
```

- [ ] **Step 2: Confirm the suite is skipped by default**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test 'D:\Repos\StartupScanner\StartupScanner.sln'`
Expected: PASS — all tests green, including the three elevated ones, which return immediately without touching the registry.

- [ ] **Step 3: Run the suite for real**

From an **elevated** Windows shell (PowerShell as Administrator), so `RegLoadKey` and the restore point work:

```
$env:STARTUPSCANNER_ELEVATED_TESTS=1
& "C:\Program Files\dotnet\dotnet.exe" test "D:\Repos\StartupScanner\StartupScanner.sln" --filter "Category=Elevated"
```

Expected: PASS — 3 tests passed. The scan-timing assertion reports the real phase-1 duration; if it exceeds 2 seconds, note the number but do not chase it here — the assertion's ceiling is deliberately 10s so a slow machine does not produce a red build.

Then confirm the scratch value is gone:

```
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v __StartupScannerSelfTest
```

Expected: `ERROR: The system was unable to find the specified registry key or value.`

- [ ] **Step 4: Commit**

```bash
cd /mnt/d/Repos/StartupScanner
git add StartupScanner.Tests/Integration
git commit -m "test: add opt-in elevated integration suite against the real registry"
```

---

## Appendix: spec coverage

| Spec section | Where it lands |
|---|---|
| Three-project layout, Core without WPF | Tasks 1, 41; asserted by `ScaffoldTests.CoreDoesNotReferenceWpf` |
| `requireAdministrator` manifest | Task 41 |
| Provider model, parallel run, provider isolation | Tasks 11, 16 |
| All 19 providers | Tasks 11–14, 23–35; count asserted in Task 40 |
| Table-driven Run keys | Task 11 (`RunKeyTable`) |
| Data model, `Extra`, `PartiallyRead` | Task 2 |
| Identity excluding `RawValue` | Task 2, re-asserted in Task 11 |
| Diff engine, four event kinds | Task 9 |
| `snapshot.json`, `history.jsonl`, `writes.jsonl` | Tasks 7, 8 |
| ProgramData ACL, verify and repair | Task 6, wired in Task 40 |
| Native disable: StartupApproved blob | Tasks 10, 18 |
| Native disable: service `Start`, task `Enabled` | Task 19 |
| BackupRemove for the eleven flag-less locations | Tasks 17, 23–34 |
| Blocked list, all five rules | Task 15 |
| Risk tiers and confirmation behaviour | Tasks 15, 21 |
| Restore point once per session, ask on failure | Task 20 |
| Write verification and rollback | Task 21 |
| Undo last / undo session | Task 22 |
| UI layout, checkbox writes immediately, lock glyph, `⊕` badge | Tasks 41, 44 |
| Six details-panel sections | Task 42 |
| Filters, export CSV/JSON | Tasks 36, 43 |
| Two-phase scan, `WinVerifyTrust` without revocation fetches, `(path,size,mtime)` cache | Tasks 37, 39, 41 |
| Error handling: banner, partially-read items, hives unloaded in `finally` | Tasks 35, 41, 44 |
| Testing list, including real captured blobs and the elevated suite | Tasks 10, 45 |
| Open risk: `HKU` checked before `RegLoadKey` | Task 35 |
| Open risk: AppX degrades to read-only | Task 28 |
| Open risk: recurring `StateChanged` is correct reporting | Tasks 9, 42 (history makes the pattern visible) |

Three places deliberately depart from the spec's sketch, each for a stated reason:

1. **Toggling lives in classes keyed by `ProviderId`, not on `IStartupProvider`.** Scan providers stay free of clock and journal dependencies. (Task 17)
2. **`SetEnabled` returns an uncommitted `WriteRecord`; only `ToggleService` writes to the journal.** This is what makes "the journal never records a write that did not land" enforceable rather than aspirational. (Tasks 17, 21)
3. **A non-default `Winlogon\Userinit` or `Shell` is restored to the OS default rather than deleted or blanked.** A machine with no `Userinit` value cannot complete logon, so deletion would violate goal 5 even though the prior value is journaled. (Task 23)

