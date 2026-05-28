using System.Text;
using System.Text.Json;
using Rinha.Api.Vector;

namespace Rinha.Api.Parsing;

internal static class PayloadFlexibleJson
{
    public static ParseResult TryParse(ReadOnlySpan<byte> payload, Span<short> output)
    {
        try
        {
            var reader = new Utf8JsonReader(payload, isFinalBlock: true, state: default);
            if (!TryReadPayload(ref reader, out var parsed))
                return ParseResult.Error;

            return TryBuildVector(parsed, output);
        }
        catch (JsonException)
        {
            return ParseResult.Error;
        }
    }

    private static ParseResult TryBuildVector(Payload parsed, Span<short> output)
    {
        var requestedParsed = PayloadParser.ParseDateTimePublic(Encoding.UTF8.GetBytes(parsed.Transaction.RequestedAt));
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
        output[12] = PayloadParser.Quantize(PayloadParser.MccRiskPublic(
            PayloadParser.ParseMccPublic(Encoding.UTF8.GetBytes(parsed.Merchant.Mcc))));
        output[13] = PayloadParser.Quantize(parsed.Merchant.AvgAmount / 10_000.0);

        if (parsed.LastTransaction is { } lastTransaction)
        {
            var lastParsed = PayloadParser.ParseDateTimePublic(Encoding.UTF8.GetBytes(lastTransaction.Timestamp));
            if (!lastParsed.HasValue)
                return ParseResult.Error;

            long minutesDiff = requestedMinute - lastParsed.Value.EpochMinute;
            output[5] = PayloadParser.Quantize(minutesDiff / 1_440.0);
            output[6] = PayloadParser.Quantize(lastTransaction.KmFromCurrent / 1_000.0);
        }
        else
        {
            output[5] = (short)-VectorConstants.Scale;
            output[6] = (short)-VectorConstants.Scale;
        }

        Span<ulong> knownHashes = stackalloc ulong[64];
        int knownCount = Math.Min(parsed.Customer.KnownMerchants.Count, 64);
        for (int i = 0; i < knownCount; i++)
            knownHashes[i] = PayloadParser.HashBytesPublic(Encoding.UTF8.GetBytes(parsed.Customer.KnownMerchants[i]));

        output[2] = parsed.Customer.AvgAmount > 0.0
            ? PayloadParser.Quantize(parsed.Transaction.Amount / parsed.Customer.AvgAmount / 10.0)
            : VectorConstants.Scale;

        ulong merchantHash = PayloadParser.HashBytesPublic(Encoding.UTF8.GetBytes(parsed.Merchant.Id));
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

    private static bool TryReadPayload(ref Utf8JsonReader reader, out Payload payload)
    {
        payload = default;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return false;

        Transaction? transaction = null;
        Customer? customer = null;
        Merchant? merchant = null;
        Terminal? terminal = null;
        LastTransaction? lastTransaction = null;
        bool lastTransactionSet = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                return false;

            var key = reader.ValueSpan;
            if (!reader.Read())
                return false;

            if (FieldMatches(key, "transaction"u8))
            {
                if (!TryReadTransaction(ref reader, out var tx))
                    return false;
                transaction = tx;
            }
            else if (FieldMatches(key, "customer"u8))
            {
                if (!TryReadCustomer(ref reader, out var c))
                    return false;
                customer = c;
            }
            else if (FieldMatches(key, "merchant"u8))
            {
                if (!TryReadMerchant(ref reader, out var m))
                    return false;
                merchant = m;
            }
            else if (FieldMatches(key, "terminal"u8))
            {
                if (!TryReadTerminal(ref reader, out var t))
                    return false;
                terminal = t;
            }
            else if (FieldMatches(key, "last_transaction"u8))
            {
                lastTransactionSet = true;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    lastTransaction = null;
                }
                else if (!TryReadLastTransaction(ref reader, out var lt))
                {
                    return false;
                }
                else
                {
                    lastTransaction = lt;
                }
            }
            else
            {
                SkipValue(ref reader);
            }
        }

        if (transaction is null || customer is null || merchant is null || terminal is null)
            return false;

