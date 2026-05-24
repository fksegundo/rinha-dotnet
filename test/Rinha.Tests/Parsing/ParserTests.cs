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

    private const string BaselineWithLastTransaction = """
        {
            "id": "tx-1",
            "transaction": {
                "amount": 384.88,
                "installments": 3,
                "requested_at": "2026-03-11T20:23:35Z"
            },
            "customer": {
                "avg_amount": 769.76,
                "tx_count_24h": 3,
                "known_merchants": ["MERC-009", "MERC-001"]
            },
            "merchant": {
                "id": "MERC-001",
                "mcc": "5912",
                "avg_amount": 298.95
            },
            "terminal": {
                "is_online": false,
                "card_present": true,
                "km_from_home": 13.7090520965
            },
            "last_transaction": {
                "timestamp": "2026-03-11T14:58:35Z",
                "km_from_current": 18.8626479774
            }
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
        var payload = """
            {
                "customer": {
                    "avg_amount": 50.0,
                    "tx_count_24h": 5,
                    "known_merchants": ["merchant-123"],
                    "extra_customer_field": true
                },
                "unexpected_top_level": "ignored",
                "last_transaction": null,
                "merchant": {
                    "id": "merchant-123",
                    "mcc": "5411",
                    "avg_amount": 80.0,
                    "extra_merchant_field": {"nested": "ignored"}
                },
                "terminal": {
                    "is_online": true,
                    "card_present": false,
                    "km_from_home": 5.0,
                    "extra_terminal_field": ["ignored"]
                },
                "transaction": {
                    "amount": 100.0,
                    "installments": 1,
                    "requested_at": "2024-01-01T12:30:00Z",
                    "extra_transaction_field": 123
                }
            }
            """;

        Span<short> baseline = stackalloc short[16];
        Span<short> vector = stackalloc short[16];
        Assert.Equal(ParseResult.Ok, PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(BasePayload), baseline));
        Assert.Equal(ParseResult.Ok, PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(payload), vector));
        Assert.Equal(baseline.ToArray(), vector.ToArray());
    }

    [Fact]
    public void ReorderedFields_MatchesBaseline()
    {
        var reordered = """
            {
                "last_transaction": {
                    "km_from_current": 18.8626479774,
                    "timestamp": "2026-03-11T14:58:35Z"
                },
                "terminal": {
                    "km_from_home": 13.7090520965,
                    "card_present": true,
                    "is_online": false
                },
                "merchant": {
                    "avg_amount": 298.95,
                    "mcc": "5912",
                    "id": "MERC-001"
                },
                "customer": {
                    "known_merchants": ["MERC-009", "MERC-001"],
                    "tx_count_24h": 3,
                    "avg_amount": 769.76
                },
                "transaction": {
                    "requested_at": "2026-03-11T20:23:35Z",
                    "installments": 3,
                    "amount": 384.88
                },
                "id": "tx-1"
            }
            """;

        AssertEquivalentVectors(BaselineWithLastTransaction, reordered);
    }

    [Fact]
    public void PascalCaseKeys_MatchesBaseline()
    {
        var pascalCase = """
            {
                "Id": "tx-1",
                "Transaction": {
                    "Amount": 384.88,
                    "Installments": 3,
                    "Requested_at": "2026-03-11T20:23:35Z"
                },
                "Customer": {
                    "Avg_amount": 769.76,
                    "Tx_count_24h": 3,
                    "Known_merchants": ["MERC-009", "MERC-001"]
                },
                "Merchant": {
                    "Id": "MERC-001",
                    "Mcc": "5912",
                    "Avg_amount": 298.95
                },
                "Terminal": {
                    "Is_online": false,
                    "Card_present": true,
                    "Km_from_home": 13.7090520965
                },
                "Last_transaction": {
                    "Timestamp": "2026-03-11T14:58:35Z",
                    "Km_from_current": 18.8626479774
                }
            }
            """;

        AssertEquivalentVectors(BaselineWithLastTransaction, pascalCase);
    }

    [Fact]
    public void MixedCaseSeparators_MatchesBaseline()
    {
        var mixedCase = """
            {
                "ID": "tx-1",
                "TRANSACTION": {
                    "AMOUNT": 384.88,
                    "installments": 3,
                    "requestedAt": "2026-03-11T20:23:35Z"
                },
                "CUSTOMER": {
                    "avg-amount": 769.76,
                    "txCount24h": 3,
                    "knownMerchants": ["MERC-009", "MERC-001"]
                },
                "MERCHANT": {
                    "ID": "MERC-001",
                    "MCC": "5912",
                    "AVG-AMOUNT": 298.95
                },
                "TERMINAL": {
                    "isOnline": false,
                    "card-present": true,
                    "kmFromHome": 13.7090520965
                },
                "lastTransaction": {
                    "TIMESTAMP": "2026-03-11T14:58:35Z",
                    "kmFromCurrent": 18.8626479774
                }
            }
            """;

        AssertEquivalentVectors(BaselineWithLastTransaction, mixedCase);
    }

    [Fact]
    public void FieldMatches_IgnoresSeparatorsAndCase()
    {
        Assert.True(PayloadFlexibleJson.FieldMatches("Requested_at"u8, "requested_at"u8));
        Assert.True(PayloadFlexibleJson.FieldMatches("avg-amount"u8, "avg_amount"u8));
        Assert.True(PayloadFlexibleJson.FieldMatches("txCount24h"u8, "tx_count_24h"u8));
        Assert.True(PayloadFlexibleJson.FieldMatches("TRANSACTION"u8, "transaction"u8));
        Assert.False(PayloadFlexibleJson.FieldMatches("amount"u8, "avg_amount"u8));
    }

    private static void AssertEquivalentVectors(string baselinePayload, string variantPayload)
    {
        Span<short> baseline = stackalloc short[16];
        Span<short> variant = stackalloc short[16];
        Assert.Equal(ParseResult.Ok, PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(baselinePayload), baseline));
        Assert.Equal(ParseResult.Ok, PayloadParser.TryParse(System.Text.Encoding.UTF8.GetBytes(variantPayload), variant));
        Assert.Equal(baseline.ToArray(), variant.ToArray());
    }
}
