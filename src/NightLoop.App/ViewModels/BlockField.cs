using CommunityToolkit.Mvvm.ComponentModel;

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

    public BlockField(BlockRow row, FieldDef def, Action<BlockRow, BlockField> changed, IReadOnlyList<string>? macroNames = null)
    {
        _row = row;
        Def = def;
        _changed = changed;
        _loading = true;
        Value = def.Get(row.Block);
        Choices = def.Kind == FieldKind.MacroName ? MacroChoices(def, Value, macroNames ?? []) : def.Choices ?? [];
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
    public bool IsMultiLine => Def.Kind == FieldKind.MultiLine;
    public bool IsChoice => Def.Kind is FieldKind.Choice or FieldKind.MacroName;
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
