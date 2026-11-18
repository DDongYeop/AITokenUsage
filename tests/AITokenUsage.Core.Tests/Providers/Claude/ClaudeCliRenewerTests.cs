using System.ComponentModel;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Claude;

public class ClaudeCliRenewerTests
{
    private const string AccountDirectory = @"C:\Users\test\AppData\Local\AITokenUsage\accounts\claude-abc123";
    private const string ClaudeExe = @"C:\Users\test\.local\bin\claude.exe";
    private const string OldToken = "renewer-old-access-token";
    private const string NewToken = "renewer-new-access-token";

    private static readonly string CredentialsPath = Path.Combine(AccountDirectory, ".credentials.json");

    private readonly FakeFileSystem _files = new();
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeTimeProvider _time = new(ClaudeTestData.Now);
    private readonly RecordingLog _log = new();

    public ClaudeCliRenewerTests()
    {
        WriteCredentials(OldToken, ClaudeTestData.Now.AddMinutes(-1));
        _processes.SetPath("claude", ClaudeExe);
    }

    private ClaudeCliRenewer CreateRenewer() => new(_processes, _files, _time, _log);

    private void WriteCredentials(string accessToken, DateTimeOffset expiresAt)
    {
        _files.Remove(CredentialsPath);
        _files.AddFile(CredentialsPath, ClaudeTestData.Credentials(accessToken, expiresAt.ToUnixTimeMilliseconds()));
    }

    private void RenewOn(string subcommand) => _processes.OnRun(spec =>
    {
        if (spec.Arguments.Contains(subcommand))
        {
            WriteCredentials(NewToken, _time.GetUtcNow().AddHours(8));
        }

        return new ProcessResult(1, "", "", false);
    });

    private static bool IsMcp(ProcessSpec spec, string subcommand) => spec.Arguments.Contains("mcp") && spec.Arguments.Contains(subcommand);

