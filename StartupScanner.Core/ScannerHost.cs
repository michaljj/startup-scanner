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
