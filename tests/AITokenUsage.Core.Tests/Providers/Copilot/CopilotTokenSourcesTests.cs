using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Copilot;

public sealed class CopilotTokenSourcesTests
{
    private const string GhPath = @"C:\Program Files\GitHub CLI\gh.exe";

    private readonly FakeTimeProvider _time = new();
    private readonly StubHttpHandler _http = new();
    private readonly FakeEnvironment _env = new();
    private readonly FakeFileSystem _files = new();
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeCredentialStore _credentials = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly List<ProcessSpec> _ghCalls = [];

    [Fact]
    public async Task Yields_every_source_in_spec_order()
    {
        _secrets.Save(CopilotSavedToken.SecretName, new CopilotSavedToken("gho_saved", "saved-user").ToJson());
        _env.Set("COPILOT_GITHUB_TOKEN", "github_pat_env1");
        _env.Set("GH_TOKEN", "gho_env2");
        _env.Set("GITHUB_TOKEN", "ghu_env3");
        GivenGh("gho_cli\n");
        _credentials.Add("copilot-cli/https://github.com:octocat", "https://github.com:octocat", "gho_copilot_cli");
        GivenLanguageServerFile("apps.json", Fixture.Read("copilot/apps.json"));
        GivenLanguageServerFile("hosts.json", Fixture.Read("copilot/hosts.json"));

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Equal(
            [
                "saved", "env:COPILOT_GITHUB_TOKEN", "env:GH_TOKEN", "env:GITHUB_TOKEN", "gh auth token",
                "credman:copilot-cli/https://github.com:octocat", "file:apps.json", "file:hosts.json",
            ],
            candidates.Select(c => c.Description).ToArray());
        Assert.Equal(
            ["gho_saved", "github_pat_env1", "gho_env2", "ghu_env3", "gho_cli", "gho_copilot_cli", "ghu_test_apps_token", "gho_test_hosts_token"],
            candidates.Select(c => c.Token).ToArray());
        Assert.Equal(CopilotTokenSourceKind.Saved, candidates[0].Kind);
        Assert.Equal("saved-user", candidates[0].Login);
        Assert.Equal("octocat", candidates[5].Login);
        Assert.Equal(CopilotTokenSourceKind.LanguageServerFile, candidates[6].Kind);
        Assert.Equal("octocat", candidates[6].Login);
    }

    [Fact]
    public async Task Environment_tokens_need_an_accepted_prefix()
    {
        _env.Set("COPILOT_GITHUB_TOKEN", "ghp_classic_pat");
        _env.Set("GH_TOKEN", "  gho_trimmed  ");
        _env.Set("GITHUB_TOKEN", "some-random-value");

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        var only = Assert.Single(candidates);
        Assert.Equal("gho_trimmed", only.Token);
        Assert.False(CopilotTokenSources.HasAcceptedPrefix("ghp_x"));
        Assert.True(CopilotTokenSources.HasAcceptedPrefix("github_pat_x"));
        Assert.True(CopilotTokenSources.HasAcceptedPrefix("ghu_x"));
    }

    [Fact]
    public async Task Gh_runs_with_token_env_removed_and_five_second_timeout()
    {
        GivenGh("gho_cli\r\n");

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Equal("gho_cli", Assert.Single(candidates).Token);
        var spec = Assert.Single(_ghCalls);
        Assert.Equal(GhPath, spec.FileName);
        Assert.Equal(["auth", "token", "--hostname", "github.com"], spec.Arguments.ToArray());
        Assert.NotNull(spec.EnvironmentOverrides);
        Assert.True(spec.EnvironmentOverrides!.ContainsKey("GH_TOKEN"));
        Assert.Null(spec.EnvironmentOverrides["GH_TOKEN"]);
        Assert.True(spec.EnvironmentOverrides.ContainsKey("GITHUB_TOKEN"));
        Assert.Null(spec.EnvironmentOverrides["GITHUB_TOKEN"]);
        Assert.Equal(TimeSpan.FromSeconds(5), spec.Timeout);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task Gh_failure_or_timeout_yields_nothing(int exitCode, bool timedOut)
    {
        GivenGh("gho_should_not_be_used", exitCode, timedOut);

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Gh_is_not_run_when_missing_from_path()
    {
        _processes.OnRun(spec =>
        {
            _ghCalls.Add(spec);
            return new ProcessResult(0, "gho_unexpected", "", false);
        });

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Empty(candidates);
        Assert.Empty(_ghCalls);
    }

    [Fact]
    public async Task Enumeration_is_lazy_so_gh_does_not_run_when_saved_token_is_taken()
    {
        _secrets.Save(CopilotSavedToken.SecretName, new CopilotSavedToken("gho_saved", "saved-user").ToJson());
        GivenGh("gho_cli");

        await foreach (var candidate in CreateSources().EnumerateAsync(CancellationToken.None))
        {
            Assert.Equal("gho_saved", candidate.Token);
            break;
        }

        Assert.Empty(_ghCalls);
    }

    [Fact]
    public async Task Duplicate_tokens_are_yielded_once()
    {
        _env.Set("GH_TOKEN", "gho_same");
        GivenGh("gho_same");

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Equal("env:GH_TOKEN", Assert.Single(candidates).Description);
    }

    [Fact]
    public async Task Broken_language_server_files_are_skipped()
    {
        GivenLanguageServerFile("apps.json", "{ not json");
        GivenLanguageServerFile("hosts.json", """{"github.com":{"user":"octocat"}}""");

        var candidates = await ToListAsync(CreateSources().EnumerateAsync(CancellationToken.None));

        Assert.Empty(candidates);
    }

    [Fact]
    public void Candidate_ToString_hides_the_token()
    {
        var candidate = new CopilotTokenCandidate(CopilotTokenSourceKind.Environment, "env:GH_TOKEN", "gho_secret_value", null);

        Assert.DoesNotContain("gho_secret_value", candidate.ToString());
    }

    private CopilotTokenSources CreateSources() =>
        new(TestContexts.Create(_http, _time, _files, _env, _processes, _credentials, _secrets));

    private void GivenGh(string stdout, int exitCode = 0, bool timedOut = false)
    {
        _processes.SetPath("gh", GhPath);
        _processes.OnRun(spec =>
        {
            _ghCalls.Add(spec);
            return new ProcessResult(exitCode, stdout, "", timedOut);
        });
    }

    private void GivenLanguageServerFile(string name, string content) =>
        _files.AddFile(Path.Combine(_env.LocalAppData, "github-copilot", name), content);

    private static async Task<List<CopilotTokenCandidate>> ToListAsync(IAsyncEnumerable<CopilotTokenCandidate> source)
    {
        var list = new List<CopilotTokenCandidate>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
