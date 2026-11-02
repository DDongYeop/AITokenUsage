using System.Security.Cryptography;
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Accounts;

/// <summary>
/// Ids of the extra Claude/Codex accounts (spec §1): <c>&lt;kind&gt;#&lt;key&gt;</c>, for example <c>claude#k3x9q2</c>.
/// The key is <see cref="KeyLength"/> characters of [a-z0-9]. The built-in panels keep their plain ids ("claude", "codex").
/// </summary>
public static class AccountIds
{
    public const char Separator = '#';

    /// <summary>Characters in the key part of an account id.</summary>
    public const int KeyLength = 6;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>The kinds that can have extra accounts: claude, codex.</summary>
    public static IReadOnlyList<string> SupportedKinds { get; } = [ProviderIds.Claude, ProviderIds.Codex];

    /// <summary>True for <c>^(claude|codex)#[a-z0-9]{6}$</c>, compared ordinally (lower case only).</summary>
    public static bool IsAccountId(string? id)
    {
        if (id is null)
        {
            return false;
        }

        int separator = id.IndexOf(Separator);
        if (separator <= 0 || id.Length - separator - 1 != KeyLength)
        {
            return false;
        }

        if (!SupportedKinds.Contains(id[..separator], StringComparer.Ordinal))
        {
            return false;
        }

        for (int i = separator + 1; i < id.Length; i++)
        {
            if (!IsKeyChar(id[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A new id of <paramref name="kind"/> with a random key.
    /// <paramref name="nextInt"/> returns a value in [0, maxExclusive); the default is <see cref="RandomNumberGenerator.GetInt32(int)"/>.
    /// </summary>
    public static string Create(string kind, Func<int, int>? nextInt = null)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!SupportedKinds.Contains(kind, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported account kind: '{kind}'.", nameof(kind));
        }

        nextInt ??= RandomNumberGenerator.GetInt32;
        var key = new char[KeyLength];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = Alphabet[nextInt(Alphabet.Length)];
        }

        return kind + Separator + new string(key);
    }

    /// <summary>The key part of an account id ("claude#k3x9q2" → "k3x9q2").</summary>
    public static string KeyOf(string accountId)
    {
        if (!IsAccountId(accountId))
        {
            throw new ArgumentException($"Not an account id: '{accountId}'.", nameof(accountId));
        }

        return accountId[^KeyLength..];
    }

    private static bool IsKeyChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}
