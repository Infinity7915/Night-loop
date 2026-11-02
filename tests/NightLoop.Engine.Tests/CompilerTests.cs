using NightLoop.Engine.Compiler;

namespace NightLoop.Engine.Tests;

public class CompilerTests
{
    [Fact]
    public void If_else_end_jumps_are_resolved()
    {
        var m = MacroCompiler.Compile(new MacroBuilder()
            .If(MacroBuilder.Focused()) // 0
            .Press("A")                 // 1
            .Else()                     // 2
            .Press("B")                 // 3
            .End()                      // 4
            .Build());

        Assert.Equal(StepKind.If, m.Steps[0].Kind);
        Assert.Equal(3, m.Steps[0].Jump);   // false → first block of else-branch
        Assert.Equal(StepKind.Jump, m.Steps[2].Kind);
        Assert.Equal(5, m.Steps[2].Jump);   // end of then-branch → past End
        Assert.Equal(StepKind.NoOp, m.Steps[4].Kind);
    }

    [Fact]
    public void If_without_else_jumps_past_end()
    {
        var m = MacroCompiler.Compile(new MacroBuilder().If(MacroBuilder.Focused()).Press("A").End().Build());
        Assert.Equal(3, m.Steps[0].Jump);
    }

    [Fact]
    public void Loops_pair_with_their_end_and_labels_resolve()
    {
        var m = MacroCompiler.Compile(new MacroBuilder()
            .Label("top")   // 0
            .Loop(3)        // 1
            .Wait(10)       // 2
            .End()          // 3
            .Goto("top")    // 4
            .Build());

        Assert.Equal(StepKind.LoopStart, m.Steps[1].Kind);
        Assert.Equal(3, m.Steps[1].Jump);
        Assert.Equal(StepKind.LoopEnd, m.Steps[3].Kind);
        Assert.Equal(1, m.Steps[3].Jump);
        Assert.Equal(StepKind.Goto, m.Steps[4].Kind);
        Assert.Equal(0, m.Steps[4].Jump);
    }

    [Fact]
    public void Disabled_blocks_become_no_ops_and_keep_indexes()
    {
        var m = MacroCompiler.Compile(new MacroBuilder()
            .Add(Contracts.MacroDocument.BlockTypes.Loop, new() { ["count"] = 5 }, enabled: false)
            .Press("A")
            .End()
            .Build());

        Assert.Equal(3, m.Steps.Length);
        Assert.All(new[] { m.Steps[0], m.Steps[2] }, s => Assert.Equal(StepKind.NoOp, s.Kind));
        Assert.Equal(StepKind.Action, m.Steps[1].Kind);
    }

    [Fact]
    public void Invalid_macro_throws_with_issues()
    {
        var ex = Assert.Throws<MacroCompileException>(() => MacroCompiler.Compile(new MacroBuilder().End().Build()));
        Assert.NotEmpty(ex.Issues);
    }
}
