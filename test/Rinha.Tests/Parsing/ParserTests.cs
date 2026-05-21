using Rinha.Api.Parsing;
using Rinha.Api.Vector;

namespace Rinha.Tests.Parsing;

public class ParserTests
{
    private const string BasePayload = """
        {
            "transaction": {
                "amount": 100.0,
                "installments": 1,
                "requested_at": "2024-01-01T12:30:00Z"
            },
            "customer": {
                "avg_amount": 50.0,
                "tx_count_24h": 5,
                "known_merchants": ["merchant-123"]
            },
            "merchant": {
                "id": "merchant-123",
                "mcc": "5411",
                "avg_amount": 80.0
            },
            "terminal": {
                "is_online": true,
                "card_present": false,
                "km_from_home": 5.0
            },
            "last_transaction": null
        }
        """;

    [Fact]
    public void TransactionFirst_And_CustomerFirst_ProduceSameVector()
    {
        Span<short> transactionFirst = stackalloc short[16];
        Assert.Equal(ParseResult.Ok, PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(BasePayload), transactionFirst));

        var customerFirst = """
            {
                "customer": {
                    "avg_amount": 50.0,
                    "tx_count_24h": 5,
                    "known_merchants": ["merchant-123"]
                },
                "last_transaction": null,
                "merchant": {
                    "id": "merchant-123",
                    "mcc": "5411",
                    "avg_amount": 80.0
                },
                "terminal": {
                    "is_online": true,
                    "card_present": false,
                    "km_from_home": 5.0
                },
                "transaction": {
                    "amount": 100.0,
                    "installments": 1,
                    "requested_at": "2024-01-01T12:30:00Z"
                }
            }
            """;

        Span<short> customerFirstVector = stackalloc short[16];
        Assert.Equal(ParseResult.Ok, PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(customerFirst), customerFirstVector));
        Assert.Equal(transactionFirst.ToArray(), customerFirstVector.ToArray());
    }

    [Fact]
    public void LastTransactionNull_SetsMissingDimsToNegativeScale()
    {
        Span<short> vector = stackalloc short[16];
        Assert.Equal(ParseResult.Ok, PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(BasePayload), vector));
        Assert.Equal((short)-VectorConstants.Scale, vector[5]);
        Assert.Equal((short)-VectorConstants.Scale, vector[6]);
    }

    [Fact]
    public void KnownMerchant_SetsDim11ToZero()
    {
        Span<short> vector = stackalloc short[16];
        Assert.Equal(ParseResult.Ok, PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(BasePayload), vector));
        Assert.Equal(0, vector[11]);
    }

    [Fact]
    public void UnknownMerchant_SetsDim11ToScale()
    {
        var payload = """
            {
                "transaction": {
                    "amount": 100.0,
                    "installments": 1,
                    "requested_at": "2024-01-01T12:30:00Z"
                },
                "customer": {
                    "avg_amount": 50.0,
                    "tx_count_24h": 5,
                    "known_merchants": ["merchant-123"]
                },
                "merchant": {
                    "id": "merchant-unknown",
                    "mcc": "5411",
                    "avg_amount": 80.0
                },
                "terminal": {
                    "is_online": true,
                    "card_present": false,
                    "km_from_home": 5.0
                },
                "last_transaction": null
            }
            """;

        Span<short> vector = stackalloc short[16];
        Assert.Equal(ParseResult.Ok, PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(payload), vector));
        Assert.Equal(VectorConstants.Scale, vector[11]);
    }

    [Fact]
    public void Fallback_AcceptsExtraFields()
    {
        var payload = BasePayload.Replace("\"last_transaction\": null", "\"extra_field\": 123,\n    \"last_transaction\": null");
        Span<short> vector = stackalloc short[16];
        Assert.Equal(ParseResult.Ok, PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(payload), vector));
    }
}
