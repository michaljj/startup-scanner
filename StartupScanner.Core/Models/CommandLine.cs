using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Models;

public static class CommandLine
{
    /// <summary>
    /// Splits a stored autostart command into image path and arguments.
    /// Environment variables are expanded first. Unquoted paths containing spaces are
    /// resolved by probing the filesystem for the longest existing prefix.
    /// </summary>
    public static (string? ImagePath, string? Arguments) Split(string rawValue, IFileSystem fs)
    {
        var text = fs.ExpandEnvironment(rawValue ?? "").Trim();
        if (text.Length == 0) return (null, null);

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close > 0)
                return (text[1..close], Rest(text[(close + 1)..]));
            return (text.Trim('"'), null);
        }

        // Longest existing prefix wins: handles "C:\Program Files\My App\run.exe /silent".
        for (var i = text.Length; i > 0; i--)
        {
            if (i < text.Length && text[i] != ' ') continue;
            var candidate = text[..i].TrimEnd(',', ' ');
            if (candidate.Length > 0 && fs.FileExists(candidate))
                return (candidate, Rest(text[i..]));
        }

        var space = text.IndexOf(' ');
        if (space < 0) return (text.TrimEnd(','), null);
        return (text[..space].TrimEnd(','), Rest(text[space..]));
    }

    private static string? Rest(string tail)
    {
        var trimmed = tail.Trim().TrimStart(',').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
