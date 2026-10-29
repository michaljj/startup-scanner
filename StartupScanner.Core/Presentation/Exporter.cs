using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StartupScanner.Core.Models;

namespace StartupScanner.Core.Presentation;

/// <summary>Writes the currently filtered rows. FileFacts are excluded: an export is an inventory, not a hash dump.</summary>
public static class Exporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] Headers =
    {
        "Id", "ProviderId", "Category", "Name", "Location", "RawValue", "ImagePath", "Arguments",
        "State", "StateSource", "Risk", "UserSid", "Publisher",
    };

    public static string ToCsv(IEnumerable<StartupItem> items)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', Headers)).Append('\n');

        foreach (var item in items)
        {
            builder.Append(string.Join(',', new[]
            {
                Field(item.Id), Field(item.ProviderId), Field(item.Category.ToString()), Field(item.Name),
                Field(item.Location), Field(item.RawValue), Field(item.ImagePath), Field(item.Arguments),
                Field(item.State.ToString()), Field(item.StateSource), Field(item.Risk.ToString()),
                Field(item.UserSid), Field(item.File?.Signature?.SignerName),
            })).Append('\n');
        }

        return builder.ToString();
    }

    public static string ToJson(IEnumerable<StartupItem> items)
        => JsonSerializer.Serialize(items.Select(Row).ToList(), Json);

    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // Newlines are folded to spaces so one row stays one line.
        var flat = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return flat.Contains(',') || flat.Contains('"')
            ? "\"" + flat.Replace("\"", "\"\"") + "\""
            : flat;
    }

    private static object Row(StartupItem item) => new
    {
        item.Id, item.ProviderId, Category = item.Category.ToString(), item.Name, item.Location,
        item.RawValue, item.ImagePath, item.Arguments, State = item.State.ToString(), item.StateSource,
        Risk = item.Risk.ToString(), item.UserSid,
        Publisher = item.File?.Signature?.SignerName,
        item.Extra,
    };
}
