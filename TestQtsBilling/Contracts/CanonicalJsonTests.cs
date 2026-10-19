using System.Text.Json;
using QtsBilling.Contracts.Serialization;

namespace TestQtsBilling.Contracts;

/// <summary>
/// QB-02/§4.4 canonical fingerprint tests: the idempotency payload hash must be stable across
/// property ordering and equivalent numeric spellings, and must change when data changes.
/// </summary>
public sealed class CanonicalJsonTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void PropertyOrder_DoesNotAffectCanonicalForm()
    {
        var first = Parse("""{"b":2,"a":1}""");
        var second = Parse("""{"a":1,"b":2}""");

        Assert.Equal(CanonicalJson.ToCanonicalString(first), CanonicalJson.ToCanonicalString(second));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.00")]
    public void EquivalentNumberSpellings_CanonicalizeIdentically(string spelling)
    {
        var canonical = CanonicalJson.ToCanonicalString(Parse($$"""{"amount":{{spelling}}}"""));

        Assert.Equal("""{"amount":1}""", canonical);
    }

    [Fact]
    public void DecimalValues_KeepSignificantDigits()
    {
        var canonical = CanonicalJson.ToCanonicalString(Parse("""{"amount":12.50}"""));

        Assert.Equal("""{"amount":12.5}""", canonical);
    }

    [Fact]
    public void Whitespace_IsRemoved()
    {
        var canonical = CanonicalJson.ToCanonicalString(Parse("{\n  \"a\":  [1,  2]\n}"));

        Assert.Equal("""{"a":[1,2]}""", canonical);
    }

    [Fact]
    public void Arrays_PreserveOrder()
    {
        Assert.NotEqual(
            CanonicalJson.ToCanonicalString(Parse("[1,2]")),
            CanonicalJson.ToCanonicalString(Parse("[2,1]")));
    }

    [Fact]
    public void NullMembers_ArePreserved()
    {
        Assert.Equal("""{"note":null}""", CanonicalJson.ToCanonicalString(Parse("""{"note":null}""" )));
    }

    [Fact]
    public void Fingerprint_IsStableAcrossEquivalentRequests()
    {
        var clientHash = CanonicalJson.ComputeFingerprint(Parse("""{"lines":[{"qty":1.0}],"total":10.00}"""));
        var serverHash = CanonicalJson.ComputeFingerprint(Parse("""{"total":10,"lines":[{"qty":1}]}"""));

        Assert.Equal(clientHash, serverHash);
        Assert.Equal(64, clientHash.Length);
    }

    [Fact]
    public void Fingerprint_ChangesWhenDataChanges()
    {
        var original = CanonicalJson.ComputeFingerprint(Parse("""{"total":10}"""));
        var altered = CanonicalJson.ComputeFingerprint(Parse("""{"total":11}"""));

        Assert.NotEqual(original, altered);
    }
}
