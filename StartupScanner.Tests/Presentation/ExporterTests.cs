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
