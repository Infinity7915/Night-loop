namespace NightLoop.Contracts.MacroDocument;

/// <summary>
/// Pairs logic.if / logic.else / logic.loop / logic.repeatUntil with their logic.end and collects labels.
/// The validator and the engine's compiler both use this, so they always agree on what a macro means.
/// </summary>
public sealed class MacroStructure
{
    /// <summary>For an opener: index of its end. For an end: index of its opener. For an else: index of its end. Otherwise -1.</summary>
    public int[] Partner { get; }

    /// <summary>For a logic.if: index of its logic.else, or -1. Otherwise -1.</summary>
    public int[] ElseOf { get; }

    public IReadOnlyDictionary<string, int> Labels { get; }

    public IReadOnlyList<MacroIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;

    private MacroStructure(int[] partner, int[] elseOf, Dictionary<string, int> labels, List<MacroIssue> issues)
    {
        Partner = partner;
        ElseOf = elseOf;
        Labels = labels;
        Issues = issues;
    }

    public static MacroStructure Analyze(IReadOnlyList<Block> blocks)
    {
        var partner = Enumerable.Repeat(-1, blocks.Count).ToArray();
        var elseOf = Enumerable.Repeat(-1, blocks.Count).ToArray();
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        var issues = new List<MacroIssue>();
        var stack = new Stack<int>();

        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            switch (b.Type)
            {
                case BlockTypes.If or BlockTypes.Loop or BlockTypes.RepeatUntil:
                    stack.Push(i);
                    if (stack.Count > MacroLimits.MaxNestingDepth)
                        issues.Add(new(b.Id, $"Nested more than {MacroLimits.MaxNestingDepth} levels deep."));
                    break;

                case BlockTypes.Else:
                    if (stack.Count == 0 || blocks[stack.Peek()].Type != BlockTypes.If)
                        issues.Add(new(b.Id, "Else must be inside an If."));
                    else if (elseOf[stack.Peek()] != -1)
                        issues.Add(new(b.Id, "This If already has an Else."));
                    else
                        elseOf[stack.Peek()] = i;
                    break;

                case BlockTypes.End:
                    if (stack.Count == 0)
                    {
                        issues.Add(new(b.Id, "End has no matching If, Loop or Repeat until."));
                        break;
                    }
                    var open = stack.Pop();
                    partner[open] = i;
                    partner[i] = open;
                    if (elseOf[open] != -1) partner[elseOf[open]] = i;
                    break;

                case BlockTypes.Label:
                    var name = b.GetString("name");
                    if (string.IsNullOrWhiteSpace(name))
                        issues.Add(new(b.Id, "Label needs a name."));
                    else if (!labels.TryAdd(name, i))
                        issues.Add(new(b.Id, $"Label \"{name}\" is used twice."));
                    break;
            }
        }

        foreach (var open in stack)
            issues.Add(new(blocks[open].Id, "This block is never closed with End."));

        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Type != BlockTypes.Goto) continue;
            var target = blocks[i].GetString("name");
            if (string.IsNullOrWhiteSpace(target) || !labels.ContainsKey(target))
                issues.Add(new(blocks[i].Id, $"Go to points at a label that doesn't exist: \"{target}\"."));
        }

        return new MacroStructure(partner, elseOf, labels, issues);
    }
}

/// <summary>A problem with one block (BlockId set) or the whole document (BlockId null).</summary>
public sealed record MacroIssue(string? BlockId, string Message)
{
    public override string ToString() => BlockId is null ? Message : $"{BlockId}: {Message}";
}
