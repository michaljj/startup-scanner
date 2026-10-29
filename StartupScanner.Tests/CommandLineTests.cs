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
