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
