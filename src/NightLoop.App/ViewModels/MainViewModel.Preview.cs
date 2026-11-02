using System.Text.Json.Nodes;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.App.ViewModels;

/// <summary>
/// Sample content for the UI smoke test (<c>--smoke</c>), so the CI screenshots show a filled editor and saved list
/// instead of an empty screen. Nothing here talks to the server or is used in normal runs.
/// </summary>
public sealed partial class MainViewModel
{
    internal void ShowSample()
    {
        var farm = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var daily = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var group = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

        var doc = NewDoc();
        doc.Name = "Auto-farm loop";
        var blocks = new (string Type, JsonObject? Params)[]
        {
            (BlockTypes.Comment, new() { ["text"] = "Get the game ready" }),
            (BlockTypes.WindowFocus, null),
            (BlockTypes.Loop, new() { ["count"] = 25 }),
            (BlockTypes.Key, new() { ["key"] = "E", ["action"] = "press", ["holdMs"] = 30, ["note"] = "interact with the node" }),
            (BlockTypes.WaitRandom, new() { ["minMs"] = 300, ["maxMs"] = 800 }),
            (BlockTypes.Click, new() { ["button"] = "left", ["clicks"] = 1, ["x"] = 640, ["y"] = 360 }),
            (BlockTypes.End, null),
            (BlockTypes.Notify, new() { ["message"] = "Farm run finished" }),
        };
        foreach (var (type, p) in blocks)
            doc.Blocks.Add(Block.Create(NextId(), type, p ?? BlockCatalog.For(type)?.Defaults()));

        _macroId = farm;
        ApplyDoc(doc);
        SelectedBlock = Blocks.Count > 3 ? Blocks[3] : null;
        ToggleCategory(Categories[0]);
        SyncText = "Draft · autosaved";

        var now = DateTimeOffset.UtcNow;
        MacroSummaryDto Row(Guid id, string name, Guid? groupId, int blocks, bool draft, string? hotkey) =>
            new(id, name, groupId, null, draft, !draft, blocks, 1, true, hotkey, hotkey, TriggerModes.Toggle, now, draft ? now : null, now);
        ApplyList(new MacroListResponse(
            [
                Row(farm, "Auto-farm loop", group, doc.Blocks.Count, true, "Ctrl+F5"),
                Row(daily, "Daily quests", group, 14, false, "F7"),
                Row(Guid.Parse("00000000-0000-0000-0000-000000000003"), "Inventory sort", null, 6, false, null),
            ],
            [new GroupDto(group, "Farming", "#34D399", 0)],
            MacroLimits.MacrosPerUser));
        Status = "Ready. F9 records, F6 plays, Ctrl+Alt+End stops everything.";
    }
}
