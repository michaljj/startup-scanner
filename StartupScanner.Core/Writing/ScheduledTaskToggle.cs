using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Writing;

public sealed class ScheduledTaskToggle(ITaskSchedulerService tasks, IClock clock) : IToggleProvider
{
    public string ProviderId => "task-scheduler";

    public ToggleCapability GetCapability(StartupItem item)
        => Guardrails.Evaluate(item).Blocked ? ToggleCapability.Blocked : ToggleCapability.Native;

    public WriteRecord SetEnabled(StartupItem item, bool enabled, IWriteJournalReader journal)
    {
        var path = TaskPath(item);
        tasks.SetEnabled(path, enabled);

        return new WriteRecord
        {
            Ts = clock.UtcNow,
            SessionId = "",
            ItemId = item.Id,
            ProviderId = ProviderId,
            Mechanism = "Task.Enabled",
            Location = path,
            Name = item.Name,
            Category = item.Category,
            UserSid = item.UserSid,
            Before = $"sz:{item.State == EnableState.Enabled}",
            After = $"sz:{enabled}",
            Enabled = enabled,
        };
    }

    public void Restore(WriteRecord record)
    {
        var (value, _) = RegistryValueCodec.Decode(record.Before ?? "sz:True");
        tasks.SetEnabled(record.Location, bool.Parse((string)value));
    }

    private static string TaskPath(StartupItem item)
        => item.Extra.TryGetValue("TaskPath", out var path) && path.Length > 0
            ? path
            : $@"{item.Location.TrimEnd('\\')}\{item.Name}";
}
