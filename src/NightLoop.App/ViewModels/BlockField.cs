using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightLoop.App.Services;

namespace NightLoop.App.ViewModels;

/// <summary>
/// One field in the block inspector. Typing writes straight into the block's params when the value is valid;
/// otherwise the error shows under the field and the block keeps its last good value.
/// </summary>
public sealed partial class BlockField : ObservableObject
{
    private readonly BlockRow _row;
    private readonly Action<BlockRow, BlockField> _changed;
    private readonly bool _loading;
    private readonly Func<int> _sliderDefault;

    public BlockField(BlockRow row, FieldDef def, Action<BlockRow, BlockField> changed, IReadOnlyList<string>? macroNames = null,
        IReadOnlyList<string>? labelNames = null, Func<int>? sliderDefault = null)
    {
        _row = row;
        _sliderDefault = sliderDefault ?? (() => 0);
        Def = def;
        _changed = changed;
        _loading = true;
        Value = def.Get(row.Block);
        Choices = def.Kind switch
        {
            FieldKind.MacroName => MacroChoices(def, Value, macroNames ?? []),
            FieldKind.LabelName => NameChoices(Value, labelNames ?? []),
            _ => def.Choices ?? [],
        };
        _loading = false;
    }

    public FieldDef Def { get; }
    public string Label => Def.Label;
    public string? Hint => Def.Hint;
    public bool HasHint => !string.IsNullOrEmpty(Def.Hint);
    public string Placeholder => Def.Placeholder ?? "";
    public IReadOnlyList<Choice> Choices { get; }
    public int MaxLength => Def.MaxLength;
    /// <summary>Numbers, keys and colors use the monospace font; notes and messages don't.</summary>
    public bool IsMono => Def.Mono;

    public bool IsText => Def.Kind == FieldKind.Text;
    /// <summary>The label and hint above the box; slider fields show their own note instead of the hint.</summary>
    public bool ShowsHint => HasHint && !IsSlider;
    public bool IsMultiLine => Def.Kind == FieldKind.MultiLine;
    public bool IsChoice => Def.Kind is FieldKind.Choice or FieldKind.MacroName or FieldKind.LabelName;
    /// <summary>A program: the box opens File Explorer, the arrow lists running apps.</summary>
    public bool IsProgram => Def.Kind == FieldKind.Program;

    /// <summary>Program fields: the file name, big; the folder (Open window only) goes under it.</summary>
    public string ProgramName => Value.Length == 0 ? "" : BlockCatalog.WindowFileName(Value);
    public string ProgramFolder => Def.FullPath && Value.Length > 0 && Value.Length > ProgramName.Length
        ? Value[..^ProgramName.Length].TrimEnd('\\', '/')
        : "";
    public bool HasProgram => Value.Length > 0;
    /// <summary>Optional program boxes (blank means the target game) can be cleared.</summary>
    public bool CanClear => IsProgram && HasProgram && !Def.FullPath;

    /// <summary>Programs with a window, listed when the arrow next to a program box is pressed.</summary>
    public List<RunningProgram> RunningPrograms { get; private set; } = [];
    [ObservableProperty] private bool _isPickingRunning;

    partial void OnIsPickingRunningChanged(bool value)
    {
        if (!value) return;
        RunningPrograms = Programs.Running();
        OnPropertyChanged(nameof(RunningPrograms));
    }

    /// <summary>Picked from the running apps.</summary>
    public RunningProgram? PickedRunning
    {
        get => null;
        set
        {
            if (value is null) return;
            IsPickingRunning = false;
            if (Def.FullPath)
            {
                if (value.Path is null)
                {
                    Error = $"Windows won't say where {value.Exe} is (it may be running as administrator). Use File Explorer instead.";
                    return;
                }
                Value = Programs.Portable(value.Path);
            }
            else Value = value.Exe;
        }
    }

    /// <summary>Clicking a program box: File Explorer.</summary>
    [RelayCommand]
    private void Browse()
    {
        if (Programs.Browse(Value, anyFile: Def.FullPath) is not { } file) return;
        Value = Def.FullPath ? Programs.Portable(file) : System.IO.Path.GetFileName(file);
    }

    [RelayCommand]
    private void ClearProgram() => Value = "";

    // ---- Slider fields (Wait after click) ----

    public bool IsSlider => Def.Kind == FieldKind.Slider;
    public int SliderMax => Math.Max(1, Def.SliderMax);
    /// <summary>Set on this block by hand; otherwise it follows the Settings slider.</summary>
    public bool HasOwnValue => IsSlider && Value.Length > 0;

    /// <summary>The slider's position: the block's own value, or the Settings one it follows. Moving it sets the block's own.</summary>
    public double SliderValue
    {
        get => HasOwnValue && int.TryParse(Value, out var n) ? n : _sliderDefault();
        set
        {
            var n = (int)Math.Round(Math.Clamp(value, 0, SliderMax));
            if (HasOwnValue ? Value == n.ToString(System.Globalization.CultureInfo.InvariantCulture) : n == _sliderDefault()) return;
            Value = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public string SliderText => $"{(int)SliderValue:N0} ms";
    public string SliderNote => HasOwnValue
        ? "Set on this block."
        : $"Follows Settings › Macros › Wait after clicks ({_sliderDefault():N0} ms).";

    /// <summary>Back to following the Settings slider.</summary>
    [RelayCommand]
    private void UseSettingsValue() => Value = "";

    /// <summary>The Settings slider moved: blocks that follow it show the new value.</summary>
    public void RefreshSlider()
    {
        if (!IsSlider) return;
        OnPropertyChanged(nameof(SliderValue));
        OnPropertyChanged(nameof(SliderText));
        OnPropertyChanged(nameof(SliderNote));
    }
    /// <summary>Choice fields can change which other fields show, so the inspector rebuilds after them.</summary>
    public bool RebuildsInspector => IsChoice;

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _error = "";

    /// <summary>For choice fields: the picked choice.</summary>
    public Choice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Value == Value);
        set { if (value is not null && value.Value != Value) Value = value.Value; }
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(ProgramName));
        OnPropertyChanged(nameof(ProgramFolder));
        OnPropertyChanged(nameof(HasProgram));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(HasOwnValue));
        RefreshSlider();
        if (_loading) return;
        string? error;
        try
        {
            error = Def.Set(_row.Block, value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            error = "That value isn't valid here.";
        }
        Error = error ?? "";
        if (Error.Length > 0) return;
        _row.Refresh();
        _changed(_row, this);
    }

    /// <summary>This macro's labels, plus the current value if its label is gone (so it shows what's broken).</summary>
    private static List<Choice> NameChoices(string current, IReadOnlyList<string> names)
    {
        var list = names.Distinct(StringComparer.Ordinal).Select(n => new Choice(n, n)).ToList();
        if (current.Length > 0 && list.All(c => c.Value != current)) list.Add(new Choice(current, $"{current} (missing)"));
        return list;
    }

    /// <summary>Saved macros to pick from, plus "All other macros" for Pause / Resume and the current value if it's gone.</summary>
    private static List<Choice> MacroChoices(FieldDef def, string current, IReadOnlyList<string> names)
    {
        var list = new List<Choice>();
        if (def.AllMacros) list.Add(new Choice("*", "All other macros"));
        list.AddRange(names.Distinct(StringComparer.Ordinal).Select(n => new Choice(n, n)));
        if (current.Length > 0 && list.All(c => c.Value != current)) list.Add(new Choice(current, current));
        return list;
    }
}
