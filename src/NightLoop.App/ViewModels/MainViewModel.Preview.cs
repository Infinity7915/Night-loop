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
        var coin = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var farming = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        var fishing = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

        // The mockup's example: a coin farm that lines the window up, then loops.
        var doc = NewDoc();
        doc.Name = "Coin farm";
        var blocks = new (string Type, JsonObject? Params)[]
        {
            (BlockTypes.WindowFocus, null),
            (BlockTypes.WindowResize, null),
            (BlockTypes.WindowPosition, null),
            (BlockTypes.Description, new()
            {
                ["text"] = "Coin farm. Start in the spawn area, facing the shop.\nResize and Position run first so every click and camera turn lands in the same place each run.\nIf it stops early, check the Scan & turn target text.",
            }),
            (BlockTypes.Label, new() { ["name"] = "farm" }),
            (BlockTypes.Loop, new() { ["count"] = 100 }),
            (BlockTypes.ScanTurn, new()
            {
                ["target"] = "text", ["value"] = "Collect", ["direction"] = "right", ["stepPx"] = 120, ["maxTurns"] = 12,
                ["whenFound"] = "click", ["onFail"] = "goto", ["failLabel"] = "farm", ["note"] = "spin until the pickup prompt is on screen",
            }),
            (BlockTypes.WaitRandom, new() { ["minMs"] = 400, ["maxMs"] = 900 }),
            (BlockTypes.HoldKey, new() { ["key"] = "W", ["holdMs"] = 1200 }),
            (BlockTypes.WaitForText, new() { ["text"] = "+1 Coin", ["match"] = "contains", ["timeoutMs"] = 1500, ["onFail"] = "continue" }),
            (BlockTypes.If, new() { ["condition"] = Conditions.Write("!textVisible", "+1 Coin", null) }),
            (BlockTypes.Key, new() { ["key"] = "Space", ["action"] = "press", ["note"] = "jump in case we are stuck" }),
            (BlockTypes.End, null),
            (BlockTypes.End, null),
            (BlockTypes.Notify, new() { ["kind"] = "both", ["message"] = "Farm done" }),
        };
        foreach (var (type, p) in blocks)
            doc.Blocks.Add(Block.Create(NextId(), type, p ?? BlockCatalog.For(type)?.Defaults()));

        _macroId = coin;
        ApplyDoc(doc);
        SelectedBlock = Blocks.Count > 2 ? Blocks[2] : null;
        ToggleCategory(Categories.First(c => c.Name == "Timing"));
        SyncText = "Draft · autosaved";

        var now = DateTimeOffset.UtcNow;
        MacroSummaryDto Row(string id, string name, Guid? groupId, int count, bool draft, string? hotkey, bool alongside = true) =>
            new(Guid.Parse(id), name, groupId, null, draft, !draft, count, 1, alongside, hotkey, hotkey, TriggerModes.Toggle, now,
                draft ? now : null, now);
        ApplyList(new MacroListResponse(
            [
                Row("00000000-0000-0000-0000-000000000001", "Coin farm", farming, doc.Blocks.Count, true, "Ctrl+F5"),
                Row("00000000-0000-0000-0000-000000000002", "Reconnect watcher", null, 14, false, "F8"),
                Row("00000000-0000-0000-0000-000000000003", "Fishing loop", fishing, 9, false, "F6"),
                Row("00000000-0000-0000-0000-000000000004", "Daily rewards", null, 6, false, "F7", alongside: false),
                Row("00000000-0000-0000-0000-000000000005", "Chest route - east island, long version with every stop", farming, 22, true, "F6"),
            ],
            [new GroupDto(farming, "Roblox · coin farming", "#A78BFA", 0), new GroupDto(fishing, "Fishing", "#34D399", 1)],
            MacroLimits.MacrosPerUser));
    }
}
