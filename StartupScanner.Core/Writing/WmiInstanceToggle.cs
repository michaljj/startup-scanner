using System.Text.Json;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// WMI has no disable flag, so the instance's whole property set is journaled as JSON, the
/// instance is deleted, and re-enable recreates it. The mechanism name tells ScanEngine that the
/// journal payload is JSON rather than an encoded registry value.
/// </summary>
public sealed class WmiInstanceToggle(IWmi wmi, IClock clock) : IToggleProvider
{
    public string ProviderId => "wmi-subscription";

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.BackupRemove;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
        => enabled ? Enable(item, journal) : Disable(item);

    public void Restore(WriteRecord record)
    {
        if (record.Before is null)
        {
            wmi.DeleteInstance(WmiSubscriptionProvider.Namespace, record.Location.Split(':')[^1]);
            return;
        }

        Recreate(record.Before);
    }

    private WriteRecord Disable(StartupItem item)
    {
        var relativePath = item.Extra["RelativePath"];
        var backup = JsonSerializer.Serialize(new Backup(
            item.Extra["ActualClass"], relativePath, PropertiesOf(item)));

        wmi.DeleteInstance(WmiSubscriptionProvider.Namespace, relativePath);

        return Build(item, enabled: false, before: backup, after: null);
    }

    private WriteRecord Enable(StartupItem item, IWriteJournalReader journal)
    {
        var backup = journal.All.LastOrDefault(r => r.ItemId == item.Id && !r.Enabled && r.Before is not null)
            ?? throw new InvalidOperationException(
                $"No backed-up WMI instance is recorded for '{item.Name}', so it cannot be re-enabled.");

        Recreate(backup.Before!);
        return Build(item, enabled: true, before: null, after: backup.Before);
    }

    private void Recreate(string json)
    {
        var backup = JsonSerializer.Deserialize<Backup>(json)
            ?? throw new InvalidOperationException("The journalled WMI instance could not be read back.");

        wmi.CreateInstance(WmiSubscriptionProvider.Namespace, backup.ClassName,
            backup.Properties.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extra is the only copy of the instance's data the scan kept, which is enough to rebuild the
    /// filter, consumer, or binding; the bookkeeping keys are dropped.
    /// </summary>
    private static Dictionary<string, string> PropertiesOf(StartupItem item)
    {
        var skip = new[] { "RelativePath", "ActualClass", "ConsumerType" };
        var properties = item.Extra
            .Where(p => !skip.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        properties["Name"] = item.Name;
        if (item.Extra.TryGetValue("Wql", out var wql)) properties["Query"] = wql;
        return properties;
    }

    private WriteRecord Build(StartupItem item, bool enabled, string? before, string? after) => new()
    {
        Ts = clock.UtcNow,
        SessionId = "",
        ItemId = item.Id,
        ProviderId = ProviderId,
        Mechanism = "Wmi.Instance",
        Location = item.Location,
        Name = item.Name,
        Category = item.Category,
        Before = before,
        After = after,
        Enabled = enabled,
    };

    private sealed record Backup(string ClassName, string RelativePath, Dictionary<string, string> Properties);
}
