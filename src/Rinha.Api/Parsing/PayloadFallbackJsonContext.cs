using System.Text.Json;
using System.Text.Json.Serialization;
using Rinha.Api.Vector;

namespace Rinha.Api.Parsing;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(FraudPayloadDto))]
[JsonSerializable(typeof(TransactionDto))]
[JsonSerializable(typeof(CustomerDto))]
[JsonSerializable(typeof(MerchantDto))]
[JsonSerializable(typeof(TerminalDto))]
[JsonSerializable(typeof(LastTransactionDto))]
public partial class PayloadFallbackJsonContext : JsonSerializerContext;

public sealed class FraudPayloadDto
{
    public TransactionDto Transaction { get; set; } = null!;
    public CustomerDto Customer { get; set; } = null!;
    public MerchantDto Merchant { get; set; } = null!;
    public TerminalDto Terminal { get; set; } = null!;
    public LastTransactionDto? LastTransaction { get; set; }
}

public sealed class TransactionDto
{
    public double Amount { get; set; }
    public int Installments { get; set; }
    public string RequestedAt { get; set; } = "";
}

public sealed class CustomerDto
{
    public double AvgAmount { get; set; }
    public int TxCount24h { get; set; }
    public List<string> KnownMerchants { get; set; } = [];
}

public sealed class MerchantDto
{
    public string Id { get; set; } = "";
    public string Mcc { get; set; } = "";
    public double AvgAmount { get; set; }
}

public sealed class TerminalDto
{
    public bool IsOnline { get; set; }
    public bool CardPresent { get; set; }
    public double KmFromHome { get; set; }
}

public sealed class LastTransactionDto
{
    public string Timestamp { get; set; } = "";
    public double KmFromCurrent { get; set; }
}

public static class PayloadFallbackJson
{
    public static ParseResult TryParse(ReadOnlySpan<byte> payload, Span<short> output)
    {
        FraudPayloadDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(payload, PayloadFallbackJsonContext.Default.FraudPayloadDto);
        }
        catch
        {
            return ParseResult.Error;
        }

        if (parsed?.Transaction is null || parsed.Customer is null || parsed.Merchant is null || parsed.Terminal is null)
            return ParseResult.Error;

        var requestedParsed = PayloadParser.ParseDateTimePublic(System.Text.Encoding.UTF8.GetBytes(parsed.Transaction.RequestedAt));
        if (!requestedParsed.HasValue)
            return ParseResult.Error;

        long requestedMinute = requestedParsed.Value.EpochMinute;

        output[0] = PayloadParser.Quantize(parsed.Transaction.Amount / 10_000.0);
        output[1] = PayloadParser.Quantize(parsed.Transaction.Installments / 12.0);
        output[3] = PayloadParser.Quantize(requestedParsed.Value.Hour / 23.0);
        output[4] = PayloadParser.Quantize(requestedParsed.Value.DayOfWeek / 6.0);
        output[7] = PayloadParser.Quantize(parsed.Terminal.KmFromHome / 1_000.0);
        output[8] = PayloadParser.Quantize(parsed.Customer.TxCount24h / 20.0);
        output[9] = parsed.Terminal.IsOnline ? VectorConstants.Scale : (short)0;
        output[10] = parsed.Terminal.CardPresent ? VectorConstants.Scale : (short)0;
        output[12] = PayloadParser.Quantize(PayloadParser.MccRiskPublic(ParseMcc(parsed.Merchant.Mcc)));
        output[13] = PayloadParser.Quantize(parsed.Merchant.AvgAmount / 10_000.0);

        if (parsed.LastTransaction is not null)
        {
            var lastParsed = PayloadParser.ParseDateTimePublic(System.Text.Encoding.UTF8.GetBytes(parsed.LastTransaction.Timestamp));
            if (!lastParsed.HasValue)
                return ParseResult.Error;

            long minutesDiff = requestedMinute - lastParsed.Value.EpochMinute;
            output[5] = PayloadParser.Quantize(minutesDiff / 1_440.0);
            output[6] = PayloadParser.Quantize(parsed.LastTransaction.KmFromCurrent / 1_000.0);
        }
        else
        {
            output[5] = (short)-VectorConstants.Scale;
            output[6] = (short)-VectorConstants.Scale;
        }

        Span<ulong> knownHashes = stackalloc ulong[64];
        int knownCount = Math.Min(parsed.Customer.KnownMerchants.Count, 64);
        for (int i = 0; i < knownCount; i++)
            knownHashes[i] = PayloadParser.HashBytesPublic(System.Text.Encoding.UTF8.GetBytes(parsed.Customer.KnownMerchants[i]));

        output[2] = parsed.Customer.AvgAmount > 0.0
            ? PayloadParser.Quantize(parsed.Transaction.Amount / parsed.Customer.AvgAmount / 10.0)
            : VectorConstants.Scale;

        ulong merchantHash = PayloadParser.HashBytesPublic(System.Text.Encoding.UTF8.GetBytes(parsed.Merchant.Id));
        bool known = false;
        for (int i = 0; i < knownCount; i++)
        {
            if (knownHashes[i] == merchantHash)
            {
                known = true;
                break;
            }
        }

        output[11] = known ? (short)0 : VectorConstants.Scale;
        return ParseResult.Ok;
    }

    private static int ParseMcc(string mcc)
    {
        return PayloadParser.ParseMccPublic(System.Text.Encoding.UTF8.GetBytes(mcc));
    }
}
