namespace AITokenUsage.Core.Abstractions;

/// <summary>Secrets owned by this app (for example the Copilot device-flow token), encrypted at rest.</summary>
public interface ISecretStore
{
    void Save(string name, string secret);

    /// <summary>Returns null when the secret does not exist or can no longer be decrypted.</summary>
    string? Load(string name);

    void Delete(string name);
}
