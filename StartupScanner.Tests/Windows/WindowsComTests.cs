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
