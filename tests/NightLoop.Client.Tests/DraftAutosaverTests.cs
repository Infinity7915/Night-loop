using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NightLoop.Contracts;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Client.Tests;

public sealed class DraftAutosaverTests
{
    private static readonly Guid Id = Guid.NewGuid();

    private static MacroDto Macro(int version, MacroDoc? draft = null, MacroDoc? saved = null) => new(
        Id, "M", null, null, 0, version, true, null, null, TriggerModes.Toggle, saved, null, draft, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static MacroDoc Doc(int blocks)
    {
        var doc = new MacroDoc { Name = "M" };
        for (var i = 0; i < blocks; i++) doc.Blocks.Add(Block.Create($"b{i + 1}", BlockTypes.Wait, new() { ["ms"] = 10 }));
        return doc;
    }

    /// <summary>Lets background tasks run after advancing fake time.</summary>
    private static async Task Settle(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }

    [Fact]
    public async Task Edits_are_debounced_into_one_draft_save()
    {
        var clock = new FakeTimeProvider();
        var server = new FakeServer();
        var version = 1;
        server.On("PUT", $"macros/{Id}/draft", (req, _) =>
        {
            Assert.Equal($"\"{version}\"", req.Headers.GetValues(ApiHeaders.IfMatch).Single());
            return FakeServer.Respond(HttpStatusCode.OK, new DraftSavedResponse(++version, DateTimeOffset.UtcNow));
        });
        await using var saver = new DraftAutosaver(server.Client(), Macro(1, draft: Doc(0)), clock);

        saver.Changed(Doc(1));
        saver.Changed(Doc(2));
        saver.Changed(Doc(3));
        Assert.Equal(SyncState.Pending, saver.State);

        clock.Advance(TimeSpan.FromSeconds(1.5));
        await Settle(() => saver.State == SyncState.Draft);

        var (_, body) = Assert.Single(server.Requests);
        Assert.Equal(3, JsonDocument.Parse(body!).RootElement.GetProperty("doc").GetProperty("blocks").GetArrayLength());
        Assert.Equal(2, saver.Version);
    }

    [Fact]
    public async Task Save_flushes_then_saves()
    {
        var server = new FakeServer();
        server.On("PUT", $"macros/{Id}/draft", HttpStatusCode.OK, new DraftSavedResponse(2, DateTimeOffset.UtcNow));
        server.On("POST", $"macros/{Id}/save", (req, _) =>
        {
            Assert.Equal("\"2\"", req.Headers.GetValues(ApiHeaders.IfMatch).Single());
            return FakeServer.Respond(HttpStatusCode.OK, Macro(3, saved: Doc(4)));
        });
        await using var saver = new DraftAutosaver(server.Client(), Macro(1, draft: Doc(0)), new FakeTimeProvider());

        var saved = await saver.SaveAsync(Doc(4));

        Assert.Equal(SyncState.Saved, saver.State);
        Assert.Equal(3, saver.Version);
        Assert.Equal(4, saved.SavedDoc!.Blocks.Count);
    }

    [Fact]
    public async Task Conflict_waits_for_the_user_then_keep_mine_resends_with_the_server_version()
    {
        var server = new FakeServer();
        var conflicted = false;
        server.On("PUT", $"macros/{Id}/draft", (req, _) =>
        {
            if (!conflicted)
            {
                conflicted = true;
                return FakeServer.Error(HttpStatusCode.Conflict, ErrorCodes.VersionConflict, "changed", Macro(5, draft: Doc(9)));
            }
            Assert.Equal("\"5\"", req.Headers.GetValues(ApiHeaders.IfMatch).Single());
            return FakeServer.Respond(HttpStatusCode.OK, new DraftSavedResponse(6, DateTimeOffset.UtcNow));
        });
        await using var saver = new DraftAutosaver(server.Client(), Macro(1, draft: Doc(0)), new FakeTimeProvider(), TimeSpan.Zero);

        saver.Changed(Doc(2));
        await saver.FlushAsync();
        Assert.Equal(SyncState.Conflict, saver.State);
        Assert.Equal(9, saver.ServerCopy!.DraftDoc!.Blocks.Count);

        saver.Changed(Doc(3)); // more edits while undecided just wait
        Assert.Single(server.Requests);

        saver.KeepMine();
        await saver.FlushAsync();
        await Settle(() => saver.State == SyncState.Draft);
        Assert.Equal(6, saver.Version);
        var last = server.Requests.Last().Body!;
        Assert.Equal(3, JsonDocument.Parse(last).RootElement.GetProperty("doc").GetProperty("blocks").GetArrayLength());
    }

    [Fact]
    public async Task Conflict_load_server_copy_drops_local_edits()
    {
        var server = new FakeServer();
        server.On("PUT", $"macros/{Id}/draft", (_, _) => FakeServer.Error(HttpStatusCode.Conflict, ErrorCodes.VersionConflict, "changed", Macro(5, saved: Doc(1))));
        await using var saver = new DraftAutosaver(server.Client(), Macro(1, draft: Doc(0)), new FakeTimeProvider(), TimeSpan.Zero);
        saver.Changed(Doc(2));
        await saver.FlushAsync();

        var copy = saver.LoadServerCopy();
        Assert.Equal(5, saver.Version);
        Assert.Equal(SyncState.Saved, saver.State);
        Assert.Single(copy.SavedDoc!.Blocks);
    }

    [Fact]
    public async Task Offline_retries_until_the_server_is_back()
    {
        var clock = new FakeTimeProvider();
        var server = new FakeServer { Offline = true };
        server.On("PUT", $"macros/{Id}/draft", HttpStatusCode.OK, new DraftSavedResponse(2, DateTimeOffset.UtcNow));
        await using var saver = new DraftAutosaver(server.Client(), Macro(1, saved: Doc(0)), clock, TimeSpan.Zero);

        saver.Changed(Doc(1));
        await Settle(() => saver.State == SyncState.Offline);

        server.Offline = false;
        await Settle(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(2)); // keeps nudging until the back-off timer is waiting
            return saver.State == SyncState.Draft;
        });
        Assert.Equal(2, saver.Version);
    }

    [Fact]
    public async Task Invalid_macro_is_an_error_with_the_server_message()
    {
        var server = new FakeServer();
        server.On("PUT", $"macros/{Id}/draft", (_, _) => FakeServer.Error(HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidMacro, "Every loop needs an End."));
        await using var saver = new DraftAutosaver(server.Client(), Macro(1, saved: Doc(0)), new FakeTimeProvider(), TimeSpan.Zero);
        saver.Changed(Doc(1));
        await saver.FlushAsync();
        Assert.Equal(SyncState.Error, saver.State);
        Assert.Equal("Every loop needs an End.", saver.Message);
    }
}
