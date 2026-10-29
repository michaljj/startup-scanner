using System.Globalization;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Providers;

public sealed class ScheduledTaskProvider(ITaskSchedulerService tasks, IFileSystem fs) : IStartupProvider
{
    public string Id => "task-scheduler";
    public StartupCategory Category => StartupCategory.ScheduledTask;

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        IReadOnlyList<ScheduledTaskInfo> all;
        try
        {
            all = tasks.EnumerateTasks();
        }
        catch (Exception ex)
        {
            ctx.Errors.Add(new ScanError(Id, "Task Scheduler", ex.Message));
            return Array.Empty<StartupItem>();
        }

        return all.Select(t => Build(t, ctx)).ToList();
    }

    private StartupItem Build(ScheduledTaskInfo task, ScanContext ctx)
    {
        var raw = string.Join("; ", task.Actions);
        var (image, args) = CommandLine.Split(task.Actions.FirstOrDefault() ?? "", fs);

        return new StartupItem
        {
            Id = Identity.Compute(Id, task.UserSid, task.Folder, task.Name),
            ProviderId = Id,
            Location = task.Folder,
            Name = task.Name,
            RawValue = raw,
            Category = Category,
            ImagePath = image,
            Arguments = args,
            State = task.Enabled ? EnableState.Enabled : EnableState.Disabled,
            StateSource = "Task.Enabled",
            UserSid = task.UserSid,
            PartiallyRead = task.PartiallyRead,
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TaskPath"] = task.Path,
                ["Triggers"] = string.Join("; ", task.Triggers),
                ["Actions"] = raw,
                ["Principal"] = task.Principal ?? "",
                ["RunLevel"] = task.RunLevel ?? "",
                ["LastRunTimeUtc"] = Stamp(task.LastRunTimeUtc),
                ["LastTaskResult"] = task.LastTaskResult.ToString(CultureInfo.InvariantCulture),
                ["NextRunTimeUtc"] = Stamp(task.NextRunTimeUtc),
                ["Xml"] = task.Xml ?? "",
            },
        };
    }

    private static string Stamp(DateTime? value)
        => value?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "";
}
