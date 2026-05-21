using Rinha.Api.Parsing;

namespace Rinha.Tests.Vector;

public class VectorContractTests
{
    [Theory]
    [InlineData("2024-01-15T08:00:00Z", 8, 0)]
    [InlineData("2024-02-29T23:59:00Z", 23, 3)]
    [InlineData("2024-03-01T00:00:00Z", 0, 4)]
    public void DateParser_JanFebMar(string iso, int hour, int dayOfWeek)
    {
        var parsed = PayloadParser.ParseDateTimePublic(System.Text.Encoding.UTF8.GetBytes(iso));
        Assert.NotNull(parsed);
        Assert.Equal(hour, parsed!.Value.Hour);
        Assert.Equal(dayOfWeek, parsed.Value.DayOfWeek);
    }

    [Theory]
    [InlineData(-1.5, -10000)]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 10000)]
    [InlineData(0.5, 5000)]
    [InlineData(0.1234, 1234)]
    public void Quantize_BoundaryValues(double input, short expected)
    {
        Assert.Equal(expected, PayloadParser.Quantize(input));
    }

    [Fact]
    public void MccRisk_KnownAndUnknown()
    {
        Assert.Equal(0.15, PayloadParser.MccRiskPublic(PayloadParser.ParseMccPublic("5411"u8)));
        Assert.Equal(0.50, PayloadParser.MccRiskPublic(PayloadParser.ParseMccPublic("9999"u8)));
        Assert.Equal(0.50, PayloadParser.MccRiskPublic(PayloadParser.ParseMccPublic("abcd"u8)));
    }

    [Fact]
    public void HashBytes_IsDeterministic()
    {
        var a = PayloadParser.HashBytesPublic("merchant-123"u8);
        var b = PayloadParser.HashBytesPublic("merchant-123"u8);
        Assert.Equal(a, b);
        Assert.NotEqual(a, PayloadParser.HashBytesPublic("merchant-456"u8));
    }
}
