using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Tests.Fakes;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class JwtTests
{
    [Fact]
    public void GetExpiry_ReadsExpClaim()
    {
        string jwt = TestJwt.Create("""{"sub":"user-1","exp":1761654321}""");

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761654321), Jwt.GetExpiry(jwt));
    }

    [Fact]
    public void GetExpiry_AcceptsFractionalExp()
    {
        string jwt = TestJwt.Create("""{"exp":1761654321.5}""");

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1761654321500), Jwt.GetExpiry(jwt));
    }

    [Fact]
    public void GetExpiry_IsNull_WithoutUsableExp()
    {
        Assert.Null(Jwt.GetExpiry(TestJwt.Create("""{"sub":"user-1"}""")));
        Assert.Null(Jwt.GetExpiry(TestJwt.Create("""{"exp":"soon"}""")));
    }

    [Fact]
    public void TryReadPayload_DecodesBase64UrlAlphabetWithoutPadding()
    {
        const string payload = """{"note":"??>>~~ü","hd":"example.test","https://api.openai.com/auth":{"chatgpt_account_id":"acct-1"}}""";
        string jwt = TestJwt.Create(payload);
        string segment = jwt.Split('.')[1];
        Assert.True(segment.Contains('-') || segment.Contains('_'), "payload must exercise the base64url alphabet");
        Assert.NotEqual(0, segment.Length % 4);

        Assert.True(Jwt.TryReadPayload(jwt, out JsonElement claims));

        Assert.Equal("example.test", claims.GetProperty("hd").GetString());
        Assert.Equal("??>>~~ü", claims.GetProperty("note").GetString());
        Assert.Equal("acct-1", claims.GetProperty("https://api.openai.com/auth").GetProperty("chatgpt_account_id").GetString());
    }

    [Fact]
    public void TryReadPayload_AcceptsPaddedSegment()
    {
        // 10 bytes -> 16 base64 characters ending in "==".
        string padded = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"exp":12}""")).Replace('+', '-').Replace('/', '_');
        Assert.EndsWith("==", padded);

        Assert.True(Jwt.TryReadPayload($"h.{padded}.s", out JsonElement claims));
        Assert.Equal(12, claims.GetProperty("exp").GetInt32());
    }

    [Fact]
    public void TryReadPayload_PayloadOutlivesInternalDocument()
    {
        Assert.True(Jwt.TryReadPayload(TestJwt.Create("""{"exp":42}"""), out JsonElement claims));
        GC.Collect();

        Assert.Equal(42, claims.GetProperty("exp").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-dots-at-all")]
    [InlineData("header..signature")]
    [InlineData("header.@@@@.signature")]
    [InlineData("header.a.signature")]
    public void TryReadPayload_RejectsMalformedTokens(string? jwt)
    {
        Assert.False(Jwt.TryReadPayload(jwt, out _));
        Assert.Null(Jwt.GetExpiry(jwt));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public void TryReadPayload_RejectsNonObjectPayloads(string payload)
    {
        Assert.False(Jwt.TryReadPayload(TestJwt.Create(payload), out _));
    }

    [Fact]
    public void TryReadPayload_RejectsInvalidUtf8()
    {
        string segment = TestJwt.Base64Url([0x7B, 0xFF, 0xFE, 0x7D]);

        Assert.False(Jwt.TryReadPayload($"h.{segment}.s", out _));
    }
}
