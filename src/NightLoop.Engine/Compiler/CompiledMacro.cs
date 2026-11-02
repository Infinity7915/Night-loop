using System.Collections.Immutable;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Engine.Compiler;

/// <summary>What a step does when the runner reaches it.</summary>
public enum StepKind
{
    /// <summary>Disabled block, comment, label, detect block, or an End/Else whose partner makes it inert.</summary>
    NoOp,
    /// <summary>Any block that does work (input, wait, window, utility).</summary>
    Action,
    /// <summary>logic.if: when the condition is false, jump to <see cref="Step.Jump"/>.</summary>
    If,
    /// <summary>logic.else reached from the then-branch: jump past the else-branch.</summary>
    Jump,
    /// <summary>logic.goto.</summary>
    Goto,
    LoopStart,
    LoopEnd,
    RepeatStart,
    RepeatEnd,
    Stop,
}

/// <summary>One compiled step. Index matches the block's position so the timeline can highlight it.</summary>
public sealed record Step(int Index, Block Block, StepKind Kind, int Jump);

/// <summary>An immutable, ready-to-run macro: a flat list of steps plus jump targets.</summary>
public sealed class CompiledMacro
{
    public required string Name { get; init; }
    public string? TargetGame { get; init; }
    public required MacroOptions Options { get; init; }
    public required ImmutableArray<Step> Steps { get; init; }
}

public sealed class MacroCompileException(IReadOnlyList<MacroIssue> issues)
    : Exception("The macro has problems: " + string.Join("; ", issues))
{
    public IReadOnlyList<MacroIssue> Issues { get; } = issues;
}
