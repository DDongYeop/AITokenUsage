namespace AITokenUsage.Core.Abstractions;

/// <summary>A generic Windows Credential Manager entry. <see cref="Secret"/> is the UTF-8 decoded blob.</summary>
public sealed record StoredCredential(string TargetName, string? UserName, string Secret)
{
    // Records print every member by default; never print the secret.
    public override string ToString() => $"StoredCredential {{ TargetName = {TargetName}, UserName = {UserName} }}";
}

public interface ICredentialStore
{
    /// <summary>Reads one CRED_TYPE_GENERIC entry and decodes its blob as UTF-8. Returns null when it does not exist.</summary>
    string? ReadGeneric(string targetName);

    /// <summary>CredEnumerate(filter), for example "copilot-cli*". Returns an empty list when nothing matches.</summary>
    IReadOnlyList<StoredCredential> EnumerateGeneric(string filter);
}