        payload = new Payload
        {
            Transaction = transaction.Value,
            Customer = customer.Value,
            Merchant = merchant.Value,
            Terminal = terminal.Value,
            LastTransaction = lastTransactionSet ? lastTransaction : null
        };
        return true;
    }

    private static bool TryReadTransaction(ref Utf8JsonReader reader, out Transaction transaction)
    {
        transaction = default;
        if (reader.TokenType != JsonTokenType.StartObject)
            return false;

        double? amount = null;
        int? installments = null;
        string? requestedAt = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                return false;

            var key = reader.ValueSpan;
            if (!reader.Read())
                return false;

            if (FieldMatches(key, "amount"u8))
            {
                if (!TryReadDouble(ref reader, out var value))
                    return false;
                amount = value;
            }
            else if (FieldMatches(key, "installments"u8))
            {
                if (!TryReadInt(ref reader, out var value))
                    return false;
                installments = value;
            }
            else if (FieldMatches(key, "requested_at"u8))
            {
                if (!TryReadString(ref reader, out var value))
                    return false;
                requestedAt = value;
            }
            else
            {
                SkipValue(ref reader);
            }
        }

        if (amount is null || installments is null || requestedAt is null)
            return false;

        transaction = new Transaction(amount.Value, installments.Value, requestedAt);
        return true;
    }

    private static bool TryReadCustomer(ref Utf8JsonReader reader, out Customer customer)
    {
        customer = default;
        if (reader.TokenType != JsonTokenType.StartObject)
            return false;

        double? avgAmount = null;
        int? txCount24h = null;
        List<string>? knownMerchants = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                return false;

            var key = reader.ValueSpan;
            if (!reader.Read())
                return false;

            if (FieldMatches(key, "avg_amount"u8))
            {
                if (!TryReadDouble(ref reader, out var value))
                    return false;
                avgAmount = value;
            }
            else if (FieldMatches(key, "tx_count_24h"u8))
            {
                if (!TryReadInt(ref reader, out var value))
                    return false;
                txCount24h = value;
            }
            else if (FieldMatches(key, "known_merchants"u8))
            {
                if (!TryReadStringArray(ref reader, out var value))
                    return false;
                knownMerchants = value;
            }
            else
            {
                SkipValue(ref reader);
            }
        }

        if (avgAmount is null || txCount24h is null || knownMerchants is null)
            return false;

        customer = new Customer(avgAmount.Value, txCount24h.Value, knownMerchants);
        return true;
    }

    private static bool TryReadMerchant(ref Utf8JsonReader reader, out Merchant merchant)
    {
        merchant = default;
        if (reader.TokenType != JsonTokenType.StartObject)
            return false;

        string? id = null;
        string? mcc = null;
        double? avgAmount = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                return false;

            var key = reader.ValueSpan;
            if (!reader.Read())
                return false;

            if (FieldMatches(key, "id"u8))
            {
                if (!TryReadString(ref reader, out var value))
                    return false;
                id = value;
            }
            else if (FieldMatches(key, "mcc"u8))
            {
                if (!TryReadString(ref reader, out var value))
                    return false;
                mcc = value;
            }
            else if (FieldMatches(key, "avg_amount"u8))
            {
                if (!TryReadDouble(ref reader, out var value))
                    return false;
                avgAmount = value;
            }
            else
            {
                SkipValue(ref reader);
            }
        }

        if (id is null || mcc is null || avgAmount is null)
            return false;

        merchant = new Merchant(id, mcc, avgAmount.Value);
        return true;
    }

    private static bool TryReadTerminal(ref Utf8JsonReader reader, out Terminal terminal)
    {
        terminal = default;
        if (reader.TokenType != JsonTokenType.StartObject)
            return false;

        bool? isOnline = null;
        bool? cardPresent = null;
        double? kmFromHome = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                return false;

            var key = reader.ValueSpan;
            if (!reader.Read())
                return false;

            if (FieldMatches(key, "is_online"u8))
            {
                if (!TryReadBool(ref reader, out var value))
                    return false;
                isOnline = value;
            }
            else if (FieldMatches(key, "card_present"u8))
            {
                if (!TryReadBool(ref reader, out var value))
                    return false;
                cardPresent = value;
            }
            else if (FieldMatches(key, "km_from_home"u8))
            {
                if (!TryReadDouble(ref reader, out var value))
                    return false;
                kmFromHome = value;
            }
            else
            {
                SkipValue(ref reader);
            }
        }

        if (isOnline is null || cardPresent is null || kmFromHome is null)
            return false;

        terminal = new Terminal(isOnline.Value, cardPresent.Value, kmFromHome.Value);
        return true;
    }

    private static bool TryReadLastTransaction(ref Utf8JsonReader reader, out LastTransaction lastTransaction)
    {
        lastTransaction = default;
        if (reader.TokenType != JsonTokenType.StartObject)
            return false;

        string? timestamp = null;
        double? kmFromCurrent = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                return false;

            var key = reader.ValueSpan;
            if (!reader.Read())
                return false;

            if (FieldMatches(key, "timestamp"u8))
            {
                if (!TryReadString(ref reader, out var value))
                    return false;
                timestamp = value;
            }
            else if (FieldMatches(key, "km_from_current"u8))
            {
                if (!TryReadDouble(ref reader, out var value))
                    return false;
                kmFromCurrent = value;
            }
            else
            {
                SkipValue(ref reader);
            }
        }

        if (timestamp is null || kmFromCurrent is null)
            return false;

        lastTransaction = new LastTransaction(timestamp, kmFromCurrent.Value);
        return true;
    }

    private static bool TryReadStringArray(ref Utf8JsonReader reader, out List<string> values)
    {
        values = [];
        if (reader.TokenType != JsonTokenType.StartArray)
            return false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return true;

            if (reader.TokenType != JsonTokenType.String)
                return false;

            values.Add(reader.GetString() ?? "");
        }

        return false;
    }

    private static bool TryReadString(ref Utf8JsonReader reader, out string value)
    {
        value = "";
        if (reader.TokenType != JsonTokenType.String)
            return false;

        value = reader.GetString() ?? "";
        return true;
    }

    private static bool TryReadDouble(ref Utf8JsonReader reader, out double value)
    {
        value = 0;
        if (reader.TokenType == JsonTokenType.Number)
            return reader.TryGetDouble(out value);

        return false;
    }

    private static bool TryReadInt(ref Utf8JsonReader reader, out int value)
    {
        value = 0;
        if (reader.TokenType == JsonTokenType.Number)
            return reader.TryGetInt32(out value);

        return false;
    }

    private static bool TryReadBool(ref Utf8JsonReader reader, out bool value)
    {
        value = false;
        return reader.TokenType switch
        {
            JsonTokenType.True => (value = true) == true,
            JsonTokenType.False => true,
            _ => false
        };
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                {
                    int depth = 0;
                    do
                    {
                        switch (reader.TokenType)
                        {
                            case JsonTokenType.StartObject:
                            case JsonTokenType.StartArray:
                                depth++;
                                break;
                            case JsonTokenType.EndObject:
                            case JsonTokenType.EndArray:
                                depth--;
                                break;
                        }
                    } while (depth > 0 && reader.Read());
                    break;
                }
            default:
                reader.TrySkip();
                break;
        }
    }

    internal static bool FieldMatches(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        int ai = 0;
        int ei = 0;

        while (ai < actual.Length)
        {
            byte ab = actual[ai];
            if (ab is (byte)'_' or (byte)'-')
            {
                ai++;
                continue;
            }

            while (ei < expected.Length && expected[ei] is (byte)'_' or (byte)'-')
                ei++;

            if (ei >= expected.Length)
                return false;

            byte eb = expected[ei];
            if (ToLowerAscii(ab) != ToLowerAscii(eb))
                return false;

            ai++;
            ei++;
        }

        while (ei < expected.Length)
        {
            if (expected[ei] is not ((byte)'_' or (byte)'-'))
                return false;
            ei++;
        }

        return true;
    }

    private static byte ToLowerAscii(byte b) =>
        b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    private readonly struct Payload
    {
        public required Transaction Transaction { get; init; }
        public required Customer Customer { get; init; }
        public required Merchant Merchant { get; init; }
        public required Terminal Terminal { get; init; }
        public LastTransaction? LastTransaction { get; init; }
    }

    private readonly struct Transaction(double amount, int installments, string requestedAt)
    {
        public double Amount { get; } = amount;
        public int Installments { get; } = installments;
        public string RequestedAt { get; } = requestedAt;
    }

    private readonly struct Customer(double avgAmount, int txCount24h, List<string> knownMerchants)
    {
        public double AvgAmount { get; } = avgAmount;
        public int TxCount24h { get; } = txCount24h;
        public List<string> KnownMerchants { get; } = knownMerchants;
    }

    private readonly struct Merchant(string id, string mcc, double avgAmount)
    {
        public string Id { get; } = id;
        public string Mcc { get; } = mcc;
        public double AvgAmount { get; } = avgAmount;
    }

    private readonly struct Terminal(bool isOnline, bool cardPresent, double kmFromHome)
    {
        public bool IsOnline { get; } = isOnline;
        public bool CardPresent { get; } = cardPresent;
        public double KmFromHome { get; } = kmFromHome;
    }

    private readonly struct LastTransaction(string timestamp, double kmFromCurrent)
    {
        public string Timestamp { get; } = timestamp;
        public double KmFromCurrent { get; } = kmFromCurrent;
    }
}
