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
