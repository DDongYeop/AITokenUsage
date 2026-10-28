using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiProjectResolverTests
{
    private readonly FakeEnvironment _env = new();
    private readonly FakeFileSystem _files = new();

    [Fact]
    public void DotEnv_parses_comments_export_quotes_and_crlf()
    {
        var content = "# comment\r\nexport GOOGLE_CLOUD_PROJECT=\"quoted-project\"\r\nGOOGLE_CLOUD_PROJECT_ID='single' \r\nOTHER=value # trailing comment\r\nNOEQUALS\r\n=novalue\r\n";

        var values = GeminiDotEnv.Parse(content);

        Assert.Equal("quoted-project", values["GOOGLE_CLOUD_PROJECT"]);
        Assert.Equal("single", values["GOOGLE_CLOUD_PROJECT_ID"]);
        Assert.Equal("value", values["OTHER"]);
        Assert.Equal(3, values.Count);
    }

    [Fact]
    public void Environment_variable_wins_over_files()
    {
        var paths = new GeminiPaths(_env);
        _env.Set("GOOGLE_CLOUD_PROJECT", "env-project");
        _files.AddFile(paths.GeminiDotEnvPath, "GOOGLE_CLOUD_PROJECT=file-project");

        Assert.Equal("env-project", CreateResolver(paths).Resolve());
    }

    [Fact]
    public void Project_id_variable_is_used_when_project_is_unset()
    {
        var paths = new GeminiPaths(_env);
        _env.Set("GOOGLE_CLOUD_PROJECT", "");
        _env.Set("GOOGLE_CLOUD_PROJECT_ID", "env-project-id");

        Assert.Equal("env-project-id", CreateResolver(paths).Resolve());
    }

    [Fact]
    public void Gemini_dotenv_is_read_before_home_dotenv()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.GeminiDotEnvPath, "GOOGLE_CLOUD_PROJECT=gemini-dir-project");
        _files.AddFile(paths.HomeDotEnvPath, "GOOGLE_CLOUD_PROJECT=home-project");

        Assert.Equal("gemini-dir-project", CreateResolver(paths).Resolve());
    }

    [Fact]
    public void Home_dotenv_is_the_last_fallback()
    {
        var paths = new GeminiPaths(_env);
        _files.AddFile(paths.GeminiDotEnvPath, "UNRELATED=1");
        _files.AddFile(paths.HomeDotEnvPath, "GOOGLE_CLOUD_PROJECT_ID=home-project-id");

        Assert.Equal("home-project-id", CreateResolver(paths).Resolve());
    }

    [Fact]
    public void Dotenv_files_follow_GEMINI_CLI_HOME()
    {
        _env.Set("GEMINI_CLI_HOME", @"D:\alt-home");
        var paths = new GeminiPaths(_env);
        _files.AddFile(@"D:\alt-home\.gemini\.env", "GOOGLE_CLOUD_PROJECT=alt-project");

        Assert.Equal("alt-project", CreateResolver(paths).Resolve());
    }

    [Fact]
    public void Returns_null_when_nothing_is_configured()
    {
        Assert.Null(CreateResolver(new GeminiPaths(_env)).Resolve());
    }

    private GeminiProjectResolver CreateResolver(GeminiPaths paths) => new(_env, _files, paths);
}
