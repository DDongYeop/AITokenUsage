using System.ComponentModel;
using System.Net;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>Guards the behaviour later provider tests rely on.</summary>
public class FakesSelfTests
{
    [Fact]
    public async Task StubHttpHandler_RecordsHeadersAndBody_AndAnswersFirstMatchingRule()
    {
        var stub = new StubHttpHandler()
            .When(StubHttpHandler.Post("https://example.test/api?x=1"), _ => StubHttpHandler.Json(HttpStatusCode.OK, """{"ok":true}"""))
            .When(_ => true, _ => StubHttpHandler.Text(HttpStatusCode.NotFound, "fallback"));
        using var http = new HttpClient(stub);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api?x=1")
        {
            Content = new StringContent("""{"project":"p-1"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", "test-access-token");
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HttpRequestMessage recorded = Assert.Single(stub.Requests);
        Assert.Equal("test-access-token", recorded.Headers.Authorization!.Parameter);
        Assert.Equal("oauth-2025-04-20", recorded.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal("application/json", recorded.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""{"project":"p-1"}""", Assert.Single(stub.RequestBodies));
        Assert.Equal("""{"project":"p-1"}""", await recorded.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StubHttpHandler_ThrowsForUnmatchedRequest()
    {
        using var http = new HttpClient(new StubHttpHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync("https://example.test/none"));
    }

    [Fact]
    public void FakeFileSystem_TracksDirectoriesPatternsQueuedReadsAndErrors()
    {
        var fs = new FakeFileSystem()
            .AddFile(@"C:\Users\test\.codex\sessions\2026\10\28\rollout-a.jsonl", "a1\na2\n", new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc))
            .AddFile("C:/Users/test/.codex/auth.json", "{}")
            .EnqueueReads(@"C:\Users\test\.codex\auth.json", "{\"tok", null);

        Assert.True(fs.DirectoryExists(@"c:\users\test\.codex\sessions\2026"));
        Assert.Equal(
            [@"C:\Users\test\.codex\sessions\2026\10\28\rollout-a.jsonl"],
            fs.EnumerateFiles(@"C:\Users\test\.codex\sessions", "rollout-*.jsonl", recursive: true));
        Assert.Empty(fs.EnumerateFiles(@"C:\Users\test\.codex\sessions", "rollout-*.jsonl", recursive: false));
        Assert.Equal(["a2", "a1"], fs.ReadLinesReverse(@"C:\Users\test\.codex\sessions\2026\10\28\rollout-a.jsonl"));

        Assert.Equal("{\"tok", fs.ReadAllTextShared(@"C:\Users\test\.codex\auth.json"));
        Assert.Null(fs.ReadAllTextShared(@"C:\Users\test\.codex\auth.json"));
        Assert.Equal("{}", fs.ReadAllTextShared(@"C:\Users\test\.codex\auth.json"));
        Assert.Equal(3, fs.ReadCount(@"C:\Users\test\.codex\auth.json"));

        fs.ThrowOnRead(@"C:\Users\test\.codex\auth.json", new IOException("locked"));
        Assert.Throws<IOException>(() => fs.ReadAllTextShared(@"C:\Users\test\.codex\auth.json"));

        fs.Remove(@"C:\Users\test\.codex\sessions");
        Assert.False(fs.FileExists(@"C:\Users\test\.codex\sessions\2026\10\28\rollout-a.jsonl"));
        Assert.Equal(DateTime.FromFileTimeUtc(0), fs.GetLastWriteTimeUtc(@"C:\Users\test\.codex\sessions\2026\10\28\rollout-a.jsonl"));
    }

    [Fact]
    public async Task FakeProcessRunner_RecordsCalls_AndThrowsWithoutHandler()
    {
        var runner = new FakeProcessRunner().SetPath("gh", @"C:\Tools\gh.exe");

        Assert.Equal(@"C:\Tools\gh.exe", runner.FindOnPath("gh"));
        Assert.Null(runner.FindOnPath("codex"));
        await Assert.ThrowsAsync<Win32Exception>(() => runner.RunAsync(new ProcessSpec("gh", ["auth", "token"]), CancellationToken.None));

        runner.OnRun(spec => new ProcessResult(0, "gho_test-token\n", "", false));
        ProcessResult result = await runner.RunAsync(new ProcessSpec("gh", ["auth", "token"]), CancellationToken.None);

        Assert.Equal("gho_test-token\n", result.StdOut);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(["auth", "token"], runner.Calls[1].Arguments);
    }

    [Fact]
    public void ProcessResult_ToString_DoesNotLeakStdoutOrStderr()
    {
        var result = new ProcessResult(0, "gho_test-token\n", "secret-err", false);

        string text = result.ToString();

        Assert.DoesNotContain("gho_test-token", text);
        Assert.DoesNotContain("secret-err", text);
        Assert.Contains("ExitCode = 0", text);
    }

    [Fact]
    public async Task FakeInteractiveProcess_RespondsToWrites_WaitsWhenSilent_AndSignalsEof()
    {
        var process = new FakeInteractiveProcess()
            .OnWrite(line => line.Contains("initialize") ? ["""{"id":1,"result":{}}"""] : []);

        await process.WriteLineAsync("""{"id":1,"method":"initialize"}""", CancellationToken.None);
        Assert.Equal("""{"id":1,"result":{}}""", await process.ReadLineAsync(CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.ReadLineAsync(cts.Token));

        process.EnqueueOutput("last").CompleteOutput();
        Assert.Equal("last", await process.ReadLineAsync(CancellationToken.None));
        Assert.Null(await process.ReadLineAsync(CancellationToken.None));
        Assert.Equal(["""{"id":1,"method":"initialize"}"""], process.Written);
    }

    [Fact]
    public void FakeCredentialStore_FiltersLikeCredEnumerate()
    {
        var creds = new FakeCredentialStore()
            .Add("copilot-cli/https://github.com:alice", "https://github.com:alice", "gho_test-a")
            .Add("gemini-cli-oauth/main-account", "main-account", "{}");

        Assert.Equal("{}", creds.ReadGeneric("gemini-cli-oauth/main-account"));
        Assert.Null(creds.ReadGeneric("gemini-cli-oauth"));
        Assert.Equal("gho_test-a", Assert.Single(creds.EnumerateGeneric("copilot-cli*")).Secret);
        Assert.Empty(creds.EnumerateGeneric("gh:*"));
    }

    [Fact]
    public void FakeEnvironment_DistinguishesUndefinedFromEmpty()
    {
        var env = new FakeEnvironment().Set("CLAUDE_CONFIG_DIR", "").Set("GH_TOKEN", "x").Set("GH_TOKEN", null);

        Assert.Equal("", env.GetVariable("CLAUDE_CONFIG_DIR"));
        Assert.Null(env.GetVariable("GH_TOKEN"));
        Assert.Equal(@"C:\Users\test", env.UserProfile);
        Assert.Equal(@"C:\Users\test\AppData\Local", env.LocalAppData);
        Assert.Equal(@"C:\Users\test\AppData\Roaming", env.RoamingAppData);
    }

    [Fact]
    public void TestContexts_FillsMissingFakes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));

        ProviderContext ctx = TestContexts.Create(new StubHttpHandler(), time);

        Assert.Same(time, ctx.Time);
        Assert.IsType<FakeFileSystem>(ctx.Files);
        Assert.IsType<FakeEnvironment>(ctx.Env);
        Assert.IsType<FakeProcessRunner>(ctx.Processes);
        Assert.IsType<FakeCredentialStore>(ctx.Credentials);
        Assert.IsType<FakeSecretStore>(ctx.Secrets);
        Assert.IsType<NullLog>(ctx.Log);
    }

    [Fact]
    public void Fixture_ReadsCopiedSampleFiles()
    {
        using JsonDocument doc = JsonDocument.Parse(Fixture.Read("common/fixture-check.json"));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(File.Exists(Fixture.GetPath("common/fixture-check.json")));
    }
}
