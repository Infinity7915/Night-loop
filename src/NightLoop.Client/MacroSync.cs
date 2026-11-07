using NightLoop.Contracts;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Client;

/// <summary>What the editor's sync chip shows.</summary>
public enum SyncState
{
    /// <summary>Matches the saved copy on the account.</summary>
    Saved,
    /// <summary>Edited; waiting for the debounce.</summary>
    Pending,
    Saving,
    /// <summary>Unsaved changes are stored as a draft on the account ("Draft · autosaved").</summary>
    Draft,
    /// <summary>The server can't be reached; changes wait and go up when it's back.</summary>
    Offline,
    /// <summary>Changed on another PC since we loaded it. The user picks Keep mine or Load server copy.</summary>
    Conflict,
    /// <summary>The server refused the change (e.g. invalid_macro). Message says why.</summary>
    Error,
}

/// <summary>
/// Autosave for the macro open in the editor (schema section 6, "Draft and save lifecycle"): edits are debounced
/// 1.5 s and sent with PUT /macros/{id}/draft and If-Match; one request at a time; network failures retry with
/// back-off; a version conflict stops autosave until the user decides.
/// </summary>
public sealed class DraftAutosaver : IAsyncDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly ApiClient _api;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _life = new();
    private string? _pendingJson;
    private CancellationTokenSource? _timer;
    private Task _loop = Task.CompletedTask;

    public DraftAutosaver(ApiClient api, MacroDto macro, TimeProvider? clock = null, TimeSpan? debounce = null)
    {
        _api = api;
        _clock = clock ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        MacroId = macro.Id;
        Version = macro.Version;
        State = macro.DraftDoc is null ? SyncState.Saved : SyncState.Draft;
    }

    public Guid MacroId { get; }
    /// <summary>The version the server last told us; sent as If-Match.</summary>
    public int Version { get; private set; }
    public SyncState State { get; private set; }
    public string? Message { get; private set; }
    /// <summary>On Conflict: the server's copy.</summary>
    public MacroDto? ServerCopy { get; private set; }

    /// <summary>Raised on a thread-pool thread; marshal to the UI yourself.</summary>
    public event Action<SyncState>? StateChanged;
    /// <summary>The account's daily counts after a draft went up (thread-pool thread, like StateChanged).</summary>
    public event Action<DailyUsage>? UsageReported;

    /// <summary>Records an edit. The document is copied now, so later changes to it don't leak into this save.</summary>
    public void Changed(MacroDoc doc)
    {
        lock (_gate)
        {
            _pendingJson = MacroJson.Serialize(doc);
            if (State == SyncState.Conflict) return; // waits for KeepMine / LoadServerCopy
            _timer?.Cancel();
            var timer = _timer = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            SetState(SyncState.Pending);
            // Start the timer here, not inside the task, so the debounce counts from this edit.
            var delay = Task.Delay(_debounce, _clock, timer.Token);
            _loop = PushAfterAsync(delay, timer.Token);
        }
    }

    private async Task PushAfterAsync(Task delay, CancellationToken ct)
    {
        try
        {
            await delay.ConfigureAwait(false);
            await PushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer edit restarted the debounce.
        }
    }

    /// <summary>Sends any pending edit now (before Save, switching macros or closing).</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        lock (_gate) _timer?.Cancel();
        await PushAsync(ct);
    }

    /// <summary>Flushes the draft, then copies it to the saved copy on the account.</summary>
    public async Task<MacroDto> SaveAsync(MacroDoc current, CancellationToken ct = default)
    {
        Changed(current);
        await FlushAsync(ct);
        if (State is SyncState.Conflict or SyncState.Error or SyncState.Offline)
            throw new InvalidOperationException(Message ?? "The draft couldn't be sent.");
        await _sending.WaitAsync(ct);
        try
        {
            var saved = await _api.SaveMacroAsync(MacroId, Version, ct);
            Version = saved.Version;
            SetState(SyncState.Saved);
            return saved;
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.VersionConflict)
        {
            EnterConflict(ex);
            throw;
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Conflict answer: overwrite the server with this PC's copy.</summary>
    public void KeepMine()
    {
        string? json;
        lock (_gate)
        {
            if (ServerCopy is null) return;
            Version = ServerCopy.Version;
            ServerCopy = null;
            json = _pendingJson;
            SetState(SyncState.Pending);
        }
        if (json is not null) Changed(MacroJson.Deserialize(json));
    }

    /// <summary>Conflict answer: drop this PC's edits and continue from the server copy (returned).</summary>
    public MacroDto LoadServerCopy()
    {
        lock (_gate)
        {
            var copy = ServerCopy ?? throw new InvalidOperationException("There is no conflict to resolve.");
            Version = copy.Version;
            ServerCopy = null;
            _pendingJson = null;
            SetState(copy.DraftDoc is null ? SyncState.Saved : SyncState.Draft);
            return copy;
        }
    }

    /// <summary>Another call (e.g. PATCH rename) moved the version on; keep autosave in step.</summary>
    public void Adopt(MacroDto macro)
    {
        lock (_gate) Version = macro.Version;
    }

    private async Task PushAsync(CancellationToken ct)
    {
        await _sending.WaitAsync(ct);
        try
        {
            var backoff = TimeSpan.FromSeconds(2);
            while (true)
            {
                string? json;
                lock (_gate)
                {
                    json = _pendingJson;
                    if (json is null || State == SyncState.Conflict) return;
                }

                SetState(SyncState.Saving);
                try
                {
                    var res = await _api.SaveDraftAsync(MacroId, Version, MacroJson.Deserialize(json), ct);
                    if (res.Usage is { } usage) UsageReported?.Invoke(usage);
                    lock (_gate)
                    {
                        Version = res.Version;
                        // Only clear if nothing newer arrived while we were sending.
                        if (ReferenceEquals(_pendingJson, json)) _pendingJson = null;
                        if (_pendingJson is null) { SetState(SyncState.Draft); return; }
                    }
                }
                catch (ApiException ex) when (ex.Code == ErrorCodes.VersionConflict)
                {
                    EnterConflict(ex);
                    return;
                }
                catch (ApiException ex) when (ex.Code == ErrorCodes.RateLimited)
                {
                    await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(2), _clock, ct);
                }
                catch (ApiException ex)
                {
                    Message = ex.Message;
                    SetState(SyncState.Error);
                    return;
                }
                catch (ServerUnreachableException ex)
                {
                    Message = ex.Message;
                    SetState(SyncState.Offline);
                    await Task.Delay(backoff, _clock, ct);
                    backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
                }
            }
        }
        finally
        {
            _sending.Release();
        }
    }

    private void EnterConflict(ApiException ex)
    {
        lock (_gate)
        {
            ServerCopy = ex.Current;
            Message = "This macro was changed on another PC.";
            SetState(SyncState.Conflict);
        }
    }

    private void SetState(SyncState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        _life.Dispose();
        _sending.Dispose();
    }
}
