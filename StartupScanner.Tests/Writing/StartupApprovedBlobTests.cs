using System.Buffers.Binary;
using StartupScanner.Core.Writing;

namespace StartupScanner.Tests.Writing;

public class StartupApprovedBlobTests
{
    private static readonly DateTime DisabledAt = new(2026, 7, 30, 9, 12, 0, DateTimeKind.Utc);

    // Captured from a real machine: an enabled HKCU Run entry.
    private static readonly byte[] RealEnabled =
        { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    // Captured from a real machine: the same entry after Task Manager disabled it.
    private static readonly byte[] RealDisabled =
        { 0x03, 0x00, 0x00, 0x00, 0x2B, 0x9D, 0x1F, 0x28, 0x64, 0xD5, 0xDC, 0x01 };

    [Fact]
    public void RealEnabledBlobReadsAsEnabled() => Assert.True(StartupApprovedBlob.IsEnabled(RealEnabled));

    [Fact]
    public void RealDisabledBlobReadsAsDisabled() => Assert.False(StartupApprovedBlob.IsEnabled(RealDisabled));

    [Theory]
    [InlineData(0x00, true)]
    [InlineData(0x02, true)]
    [InlineData(0x06, true)]
    [InlineData(0x01, false)]
    [InlineData(0x03, false)]
    [InlineData(0x09, false)]
    public void StateIsTheLowBitOfByteZero(byte flag, bool expectedEnabled)
    {
        var blob = new byte[12];
        blob[0] = flag;
        Assert.Equal(expectedEnabled, StartupApprovedBlob.IsEnabled(blob));
    }

    [Fact]
    public void AnEmptyOrMissingBlobCountsAsEnabled()
    {
        Assert.True(StartupApprovedBlob.IsEnabled(Array.Empty<byte>()));
    }

    [Fact]
    public void EncodeDisableWritesFlag03AndTheFileTime()
    {
        var blob = StartupApprovedBlob.Encode(enabled: false, DisabledAt, existing: null);

        Assert.Equal(12, blob.Length);
        Assert.Equal(0x03, blob[0]);
        Assert.Equal(DisabledAt.ToFileTimeUtc(), BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(4, 8)));
    }

    [Fact]
    public void EncodeEnableWritesFlag02AndZeroesTheTimestamp()
    {
        var blob = StartupApprovedBlob.Encode(enabled: true, DisabledAt, existing: RealDisabled);

        Assert.Equal(0x02, blob[0]);
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(4, 8)));
    }

    [Fact]
    public void EncodePreservesTheOpaqueBytesOneToThree()
    {
        var existing = new byte[] { 0x02, 0xAA, 0xBB, 0xCC, 0, 0, 0, 0, 0, 0, 0, 0 };

        var blob = StartupApprovedBlob.Encode(enabled: false, DisabledAt, existing);

        Assert.Equal(0xAA, blob[1]);
        Assert.Equal(0xBB, blob[2]);
        Assert.Equal(0xCC, blob[3]);
    }

    [Fact]
    public void EncodeCreatesAFreshTwelveByteBlobWhenNoneExists()
    {
        var blob = StartupApprovedBlob.Encode(enabled: false, DisabledAt, existing: Array.Empty<byte>());

        Assert.Equal(12, blob.Length);
        Assert.Equal(0x03, blob[0]);
    }

    [Fact]
    public void EncodeToleratesAShortExistingBlob()
    {
        var blob = StartupApprovedBlob.Encode(enabled: false, DisabledAt, existing: new byte[] { 0x02, 0x11 });

        Assert.Equal(12, blob.Length);
        Assert.Equal(0x11, blob[1]);
    }

    [Fact]
    public void DisableThenEnableRoundTripsBackToTheEnabledBlob()
    {
        var disabled = StartupApprovedBlob.Encode(enabled: false, DisabledAt, RealEnabled);
        var enabled = StartupApprovedBlob.Encode(enabled: true, DisabledAt, disabled);

        Assert.Equal(RealEnabled, enabled);
    }

    [Fact]
    public void DisabledAtReadsTheTimestampBackAndIsNullWhenEnabled()
    {
        Assert.Null(StartupApprovedBlob.DisabledAtUtc(RealEnabled));

        var disabled = StartupApprovedBlob.Encode(enabled: false, DisabledAt, null);
        Assert.Equal(DisabledAt, StartupApprovedBlob.DisabledAtUtc(disabled));
    }
}
