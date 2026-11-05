using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Contracts.Tests;

/// <summary>A changed copy of the open-source app must not be able to store anything but a real macro.</summary>
public class MacroSanitizerTests
{
    private static MacroDoc Doc(params Block[] blocks) => new() { Name = "Test", Blocks = [.. blocks] };

    [Fact]
    public void Unknown_block_and_condition_settings_are_dropped()
    {
        var wait = Block.Create("b1", BlockTypes.Wait, new() { ["ms"] = 100, ["stash"] = new string('x', 9000) });
        var iff = Block.Create("b2", BlockTypes.If, new()
        {
            ["condition"] = new JsonObject
            {
                ["kind"] = "all",
                ["conditions"] = new JsonArray(new JsonObject { ["kind"] = "keyHeld", ["key"] = "F1", ["hidden"] = "data" }),
                ["extra"] = 1,
            },
        });
        var end = Block.Create("b3", BlockTypes.End);
        var doc = Doc(wait, iff, end);

        Assert.Equal(3, MacroSanitizer.Clean(doc));
        Assert.False(wait.Params.ContainsKey("stash"));
        Assert.Equal(100, wait.GetInt("ms"));
        var cond = iff.GetObject("condition")!;
        Assert.False(cond.ContainsKey("extra"));
        Assert.False(((JsonObject)((JsonArray)cond["conditions"]!)[0]!).ContainsKey("hidden"));
        Assert.Empty(MacroValidator.Validate(doc));
    }

    [Theory]
    [InlineData("not a picture")]
    [InlineData("nl2:@@@@")]
    public void Image_data_must_be_a_real_saved_picture(string data)
    {
        var doc = Doc(Block.Create("b1", BlockTypes.WaitForImage, new() { ["imageData"] = data }));
        Assert.Contains(MacroValidator.Validate(doc), i => i.Message.Contains("picture"));
        var ok = Doc(Block.Create("b1", BlockTypes.WaitForImage, new() { ["imageData"] = "nl2:" + Convert.ToBase64String(new byte[30]) }));
        Assert.Empty(MacroValidator.Validate(ok));
    }

    [Fact]
    public void Every_setting_is_bounded_on_every_block()
    {
        Assert.Contains(MacroValidator.Validate(Doc(Block.Create("b1", BlockTypes.Wait, new() { ["ms"] = 5, ["note"] = new string('n', 201) }))),
            i => i.Message.Contains("Note"));
        // "path" is only checked on Open window; on any other block it still can't hold a pile of text.
        Assert.Contains(MacroValidator.Validate(Doc(Block.Create("b1", BlockTypes.Wait, new() { ["ms"] = 5, ["path"] = new string('p', 5000) }))),
            i => i.Message.Contains("too long"));
        Assert.Contains(MacroValidator.Validate(Doc(Block.Create(new string('i', 65), BlockTypes.Wait, new() { ["ms"] = 5 }))),
            i => i.Message.Contains("64"));
        Assert.Contains(MacroValidator.Validate(new MacroDoc { Name = "T", TargetGame = new string('g', 300) }), i => i.Message.Contains("program name"));
    }

    [Fact]
    public void Every_setting_the_app_reads_is_kept()
    {
        // Settings the app and engine read by name. If one is missing from the list, the server would drop it.
        var src = Path.Combine(RepoRoot(), "src");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var patterns = new[]
        {
            new Regex(@"\b(?:b|block|Block)\.Get(?:String|Int|Bool|Double|Point|Object)\(""([a-zA-Z]+)"""),
            new Regex(@"\b(?:b|block|Block)\.Params(?:\.ContainsKey\(|\[)""([a-zA-Z]+)"""),
            new Regex(@"\bF\.[A-Za-z]+\(""([a-zA-Z]+)"""),
        };
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            foreach (var re in patterns)
                foreach (Match m in re.Matches(text))
                    keys.Add(m.Groups[1].Value);
        }
        Assert.NotEmpty(keys);
        Assert.Equal("", string.Join(", ", keys.Where(k => !MacroSanitizer.BlockKeys.Contains(k)).Order()));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NightLoop.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Can't find NightLoop.sln.");
    }
}
