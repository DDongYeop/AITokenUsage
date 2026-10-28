using AITokenUsage.Core.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Fakes;

public static class TestContexts
{
    /// <summary>Builds a ProviderContext from fakes; every omitted dependency gets a fresh empty fake.</summary>
    public static ProviderContext Create(
        StubHttpHandler http,
        FakeTimeProvider time,
        FakeFileSystem? files = null,
        FakeEnvironment? env = null,
        FakeProcessRunner? proc = null,
        FakeCredentialStore? creds = null,
        FakeSecretStore? secrets = null,
        IAppLog? log = null)
        => new(
            new HttpClient(http, disposeHandler: false),
            time,
            files ?? new FakeFileSystem(),
            env ?? new FakeEnvironment(),
            proc ?? new FakeProcessRunner(),
            creds ?? new FakeCredentialStore(),
            secrets ?? new FakeSecretStore(),
            log ?? new NullLog());
}
