using System.Globalization;
using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Models;
using StartupScanner.Core.Storage;

namespace StartupScanner.Core.Providers;

public sealed record GpoScriptEntry(string Section, int Index, string CmdLine, string Parameters);

/// <summary>
/// Group Policy startup, shutdown, logon, and logoff scripts. scripts.ini is the store; the
/// State\...\Scripts registry keys are a cache Group Policy rewrites, so they are reported as
/// context rather than treated as truth.
/// </summary>
public sealed class GpoScriptProvider(IFileSystem fs, IRegistry registry) : IStartupProvider
{
    private const string StateKey =
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State";

    public string Id => "gpo-script";
    public StartupCategory Category => StartupCategory.GpoScript;

    public static string MachineIniPath(IFileSystem fs)
        => fs.ExpandEnvironment(@"%SystemRoot%\System32\GroupPolicy\Machine\Scripts\scripts.ini");

    public static string UserIniPath(IFileSystem fs)
        => fs.ExpandEnvironment(@"%SystemRoot%\System32\GroupPolicy\User\Scripts\scripts.ini");

    public static IReadOnlyList<GpoScriptEntry> ParseScriptsIni(string text)
    {
        var commands = new Dictionary<(string Section, int Index), (string? CmdLine, string? Parameters)>();
        var order = new List<(string Section, int Index)>();
        var section = "";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().Trim('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            var split = line.IndexOf('=');
            if (split <= 0) continue;

            var key = line[..split];
            var value = line[(split + 1)..];
            var digits = new string(key.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) continue;

            var index = int.Parse(digits, CultureInfo.InvariantCulture);
            var field = key[digits.Length..];
            var slot = (section, index);

            if (!commands.ContainsKey(slot))
            {
                commands[slot] = (null, null);
                order.Add(slot);
            }

            commands[slot] = field.Equals("CmdLine", StringComparison.OrdinalIgnoreCase)
                ? (value, commands[slot].Parameters)
                : (commands[slot].CmdLine, value);
        }

        return order
            .Where(slot => !string.IsNullOrWhiteSpace(commands[slot].CmdLine))
            .Select(slot => new GpoScriptEntry(slot.Section, slot.Index,
                                               commands[slot].CmdLine!, commands[slot].Parameters ?? ""))
            .ToList();
    }

    public IEnumerable<StartupItem> Scan(ScanContext ctx)
    {
        foreach (var item in ScanIni(MachineIniPath(fs), "Machine", null, ctx)) yield return item;
        foreach (var item in ScanIni(UserIniPath(fs), "User", ctx.UserSid, ctx)) yield return item;
    }

    private IReadOnlyList<StartupItem> ScanIni(string iniPath, string scope, string? sid, ScanContext ctx)
    {
        string text;
        try
        {
            if (!fs.FileExists(iniPath)) return Array.Empty<StartupItem>();
            text = fs.ReadAllText(iniPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ctx.Errors.Add(new ScanError(Id, iniPath, ex.Message));
            return Array.Empty<StartupItem>();
        }

        var items = new List<StartupItem>();

        foreach (var entry in ParseScriptsIni(text))
        {
            var name = $"{entry.Section}[{entry.Index}]";
            var raw = string.IsNullOrWhiteSpace(entry.Parameters)
                ? entry.CmdLine
                : $"{entry.CmdLine} {entry.Parameters}";
            var (image, args) = CommandLine.Split(raw, fs);

            items.Add(new StartupItem
            {
                Id = Identity.Compute(Id, sid, iniPath, name),
                ProviderId = Id,
                Location = iniPath,
                Name = name,
                RawValue = raw,
                Category = Category,
                ImagePath = image,
                Arguments = args,
                State = EnableState.Enabled,
                StateSource = "ScriptsIniEntry",
                UserSid = sid,
                Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Section"] = entry.Section,
                    ["Index"] = entry.Index.ToString(CultureInfo.InvariantCulture),
                    ["CmdLine"] = entry.CmdLine,
                    ["Parameters"] = entry.Parameters,
                    ["Scope"] = scope,
                    ["RegistryStateScript"] = RegistryStateScript(scope, entry),
                },
            });
        }

        return items;
    }

    private string RegistryStateScript(string scope, GpoScriptEntry entry)
    {
        var path = $@"{StateKey}\{scope}\Scripts\{entry.Section}\{entry.Index}\{entry.Index}";
        try
        {
            using var key = registry.OpenKey(path);
            return key is null ? "" : RegistryValueCodec.ToDisplay(key.GetValue("Script"), key.GetValueType("Script"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "";
        }
    }
}
