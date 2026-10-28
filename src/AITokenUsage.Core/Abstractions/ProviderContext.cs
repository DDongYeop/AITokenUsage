namespace AITokenUsage.Core.Abstractions;

/// <summary>Every external dependency a usage provider may touch. Tests replace each one with a fake.</summary>
public sealed record ProviderContext(
    HttpClient Http,
    TimeProvider Time,
    IFileSystem Files,
    IEnvironment Env,
    IProcessRunner Processes,
    ICredentialStore Credentials,
    ISecretStore Secrets,
    IAppLog Log);
