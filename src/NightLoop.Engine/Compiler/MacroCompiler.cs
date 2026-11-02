using System.Collections.Immutable;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Engine.Compiler;

/// <summary>
/// Turns a macro document into a <see cref="CompiledMacro"/> (schema section 7, "Compile step").
/// Disabled blocks compile to no-ops so indexes stay stable. A disabled If behaves as if its condition
/// were true; a Loop or Repeat until with a disabled opener or End runs its body once.
/// </summary>
public static class MacroCompiler
{
    public static CompiledMacro Compile(MacroDoc doc)
    {
        var issues = MacroValidator.Validate(doc);
        if (issues.Count > 0) throw new MacroCompileException(issues);

        var blocks = doc.Blocks;
        var s = MacroStructure.Analyze(blocks);
        var steps = ImmutableArray.CreateBuilder<Step>(blocks.Count);

        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (!b.Enabled)
            {
                steps.Add(new Step(i, b, StepKind.NoOp, -1));
                continue;
            }

            steps.Add(b.Type switch
            {
                BlockTypes.If => new Step(i, b, StepKind.If, (s.ElseOf[i] != -1 ? s.ElseOf[i] : s.Partner[i]) + 1),
                BlockTypes.Else => new Step(i, b, StepKind.Jump, s.Partner[i] + 1),
                BlockTypes.Loop when blocks[s.Partner[i]].Enabled => new Step(i, b, StepKind.LoopStart, s.Partner[i]),
                BlockTypes.RepeatUntil when blocks[s.Partner[i]].Enabled => new Step(i, b, StepKind.RepeatStart, s.Partner[i]),
                BlockTypes.End => CompileEnd(i, b, blocks[s.Partner[i]], s.Partner[i]),
                BlockTypes.Goto => new Step(i, b, StepKind.Goto, s.Labels[b.GetString("name")!]),
                BlockTypes.Stop => new Step(i, b, StepKind.Stop, -1),
                BlockTypes.Loop or BlockTypes.RepeatUntil or BlockTypes.Label or BlockTypes.Comment or BlockTypes.Description
                    or BlockTypes.DetectText or BlockTypes.DetectImage or BlockTypes.DetectPixelColor => new Step(i, b, StepKind.NoOp, -1),
                _ => new Step(i, b, StepKind.Action, -1),
            });
        }

        return new CompiledMacro
        {
            Name = doc.Name,
            TargetGame = doc.TargetGame,
            Options = doc.Options,
            Steps = steps.MoveToImmutable(),
        };
    }

    private static Step CompileEnd(int i, Block end, Block opener, int openerIndex)
    {
        if (!opener.Enabled) return new Step(i, end, StepKind.NoOp, -1);
        return opener.Type switch
        {
            BlockTypes.Loop => new Step(i, end, StepKind.LoopEnd, openerIndex),
            BlockTypes.RepeatUntil => new Step(i, end, StepKind.RepeatEnd, openerIndex),
            _ => new Step(i, end, StepKind.NoOp, -1),
        };
    }
}
