using System.Reflection;

namespace AITokenUsage.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Tests_RunOnWindows()
    {
        Assert.True(OperatingSystem.IsWindows());
    }

    [Fact]
    public void CoreAssembly_IsReferencedAndLoadable()
    {
        Assembly core = Assembly.Load("AITokenUsage.Core");

        Assert.Equal("AITokenUsage.Core", core.GetName().Name);
    }
}