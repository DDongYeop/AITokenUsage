using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>The device-flow token the app saved through ISecretStore (DPAPI) as {"token","login"}.</summary>
public sealed record CopilotSavedToken(string Token, string? Login)
{
    public const string SecretName = "copilot-token";

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("token", Token);
            if (Login is null)
            {
                writer.WriteNull("login");
            }
            else
            {
                writer.WriteString("login", Login);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static CopilotSavedToken? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var token = Json.Str(root, "token");
            return string.IsNullOrWhiteSpace(token) ? null : new CopilotSavedToken(token.Trim(), Json.Str(root, "login"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Loads and parses the saved token; unreadable or undecryptable data counts as "none".</summary>
    public static CopilotSavedToken? Load(ISecretStore secrets, IAppLog log)
    {
        try
        {
            return TryParse(secrets.Load(SecretName));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            log.Warn("Copilot: saved token could not be read", ex);
            return null;
        }
    }

    public override string ToString() => $"CopilotSavedToken {{ Login = {Login} }}";
}
