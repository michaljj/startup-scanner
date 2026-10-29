using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Providers;

/// <summary>
/// WMI permanent event subscriptions. A working subscription needs a filter, a consumer, and a
/// binding, and all three are reported because breaking any one of them stops it firing.
/// </summary>
public sealed class WmiSubscriptionProvider(IWmi wmi) : IStartupProvider
{
    public const string Namespace = @"root\subscription";

    private static readonly string[] Classes = { "__EventFilter", "__EventConsumer", "__FilterToConsumerBinding" };

    public string Id => "wmi-subscription";
    public StartupCategory Category => StartupCategory.WmiSubscription;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var className in Classes)
        {
            IReadOnlyList<WmiInstance> instances;
            try
            {
                instances = wmi.Query(Namespace, $"SELECT * FROM {className}");
            }
            catch (Exception ex)
            {
                ctx.Errors.Add(new ScanError(Id, $"{Namespace}:{className}", ex.Message));
                continue;
            }

            foreach (var instance in instances) yield return Build(className, instance);
        }
    }

    private StartupItem Build(string queriedClass, WmiInstance instance)
    {
        var location = $"{Namespace}:{queriedClass}";
        var name = Text(instance, "Name") is { Length: > 0 } named ? named : instance.RelativePath;
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RelativePath"] = instance.RelativePath,
            ["ActualClass"] = instance.ClassName,
        };

        string raw;
        string? image = null;

        switch (queriedClass)
        {
            case "__EventFilter":
                raw = Text(instance, "Query");
                extra["Wql"] = raw;
                extra["QueryLanguage"] = Text(instance, "QueryLanguage");
                extra["EventNamespace"] = Text(instance, "EventNamespace");
                break;

            case "__EventConsumer":
                raw = FirstNonEmpty(instance, "CommandLineTemplate", "ExecutablePath", "ScriptText", "ScriptFileName");
                extra["ConsumerType"] = instance.ClassName;
                image = raw.Length > 0 ? raw.Split(' ')[0].Trim('"') : null;
                break;

            default:
                extra["Filter"] = Text(instance, "Filter");
                extra["Consumer"] = Text(instance, "Consumer");
                raw = $"{extra["Filter"]} -> {extra["Consumer"]}";
                break;
        }

        return new StartupItem
        {
            Id = Identity.Compute(Id, null, location, name),
            ProviderId = Id,
            Location = location,
            Name = name,
            RawValue = raw,
            Category = Category,
            ImagePath = image,
            State = EnableState.Enabled,
            StateSource = "InstancePresent",
            Extra = extra,
        };
    }

    private static string Text(WmiInstance instance, string property)
        => instance.Properties.TryGetValue(property, out var value) ? value?.ToString() ?? "" : "";

    private static string FirstNonEmpty(WmiInstance instance, params string[] properties)
        => properties.Select(p => Text(instance, p)).FirstOrDefault(v => v.Length > 0) ?? "";
}
