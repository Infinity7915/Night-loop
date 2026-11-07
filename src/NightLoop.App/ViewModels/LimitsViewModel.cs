using CommunityToolkit.Mvvm.ComponentModel;
using NightLoop.Client;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>
/// The account's limits: macros and blocks held out of the caps, new macros and blocks placed today, and when the
/// daily counts reset (midnight UTC, shown in local time). Settings › Account shows it, the editor checks it before
/// placing blocks, and it raises <see cref="Notice"/> (an in-app message, never a Windows one) once 10 or fewer
/// blocks or macros are left.
/// </summary>
public sealed partial class LimitsViewModel(ApiClient? api) : ObservableObject
{
    /// <summary>Blocks placed in the editor since the server last reported (they go up with the next autosave).</summary>
    private int _pendingBlocks;
    private int? _blocksWarnedAt;
    private int? _macrosWarnedAt;

    /// <summary>Null until the server has answered once (or when it's too old to report limits).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage), nameof(MacrosText), nameof(MacrosPercent), nameof(NewMacrosText),
        nameof(NewMacrosPercent), nameof(BlocksText), nameof(BlocksPercent), nameof(BlocksTodayText), nameof(BlocksTodayPercent),
        nameof(ResetText), nameof(BlocksLeft), nameof(MacrosLeft))]
    private DailyUsage? _usage;

    /// <summary>"8 blocks left to place today…": shown by the window as an in-app message.</summary>
    public event Action<string>? Notice;

    public bool HasUsage => Usage is not null;

    public int BlocksLeft => Usage is null ? int.MaxValue : Math.Max(0, Usage.BlocksLeft - _pendingBlocks);
    public int MacrosLeft => Usage?.MacrosLeft ?? int.MaxValue;

    private int BlocksNow => (Usage?.Blocks ?? 0) + _pendingBlocks;
    private int BlocksTodayNow => Math.Min((Usage?.BlocksToday ?? 0) + _pendingBlocks, Usage?.BlocksPerDay ?? 0);

    public string MacrosText => Usage is null ? "" : $"{Usage.Macros} of {Usage.MacrosMax}";
    public double MacrosPercent => Percent(Usage?.Macros, Usage?.MacrosMax);
    public string NewMacrosText => Usage is null ? "" : $"{Usage.NewMacrosToday} of {Usage.NewMacrosPerDay}";
    public double NewMacrosPercent => Percent(Usage?.NewMacrosToday, Usage?.NewMacrosPerDay);
    public string BlocksText => Usage is null ? "" : $"{BlocksNow:N0} of {Usage.BlocksMax:N0}";
    public double BlocksPercent => Percent(BlocksNow, Usage?.BlocksMax);
    public string BlocksTodayText => Usage is null ? "" : $"{BlocksTodayNow} of {Usage.BlocksPerDay}";
    public double BlocksTodayPercent => Percent(BlocksTodayNow, Usage?.BlocksPerDay);

    /// <summary>"Daily counts reset at 7:00 PM (in 3 h 12 min)."</summary>
    public string ResetText
    {
        get
        {
            if (Usage is null) return "";
            var left = Usage.ResetsAt - DateTimeOffset.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            var span = left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : $"{Math.Max(1, left.Minutes)} min";
            return $"New macros and blocks today reset at {Usage.ResetsAt.ToLocalTime():t} (in {span}), midnight UTC.";
        }
    }

    /// <summary>Asks the server. Quietly does nothing when it can't be reached or doesn't know /me/limits yet.</summary>
    public async Task RefreshAsync()
    {
        if (api is null) return;
        try
        {
            Apply(await api.GetLimitsAsync());
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            // Settings keeps the last numbers; the editor falls back to the server's own checks.
        }
    }

    /// <summary>The server's counts (after an autosave or a refresh). They already include anything placed before it.</summary>
    public void Apply(DailyUsage usage)
    {
        _pendingBlocks = 0;
        Usage = usage;
        Check();
    }

    /// <summary>
    /// Before the editor places <paramref name="count"/> new blocks. False (with why) when they don't fit in what's
    /// left today or on the account.
    /// </summary>
    public bool CanPlace(int count, out string why)
    {
        why = "";
        if (Usage is null || count <= BlocksLeft) return true;
        why = BlocksLeft == 0 ? NoneLeftText() : $"Only {Plural(BlocksLeft, "more block")} can be placed {Where()}, so these {count} don't fit.";
        return false;
    }

    /// <summary>Why nothing more can be placed (add, paste).</summary>
    public string NoneLeftText() => Usage is not null && TodayIsTighter()
        ? $"You've placed {Usage.BlocksPerDay} blocks today, the most for one day. More at {Usage.ResetsAt.ToLocalTime():t}."
        : $"Your macros hold {Usage?.BlocksMax ?? MacroLimits.BlocksPerUser:N0} blocks, the most an account can have. Delete some to add more.";

    /// <summary>The editor placed blocks; counted here until the next autosave reports the server's numbers.</summary>
    public void Placed(int count)
    {
        if (count <= 0 || Usage is null) return;
        _pendingBlocks += count;
        OnPropertyChanged(nameof(BlocksLeft));
        OnPropertyChanged(nameof(BlocksText));
        OnPropertyChanged(nameof(BlocksPercent));
        OnPropertyChanged(nameof(BlocksTodayText));
        OnPropertyChanged(nameof(BlocksTodayPercent));
        Check();
    }

    /// <summary>Warns each time the count left drops while it's 10 or fewer; leaving that zone (a reset) starts over.</summary>
    private void Check()
    {
        if (Usage is null) return;
        var blocks = BlocksLeft;
        if (blocks > MacroLimits.WarnWhenLeft) _blocksWarnedAt = null;
        else if (_blocksWarnedAt is null || blocks < _blocksWarnedAt)
        {
            _blocksWarnedAt = blocks;
            Notice?.Invoke(blocks == 0
                ? NoneLeftText()
                : $"{Plural(blocks, "block")} left to place {Where()}.{(TodayIsTighter() ? $" More at {Usage.ResetsAt.ToLocalTime():t}." : "")}");
        }

        var macros = MacrosLeft;
        if (macros > MacroLimits.WarnWhenLeft) _macrosWarnedAt = null;
        else if (_macrosWarnedAt is null || macros < _macrosWarnedAt)
        {
            _macrosWarnedAt = macros;
            var today = Usage.NewMacrosPerDay - Usage.NewMacrosToday < Usage.MacrosMax - Usage.Macros;
            Notice?.Invoke(macros == 0
                ? today
                    ? $"You've made {Usage.NewMacrosPerDay} new macros today, the most for one day. More at {Usage.ResetsAt.ToLocalTime():t}."
                    : $"You have {Usage.MacrosMax} macros, the most an account can hold. Delete one to make room."
                : today
                    ? $"You can make {Plural(macros, "more new macro")} today."
                    : $"You can make {Plural(macros, "more macro")} ({Usage.MacrosMax} at most).");
        }
    }

    /// <summary>Today's cap runs out before the account's.</summary>
    private bool TodayIsTighter() =>
        Usage is not null && Usage.BlocksPerDay - Usage.BlocksToday <= Usage.BlocksMax - Usage.Blocks;

    private string Where() => TodayIsTighter() ? "today" : "on your account";

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private static double Percent(int? used, int? max) =>
        used is null || max is null or 0 ? 0 : Math.Clamp(100.0 * used.Value / max.Value, 0, 100);
}
