using System.Net;
using System.Text.Json;
using NightLoop.Contracts;
using NightLoop.Contracts.Api;

namespace NightLoop.Client.Tests;

public sealed class AccountSessionTests
{
    private static readonly UserDto User = new(Guid.NewGuid(), 1043, "a@b.co", "user#1043", true, 3, true, TwofaModes.NewDevices, DateTimeOffset.UtcNow);
    private static readonly DeviceIdentity Device = new(Guid.NewGuid(), new string('b', 64), "Windows 11 · TEST");

    [Fact]
    public async Task Restores_a_saved_token()
    {
        var server = new FakeServer();
        server.On("GET", "me", HttpStatusCode.OK, new MeResponse(User));
        var store = new MemorySessionStore();
        store.Save(new StoredSession { Token = "saved" });
        using var session = new AccountSession(server.Client(), store, Device);

        Assert.True(await session.TryRestoreAsync());
        Assert.Equal(User, session.User);
    }

    [Fact]
    public async Task Rejected_saved_token_is_forgotten()
    {
        var server = new FakeServer();
        server.On("GET", "me", (_, _) => FakeServer.Error(HttpStatusCode.Unauthorized, ErrorCodes.SessionInvalid));
        var store = new MemorySessionStore();
        store.Save(new StoredSession { Token = "old" });
        using var session = new AccountSession(server.Client(), store, Device);

        Assert.False(await session.TryRestoreAsync());
        Assert.Null(store.Load().Token);
    }

