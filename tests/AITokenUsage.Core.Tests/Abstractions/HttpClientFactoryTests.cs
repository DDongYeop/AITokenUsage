using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Abstractions;

public class HttpClientFactoryTests
{
    [Fact]
    public void Create_Uses15SecondTimeout()
    {
        using HttpClient client = HttpClientFactory.Create();

        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), HttpClientFactory.RequestTimeout);
    }
}
