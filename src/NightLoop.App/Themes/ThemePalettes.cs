namespace NightLoop.App.Themes;

/// <summary>
/// A color theme on top of the dark or light palette: it swaps the backgrounds, borders and text, and comes with an
/// accent that suits it. Night is the original purple and follows the Dark / Light / System mode; the others pick
/// their own.
/// </summary>
public sealed record ThemePalette(string Id, string Name, bool Light, string Accent, IReadOnlyDictionary<string, string> Colors)
{
    /// <summary>The page color, for the little preview on the theme button.</summary>
    public string Preview => Colors.TryGetValue("Bg", out var bg) ? bg : Light ? "#F5F3FA" : "#110D1A";
}

public static class ThemePalettes
{
    public const string NightId = "night";

    private static ThemePalette Make(string id, string name, bool light, string accent, string bg, string sidebar, string panel,
        string raised, string border, string borderStrong, string text, string soft, string muted, string faint,
        string current, string inset, string structural, string overlay) =>
        new(id, name, light, accent, new Dictionary<string, string>
        {
            ["Bg"] = bg, ["Sidebar"] = sidebar, ["Panel"] = panel, ["PanelRaised"] = raised,
            ["Border"] = border, ["BorderStrong"] = borderStrong,
            ["Text"] = text, ["TextSoft"] = soft, ["TextMuted"] = muted, ["TextFaint"] = faint,
            ["Current"] = current, ["Inset"] = inset, ["Structural"] = structural, ["Overlay"] = overlay,
        });

    public static readonly IReadOnlyList<ThemePalette> All =
    [
        new(NightId, "Night", false, "#A78BFA", new Dictionary<string, string>()),
        Make("rose", "Rose", false, "#F472B6",
            "#1A0E15", "#1F111A", "#24141E", "#301B28", "#44283B", "#643A55",
            "#F7EAF1", "#E4CCDA", "#B693A8", "#86677A", "#3B1F31", "#1F111A", "#2A1825", "#B3120710"),
        Make("bloodmoon", "Blood moon", false, "#E5484D",
            "#140A0A", "#190C0C", "#1F1010", "#2B1616", "#422020", "#66302E",
            "#F5E9E7", "#E2CAC6", "#B08E89", "#806360", "#3A1A18", "#190C0C", "#261414", "#B30F0505"),
        Make("ocean", "Ocean", false, "#38BDF8",
            "#0A121A", "#0C1620", "#101C28", "#172736", "#22384C", "#33536E",
            "#E8F1F8", "#C7D8E6", "#8AA3B8", "#61798D", "#173247", "#0C1620", "#132231", "#B305101A"),
        Make("cyber", "Cyber", false, "#E4FF3A",
            "#0A0A12", "#0D0D18", "#11111F", "#1A1A2E", "#2A2A48", "#3F3F6B",
            "#EEF0FF", "#CFD3F2", "#8F94BF", "#646892", "#1E2140", "#0D0D18", "#15152A", "#B305050C"),
        Make("lunar", "Lunar", false, "#C7D2FE",
            "#111317", "#14171C", "#191C22", "#22262E", "#31363F", "#4A515D",
            "#EDEFF3", "#D0D4DC", "#969CA8", "#6B717C", "#262C38", "#14171C", "#1C2028", "#B308090C"),
        Make("vanilla", "Vanilla", true, "#C8892B",
            "#FAF2DC", "#F2E7C6", "#FFF8E4", "#F4EACB", "#E4D5AC", "#CBB781",
            "#2B2414", "#4A4128", "#75694A", "#9A8D6B", "#EFDFAE", "#FCF5E2", "#F4EACD", "#802B2414"),
    ];

    public static ThemePalette Default => All[0];

    public static ThemePalette For(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Default;
}
