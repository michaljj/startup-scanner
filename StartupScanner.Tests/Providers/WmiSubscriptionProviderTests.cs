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
        // The payload wraps the instance: { ClassName, RelativePath, Properties: { ... } }.
        using var restored = JsonDocument.Parse(record.Before!);
        Assert.True(restored.RootElement.GetProperty("Properties").TryGetProperty("Query", out _));
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
