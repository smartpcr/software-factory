// -----------------------------------------------------------------------
// <copyright file="StubHttpMessageHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Providers;

using System.Net;
using System.Net.Http.Headers;

/// <summary>
/// Tiny in-memory routing harness used by Phase-5 provider tests.
/// Avoids pulling WireMock.Net just for a couple of canned responses.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)> routes = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public StubHttpMessageHandler When(string urlSubstring, string body, string contentType = "application/json", HttpStatusCode status = HttpStatusCode.OK, IDictionary<string, string>? headers = null)
    {
        return this.When(
            req => req.RequestUri!.ToString().Contains(urlSubstring, StringComparison.OrdinalIgnoreCase),
            _ =>
            {
                var resp = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
                };
                if (headers is not null)
                {
                    foreach (var kv in headers) resp.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
                return resp;
            });
    }

    public StubHttpMessageHandler When(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        this.routes.Add((match, respond));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.Requests.Add(request);
        foreach (var (match, respond) in this.routes)
        {
            if (match(request)) return Task.FromResult(respond(request));
        }
        Console.Error.WriteLine($"[StubHttpMessageHandler] NO ROUTE: {request.Method} {request.RequestUri}");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"no route for {request.Method} {request.RequestUri}"),
        });
    }
}
