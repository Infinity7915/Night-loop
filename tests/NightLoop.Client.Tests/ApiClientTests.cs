using System.Net;
using NightLoop.Contracts;
using NightLoop.Contracts.Api;

namespace NightLoop.Client.Tests;

public sealed class ApiClientTests
{
    private static readonly UserDto User = new(Guid.NewGuid(), 1043, "a@b.co", "user#1043", true, 3, true, TwofaModes.EverySignin, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Sends_token_install_id_and_version()
    {
        var server = new FakeServer();
        server.On("GET", "me", HttpStatusCode.OK, new MeResponse(User));
        var api = server.Client("1.4.2");
        api.Token = "tok";

        await api.MeAsync();

        var (req, _) = Assert.Single(server.Requests);
        Assert.Equal("Bearer tok", req.Headers.Authorization!.ToString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", req.Headers.GetValues(ApiHeaders.InstallId).Single());
        Assert.Equal("1.4.2", req.Headers.GetValues(ApiHeaders.ClientVersion).Single());
    }

    [Fact]
    public async Task Error_bodies_become_api_exceptions_with_fields()
    {
        var server = new FakeServer();
        server.On("POST", "auth/register", (_, _) => FakeServer.Respond(HttpStatusCode.UnprocessableEntity,
            new ErrorResponse(ErrorCodes.WeakPassword, "Too short", new Dictionary<string, string> { ["password"] = "Use at least 12 characters." })));
        var ex = await Assert.ThrowsAsync<ApiException>(() => server.Client().RegisterAsync(new RegisterRequest("a@b.co", "x", true)));
        Assert.Equal(422, ex.Status);
        Assert.Equal(ErrorCodes.WeakPassword, ex.Code);
        Assert.Equal("Use at least 12 characters.", ex.MessageFor("password"));
    }

    [Fact]
    public async Task Network_failure_is_server_unreachable()
    {
        var server = new FakeServer { Offline = true };
        await Assert.ThrowsAsync<ServerUnreachableException>(() => server.Client().HealthAsync());
    }

    [Fact]
    public async Task Session_replaced_raises_session_ended_and_drops_the_token()
    {
        var server = new FakeServer();
        server.On("GET", "macros", (_, _) => FakeServer.Error(HttpStatusCode.Unauthorized, ErrorCodes.SessionReplaced));
        var api = server.Client();
        api.Token = "tok";
        SessionEndedEventArgs? ended = null;
        api.SessionEnded += (_, e) => ended = e;

        await Assert.ThrowsAsync<ApiException>(() => api.ListMacrosAsync());

        Assert.Equal(SessionEndReason.Replaced, ended!.Reason);
        Assert.Null(api.Token);
    }

    [Fact]
    public async Task Login_answers_with_either_a_token_or_a_challenge()
    {
        var server = new FakeServer();
        var device = new DeviceInfo(Guid.NewGuid(), new string('a', 64), "PC");
        server.On("POST", "auth/login", HttpStatusCode.Accepted, new ChallengeResponse(Guid.Parse("22222222-2222-2222-2222-222222222222")));
        var challenge = await server.Client().LoginAsync(new LoginRequest("a@b.co", "pw", device));
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), challenge.ChallengeId);
        Assert.Null(challenge.Token);

        server.On("POST", "auth/login", HttpStatusCode.OK, new AuthResponse("tok", User));
        var direct = await server.Client().LoginAsync(new LoginRequest("a@b.co", "pw", device));
        Assert.Equal("tok", direct.Token);
        Assert.Null(direct.ChallengeId);
    }

    [Fact]
    public async Task Macro_writes_send_if_match()
    {
        var server = new FakeServer();
        var id = Guid.NewGuid();
        server.On("PUT", $"macros/{id}/draft", HttpStatusCode.OK, new DraftSavedResponse(8, DateTimeOffset.UtcNow));
        await server.Client().SaveDraftAsync(id, 7, new Contracts.MacroDocument.MacroDoc { Name = "x" });
        var (req, body) = Assert.Single(server.Requests);
        Assert.Equal("\"7\"", req.Headers.GetValues(ApiHeaders.IfMatch).Single());
        Assert.Contains("\"doc\"", body);
    }
}
