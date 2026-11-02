using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>
/// One field in the block inspector. Typing writes straight into the block's params when the value is valid;
/// otherwise the error shows under the field and the block keeps its last good value.
/// </summary>
public sealed partial class BlockField : ObservableObject
{
    private readonly BlockRow _row;
    private readonly Action<BlockRow> _changed;
    private bool _loading;

    public BlockField(BlockRow row, FieldDef def, Action<BlockRow> changed, IReadOnlyList<string>? macroNames = null)
    {
        _row = row;
        Def = def;
        _changed = changed;
        MacroNames = macroNames ?? [];
        _loading = true;
        Value = Read();
        _loading = false;
    }

    public FieldDef Def { get; }
    public string Label => Def.Label;
    public string? Hint => Def.Hint;
    public bool HasHint => !string.IsNullOrEmpty(Def.Hint);
    public FieldKind Kind => Def.Kind;
    public IReadOnlyList<Choice> Choices => Def.Choices ?? [];
    public IReadOnlyList<string> MacroNames { get; }
    public int MaxLength => Def.MaxLength;

    public bool IsText => Kind is FieldKind.Text or FieldKind.Int or FieldKind.Key;
    public bool IsMultiLine => Kind == FieldKind.MultiLine;
    public bool IsChoice => Kind == FieldKind.Choice;
    public bool IsMacroName => Kind == FieldKind.MacroName;

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _error = "";

    /// <summary>For choice fields: the picked choice.</summary>
    public Choice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Value == Value);
        set { if (value is not null) Value = value.Value; }
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedChoice));
        if (_loading) return;
        Error = Write(value) ?? "";
        if (Error.Length == 0)
        {
            _row.Refresh();
            _changed(_row);
        }
    }

    private JsonObject Params => _row.Block.Params;

    private string Read()
    {
        if (Def.Path.StartsWith("condition.", StringComparison.Ordinal))
        {
            var (kind, value) = Conditions.Read(Params["condition"] as JsonObject);
            return Def.Path == "condition.kind" ? kind : value;
        }
        var node = Find(create: false, out var key)?[key];
        return node switch
        {
            JsonValue v when v.TryGetValue(out string? s) => s,
            JsonValue v => v.ToJsonString(),
            _ => "",
        };
    }

    /// <summary>Writes the value; returns an error message instead when it isn't valid.</summary>
    private string? Write(string text)
    {
        if (Def.Path.StartsWith("condition.", StringComparison.Ordinal))
        {
            var (kind, value) = Conditions.Read(Params["condition"] as JsonObject);
            if (Def.Path == "condition.kind") kind = text; else value = text;
            if (kind.TrimStart('!') == "processRunning" && string.IsNullOrWhiteSpace(value))
                return Def.Path == "condition.value" ? "Enter a process name, like RobloxPlayerBeta.exe." : null;
            Params["condition"] = Conditions.Write(kind, value);
            return null;
        }

        var parent = Find(create: true, out var key)!;
        var trimmed = text.Trim();
        switch (Def.Kind)
        {
            case FieldKind.Int:
            case FieldKind.Choice when Def.IntChoice:
                if (trimmed.Length == 0 && Def.Optional)
                {
                    parent.Remove(key);
                    return null;
                }
                if (!int.TryParse(trimmed, out var n)) return "Enter a whole number.";
                if (n < Def.Min || n > Def.Max) return $"Use {Def.Min:N0} to {Def.Max:N0}.";
                parent[key] = n;
                return null;
            case FieldKind.Key:
                if (!KeyCatalog.IsKnown(trimmed)) return "Not a key Night-loop knows. Try A, Space, F1, LeftShift or Enter.";
                parent[key] = KeyCatalog.TryGetVirtualKey(trimmed, out var vk) ? KeyCatalog.NameOf(vk) ?? trimmed : trimmed;
                return null;
            default:
                if (trimmed.Length == 0 && !Def.Optional) return $"{Def.Label} can't be empty.";
                if (text.Length > Def.MaxLength) return $"Use at most {Def.MaxLength:N0} characters.";
                if (trimmed.Length == 0) parent.Remove(key);
                else parent[key] = Def.Kind == FieldKind.MultiLine ? text : trimmed;
                return null;
        }
    }

    /// <summary>Finds the object holding the field ("from.x" → params.from) and the key inside it.</summary>
    private JsonObject? Find(bool create, out string key)
    {
        var parts = Def.Path.Split('.');
        var obj = Params;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (obj[parts[i]] is JsonObject child) obj = child;
            else if (create) obj = (JsonObject)(obj[parts[i]] = new JsonObject());
            else { key = parts[^1]; return null; }
        }
        key = parts[^1];
        return obj;
    }
}