    [Fact]
    public async Task Sign_in_with_code_saves_token_and_trust_token_then_reuses_trust()
    {
        var server = new FakeServer();
        var challenge = Guid.NewGuid();
        server.On("POST", "auth/login", HttpStatusCode.Accepted, new ChallengeResponse(challenge));
        server.On("POST", "auth/login/verify", HttpStatusCode.OK, new AuthResponse("tok", User, "trust-me"));
        var store = new MemorySessionStore();
        using var session = new AccountSession(server.Client(), store, Device);
        UserDto? signedIn = null;
        session.SignedIn += u => signedIn = u;

        var step = await session.SignInAsync("A@B.co", "pw");
        Assert.Equal(challenge, Assert.IsType<SignInResult.NeedsCode>(step).ChallengeId);
        await session.VerifySignInAsync(challenge, " 123456 ", trustThisPc: true);

        Assert.Equal(User, signedIn);
        Assert.Equal("tok", store.Load().Token);
        Assert.Equal("trust-me", store.Load().TrustTokens["a@b.co"]);

        await session.SignInAsync("a@b.co", "pw");
        var login = server.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.EndsWith("auth/login")).Last();
        Assert.Equal("trust-me", JsonDocument.Parse(login.Body!).RootElement.GetProperty("trustToken").GetString());
    }

    [Fact]
    public async Task Replaced_session_signs_out_with_a_message()
    {
        var server = new FakeServer();
        server.On("POST", "auth/login", HttpStatusCode.OK, new AuthResponse("tok", User));
        server.On("GET", "me", (_, _) => FakeServer.Error(HttpStatusCode.Unauthorized, ErrorCodes.SessionReplaced));
        var store = new MemorySessionStore();
        using var session = new AccountSession(server.Client(), store, Device);
        SessionEndedEventArgs? ended = null;
        session.SignedOut += e => ended = e;

        await session.SignInAsync("a@b.co", "pw");
        await session.HeartbeatAsync();

        Assert.Equal(SessionEndReason.Replaced, ended!.Reason);
        Assert.Equal("Signed in on another device.", ended.Message);
        Assert.False(session.IsSignedIn);
        Assert.Null(store.Load().Token);
    }

    [Fact]
    public async Task Offline_heartbeats_raise_the_grace_event_after_five_minutes()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var server = new FakeServer();
        server.On("POST", "auth/login", HttpStatusCode.OK, new AuthResponse("tok", User));
        using var session = new AccountSession(server.Client(), new MemorySessionStore(), Device, clock);
        var expired = 0;
        session.OfflineGraceExpired += () => expired++;
        await session.SignInAsync("a@b.co", "pw");

        server.Offline = true;
        await session.HeartbeatAsync();
        Assert.True(session.IsOffline);
        Assert.Equal(0, expired);

        clock.Advance(TimeSpan.FromMinutes(5));
        await session.HeartbeatAsync();
        await session.HeartbeatAsync();
        Assert.Equal(1, expired);
    }

    [Fact]
    public async Task Sign_out_forgets_the_token_even_offline()
    {
        var server = new FakeServer();
        server.On("POST", "auth/login", HttpStatusCode.OK, new AuthResponse("tok", User));
        var store = new MemorySessionStore();
        using var session = new AccountSession(server.Client(), store, Device);
        await session.SignInAsync("a@b.co", "pw");

        server.Offline = true;
        await session.SignOutAsync();
        Assert.Null(store.Load().Token);
        Assert.False(session.IsSignedIn);
    }

    private static readonly UserDto Strict = User with { TwofaMode = TwofaModes.EverySignin };

    [Fact]
    public async Task Every_sign_in_asks_for_a_code_before_opening()
    {
        var server = new FakeServer();
        var challenge = Guid.NewGuid();
        server.On("GET", "me", HttpStatusCode.OK, new MeResponse(Strict));
        server.On("POST", "me/unlock", HttpStatusCode.OK, new UnlockResponse(challenge));
        server.On("POST", "me/unlock/verify", HttpStatusCode.OK, new MeResponse(Strict));
        var store = new MemorySessionStore();
        store.Save(new StoredSession { Token = "saved" });
        using var session = new AccountSession(server.Client(), store, Device);
        UserDto? signedIn = null;
        session.SignedIn += u => signedIn = u;

        Assert.True(await session.TryRestoreAsync());
        Assert.Equal(challenge, session.PendingUnlock);
        Assert.False(session.IsSignedIn);
        Assert.Null(signedIn);
        Assert.Equal("a@b.co", session.PendingUnlockEmail);

        await session.VerifyUnlockAsync(" 123456 ");
        Assert.Equal(Strict, signedIn);
        Assert.Null(session.PendingUnlock);
        var verify = server.Requests.Last(r => r.Request.RequestUri!.AbsolutePath.EndsWith("me/unlock/verify"));
        Assert.Equal("123456", JsonDocument.Parse(verify.Body!).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task New_devices_only_opens_without_a_code()
    {
        var server = new FakeServer();
        server.On("GET", "me", HttpStatusCode.OK, new MeResponse(User));
        var store = new MemorySessionStore();
        store.Save(new StoredSession { Token = "saved" });
        using var session = new AccountSession(server.Client(), store, Device);

        Assert.True(await session.TryRestoreAsync());
        Assert.True(session.IsSignedIn);
        Assert.Null(session.PendingUnlock);
        Assert.DoesNotContain(server.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith("me/unlock"));
    }

    [Fact]
    public async Task An_older_server_without_launch_codes_opens_as_before()
    {
        var server = new FakeServer();
        server.On("GET", "me", HttpStatusCode.OK, new MeResponse(Strict));
        // No route for me/unlock: the fake answers 404.
        var store = new MemorySessionStore();
        store.Save(new StoredSession { Token = "saved" });
        using var session = new AccountSession(server.Client(), store, Device);

        Assert.True(await session.TryRestoreAsync());
        Assert.True(session.IsSignedIn);
    }

    [Fact]
    public async Task A_launch_code_that_could_not_be_sent_can_be_sent_again_but_never_skipped()
    {
        var server = new FakeServer();
        var challenge = Guid.NewGuid();
        server.On("GET", "me", HttpStatusCode.OK, new MeResponse(Strict));
        server.On("POST", "me/unlock", (_, _) => FakeServer.Error(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited, "Too many codes today."));
        var store = new MemorySessionStore();
        store.Save(new StoredSession { Token = "saved" });
        using var session = new AccountSession(server.Client(), store, Device);

        Assert.True(await session.TryRestoreAsync());
        Assert.Equal(Guid.Empty, session.PendingUnlock);
        Assert.Equal("Too many codes today.", session.PendingUnlockError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.VerifyUnlockAsync("123456"));
        Assert.False(session.IsSignedIn);

        server.On("POST", "me/unlock", HttpStatusCode.OK, new UnlockResponse(challenge));
        Assert.Equal(challenge, await session.ResendUnlockAsync());
        Assert.Null(session.PendingUnlockError);
    }

    [Fact]
    public async Task Signing_out_from_the_launch_code_forgets_the_token()
    {
        var server = new FakeServer();
        server.On("GET", "me", HttpStatusCode.OK, new MeResponse(Strict));
        server.On("POST", "me/unlock", HttpStatusCode.OK, new UnlockResponse(Guid.NewGuid()));
        server.On("POST", "auth/logout", HttpStatusCode.NoContent);
        var store = new MemorySessionStore();
        store.Save(new StoredSession { Token = "saved" });
        using var session = new AccountSession(server.Client(), store, Device);
        SessionEndedEventArgs? ended = null;
        session.SignedOut += e => ended = e;

        await session.TryRestoreAsync();
        await session.SignOutAsync();

        Assert.Equal(SessionEndReason.SignedOut, ended!.Reason);
        Assert.Null(store.Load().Token);
        Assert.Null(session.PendingUnlock);
    }
}
