using System.Net;
using System.Text;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>
/// Scripted HTTP handler. Rules are checked in registration order and the first match answers.
/// A request that matches no rule throws InvalidOperationException. Every request is recorded as a
/// detached copy (method, URI, request and content headers, body) so tests can inspect it after the
/// caller disposed the original.
/// </summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _rules = [];
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string?> _bodies = [];

    /// <summary>Recorded copies of every request, in order. Read bodies with RequestBodies or Content.ReadAsStringAsync().</summary>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>Request bodies decoded as UTF-8, parallel to <see cref="Requests"/> (null when there was no content).</summary>
    public IReadOnlyList<string?> RequestBodies
    {
        get
        {
            lock (_gate)
            {
                return _bodies.ToArray();
            }
        }
    }

    public StubHttpHandler When(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        lock (_gate)
        {
            _rules.Add((match, respond));
        }

        return this;
    }

    /// <summary>Predicate: GET of exactly this absolute URL (query string included).</summary>
    public static Func<HttpRequestMessage, bool> Get(string absoluteUrl) =>
        request => request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == new Uri(absoluteUrl).AbsoluteUri;

    /// <summary>Predicate: POST of exactly this absolute URL (query string included).</summary>
    public static Func<HttpRequestMessage, bool> Post(string absoluteUrl) =>
        request => request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == new Uri(absoluteUrl).AbsoluteUri;

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        string? body = null;
        if (request.Content is not null)
        {
            byte[] bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            body = Encoding.UTF8.GetString(bytes);
            var content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            copy.Content = content;
        }

        Func<HttpRequestMessage, HttpResponseMessage>? respond;
        lock (_gate)
        {
            _requests.Add(copy);
            _bodies.Add(body);
            respond = _rules.FirstOrDefault(rule => rule.Match(request)).Respond;
        }

        if (respond is null)
        {
            throw new InvalidOperationException($"StubHttpHandler: no rule matched {request.Method} {request.RequestUri}.");
        }

        HttpResponseMessage response = respond(request);
        response.RequestMessage ??= request;
        return response;
    }
}
