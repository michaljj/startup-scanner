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
