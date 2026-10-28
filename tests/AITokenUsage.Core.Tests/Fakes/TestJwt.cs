using System.Text;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>Builds unsigned fake JWTs for tests: base64url(header).base64url(payload).fake-signature.</summary>
public static class TestJwt
{
    public static string Create(string payloadJson) =>
        $"{Base64Url("""{"alg":"none","typ":"JWT"}""")}.{Base64Url(payloadJson)}.fake-signature";

    public static string Base64Url(string text) => Base64Url(Encoding.UTF8.GetBytes(text));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
