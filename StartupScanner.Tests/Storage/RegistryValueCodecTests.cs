using StartupScanner.Core.Abstractions;
using StartupScanner.Core.Storage;

namespace StartupScanner.Tests.Storage;

public class RegistryValueCodecTests
{
    [Theory]
    [InlineData("plain text", RegistryValueType.String)]
    [InlineData(@"%SystemRoot%\x.exe", RegistryValueType.ExpandString)]
    public void RoundTripsStrings(string value, RegistryValueType type)
    {
        var (decoded, decodedType) = RegistryValueCodec.Decode(RegistryValueCodec.Encode(value, type)!);
        Assert.Equal(value, decoded);
        Assert.Equal(type, decodedType);
    }

    [Fact]
    public void RoundTripsDword()
    {
        var (decoded, type) = RegistryValueCodec.Decode(RegistryValueCodec.Encode(4, RegistryValueType.Dword)!);
        Assert.Equal(4, decoded);
        Assert.Equal(RegistryValueType.Dword, type);
    }

    [Fact]
    public void RoundTripsQword()
    {
        var (decoded, type) = RegistryValueCodec.Decode(RegistryValueCodec.Encode(9_000_000_000L, RegistryValueType.Qword)!);
        Assert.Equal(9_000_000_000L, decoded);
        Assert.Equal(RegistryValueType.Qword, type);
    }

    [Fact]
    public void RoundTripsBinaryByteForByte()
    {
        var blob = new byte[] { 0x02, 0x00, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00, 0x00, 0x00 };
        var (decoded, type) = RegistryValueCodec.Decode(RegistryValueCodec.Encode(blob, RegistryValueType.Binary)!);
        Assert.Equal(blob, Assert.IsType<byte[]>(decoded));
        Assert.Equal(RegistryValueType.Binary, type);
    }

    [Fact]
    public void RoundTripsMultiStringIncludingEmbeddedSeparators()
    {
        var value = new[] { "autocheck autochk *", "a;b", "" };
        var (decoded, type) = RegistryValueCodec.Decode(RegistryValueCodec.Encode(value, RegistryValueType.MultiString)!);
        Assert.Equal(value, Assert.IsType<string[]>(decoded));
        Assert.Equal(RegistryValueType.MultiString, type);
    }

    [Fact]
    public void EncodesNullAsNull()
    {
        Assert.Null(RegistryValueCodec.Encode(null, RegistryValueType.None));
    }

    [Fact]
    public void DisplayJoinsMultiStringWithSemicolons()
    {
        var display = RegistryValueCodec.ToDisplay(new[] { "autocheck autochk *" }, RegistryValueType.MultiString);
        Assert.Equal("autocheck autochk *", display);

        var two = RegistryValueCodec.ToDisplay(new[] { "a", "b" }, RegistryValueType.MultiString);
        Assert.Equal("a; b", two);
    }

    [Fact]
    public void DisplayRendersBinaryAsHex()
    {
        Assert.Equal("02 00 03", RegistryValueCodec.ToDisplay(new byte[] { 2, 0, 3 }, RegistryValueType.Binary));
    }

    [Fact]
    public void DecodeRejectsUnknownPrefix()
    {
        Assert.Throws<FormatException>(() => RegistryValueCodec.Decode("nope:whatever"));
    }
}
