using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightLoop.Client;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>Saved macros: the n / 15 meter, search, groups, the drafts banner and each macro's options.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Colors offered for groups, as in the mockup (the server accepts any #RRGGBB).</summary>
    public static readonly IReadOnlyList<string> GroupColors =
        ["#A78BFA", "#F472B6", "#F87171", "#F59E0B", "#34D399", "#4CC9F0", "#58A6FF", "#E5E7EB"];

    private readonly List<SavedMacroRow> _rows = [];
    private readonly HashSet<Guid> _collapsed = [];
    private IReadOnlyList<GroupDto> _groups = [];
    private int _limit = MacroLimits.MacrosPerUser;

    public ObservableCollection<GroupSection> Sections { get; } = [];
    public ObservableCollection<GroupChoice> GroupChoices { get; } = [];

    [ObservableProperty] private string _usedText = "";
    [ObservableProperty] private double _usedPercent;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _draftCount;
    [ObservableProperty] private bool _hasNoMatch;
    [ObservableProperty] private bool _isLibraryEmpty = true;

    public string DraftBanner => DraftCount == 1
        ? $"1 draft is autosaved to your account but not saved yet. Drafts count toward your {_limit}-macro limit."
        : $"{DraftCount} drafts are autosaved to your account but not saved yet. Drafts count toward your {_limit}-macro limit.";

    partial void OnDraftCountChanged(int value) => OnPropertyChanged(nameof(DraftBanner));
    partial void OnSearchTextChanged(string value) => BuildSections();

    // ---- Group form (new or rename) ----
    [ObservableProperty] private bool _isGroupFormOpen;
    [ObservableProperty] private string _groupFormName = "";
    [ObservableProperty] private string _groupFormColor = "#A78BFA";
    [ObservableProperty] private string _groupFormError = "";
    [ObservableProperty] private string _groupFormTitle = "New group";
    private GroupDto? _editingGroup;

    /// <summary>Reloads the saved-macros list, groups and the n / 15 meter.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            ApplyList(await _api.ListMacrosAsync());
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            Status = ex.Message;
        }
    }

    private void ApplyList(MacroListResponse list)
    {
        _limit = list.Limit;
        _groups = list.Groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Rebuild the Group dropdown choices first: each row's Update picks its choice from this list.
        GroupChoices.Clear();
        GroupChoices.Add(new GroupChoice(null, "Ungrouped"));
        foreach (var g in _groups) GroupChoices.Add(new GroupChoice(g.Id, g.Name));

        var old = _rows.ToDictionary(r => r.Id);
        _rows.Clear();
        foreach (var m in list.Macros.OrderByDescending(m => m.UpdatedAt))
        {
            var row = old.TryGetValue(m.Id, out var existing) ? existing : new SavedMacroRow(this, m.Id);
            row.Update(m);
            _rows.Add(row);
        }
        UsedText = $"{list.Macros.Count} / {list.Limit} used";
        UsedPercent = list.Limit == 0 ? 0 : 100.0 * list.Macros.Count / list.Limit;
        OnPropertyChanged(nameof(IsAtLimit));
        OnPropertyChanged(nameof(LimitText));
        OnPropertyChanged(nameof(MeterLevel));
        if (!IsAtLimit) ShowLimit = false;

        RefreshRows();
        BuildSections();
        RefreshHotkeys();
    }

    /// <summary>Updates "In editor", running and draft flags without asking the server.</summary>
    private void RefreshRows()
    {
        foreach (var r in _rows)
        {
            r.IsInEditor = r.Id == _macroId;
            r.SetRunning(_runsById.TryGetValue(r.Id, out var run) && run.IsActive);
        }
        DraftCount = _rows.Count(r => r.HasDraft);
        IsLibraryEmpty = _rows.Count == 0;
        _nameToId = _rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The editor's autosave changed the open macro's draft state.</summary>
    private void MarkEditorRow(bool hasDraft)
    {
        if (_rows.FirstOrDefault(r => r.Id == _macroId) is not { } row) return;
        row.HasDraft = hasDraft;
        // Only once the editor shows this macro: while opening one, the name box still holds the previous macro's
        // name, and copying it here swapped the two names in the list (and in Run macro's name lookup).
        if (!_opening && !_loading) row.Name = string.IsNullOrWhiteSpace(MacroName) ? row.Name : MacroName.Trim();
        row.BlockCount = _doc.Blocks.Count;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        DraftCount = _rows.Count(r => r.HasDraft);
    }

    /// <summary>The n / 15 meter's color: accent, amber from 12, red when full.</summary>
    public string MeterLevel => _rows.Count >= _limit ? "full" : _rows.Count >= _limit - 3 ? "high" : "ok";

    private IReadOnlyList<string> LibraryNames() =>
        _rows.Where(r => r.Id != _macroId).Select(r => r.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void BuildSections()
    {
        var query = SearchText.Trim();
        var visible = query.Length == 0
            ? _rows
            : _rows.Where(r => r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        HasNoMatch = query.Length > 0 && visible.Count == 0 && _rows.Count > 0;

        Sections.Clear();
        foreach (var g in _groups)
        {
            var rows = visible.Where(r => r.GroupId == g.Id).ToList();
            if (query.Length > 0 && rows.Count == 0) continue;
            Sections.Add(new GroupSection(this, g, rows, _collapsed.Contains(g.Id)));
        }
        var loose = visible.Where(r => r.GroupId is null || _groups.All(g => g.Id != r.GroupId)).ToList();
        if (loose.Count > 0 || (Sections.Count > 0 && query.Length == 0))
            Sections.Add(new GroupSection(this, null, loose, false));
    }

    internal string? GroupColorOf(Guid? id) => id is { } g ? _groups.FirstOrDefault(x => x.Id == g)?.Color : null;

    internal void SetCollapsed(GroupSection section, bool collapsed)
    {
        if (section.Group is not { } g) return;
        if (collapsed) _collapsed.Add(g.Id);
        else _collapsed.Remove(g.Id);
    }

    // ---- Row actions ----

    internal Task OpenRowAsync(SavedMacroRow row) => row.IsInEditor ? Task.CompletedTask : OpenAsync(row.Id);

    internal async Task SaveRowAsync(SavedMacroRow row)
    {
        if (row.IsInEditor)
        {
            await SaveAsync();
            return;
        }
        try
        {
            await _api.SaveMacroAsync(row.Id, row.Version);
            Status = $"Saved \"{row.Name}\".";
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            Status = $"Couldn't save \"{row.Name}\": {ex.Message}";
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        foreach (var row in _rows.Where(r => r.HasDraft).ToList())
        {
            if (row.IsInEditor)
            {
                SyncDoc();
                if (_autosaver is not null)
                {
                    try { await _autosaver.SaveAsync(_doc); }
                    catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException) { Status = ex.Message; }
                }
                continue;
            }
            try { await _api.SaveMacroAsync(row.Id, row.Version); }
            catch (Exception ex) when (ex is ApiException or ServerUnreachableException) { Status = $"Couldn't save \"{row.Name}\": {ex.Message}"; }
        }
        await RefreshAsync();
        if (DraftCount == 0) Status = "Saved every draft.";
    }

    /// <summary>
    /// Turns every "Delete?" and "Delete group?" back into the plain delete button, except <paramref name="keep"/>
    /// (the one being pressed right now). Clicking anything else, a key, another tab or another window all do this,
    /// so a delete pressed by accident doesn't stay armed.
    /// </summary>
    public void CancelDeleteConfirms(object? keep = null)
    {
        foreach (var section in Sections)
        {
            if (!ReferenceEquals(section, keep)) section.ConfirmingDelete = false;
            foreach (var row in section.Rows)
                if (!ReferenceEquals(row, keep)) row.ConfirmingDelete = false;
        }
    }

    internal Task DeleteRowAsync(SavedMacroRow row) => DeleteMacroAsync(row.Id, row.Name);

    private async Task DeleteMacroAsync(Guid id, string name)
    {
        if (id == _macroId && (IsRecording || IsPlaying)) return;
        try
        {
            if (_runsById.TryGetValue(id, out var run)) run.Stop();
            if (id == _macroId)
            {
                await DisposeAutosaverAsync();
                await _api.DeleteMacroAsync(id);
                _macroId = null;
                ApplyDoc(NewDoc(), starter: true);
                SyncText = "Not on your account yet";
            }
            else
            {
                await _api.DeleteMacroAsync(id);
            }
            Status = $"Deleted \"{name}\".";
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            Status = ex.Message;
        }
    }

    /// <summary>Saves one macro's run options from its row (start key, trigger, run alongside, group).</summary>
    internal async Task UpdateRowAsync(SavedMacroRow row, UpdateMacroRequest req)
    {
        if (row.IsInEditor)
        {
            // The editor owns this macro's document: change it there so autosave carries it.
            if (req.HotkeyStart is not null) EditorHotkey = req.HotkeyStart;
            if (req.TriggerMode is not null) EditorTriggerMode = req.TriggerMode;
            if (req.RunAlongside is { } ra) EditorRunAlongside = ra;
            // The start key and the group are saved straight away; trigger and run alongside ride with autosave.
            if (req.GroupId is null && req.Ungroup is null && req.HotkeyStart is null) return;
            req = new UpdateMacroRequest(GroupId: req.GroupId, Ungroup: req.Ungroup, HotkeyStart: req.HotkeyStart, HotkeyStop: req.HotkeyStop);
            await FlushAsync();
        }
        try
        {
            var updated = await _api.UpdateMacroAsync(row.Id, req);
            if (row.IsInEditor) _autosaver?.Adopt(updated);
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            Status = ex.Message;
        }
        await RefreshAsync();
    }

    // ---- Groups ----

    [RelayCommand]
    private void NewGroup()
    {
        _editingGroup = null;
        GroupFormTitle = "New group";
        GroupFormName = "";
        GroupFormColor = "#F472B6";
        GroupFormError = "";
        IsGroupFormOpen = true;
        OnPropertyChanged(nameof(GroupFormSaveText));
    }

    internal void EditGroup(GroupDto group)
    {
        _editingGroup = group;
        GroupFormTitle = "Edit group";
        GroupFormName = group.Name;
        GroupFormColor = group.Color;
        GroupFormError = "";
        IsGroupFormOpen = true;
        OnPropertyChanged(nameof(GroupFormSaveText));
    }

    [RelayCommand]
    private void PickGroupColor(string color) => GroupFormColor = color;

    /// <summary>"Custom" color box under the swatches: any #RRGGBB.</summary>
    public string GroupFormCustomColor
    {
        get => GroupFormColor;
        set
        {
            var v = (value ?? "").Trim();
            if (!v.StartsWith('#')) v = "#" + v;
            if (LocalData.LocalSettings.IsHexColor(v)) GroupFormColor = v.ToUpperInvariant();
        }
    }

    public string GroupFormSaveText => _editingGroup is null ? "Create group" : "Save group";
    public string GroupPreviewName => GroupFormName.Trim().Length > 0 ? GroupFormName.Trim() : "Group name";

    partial void OnGroupFormColorChanged(string value) => OnPropertyChanged(nameof(GroupFormCustomColor));
    partial void OnGroupFormNameChanged(string value)
    {
        OnPropertyChanged(nameof(GroupPreviewName));
        GroupFormError = "";
    }

    [RelayCommand]
    private void CancelGroupForm() => IsGroupFormOpen = false;

    [RelayCommand]
    private async Task SubmitGroupFormAsync()
    {
        var name = GroupFormName.Trim();
        if (name.Length == 0)
        {
            GroupFormError = "Give the group a name.";
            return;
        }
        if (name.Length > AccountRules.GroupNameMaxLength)
        {
            GroupFormError = $"Use at most {AccountRules.GroupNameMaxLength} characters.";
            return;
        }
        try
        {
            if (_editingGroup is { } g) await _api.UpdateGroupAsync(g.Id, new UpdateGroupRequest(name, GroupFormColor));
            else await _api.CreateGroupAsync(name, GroupFormColor);
            IsGroupFormOpen = false;
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            GroupFormError = ex.Message;
        }
    }

    internal async Task DeleteGroupAsync(GroupDto group)
    {
        try
        {
            await _api.DeleteGroupAsync(group.Id);
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            Status = ex.Message;
        }
    }
}

/// <summary>One group (or "Ungrouped") in the saved-macros list.</summary>
public sealed partial class GroupSection : ObservableObject
{
    private readonly MainViewModel _owner;

    public GroupSection(MainViewModel owner, GroupDto? group, IReadOnlyList<SavedMacroRow> rows, bool collapsed)
    {
        _owner = owner;
        Group = group;
        Rows = rows;
        _isCollapsed = collapsed;
    }

    public GroupDto? Group { get; }
    public IReadOnlyList<SavedMacroRow> Rows { get; }
    public string Name => Group?.Name ?? "Ungrouped";
    public string Color => Group?.Color ?? "#6E7781";
    public bool IsGroup => Group is not null;
    public string CountText => Rows.Count == 1 ? "1 macro" : $"{Rows.Count} macros";
    public bool IsEmpty => Rows.Count == 0;

    [ObservableProperty] private bool _isCollapsed;
    /// <summary>First press of delete turns the button into "Delete group?"; the second deletes.</summary>
    [ObservableProperty] private bool _confirmingDelete;

    partial void OnIsCollapsedChanged(bool value) => _owner.SetCollapsed(this, value);

    [RelayCommand]
    private void Toggle() => IsCollapsed = !IsCollapsed;

    [RelayCommand]
    private void Rename()
    {
        if (Group is { } g) _owner.EditGroup(g);
    }

    [RelayCommand]
    private Task DeleteAsync()
    {
        if (Group is not { } g) return Task.CompletedTask;
        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;
            return Task.CompletedTask;
        }
        ConfirmingDelete = false;
        return _owner.DeleteGroupAsync(g);
    }
}

/// <summary>A choice in a macro's Group dropdown. Id is null for Ungrouped.</summary>
public sealed record GroupChoice(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One macro in the saved list, with its expandable options.</summary>
public sealed partial class SavedMacroRow : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _updating;

    public SavedMacroRow(MainViewModel owner, Guid id)
    {
        _owner = owner;
        Id = id;
    }

    public Guid Id { get; }
    public int Version { get; private set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private Guid? _groupId;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private int _blockCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private DateTimeOffset _updatedAt;
    [ObservableProperty] private bool _hasDraft;
    [ObservableProperty] private bool _isInEditor;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isRunningState;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkey), nameof(HotkeyLabel))]
    private string _hotkey = "";
    [ObservableProperty] private string _triggerMode = TriggerModes.Toggle;
    [ObservableProperty] private bool _runAlongside = true;
    [ObservableProperty] private bool _showOptions;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyLabel))]
    private bool _isCapturing;
    [ObservableProperty] private string _hotkeyError = "";
    [ObservableProperty] private GroupChoice? _selectedGroup;
    /// <summary>First press of delete turns the button into "Delete?"; the second deletes.</summary>
    [ObservableProperty] private bool _confirmingDelete;

    public string Detail => $"{BlockCount} blocks · edited {Edited(UpdatedAt)}";
    public bool HasHotkey => Hotkey.Length > 0;
    /// <summary>The key chip, e.g. "Ctrl + F5".</summary>
    public string HotkeyChip => HasHotkey ? MainViewModel.KeyText(Hotkey) : "No key";
    public string HotkeyLabel => IsCapturing ? "Press a key combo… (Esc cancels)" : HasHotkey ? $"{MainViewModel.KeyText(Hotkey)}  ·  Change" : "None  ·  Change";
    public string StateText => IsRunningState ? "Running" : "Off";
    /// <summary>Group color for the row's left bar and tint (Ungrouped rows have none).</summary>
    public string? GroupColor => _owner.GroupColorOf(GroupId);

    partial void OnHotkeyChanged(string value) => OnPropertyChanged(nameof(HotkeyChip));
    partial void OnGroupIdChanged(Guid? value) => OnPropertyChanged(nameof(GroupColor));
    public IReadOnlyList<GroupChoice> GroupChoices => _owner.GroupChoices;

    /// <summary>The on/off switch: starts or stops this macro.</summary>
    public bool IsRunning
    {
        get => IsRunningState;
        set
        {
            if (value == IsRunningState) return;
            IsRunningState = value;
            OnPropertyChanged();
            if (!_updating) _owner.ToggleMacro(Id);
        }
    }

    internal void SetRunning(bool running)
    {
        _updating = true;
        IsRunning = running;
        _updating = false;
    }

    internal void Update(MacroSummaryDto m)
    {
        _updating = true;
        Version = m.Version;
        Name = m.Name;
        GroupId = m.GroupId;
        BlockCount = m.BlockCount;
        UpdatedAt = m.UpdatedAt;
        HasDraft = m.HasDraft;
        Hotkey = m.HotkeyStart ?? "";
        TriggerMode = m.TriggerMode;
        RunAlongside = m.RunAlongside;
        SelectedGroup = _owner.GroupChoices.FirstOrDefault(c => c.Id == m.GroupId);
        OnPropertyChanged(nameof(GroupColor));
        _updating = false;
    }

    partial void OnTriggerModeChanged(string value)
    {
        if (!_updating) _ = _owner.UpdateRowAsync(this, new UpdateMacroRequest(TriggerMode: value));
    }

    partial void OnRunAlongsideChanged(bool value)
    {
        if (!_updating) _ = _owner.UpdateRowAsync(this, new UpdateMacroRequest(RunAlongside: value));
    }

    partial void OnSelectedGroupChanged(GroupChoice? value)
    {
        if (_updating || value is null || value.Id == GroupId) return;
        _ = _owner.UpdateRowAsync(this, value.Id is { } id ? new UpdateMacroRequest(GroupId: id) : new UpdateMacroRequest(Ungroup: true));
    }

    [RelayCommand]
    private Task OpenAsync() => _owner.OpenRowAsync(this);

    [RelayCommand]
    private Task SaveAsync() => _owner.SaveRowAsync(this);

    [RelayCommand]
    private Task DeleteAsync()
    {
        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;
            return Task.CompletedTask;
        }
        ConfirmingDelete = false;
        return _owner.DeleteRowAsync(this);
    }

    [RelayCommand]
    private void ToggleOptions() => ShowOptions = !ShowOptions;

    partial void OnShowOptionsChanged(bool value)
    {
        if (!value) ConfirmingDelete = false;
    }

    [RelayCommand]
    private void CaptureHotkey()
    {
        HotkeyError = "";
        IsCapturing = true;
        _owner.CapturingRow = this;
    }

    /// <summary>The None button: the macro gets no start / stop key.</summary>
    [RelayCommand]
    private void ClearHotkey()
    {
        if (IsCapturing) { IsCapturing = false; _owner.CapturingRow = null; }
        HotkeyError = "";
        if (HasHotkey) OnCaptured(null, clear: true);
    }

    /// <summary>Called by the view with the key pressed while capturing. Escape cancels, Backspace/Delete clears.</summary>
    public void OnCaptured(Hotkey? hotkey, bool clear)
    {
        IsCapturing = false;
        _owner.CapturingRow = null;
        if (clear)
        {
            _ = _owner.UpdateRowAsync(this, new UpdateMacroRequest(HotkeyStart: "", HotkeyStop: ""));
            return;
        }
        if (hotkey is not { } h) return;
        if (_owner.HotkeyProblem(h, Id) is { } problem)
        {
            HotkeyError = problem;
            return;
        }
        HotkeyError = "";
        _ = _owner.UpdateRowAsync(this, new UpdateMacroRequest(HotkeyStart: h.ToString(), HotkeyStop: h.ToString()));
    }

    private static string Edited(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (now - local < TimeSpan.FromMinutes(1)) return "Just now";
        if (local.Date == now.Date) return $"Today, {local:t}";
        if (local.Date == now.Date.AddDays(-1)) return "Yesterday";
        return local.ToString("MMM d");
    }
}
