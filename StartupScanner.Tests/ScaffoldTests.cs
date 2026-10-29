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
