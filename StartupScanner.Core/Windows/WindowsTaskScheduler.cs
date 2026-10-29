using System.Globalization;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Windows;

/// <summary>
/// Task Scheduler 2.0 through late-bound COM. Late binding avoids an interop assembly for the
/// handful of members needed, at the cost of no compile-time checking on those calls.
/// </summary>
public sealed class WindowsTaskScheduler : ITaskSchedulerService
{
    private const int TaskEnumHidden = 1;

    public IReadOnlyList<ScheduledTaskInfo> EnumerateTasks()
    {
        dynamic service = Connect();
        var tasks = new List<ScheduledTaskInfo>();
        Walk(service.GetFolder("\\"), tasks);
        return tasks;
    }

    public void SetEnabled(string taskPath, bool enabled)
    {
        dynamic service = Connect();
        var lastSlash = taskPath.LastIndexOf('\\');
        var folderPath = lastSlash <= 0 ? "\\" : taskPath[..lastSlash];
        var name = taskPath[(lastSlash + 1)..];

        dynamic folder = service.GetFolder(folderPath);
        dynamic task = folder.GetTask(name);
        task.Enabled = enabled;
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("The Task Scheduler COM service is not registered.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> into)
    {
        foreach (dynamic task in folder.GetTasks(TaskEnumHidden)) into.Add(Read(task, (string)folder.Path));
        foreach (dynamic child in folder.GetFolders(0)) Walk(child, into);
    }

    private static ScheduledTaskInfo Read(dynamic task, string folderPath)
    {
        var path = (string)task.Path;
        var name = (string)task.Name;

        try
        {
            dynamic definition = task.Definition;

            return new ScheduledTaskInfo
            {
                Path = path,
                Name = name,
                Folder = folderPath.Length == 0 ? "\\" : folderPath,
                Enabled = (bool)task.Enabled,
                // The collections are cast to IEnumerable so the call is not dynamically dispatched:
                // a lambda or method group cannot be an argument to a dynamic invocation.
                Triggers = Describe((System.Collections.IEnumerable)definition.Triggers,
                                    t => $"Type {(int)t.Type}: {(string)t.Id ?? ""}".Trim()),
                Actions = Describe((System.Collections.IEnumerable)definition.Actions, ActionText),
                Principal = Try(() => (string)definition.Principal.UserId) ?? Try(() => (string)definition.Principal.GroupId),
                RunLevel = Try(() => ((int)definition.Principal.RunLevel).ToString(CultureInfo.InvariantCulture)),
                LastRunTimeUtc = TryValue(() => ((DateTime)task.LastRunTime).ToUniversalTime()),
                LastTaskResult = TryValue(() => (int)task.LastTaskResult) ?? 0,
                NextRunTimeUtc = TryValue(() => ((DateTime)task.NextRunTime).ToUniversalTime()),
                Xml = Try(() => (string)definition.XmlText),
            };
        }
        catch (Exception)
        {
            // Protected tasks list but refuse to expose their definition, even elevated.
            return new ScheduledTaskInfo
            {
                Path = path,
                Name = name,
                Folder = folderPath.Length == 0 ? "\\" : folderPath,
                Enabled = TryValue(() => (bool)task.Enabled) ?? false,
                PartiallyRead = true,
            };
        }
    }

    private static string ActionText(dynamic action)
    {
        var path = Try(() => (string)action.Path);
        var arguments = Try(() => (string)action.Arguments);
        if (path is not null) return string.IsNullOrWhiteSpace(arguments) ? path : $"{path} {arguments}";

        return Try(() => (string)action.ClassId) ?? $"Type {(int)action.Type}";
    }

    private static IReadOnlyList<string> Describe(System.Collections.IEnumerable collection, Func<dynamic, string> render)
    {
        var described = new List<string>();
        foreach (dynamic entry in collection)
        {
            var text = Try(() => render(entry));
            if (!string.IsNullOrWhiteSpace(text)) described.Add(text!);
        }
        return described;
    }

    private static T? Try<T>(Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Value-type counterpart: an unconstrained T? is not Nullable&lt;T&gt;, so ?? would not compile.</summary>
    private static T? TryValue<T>(Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
