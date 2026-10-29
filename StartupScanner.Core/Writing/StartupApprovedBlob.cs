using System.Buffers.Binary;

namespace StartupScanner.Core.Writing;

/// <summary>
/// The 12-byte StartupApproved value Task Manager and msconfig use.
/// Byte 0 is the state flag, bytes 1-3 are opaque and preserved verbatim, bytes 4-11 are a
/// little-endian FILETIME of the disable moment (zero while enabled).
/// </summary>
public static class StartupApprovedBlob
{
    public const int Length = 12;

    /// <summary>
    /// Windows has written several flag bytes over the years (0x00/0x02/0x06 enabled,
    /// 0x01/0x03/0x09 disabled). The low bit is the part that has always meant "disabled".
    /// </summary>
    public static bool IsEnabled(byte[] blob) => blob.Length == 0 || (blob[0] & 0x01) == 0;

    public static byte[] Encode(bool enabled, DateTime disabledAtUtc, byte[]? existing)
    {
        var blob = new byte[Length];
        if (existing is { Length: > 0 })
            Array.Copy(existing, blob, Math.Min(existing.Length, Length));

        blob[0] = enabled ? (byte)0x02 : (byte)0x03;
        BinaryPrimitives.WriteInt64LittleEndian(
            blob.AsSpan(4, 8), enabled ? 0L : disabledAtUtc.ToFileTimeUtc());
        return blob;
    }

    public static DateTime? DisabledAtUtc(byte[] blob)
    {
        if (blob.Length < Length) return null;
        var stamp = BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(4, 8));
        return stamp == 0 ? null : DateTime.FromFileTimeUtc(stamp);
    }
}
