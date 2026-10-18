namespace Conversia.Web.Components.Shared;

/// <summary>
/// The icons of the interface: hand-drawn on a 24px grid, painted with a 1.75 stroke and no fill by <c>Icon.razor</c>.
/// To add one, draw it inside a 24x24 box with round caps and joins, and add its inner SVG markup here under a short name.
/// </summary>
public static class IconSet
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["home"] = "<path d=\"M3.5 10.5 12 3.5l8.5 7\"/><path d=\"M5.5 9.5V20h13V9.5\"/><path d=\"M9.5 20v-5.5h5V20\"/>",
        ["compass"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"m15.5 8.5-2 5-5 2 2-5z\"/>",
        ["box"] = "<path d=\"M12 3 3.5 7.5v9L12 21l8.5-4.5v-9z\"/><path d=\"M3.5 7.5 12 12l8.5-4.5\"/><path d=\"M12 12v9\"/>",
        ["users"] = "<circle cx=\"9\" cy=\"8.5\" r=\"3.25\"/><path d=\"M3 19.5c.4-3.3 2.7-5.2 6-5.2s5.6 1.9 6 5.2\"/><circle cx=\"17\" cy=\"9.5\" r=\"2.5\"/><path d=\"M17.5 14.4c2.3.2 3.7 1.7 4 4.1\"/>",
        ["user"] = "<circle cx=\"12\" cy=\"8\" r=\"3.75\"/><path d=\"M4.5 20c.6-3.8 3.5-6 7.5-6s6.9 2.2 7.5 6\"/>",
        ["lock"] = "<rect x=\"4.5\" y=\"10.5\" width=\"15\" height=\"10\" rx=\"2.5\"/><path d=\"M8 10.5V8a4 4 0 0 1 8 0v2.5\"/><path d=\"M12 14.5v2.5\"/>",
        ["mail"] = "<rect x=\"3\" y=\"5.5\" width=\"18\" height=\"13\" rx=\"3\"/><path d=\"m4 8 8 5.5L20 8\"/>",
        ["key"] = "<circle cx=\"8\" cy=\"15\" r=\"4\"/><path d=\"m11 12 8.5-8.5\"/><path d=\"m16 7 2.5 2.5\"/>",
        ["search"] = "<circle cx=\"11\" cy=\"11\" r=\"6.5\"/><path d=\"m16 16 4.5 4.5\"/>",
        ["x"] = "<path d=\"M6 6l12 12M18 6 6 18\"/>",
        ["plus"] = "<path d=\"M12 5v14M5 12h14\"/>",
        ["pencil"] = "<path d=\"M4 20l1.2-4.6L16.6 4a2.1 2.1 0 0 1 3 3L8.2 18.4z\"/><path d=\"m14.5 6 3.5 3.5\"/>",
        ["trash"] = "<path d=\"M4 7h16\"/><path d=\"M9.5 7V4.5h5V7\"/><path d=\"M6 7l1 12.5A1.5 1.5 0 0 0 8.5 21h7a1.5 1.5 0 0 0 1.5-1.5L18 7\"/><path d=\"M10 11v6M14 11v6\"/>",
        ["check"] = "<path d=\"m5 12.5 4.5 4.5L19 7.5\"/>",
        ["check-circle"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"m8 12.5 3 3 5-6\"/>",
        ["x-circle"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"m9 9 6 6M15 9l-6 6\"/>",
        ["alert-triangle"] = "<path d=\"M12 4 2.8 19.5h18.4z\"/><path d=\"M12 10v4.5\"/><path d=\"M12 17.25h.01\"/>",
        ["alert-circle"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7.5v5.5\"/><path d=\"M12 16.5h.01\"/>",
        ["info"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 11v5.5\"/><path d=\"M12 7.75h.01\"/>",
        ["shield-check"] = "<path d=\"M12 3 4.5 6v5.5c0 4.5 3 8 7.5 9.5 4.5-1.5 7.5-5 7.5-9.5V6z\"/><path d=\"m8.75 12 2.5 2.5 4-4.5\"/>",
        ["layers"] = "<path d=\"m12 3.5 9 4.75-9 4.75-9-4.75z\"/><path d=\"m3 12.5 9 4.75 9-4.75\"/><path d=\"m3 16.5 9 4.75 9-4.75\"/>",
        ["sun"] = "<circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 2.5V5M12 19v2.5M2.5 12H5M19 12h2.5M5.3 5.3l1.8 1.8M16.9 16.9l1.8 1.8M18.7 5.3l-1.8 1.8M7.1 16.9l-1.8 1.8\"/>",
        ["moon"] = "<path d=\"M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5z\"/>",
        ["menu"] = "<path d=\"M4 7h16M4 12h16M4 17h16\"/>",
        ["log-out"] = "<path d=\"M9.5 4.5H6a2 2 0 0 0-2 2v11a2 2 0 0 0 2 2h3.5\"/><path d=\"m14 8 4 4-4 4\"/><path d=\"M18 12H9.5\"/>",
        ["log-in"] = "<path d=\"M14.5 4.5H18a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2h-3.5\"/><path d=\"m10 8 4 4-4 4\"/><path d=\"M14 12H4.5\"/>",
        ["arrow-right"] = "<path d=\"M5 12h14M13 6l6 6-6 6\"/>",
        ["arrow-left"] = "<path d=\"M19 12H5M11 6l-6 6 6 6\"/>",
        ["chevron-left"] = "<path d=\"m14.5 6-6 6 6 6\"/>",
        ["chevron-right"] = "<path d=\"m9.5 6 6 6-6 6\"/>",
        ["building"] = "<rect x=\"4.5\" y=\"3.5\" width=\"10\" height=\"17\" rx=\"1.5\"/><path d=\"M14.5 9.5H19a1.5 1.5 0 0 1 1.5 1.5v9.5h-6\"/><path d=\"M8 8h3M8 12h3M8 16h3\"/>",
        ["sparkles"] = "<path d=\"m10 4 1.6 4.4L16 10l-4.4 1.6L10 16l-1.6-4.4L4 10l4.4-1.6z\"/><path d=\"m18 14.5.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8z\"/>",
        ["zap"] = "<path d=\"M13 3 5 13.5h6L10.5 21 19 10h-6z\"/>",
        ["database"] = "<ellipse cx=\"12\" cy=\"6\" rx=\"7.5\" ry=\"3\"/><path d=\"M4.5 6v6c0 1.7 3.4 3 7.5 3s7.5-1.3 7.5-3V6\"/><path d=\"M4.5 12v6c0 1.7 3.4 3 7.5 3s7.5-1.3 7.5-3v-6\"/>",
        ["globe"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M3 12h18\"/><path d=\"M12 3c2.5 2.5 3.8 5.5 3.8 9s-1.3 6.5-3.8 9c-2.5-2.5-3.8-5.5-3.8-9S9.5 5.5 12 3z\"/>",
        ["refresh"] = "<path d=\"M20 11a8 8 0 0 0-14.5-4.5L4 8\"/><path d=\"M4 4v4h4\"/><path d=\"M4 13a8 8 0 0 0 14.5 4.5L20 16\"/><path d=\"M20 20v-4h-4\"/>",
        ["activity"] = "<path d=\"M3 12h4l2.5-6 5 12L17 12h4\"/>",
        ["tag"] = "<path d=\"M3.5 12.2V4.5a1 1 0 0 1 1-1h7.7a1 1 0 0 1 .7.3l7.3 7.3a1 1 0 0 1 0 1.4l-7.7 7.7a1 1 0 0 1-1.4 0l-7.3-7.3a1 1 0 0 1-.3-.7z\"/><path d=\"M8 8h.01\"/>",
    };

    public static bool Has(string name) => Paths.ContainsKey(name);

    public static string Markup(string name)
        => Paths.TryGetValue(name, out var markup) ? markup : Paths["info"];
}
