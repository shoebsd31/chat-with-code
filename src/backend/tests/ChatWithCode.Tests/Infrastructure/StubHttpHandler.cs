using System.Net;
using System.Text;

namespace ChatWithCode.Tests.Infrastructure;

/// <summary>Answers HTTP requests from a route table and records what was asked.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public StubHttpHandler On(string pathAndQueryStart, HttpStatusCode status, string body, string contentType = "application/json")
    {
        _routes.Add((
            r => r.RequestUri!.PathAndQuery.StartsWith(pathAndQueryStart, StringComparison.Ordinal),
            _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) }));
        return this;
    }

    public StubHttpHandler On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((match, respond));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        foreach (var (match, respond) in _routes)
        {
            if (match(request))
            {
                return respond(request);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"No stub for {request.RequestUri}") };
    }
}
