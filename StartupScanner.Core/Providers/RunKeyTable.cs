using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Providers;

/// <summary>
/// One row per concrete autostart key. RootToken is "HKLM" or the "%USER%" sentinel, which the
/// provider replaces with ScanContext.UserRoot — that is how a logged-out user's mounted hive is
/// scanned without provider changes. Adding a location is one row here, not new code.
/// </summary>
public sealed record RunKeyDefinition(
    string RootToken,
    string SubKey,
    RegistryViewKind View,
    string? ApprovedSubKey,
    bool WalkSubKeys = false);

public static class RunKeyTable
{
    private const string MachineCv = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private const string UserCv = @"Software\Microsoft\Windows\CurrentVersion";
    private const string MachineTs = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Terminal Server\Install\Software\Microsoft\Windows\CurrentVersion";
    private const string UserTs = @"Software\Microsoft\Windows NT\CurrentVersion\Terminal Server\Install\Software\Microsoft\Windows\CurrentVersion";

    public static readonly IReadOnlyList<RunKeyDefinition> All = Build();

    private static List<RunKeyDefinition> Build()
    {
        var rows = new List<RunKeyDefinition>();

        // HKLM\SOFTWARE is WOW64-redirected, so every machine row is scanned in both views.
        foreach (var view in new[] { RegistryViewKind.Registry64, RegistryViewKind.Registry32 })
        {
            var approved = view == RegistryViewKind.Registry32 ? "Run32" : "Run";
            rows.Add(new("HKLM", MachineCv + @"\Run", view, approved));
            rows.Add(new("HKLM", MachineCv + @"\RunOnce", view, null));
            rows.Add(new("HKLM", MachineCv + @"\RunOnce\Setup", view, null));
            rows.Add(new("HKLM", MachineCv + @"\RunOnceEx", view, null, WalkSubKeys: true));
            rows.Add(new("HKLM", MachineCv + @"\RunServices", view, null));
            rows.Add(new("HKLM", MachineCv + @"\RunServicesOnce", view, null));
            rows.Add(new("HKLM", MachineCv + @"\Policies\Explorer\Run", view, null));
            rows.Add(new("HKLM", MachineTs + @"\Run", view, null));
            rows.Add(new("HKLM", MachineTs + @"\RunOnce", view, null));
        }

        // HKCU\Software is not WOW64-redirected, so per-user rows are scanned once.
        rows.Add(new("%USER%", UserCv + @"\Run", RegistryViewKind.Default, "Run"));
        rows.Add(new("%USER%", UserCv + @"\RunOnce", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserCv + @"\RunOnce\Setup", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserCv + @"\RunOnceEx", RegistryViewKind.Default, null, WalkSubKeys: true));
        rows.Add(new("%USER%", UserCv + @"\RunServices", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserCv + @"\RunServicesOnce", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserCv + @"\Policies\Explorer\Run", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserTs + @"\Run", RegistryViewKind.Default, null));
        rows.Add(new("%USER%", UserTs + @"\RunOnce", RegistryViewKind.Default, null));

        return rows;
    }
}
