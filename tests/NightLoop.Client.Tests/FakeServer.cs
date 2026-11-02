using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NightLoop.Contracts.Api;

namespace NightLoop.Client.Tests;

/// <summary>A scripted stand-in for the server: each route answers with a function of the request.</summary>
public sealed class FakeServer : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, string?, HttpResponseMessage>> _routes = new();

    public ConcurrentQueue<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();
    public bool Offline { get; set; }

    public void On(string method, string path, Func<HttpRequestMessage, string?, HttpResponseMessage> answer) =>
        _routes[$"{method} {path}"] = answer;

    public void On(string method, string path, HttpStatusCode status, object? body = null) =>
        On(method, path, (_, _) => Respond(status, body));

    public static HttpResponseMessage Respond(HttpStatusCode status, object? body = null)
    {
        var res = new HttpResponseMessage(status);
        if (body is not null)
            res.Content = new StringContent(JsonSerializer.Serialize(body, ApiClient.Json), Encoding.UTF8, "application/json");
        return res;
    }

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message = "nope", MacroDto? current = null) =>
        Respond(status, new ErrorResponse(code, message, null, current));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Offline) throw new HttpRequestException("No route to host");
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request, body));
        var key = $"{request.Method} {request.RequestUri!.AbsolutePath.Replace("/v1/", "", StringComparison.Ordinal)}";
        return _routes.TryGetValue(key, out var answer)
            ? answer(request, body)
            : Error(HttpStatusCode.NotFound, "not_found", $"No fake route for {key}");
    }

    public ApiClient Client(string version = "0.1.0") =>
        new(new HttpClient(this) { BaseAddress = new Uri("http://test/v1/") }, Guid.Parse("11111111-1111-1111-1111-111111111111"), version);
}
