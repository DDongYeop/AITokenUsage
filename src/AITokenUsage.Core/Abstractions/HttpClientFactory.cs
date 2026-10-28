using System.Net;

namespace AITokenUsage.Core.Abstractions;

public static class HttpClientFactory
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>One shared client for the whole app: no redirects, 15 s timeout, connections recycled every 10 minutes.</summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
    }
}
