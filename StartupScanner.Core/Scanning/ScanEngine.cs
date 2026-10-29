using System.Collections.Concurrent;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Providers;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Scanning;

public sealed class ScanEngine(IReadOnlyList<IStartupProvider> providers, IWriteJournalReader journal)
{
    public ScanResult Run(ScanContext ctx)
    {
        var collected = new ConcurrentBag<StartupItem>();
        var failures = new ConcurrentBag<ScanError>();

        Parallel.ForEach(providers, provider =>
        {
            try
            {
                foreach (var item in provider.Scan(ctx)) collected.Add(item);
            }
            catch (Exception ex)
            {
                failures.Add(new ScanError(provider.Id, "provider", ex.Message));
            }
        });

        var items = collected.ToList();
        items.AddRange(ReInjectRemoved(items));

        var stamped = items
            .Select(i => i with { Risk = RiskTiers.For(i) })
            .OrderBy(i => i.ProviderId, StringComparer.Ordinal)
            .ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScanResult(stamped, ctx.Errors.Concat(failures).ToList());
    }

    /// <summary>Mechanisms that remove an entry rather than flag it, so its row must be synthesised back.</summary>
    private static readonly HashSet<string> ReInjectableMechanisms =
        new(StringComparer.Ordinal) { "BackupRemove", "Wmi.Instance", "GpoScript.Ini" };

    /// <summary>
    /// A removed-and-backed-up entry is gone from where its provider looks, so no provider can see
    /// it. Synthesise it from its journal record so it stays visible and re-enableable.
    /// </summary>
    private IEnumerable<StartupItem> ReInjectRemoved(IReadOnlyList<StartupItem> live)
    {
        var present = live.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var id in journal.All.Select(r => r.ItemId).Distinct(StringComparer.Ordinal))
        {
            if (present.Contains(id)) continue;

            var record = journal.GetLast(id);
            if (record is null || record.Enabled || record.Before is null
                || !ReInjectableMechanisms.Contains(record.Mechanism))
                continue;

            yield return new StartupItem
            {
                Id = record.ItemId,
                ProviderId = record.ProviderId,
                Location = record.Location,
                Name = record.Name,
                RawValue = Display(record.Before),
                Category = record.Category,
                State = EnableState.Disabled,
                StateSource = "RemovedByScanner",
                UserSid = record.UserSid,
            };
        }
    }

    /// <summary>WMI and GPO script backups are not RegistryValueCodec strings; show them as stored.</summary>
    private static string Display(string encoded)
    {
        try
        {
            var (value, type) = RegistryValueCodec.Decode(encoded);
            return RegistryValueCodec.ToDisplay(value, type);
        }
        catch (FormatException)
        {
            return encoded;
        }
    }
}
