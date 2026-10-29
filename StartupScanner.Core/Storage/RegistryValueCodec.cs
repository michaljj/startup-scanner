using System.Globalization;
using System.Text.Json;
using StartupScanner.Core.Abstractions;

namespace StartupScanner.Core.Storage;

/// <summary>
/// Lossless registry-value &lt;-&gt; string codec for the write journal, plus a separate
/// lossy rendering for display. Encoded form is "type:payload".
/// </summary>
public static class RegistryValueCodec
{
    public static string? Encode(object? value, RegistryValueType type)
    {
        if (value is null) return null;
        return type switch
        {
            RegistryValueType.String => "sz:" + (string)value,
            RegistryValueType.ExpandString => "expand:" + (string)value,
            RegistryValueType.Dword => "dword:" + Convert.ToInt32(value).ToString(CultureInfo.InvariantCulture),
            RegistryValueType.Qword => "qword:" + Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture),
            RegistryValueType.Binary => "binary:" + Convert.ToBase64String((byte[])value),
            RegistryValueType.MultiString => "multi:" + JsonSerializer.Serialize((string[])value),
            _ => "sz:" + Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    public static (object Value, RegistryValueType Type) Decode(string encoded)
    {
        var split = encoded.IndexOf(':');
        if (split < 0) throw new FormatException($"Malformed encoded registry value: '{encoded}'");
        var prefix = encoded[..split];
        var payload = encoded[(split + 1)..];

        return prefix switch
        {
            "sz" => (payload, RegistryValueType.String),
            "expand" => (payload, RegistryValueType.ExpandString),
            "dword" => (int.Parse(payload, CultureInfo.InvariantCulture), RegistryValueType.Dword),
            "qword" => (long.Parse(payload, CultureInfo.InvariantCulture), RegistryValueType.Qword),
            "binary" => (Convert.FromBase64String(payload), RegistryValueType.Binary),
            "multi" => (JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>(), RegistryValueType.MultiString),
            _ => throw new FormatException($"Unknown encoded registry value prefix: '{prefix}'")
        };
    }

    /// <summary>Human-facing rendering used for StartupItem.RawValue. Not reversible.</summary>
    public static string ToDisplay(object? value, RegistryValueType type) => value switch
    {
        null => "",
        string[] multi => string.Join("; ", multi),
        byte[] blob => string.Join(' ', blob.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };
}
