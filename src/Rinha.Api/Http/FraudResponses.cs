namespace Rinha.Api.Http;

public static class FraudResponses
{
    public static ReadOnlyMemory<byte> ForFraudCount(int fraudCount) =>
        fraudCount switch
        {
            0 => Body0,
            1 => Body1,
            2 => Body2,
            3 => Body3,
            4 => Body4,
            _ => Body5
        };

    public static readonly byte[] Body0 = "{\"approved\":true,\"fraud_score\":0.0}"u8.ToArray();
    public static readonly byte[] Body1 = "{\"approved\":true,\"fraud_score\":0.2}"u8.ToArray();
    public static readonly byte[] Body2 = "{\"approved\":true,\"fraud_score\":0.4}"u8.ToArray();
    public static readonly byte[] Body3 = "{\"approved\":false,\"fraud_score\":0.6}"u8.ToArray();
    public static readonly byte[] Body4 = "{\"approved\":false,\"fraud_score\":0.8}"u8.ToArray();
    public static readonly byte[] Body5 = "{\"approved\":false,\"fraud_score\":1.0}"u8.ToArray();
}
