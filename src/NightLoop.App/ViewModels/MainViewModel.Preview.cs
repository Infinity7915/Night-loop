using System.Text.Json.Nodes;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Screen;

namespace NightLoop.App.ViewModels;

/// <summary>
/// Sample content for the UI smoke test (<c>--smoke</c>), so the CI screenshots show a filled editor and saved list
/// instead of an empty screen. Nothing here talks to the server or is used in normal runs.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary><paramref name="select"/>: which block the inspector shows ("position", "image", "open", "close" or "scan").</summary>
    internal void ShowSample(string select = "position")
    {
        var coin = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var farming = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        var fishing = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

        // The mockup's example: a coin farm that lines the window up, then loops.
        var doc = NewDoc();
        doc.Name = "Coin farm";
        var blocks = new (string Type, JsonObject? Params)[]
        {
            (BlockTypes.WindowOpen, new()
            {
                ["path"] = @"%LOCALAPPDATA%\Roblox\Versions\RobloxPlayerBeta.exe", ["ifRunning"] = "focus", ["wait"] = true, ["timeoutMs"] = 60_000,
            }),
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
            (BlockTypes.Key, new() { ["key"] = "LeftShift", ["action"] = "down", ["note"] = "sprint" }),
            (BlockTypes.HoldKey, new() { ["key"] = "W", ["holdMs"] = 1200 }),
            (BlockTypes.Wait, new() { ["ms"] = 180 }),
            (BlockTypes.Key, new() { ["key"] = "LeftShift", ["action"] = "up" }),
            (BlockTypes.WaitForImage, new()
            {
                ["image"] = "coin", ["imageData"] = SampleCoin(), ["confidencePct"] = 85, ["timeoutMs"] = 3000, ["onFail"] = "continue",
            }),
            (BlockTypes.WaitForText, new() { ["text"] = "+1 Coin", ["match"] = "contains", ["timeoutMs"] = 1500, ["onFail"] = "continue" }),
            (BlockTypes.If, new() { ["condition"] = Conditions.Write("!textVisible", "+1 Coin", null) }),
            (BlockTypes.Key, new() { ["key"] = "Space", ["action"] = "press", ["note"] = "jump in case we are stuck" }),
            (BlockTypes.End, null),
            (BlockTypes.End, null),
            (BlockTypes.Notify, new() { ["kind"] = "both", ["message"] = "Farm done" }),
            (BlockTypes.WindowClose, new() { ["process"] = "RobloxPlayerBeta.exe", ["how"] = "closeThenEnd", ["timeoutMs"] = 10_000 }),
        };
        foreach (var (type, p) in blocks)
            doc.Blocks.Add(Block.Create(NextId(), type, p ?? BlockCatalog.For(type)?.Defaults()));

        _macroId = coin;
        ApplyDoc(doc);
        var pick = select switch
        {
            "image" => BlockTypes.WaitForImage,
            "open" => BlockTypes.WindowOpen,
            "close" => BlockTypes.WindowClose,
            "scan" => BlockTypes.ScanTurn,
            _ => BlockTypes.WindowPosition,
        };
        SelectedBlock = Blocks.FirstOrDefault(r => r.Block.Type == pick);
        // The default picture shows a few ticked blocks, as after a tick and a Shift+click.
        if (select == "position" && Blocks.Count > 4)
        {
            CheckRow(Blocks[2], shift: false);
            CheckRow(Blocks[4], shift: true);
        }
        ShowEditorOptions = select == "image";
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

    /// <summary>A small round coin picture for the sample's Wait for image block.</summary>
    private static string SampleCoin()
    {
        const int size = 40;
        var px = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var d = Math.Sqrt((x - 19.5) * (x - 19.5) + (y - 19.5) * (y - 19.5));
            var v = d < 14 ? 230 - (int)(d * 4) : d < 18 ? 120 : 30;
            if (d < 9 && Math.Abs(x - 19.5) < 2) v = 90; // the coin's mark
            var i = (y * size + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = (byte)v;
            px[i + 3] = 255;
        }
        return ImageTemplate.FromImage(new ScreenImage(0, 0, size, size, px)).Encode();
    }
}
