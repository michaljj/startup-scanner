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
