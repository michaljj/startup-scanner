using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

/// <summary>
/// Disabling a Group Policy script rewrites scripts.ini without that entry's CmdLine/Parameters
/// pair. The entire prior file is journaled, so re-enable restores it byte for byte instead of
/// attempting to renumber entries back into place.
/// </summary>
public sealed class GpoScriptToggle(IFileSystem fs, IClock clock) : IToggleProvider
{
    public string ProviderId => "gpo-script";

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.BackupRemove;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        if (enabled)
        {
            var backup = journal.All.LastOrDefault(r => r.ItemId == item.Id && !r.Enabled && r.Before is not null)
                ?? throw new InvalidOperationException(
                    $"No backed-up scripts.ini is recorded for '{item.Name}', so it cannot be re-enabled.");

            fs.WriteAllText(item.Location, backup.Before!);
            return Build(item, enabled: true, before: null, after: backup.Before);
        }

        var original = fs.ReadAllText(item.Location);
        fs.WriteAllText(item.Location, Remove(original, item.Extra["Section"], item.Extra["Index"]));
        return Build(item, enabled: false, before: original, after: null);
    }

    public void Restore(WriteRecord record)
    {
        if (record.Before is null) return;
        fs.WriteAllText(record.Location, record.Before);
    }

    /// <summary>Drops the "NCmdLine=" and "NParameters=" lines from the given section.</summary>
    private static string Remove(string text, string section, string index)
    {
        var kept = new List<string>();
        var current = "";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                current = trimmed[1..^1];
                kept.Add(line);
                continue;
            }

            var isTarget = current.Equals(section, StringComparison.OrdinalIgnoreCase)
                && (trimmed.StartsWith($"{index}CmdLine=", StringComparison.OrdinalIgnoreCase)
                 || trimmed.StartsWith($"{index}Parameters=", StringComparison.OrdinalIgnoreCase));

            if (!isTarget) kept.Add(line);
        }

        return string.Join("\n", kept);
    }

    private WriteRecord Build(StartupItem item, bool enabled, string? before, string? after) => new()
    {
        Ts = clock.UtcNow,
        SessionId = "",
        ItemId = item.Id,
        ProviderId = ProviderId,
        Mechanism = "GpoScript.Ini",
        Location = item.Location,
        Name = item.Name,
        Category = item.Category,
        UserSid = item.UserSid,
        Before = before,
        After = after,
        Enabled = enabled,
    };
}
