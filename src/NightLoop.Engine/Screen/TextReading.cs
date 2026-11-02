namespace NightLoop.Engine.Screen;

/// <summary>A rectangle in screen pixels.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public (int X, int Y) Center => (Left + Width / 2, Top + Height / 2);

    public static ScreenRect Union(IEnumerable<ScreenRect> rects)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var x in rects)
        {
            l = Math.Min(l, x.Left);
            t = Math.Min(t, x.Top);
            r = Math.Max(r, x.Right);
            b = Math.Max(b, x.Bottom);
        }
        return l == int.MaxValue ? default : new ScreenRect(l, t, r - l, b - t);
    }
}

/// <summary>One word the text reader found, in screen pixels.</summary>
public sealed record TextWord(string Text, ScreenRect Bounds);

/// <summary>One line of text, made of words in reading order.</summary>
public sealed record TextLine(IReadOnlyList<TextWord> Words)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

/// <summary>Reads text from a capture. The app's version uses Windows' own text recognition (Windows.Media.Ocr).</summary>
public interface ITextReader
{
    Task<IReadOnlyList<TextLine>> ReadAsync(ScreenImage image, CancellationToken cancellationToken);
}

/// <summary>Finds a phrase among the lines a text reader returned.</summary>
public static class TextSearch
{
    /// <summary>
    /// The screen area of <paramref name="phrase"/>. Exact means the phrase's words appear as whole words, in order;
    /// contains also matches inside longer words ("Coin" in "+1 Coins").
    /// </summary>
    public static ScreenRect? Find(IReadOnlyList<TextLine> lines, string phrase, bool contains, bool matchCase)
    {
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var wanted = Split(phrase);
        if (wanted.Length == 0) return null;

        foreach (var line in lines)
        {
            var words = line.Words;
            for (var i = 0; i < words.Count; i++)
            {
                if (wanted.Length == 1)
                {
                    var w = Clean(words[i].Text);
                    if (contains ? w.Contains(wanted[0], cmp) : string.Equals(w, wanted[0], cmp))
                        return words[i].Bounds;
                    continue;
                }
                if (i + wanted.Length > words.Count) break;
                var ok = true;
                for (var k = 0; k < wanted.Length && ok; k++)
                {
                    var w = Clean(words[i + k].Text);
                    // With "contains", only the first word may have extra text before it and the last after it.
                    ok = contains && k == 0 ? w.EndsWith(wanted[k], cmp)
                        : contains && k == wanted.Length - 1 ? w.StartsWith(wanted[k], cmp)
                        : string.Equals(w, wanted[k], cmp);
                }
                if (ok) return ScreenRect.Union(words.Skip(i).Take(wanted.Length).Select(x => x.Bounds));
            }
        }

        // Text readers sometimes split or join words differently ("+1Coin"): fall back to the line text without spaces.
        var squashed = string.Concat(wanted);
        foreach (var line in lines)
        {
            var joined = string.Concat(line.Words.Select(w => Clean(w.Text)));
            if (joined.Contains(squashed, cmp) && (contains || joined.Length == squashed.Length))
                return ScreenRect.Union(line.Words.Select(w => w.Bounds));
        }
        return null;
    }

    private static string[] Split(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Clean).Where(w => w.Length > 0).ToArray();

    /// <summary>Drops punctuation readers often add or lose at word edges, like "Claim!" or "(Shop)".</summary>
    private static string Clean(string word) => word.Trim('.', ',', '!', '?', ':', ';', '"', '\'', '(', ')', '[', ']');
}
