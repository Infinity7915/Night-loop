using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightLoop.LocalData;

namespace NightLoop.App.ViewModels;

/// <summary>
/// The target game (schema section 8): macros only send input while this game's window is in front. Picked in
/// Settings, shown on the Macros banner. Built-in games live in code, added ones in games.json, the pick in
/// settings.json, so all of it stays on this PC.
/// </summary>
public sealed partial class TargetGamesViewModel : ObservableObject
{
    private readonly GamesStore _store;
    private readonly LocalSettings _settings;

    public TargetGamesViewModel(GamesStore store, LocalSettings settings)
    {
        _store = store;
        _settings = settings;
        var saved = settings.ActiveGame;
        foreach (var g in store.Load())
            Games.Add(new GameOption(this, g, isUserGame: !GamesStore.BuiltIn.Contains(g)));
        _active = Games.FirstOrDefault(o => o.Game.Name == saved) ?? Games[0];
        _active.IsActive = true;
    }

    public ObservableCollection<GameOption> Games { get; } = [];

    /// <summary>Running apps with a window, offered when adding a game.</summary>
    public ObservableCollection<GameTarget> RunningApps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Target), nameof(Banner), nameof(BannerGame))]
    private GameOption _active;

    /// <summary>The game macros lock onto. Exe is null for N/A.</summary>
    public GameTarget Target => Active.Game;

    public string BannerGame => Target.Name;

    public string Banner => Target.Exe is null
        ? "No target game: input goes to whatever window is in front."
        : $"Input goes to {Target.Name} only, and pauses when it isn't focused.";

    public event Action<GameTarget>? TargetChanged;

    // ---- Add a game ----
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newExe = "";
    [ObservableProperty] private string _addError = "";
    [ObservableProperty] private GameTarget? _pickedApp;

    partial void OnPickedAppChanged(GameTarget? value)
    {
        if (value is null) return;
        NewName = value.Name;
        NewExe = value.Exe ?? "";
    }

    internal void Select(GameOption option)
    {
        if (Active == option) return;
        Active.IsActive = false;
        Active = option;
        option.IsActive = true;
        _settings.ActiveGame = option.Game.Exe is null ? null : option.Game.Name;
        TargetChanged?.Invoke(option.Game);
    }

    /// <summary>Selects a game by name (used when a macro remembers its game). Unknown names are ignored.</summary>
    public void SelectByName(string? name)
    {
        var option = name is null ? Games[0] : Games.FirstOrDefault(o => o.Game.Name == name);
        if (option is not null) Select(option);
    }

    [RelayCommand]
    private void StartAdd()
    {
        NewName = "";
        NewExe = "";
        AddError = "";
        PickedApp = null;
        RunningApps.Clear();
        foreach (var app in FindRunningApps()) RunningApps.Add(app);
        IsAdding = true;
    }

    [RelayCommand]
    private void CancelAdd() => IsAdding = false;

    [RelayCommand]
    private void ConfirmAdd()
    {
        var exe = NewExe.Trim();
        if (exe.Length > 0 && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        var game = new GameTarget(NewName.Trim(), exe);
        if (!GamesStore.IsValid(game))
        {
            AddError = "Give it a name (up to 40 characters) and the program's file name, like VALORANT-Win64-Shipping.exe.";
            return;
        }
        if (Games.Any(o => string.Equals(o.Game.Name, game.Name, StringComparison.OrdinalIgnoreCase)))
        {
            AddError = $"There's already a game called {game.Name}.";
            return;
        }
        if (Games.Count(o => o.IsUserGame) >= GamesStore.MaxUserGames)
        {
            AddError = $"You can add up to {GamesStore.MaxUserGames} games.";
            return;
        }
        var option = new GameOption(this, game, isUserGame: true);
        Games.Add(option);
        SaveUserGames();
        IsAdding = false;
        option.RefreshStatus();
        Select(option);
    }

    internal void Remove(GameOption option)
    {
        if (!option.IsUserGame) return;
        if (Active == option) Select(Games[0]);
        Games.Remove(option);
        SaveUserGames();
    }

    /// <summary>Updates the Detected / Not running labels.</summary>
    public void RefreshStatus()
    {
        foreach (var g in Games) g.RefreshStatus();
    }

    private void SaveUserGames()
    {
        try
        {
            _store.SaveUserGames(Games.Where(o => o.IsUserGame).Select(o => o.Game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddError = "Couldn't save the games list: " + ex.Message;
        }
    }

    private static IEnumerable<GameTarget> FindRunningApps()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<GameTarget>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                var exe = p.ProcessName + ".exe";
                if (exe.Equals("NightLoop.exe", StringComparison.OrdinalIgnoreCase) || !seen.Add(exe)) continue;
                var title = p.MainWindowTitle.Length > 40 ? p.MainWindowTitle[..40] : p.MainWindowTitle;
                list.Add(new GameTarget(title, exe));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process ended or is protected; skip it.
            }
            finally
            {
                p.Dispose();
            }
        }
        return list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    internal static bool IsRunning(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }
}

/// <summary>One row in Settings → Target game.</summary>
public sealed partial class GameOption : ObservableObject
{
    private readonly TargetGamesViewModel _owner;

    public GameOption(TargetGamesViewModel owner, GameTarget game, bool isUserGame)
    {
        _owner = owner;
        Game = game;
        IsUserGame = isUserGame;
        Detail = game.Exe is null
            ? "No target — macros can send input to any window"
            : $"Matches the {game.Exe} process";
    }

    public GameTarget Game { get; }
    public string Name => Game.Name;
    public string Detail { get; }
    public bool IsUserGame { get; }

    /// <summary>"Detected", "Not running" or "" for N/A.</summary>
    [ObservableProperty] private string _status = "";

    private bool _isActive;

    /// <summary>Bound to the row's radio button. Checking it makes this the target game.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetProperty(ref _isActive, value)) return;
            if (value) _owner.Select(this);
        }
    }

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    public void RefreshStatus() =>
        Status = Game.Exe is null ? "" : TargetGamesViewModel.IsRunning(Game.Exe) ? "Detected" : "Not running";
}
