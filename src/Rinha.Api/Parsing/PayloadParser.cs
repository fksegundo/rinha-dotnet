using System.Buffers.Text;
using System.Numerics;
using System.Runtime.CompilerServices;
using Rinha.Api.Vector;

namespace Rinha.Api.Parsing;

public enum ParseResult
{
    Ok,
    Error
}

public static class PayloadParser
{
    public static ParseResult TryParse(ReadOnlySpan<byte> payload, Span<short> output)
    {
        output.Clear();

        if (TryParseTransactionFirst(payload, output))
            return ParseResult.Ok;

        output.Clear();

        if (TryParseCustomerFirst(payload, output))
            return ParseResult.Ok;

        output.Clear();

        return PayloadFlexibleJson.TryParse(payload, output);
    }

    internal static ParsedDateTime? ParseDateTimePublic(ReadOnlySpan<byte> iso)
    {
        return ParseDateTime(iso, out var parsed) ? parsed : null;
    }

    internal static int ParseMccPublic(ReadOnlySpan<byte> mcc) => ParseMcc(mcc);

    internal static double MccRiskPublic(int mcc) => MccRisk(mcc);

    internal static ulong HashBytesPublic(ReadOnlySpan<byte> bytes) => HashBytes(bytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short Quantize(double value)
    {
        if (value <= -1.0)
            return -VectorConstants.Scale;
        if (value <= 0.0)
            return 0;
        if (value >= 1.0)
            return VectorConstants.Scale;
        return (short)Math.Round(value * VectorConstants.Scale);
    }

    private static bool TryParseTransactionFirst(ReadOnlySpan<byte> json, Span<short> output)
    {
        int cursor = 0;
        Span<ulong> knownHashes = stackalloc ulong[64];
        int knownCount;

        if (!FindAndReadDouble(json, "\"amount\""u8, ref cursor, out double amount))
            return false;
        output[0] = Quantize(amount / 10_000.0);

        if (!FindAndReadInt(json, "\"installments\""u8, ref cursor, out int installments))
            return false;
        output[1] = Quantize(installments / 12.0);

        if (!FindAndReadString(json, "\"requested_at\""u8, cursor, out cursor, out var requestedAt))
            return false;
        if (!ParseDateTime(requestedAt, out var parsed))
            return false;
        long requestedMinute = parsed.EpochMinute;
        output[3] = Quantize(parsed.Hour / 23.0);
        output[4] = Quantize(parsed.DayOfWeek / 6.0);

        if (!FindAndReadDouble(json, "\"avg_amount\""u8, ref cursor, out double customerAvgAmount))
            return false;

        if (!FindAndReadInt(json, "\"tx_count_24h\""u8, ref cursor, out int txCount24h))
            return false;
        output[8] = Quantize(txCount24h / 20.0);

        if (!FindAndReadKnownMerchants(json, ref cursor, knownHashes, out knownCount))
            return false;

        if (!FindAndReadString(json, "\"id\""u8, cursor, out cursor, out var merchantId))
            return false;
        ulong merchantHash = HashBytes(merchantId);

        if (!FindAndReadString(json, "\"mcc\""u8, cursor, out cursor, out var mcc))
            return false;
        output[12] = Quantize(MccRisk(ParseMcc(mcc)));

        if (!FindAndReadDouble(json, "\"avg_amount\""u8, ref cursor, out double merchantAvgAmount))
            return false;
        output[13] = Quantize(merchantAvgAmount / 10_000.0);

        if (!FindAndReadBool(json, "\"is_online\""u8, ref cursor, out bool isOnline))
            return false;
        output[9] = isOnline ? VectorConstants.Scale : (short)0;

        if (!FindAndReadBool(json, "\"card_present\""u8, ref cursor, out bool cardPresent))
            return false;
        output[10] = cardPresent ? VectorConstants.Scale : (short)0;

        if (!FindAndReadDouble(json, "\"km_from_home\""u8, ref cursor, out double kmFromHome))
            return false;
        output[7] = Quantize(kmFromHome / 1_000.0);

        if (!FindValueStart(json, "\"last_transaction\""u8, ref cursor, out int lastValueStart))
            return false;

        if (json[lastValueStart] == (byte)'n')
        {
            output[5] = (short)-VectorConstants.Scale;
            output[6] = (short)-VectorConstants.Scale;
        }
        else
        {
            cursor = lastValueStart;
            if (!FindAndReadString(json, "\"timestamp\""u8, cursor, out cursor, out var lastTimestamp))
                return false;
            if (!FindAndReadDouble(json, "\"km_from_current\""u8, ref cursor, out double lastKm))
                return false;

            if (!ParseDateTime(lastTimestamp, out var lastParsed))
                return false;
            long minutesDiff = requestedMinute - lastParsed.EpochMinute;
            output[5] = Quantize(minutesDiff / 1_440.0);
            output[6] = Quantize(lastKm / 1_000.0);
        }

        FinishVector(output, amount, customerAvgAmount, merchantHash, knownHashes[..knownCount]);
        return true;
    }

    private static bool TryParseCustomerFirst(ReadOnlySpan<byte> json, Span<short> output)
    {
        int cursor = 0;
        Span<ulong> knownHashes = stackalloc ulong[64];

        if (!FindAndReadDouble(json, "\"avg_amount\""u8, ref cursor, out double customerAvgAmount))
            return false;

        if (!FindAndReadInt(json, "\"tx_count_24h\""u8, ref cursor, out int txCount24h))
            return false;
        output[8] = Quantize(txCount24h / 20.0);

        if (!FindAndReadKnownMerchants(json, ref cursor, knownHashes, out int knownCount))
            return false;

        if (!FindValueStart(json, "\"last_transaction\""u8, ref cursor, out int lastValueStart))
            return false;

        bool hasLastTransaction = json[lastValueStart] != (byte)'n';
        ReadOnlySpan<byte> lastTimestamp = default;
        double lastKm = 0;
        if (hasLastTransaction)
        {
            cursor = lastValueStart;
            if (!FindAndReadString(json, "\"timestamp\""u8, cursor, out cursor, out lastTimestamp))
                return false;
            if (!FindAndReadDouble(json, "\"km_from_current\""u8, ref cursor, out lastKm))
                return false;
        }

        if (!FindAndReadString(json, "\"id\""u8, cursor, out cursor, out var merchantId))
            return false;
        ulong merchantHash = HashBytes(merchantId);

        if (!FindAndReadString(json, "\"mcc\""u8, cursor, out cursor, out var mcc))
            return false;
        output[12] = Quantize(MccRisk(ParseMcc(mcc)));

        if (!FindAndReadDouble(json, "\"avg_amount\""u8, ref cursor, out double merchantAvgAmount))
            return false;
        output[13] = Quantize(merchantAvgAmount / 10_000.0);

        if (!FindAndReadBool(json, "\"is_online\""u8, ref cursor, out bool isOnline))
            return false;
        output[9] = isOnline ? VectorConstants.Scale : (short)0;

        if (!FindAndReadBool(json, "\"card_present\""u8, ref cursor, out bool cardPresent))
            return false;
        output[10] = cardPresent ? VectorConstants.Scale : (short)0;

        if (!FindAndReadDouble(json, "\"km_from_home\""u8, ref cursor, out double kmFromHome))
            return false;
        output[7] = Quantize(kmFromHome / 1_000.0);

        if (!FindAndReadDouble(json, "\"amount\""u8, ref cursor, out double amount))
            return false;
        output[0] = Quantize(amount / 10_000.0);

        if (!FindAndReadInt(json, "\"installments\""u8, ref cursor, out int installments))
            return false;
        output[1] = Quantize(installments / 12.0);

        if (!FindAndReadString(json, "\"requested_at\""u8, cursor, out cursor, out var requestedAt))
            return false;
        if (!ParseDateTime(requestedAt, out var parsed))
            return false;
        long requestedMinute = parsed.EpochMinute;
        output[3] = Quantize(parsed.Hour / 23.0);
        output[4] = Quantize(parsed.DayOfWeek / 6.0);

        if (!hasLastTransaction)
        {
            output[5] = (short)-VectorConstants.Scale;
            output[6] = (short)-VectorConstants.Scale;
        }
        else
        {
            if (!ParseDateTime(lastTimestamp, out var lastParsed))
                return false;
            long minutesDiff = requestedMinute - lastParsed.EpochMinute;
            output[5] = Quantize(minutesDiff / 1_440.0);
            output[6] = Quantize(lastKm / 1_000.0);
        }

        FinishVector(output, amount, customerAvgAmount, merchantHash, knownHashes[..knownCount]);
        return true;
    }

    private static bool FindValueStart(ReadOnlySpan<byte> json, ReadOnlySpan<byte> name, ref int cursor, out int valueStart)
    {
        valueStart = 0;
        if (cursor >= json.Length)
            return false;

        int rel = json[cursor..].IndexOf(name);
        if (rel < 0)
            return false;

        int afterName = cursor + rel + name.Length;
        int relColon = json[afterName..].IndexOf((byte)':');
        if (relColon < 0)
            return false;

        valueStart = afterName + relColon + 1;
        while (valueStart < json.Length && IsJsonWhitespace(json[valueStart]))
            valueStart++;

        cursor = valueStart;
        return true;
    }

    private static bool FindAndReadDouble(ReadOnlySpan<byte> json, ReadOnlySpan<byte> name, ref int cursor, out double value)
    {
        value = 0;
        return FindValueStart(json, name, ref cursor, out int start) && ReadDoubleAt(json, start, out value);
    }

    private static bool FindAndReadInt(ReadOnlySpan<byte> json, ReadOnlySpan<byte> name, ref int cursor, out int value)
    {
        value = 0;
        return FindValueStart(json, name, ref cursor, out int start) && ReadIntAt(json, start, out value);
    }

    private static bool FindAndReadBool(ReadOnlySpan<byte> json, ReadOnlySpan<byte> name, ref int cursor, out bool value)
    {
        value = false;
        return FindValueStart(json, name, ref cursor, out int start) && ReadBoolAt(json, start, out value);
    }

    private static bool FindAndReadString(ReadOnlySpan<byte> json, ReadOnlySpan<byte> name, int cursor, out int newCursor, out ReadOnlySpan<byte> value)
    {
        value = default;
        newCursor = cursor;
        return FindValueStart(json, name, ref newCursor, out int start) && ReadStringAt(json, start, out value);
    }

    private static bool FindAndReadKnownMerchants(ReadOnlySpan<byte> json, ref int cursor, Span<ulong> hashes, out int count)
    {
        count = 0;
        if (!FindValueStart(json, "\"known_merchants\""u8, ref cursor, out int start))
            return false;

        if (json[start] != (byte)'[')
            return false;

        int arrayEndRel = json[start..].IndexOf((byte)']');
        if (arrayEndRel < 0)
            return false;
        int arrayEnd = start + arrayEndRel;

        int i = start + 1;
        while (i < arrayEnd && count < hashes.Length)
        {
            while (i < arrayEnd && json[i] != (byte)'"')
                i++;
            if (i >= arrayEnd)
                break;

            int contentStart = i + 1;
            int rel = json[contentStart..arrayEnd].IndexOf((byte)'"');
            if (rel < 0)
                break;

            hashes[count++] = HashBytes(json.Slice(contentStart, rel));
            i = contentStart + rel + 1;
        }

        cursor = arrayEnd + 1;
        return true;
    }

    private static bool ReadDoubleAt(ReadOnlySpan<byte> json, int start, out double value)
    {
        value = 0;
        return start < json.Length
            && Utf8Parser.TryParse(json[start..], out value, out int consumed)
            && consumed > 0;
    }

    private static bool ReadIntAt(ReadOnlySpan<byte> json, int start, out int value)
    {
        value = 0;
        return start < json.Length
            && Utf8Parser.TryParse(json[start..], out value, out int consumed)
            && consumed > 0;
    }

    private static bool ReadBoolAt(ReadOnlySpan<byte> json, int start, out bool value)
    {
        value = false;
        if (start >= json.Length)
            return false;

        var s = json[start..];
        if (s.StartsWith("true"u8))
        {
            value = true;
            return true;
        }

        if (s.StartsWith("false"u8))
        {
            value = false;
            return true;
        }

        return false;
    }

    private static bool ReadStringAt(ReadOnlySpan<byte> json, int start, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (start >= json.Length || json[start] != (byte)'"')
            return false;

        int contentStart = start + 1;
        bool escaped = false;
        for (int i = contentStart; i < json.Length; i++)
        {
            byte b = json[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (b == (byte)'\\')
            {
                escaped = true;
                continue;
            }

            if (b == (byte)'"')
            {
                value = json.Slice(contentStart, i - contentStart);
                return true;
            }
        }

        return false;
    }

    private static bool IsJsonWhitespace(byte b) =>
        b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong HashBytes(ReadOnlySpan<byte> value)
    {
        const ulong kMul = 0x517cc1b727220a95ul;
        ulong hash = kMul;
        int i = 0;

        while (i + 8 <= value.Length)
        {
            ulong word = BitConverter.ToUInt64(value.Slice(i));
            hash = BitOperations.RotateLeft(hash, 5) ^ word;
            hash *= kMul;
            i += 8;
        }

        for (; i < value.Length; i++)
        {
            hash = BitOperations.RotateLeft(hash, 5) ^ value[i];
            hash *= kMul;
        }

        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ParseMcc(ReadOnlySpan<byte> mcc)
    {
        if (mcc.Length != 4)
            return 0;

        int a = mcc[0] - (byte)'0';
        int b = mcc[1] - (byte)'0';
        int c = mcc[2] - (byte)'0';
        int d = mcc[3] - (byte)'0';
        if (a > 9 || b > 9 || c > 9 || d > 9)
            return 0;

        return a * 1000 + b * 100 + c * 10 + d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double MccRisk(int mcc) => mcc switch
    {
        5411 => 0.15,
        5812 => 0.30,
        5912 => 0.20,
        5944 => 0.45,
        7801 => 0.80,
        7802 => 0.75,
        7995 => 0.85,
        4511 => 0.35,
        5311 => 0.25,
        5999 => 0.50,
        _ => 0.50
    };

    internal readonly struct ParsedDateTime
    {
        public long EpochMinute { get; init; }
        public int Hour { get; init; }
        public int DayOfWeek { get; init; }
    }

    private static bool ParseDateTime(ReadOnlySpan<byte> iso, out ParsedDateTime parsed)
    {
        parsed = default;
        if (iso.Length < 16)
            return false;

        int y = Parse4(iso, 0);
        int m = Parse2(iso, 5);
        int d = Parse2(iso, 8);
        int hh = Parse2(iso, 11);
        int mm = Parse2(iso, 14);
        if (y < 0 || m < 0 || d < 0 || hh < 0 || mm < 0)
            return false;

        long days = DaysFromCivil(y, m, d);
        parsed = new ParsedDateTime
        {
            EpochMinute = days * 1440 + hh * 60L + mm,
            Hour = hh,
            DayOfWeek = (int)((days + 3) % 7)
        };
        return true;
    }

    private static int Parse2(ReadOnlySpan<byte> s, int offset)
    {
        if (offset + 2 > s.Length)
            return -1;
        int a = s[offset] - (byte)'0';
        int b = s[offset + 1] - (byte)'0';
        return a > 9 || b > 9 ? -1 : a * 10 + b;
    }

    private static int Parse4(ReadOnlySpan<byte> s, int offset)
    {
        if (offset + 4 > s.Length)
            return -1;
        int a = s[offset] - (byte)'0';
        int b = s[offset + 1] - (byte)'0';
        int c = s[offset + 2] - (byte)'0';
        int d = s[offset + 3] - (byte)'0';
        return a > 9 || b > 9 || c > 9 || d > 9 ? -1 : a * 1000 + b * 100 + c * 10 + d;
    }

    private static long DaysFromCivil(int y, int m, int d)
    {
        int y0 = m <= 2 ? y - 1 : y;
        int era = y0 >= 0 ? y0 / 400 : (y0 - 399) / 400;
        uint yoe = (uint)(y0 - era * 400);
        int shiftedMonth = m + (m > 2 ? -3 : 9);
        uint doy = (153u * (uint)shiftedMonth + 2u) / 5u + (uint)d - 1u;
        uint doe = yoe * 365u + yoe / 4u - yoe / 100u + doy;
        return era * 146097L + doe - 719468L;
    }

    private static void FinishVector(Span<short> output, double amount, double customerAvgAmount, ulong merchantHash, ReadOnlySpan<ulong> knownHashes)
    {
        output[2] = customerAvgAmount > 0.0
            ? Quantize(amount / customerAvgAmount / 10.0)
            : VectorConstants.Scale;

        bool known = false;
        foreach (ulong h in knownHashes)
        {
            if (h == merchantHash)
            {
                known = true;
                break;
            }
        }

        output[11] = known ? (short)0 : VectorConstants.Scale;
    }
}