    [Fact]
    public async Task Runs_mcp_get_in_the_account_folder_with_only_this_login()
    {
        RenewOn("get");

        ClaudeCredentialReadResult? result = await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        ProcessSpec spec = Assert.Single(_processes.Calls);
        Assert.Equal(ClaudeExe, spec.FileName);
        Assert.Equal(new[] { "mcp", "get", "aitokenusage-renew" }, spec.Arguments);
        Assert.Equal(AccountDirectory, spec.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(30), spec.Timeout);
        var environment = spec.EnvironmentOverrides!;
        Assert.Equal(9, environment.Count);
        Assert.Equal(AccountDirectory, environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("1", environment["DISABLE_AUTOUPDATER"]);
        Assert.Equal("1", environment["NoDefaultCurrentDirectoryInExePath"]);
        string[] removedVariables =
        [
            "CLAUDE_SECURESTORAGE_CONFIG_DIR", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN",
            "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX",
        ];
        foreach (string removed in removedVariables)
        {
            Assert.True(environment.ContainsKey(removed), removed);
            Assert.Null(environment[removed]);
        }

        Assert.NotNull(result);
        Assert.Equal(ClaudeCredentialState.Ok, result.State);
        Assert.Equal(NewToken, result.Credentials!.AccessToken);
    }

    [Fact]
    public async Task Npm_shim_runs_through_the_system_cmd_with_its_full_path()
    {
        const string shim = @"C:\Users\Test User\AppData\Roaming\npm\claude.cmd";
        _processes.SetPath("claude", shim);
        RenewOn("get");

        await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        ProcessSpec spec = Assert.Single(_processes.Calls);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), spec.FileName);
        Assert.Equal(new[] { "/d", "/c", shim, "mcp", "get", "aitokenusage-renew" }, spec.Arguments);
        Assert.Equal(AccountDirectory, spec.WorkingDirectory);
        Assert.Equal(AccountDirectory, spec.EnvironmentOverrides!["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("1", spec.EnvironmentOverrides!["NoDefaultCurrentDirectoryInExePath"]);
    }

    [Theory]
    [InlineData(@"C:\Users\Kim (Work)\AppData\Roaming\npm\claude.cmd")]
    [InlineData(@"C:\Users\R&D\AppData\Roaming\npm\claude.cmd")]
    [InlineData(@"C:\Users\a,b\AppData\Roaming\npm\claude.cmd")]
    [InlineData(@"C:\Users\100%\AppData\Roaming\npm\claude.cmd")]
    public async Task A_shim_path_cmd_would_misread_runs_by_name_without_the_current_folder(string shim)
    {
        _processes.SetPath("claude", shim);
        RenewOn("get");

        await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        ProcessSpec spec = Assert.Single(_processes.Calls);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), spec.FileName);
        Assert.Equal(new[] { "/d", "/c", "claude", "mcp", "get", "aitokenusage-renew" }, spec.Arguments);
        Assert.Equal("1", spec.EnvironmentOverrides!["NoDefaultCurrentDirectoryInExePath"]);
    }

    [Fact]
    public async Task Falls_back_to_mcp_list_when_mcp_get_did_not_renew()
    {
        RenewOn("list");

        ClaudeCredentialReadResult? result = await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        Assert.Equal(2, _processes.Calls.Count);
        Assert.True(IsMcp(_processes.Calls[0], "get"));
        ProcessSpec list = _processes.Calls[1];
        Assert.Equal(new[] { "mcp", "list" }, list.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(60), list.Timeout);
        Assert.Equal(AccountDirectory, list.WorkingDirectory);
        Assert.Equal(AccountDirectory, list.EnvironmentOverrides!["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(NewToken, result!.Credentials!.AccessToken);
    }

    [Fact]
    public async Task Success_is_judged_by_the_file_not_the_exit_code()
    {
        _processes.OnRun(_ => new ProcessResult(0, "", "", false));

        ClaudeCredentialReadResult? result = await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        Assert.Equal(2, _processes.Calls.Count);
        Assert.Equal(ClaudeCredentialState.Ok, result!.State);
        Assert.Equal(OldToken, result.Credentials!.AccessToken);
        Assert.Contains(_log.Entries, e => e.Level == "WARN" && e.Message.Contains("not renewed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unexpired_token_after_mcp_get_is_returned_without_mcp_list()
    {
        // Claude Code renews from 5 minutes before expiry: an unexpired token it left alone gains nothing from mcp list.
        WriteCredentials(OldToken, ClaudeTestData.Now.AddMinutes(4));

        ClaudeCredentialReadResult? result = await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        Assert.Single(_processes.Calls);
        Assert.Equal(OldToken, result!.Credentials!.AccessToken);
        Assert.Contains(_log.Entries, e => e.Message.Contains("not renewed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_expired_token_after_mcp_get_runs_mcp_list_even_one_second_past_expiry()
    {
        WriteCredentials(OldToken, ClaudeTestData.Now.AddSeconds(-1));
        RenewOn("list");

        ClaudeCredentialReadResult? result = await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        Assert.Equal(2, _processes.Calls.Count);
        Assert.Equal(NewToken, result!.Credentials!.AccessToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_read_after_mcp_get_runs_mcp_list(bool removeFile)
    {
        _processes.OnRun(spec =>
        {
            if (IsMcp(spec, "get"))
            {
                _files.Remove(CredentialsPath);
                if (!removeFile)
                {
                    _files.AddFile(CredentialsPath, """{"claudeAiOauth":{"accessTo""");
                }
            }
            else
            {
                WriteCredentials(NewToken, _time.GetUtcNow().AddHours(8));
            }

            return new ProcessResult(1, "", "", false);
        });

        ClaudeCredentialReadResult? result = await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        Assert.Equal(2, _processes.Calls.Count);
        Assert.Equal(NewToken, result!.Credentials!.AccessToken);
    }

    [Fact]
    public async Task Claude_not_on_path_runs_nothing()
    {
        _processes.SetPath("claude", null);
        RenewOn("get");

        ClaudeCredentialReadResult? result = await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(_processes.Calls);
        Assert.Contains(_log.Entries, e => e.Level == "WARN" && e.Message.Contains("PATH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_command_that_cannot_start_counts_as_not_renewed()
    {
        _processes.OnRun(_ => throw new Win32Exception(2, "The system cannot find the file specified: secret-path"));

        ClaudeCredentialReadResult? result = await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        Assert.Equal(2, _processes.Calls.Count);
        Assert.Equal(OldToken, result!.Credentials!.AccessToken);
        Assert.Contains(_log.Entries, e => e.Message.Contains("Win32Exception", StringComparison.Ordinal));
        Assert.DoesNotContain("secret-path", _log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logs_name_exit_code_timeout_duration_and_outcome_but_never_the_output_or_a_token()
    {
        _processes.OnRun(spec =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(1400));
            if (IsMcp(spec, "get"))
            {
                return new ProcessResult(-1, "claude.ai Secret Connector: connected " + OldToken, "stderr-detail " + OldToken, true);
            }

            WriteCredentials(NewToken, _time.GetUtcNow().AddHours(8));
            return new ProcessResult(0, "claude.ai Other Connector " + NewToken, "list-stderr", false);
        });

        await CreateRenewer().RenewAsync(AccountDirectory, CancellationToken.None);

        LogEntry get = Assert.Single(_log.Entries, e => e.Message.Contains("claude mcp get", StringComparison.Ordinal));
        Assert.Contains("exit -1", get.Message, StringComparison.Ordinal);
        Assert.Contains("timed out yes", get.Message, StringComparison.Ordinal);
        Assert.Contains("1.4 s", get.Message, StringComparison.Ordinal);
        Assert.Contains("not renewed", get.Message, StringComparison.Ordinal);
        LogEntry list = Assert.Single(_log.Entries, e => e.Message.Contains("claude mcp list", StringComparison.Ordinal));
        Assert.Contains("exit 0", list.Message, StringComparison.Ordinal);
        Assert.Contains("timed out no", list.Message, StringComparison.Ordinal);
        Assert.EndsWith(", renewed.", list.Message, StringComparison.Ordinal);
        foreach (string secret in new[] { "Secret Connector", "Other Connector", "stderr-detail", "list-stderr", OldToken, NewToken })
        {
            Assert.DoesNotContain(secret, _log.AllText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cancellation_is_passed_through()
    {
        RenewOn("get");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateRenewer().RenewAsync(AccountDirectory, cts.Token));
    }

    [Theory]
    [InlineData(301, true)]
    [InlineData(300, false)]
    [InlineData(-1, false)]
    public void IsFresh_needs_more_than_five_minutes_left(int secondsLeft, bool expected)
    {
        var read = new ClaudeCredentialReadResult(
            ClaudeCredentialState.Ok,
            new ClaudeCredentials("t", ClaudeTestData.Now.AddSeconds(secondsLeft), null, null, null));

        Assert.Equal(expected, ClaudeCliRenewer.IsFresh(read, ClaudeTestData.Now));
    }

    [Fact]
    public void IsFresh_takes_a_login_without_expiry_as_it_is_and_never_a_failed_read()
    {
        Assert.True(ClaudeCliRenewer.IsFresh(
            new ClaudeCredentialReadResult(ClaudeCredentialState.Ok, new ClaudeCredentials("t", null, null, null, null)), ClaudeTestData.Now));
        Assert.False(ClaudeCliRenewer.IsFresh(new ClaudeCredentialReadResult(ClaudeCredentialState.NotInstalled), ClaudeTestData.Now));
    }
}
