using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Abstractions;

public class RealEnvironmentTests
{
    private readonly RealEnvironment _env = new();

    [Fact]
    public void Folders_MatchEnvironmentSpecialFolders()
    {
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _env.UserProfile);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _env.LocalAppData);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _env.RoamingAppData);
    }

    [Fact]
    public void GetVariable_ReturnsValueOrNull()
    {
        Environment.SetEnvironmentVariable("AITU_TEST_VAR", "value-1");
        try
        {
            Assert.Equal("value-1", _env.GetVariable("AITU_TEST_VAR"));
            Assert.Null(_env.GetVariable("AITU_TEST_VAR_THAT_DOES_NOT_EXIST"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITU_TEST_VAR", null);
        }
    }
}
