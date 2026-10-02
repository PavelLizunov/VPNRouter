namespace VPNRouter.Tools.UiProbe;

public sealed class RenderResult
{
    public string Label { get; init; } = string.Empty;
    public byte[] Png { get; init; } = Array.Empty<byte>();
    public List<Finding> Findings { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}

public sealed class MatrixResult
{
    public byte[] Sheet { get; init; } = Array.Empty<byte>();
    public List<RenderResult> Cells { get; init; } = new();
}

// The operations behind the MCP tools. All of them must run on the Avalonia UI thread.
public static class Probe
{
    public const int MaxMatrixCells = 12;

    public static RenderResult Render(ProbeOptions options)
    {
        using var session = ProbeSession.Open(options);
        var png = session.CapturePng();
        return new RenderResult
        {
            Label = options.Label,
            Png = png,
            Findings = LayoutLint.Run(session.Window),
            Notes = session.Notes,
        };
    }

    public static string Tree(ProbeOptions options, int maxLines)
    {
        using var session = ProbeSession.Open(options);
        session.Layout();
        return UiTree.Dump(session.Window, maxLines);
    }

    public static MatrixResult Matrix(
        ProbeOptions template,
        IReadOnlyList<string> surfaces,
        IReadOnlyList<string> scenarios,
        IReadOnlyList<string> themes,
        IReadOnlyList<string> languages,
        IReadOnlyList<int> widths,
        int columns)
    {
        var cells = new List<RenderResult>();
        foreach (var surface in surfaces)
        foreach (var scenario in scenarios)
        foreach (var theme in themes)
        foreach (var language in languages)
        foreach (var width in widths)
        {
            if (cells.Count >= MaxMatrixCells)
                throw new ArgumentException($"A matrix is limited to {MaxMatrixCells} cells; split the request.");
            var o = template.Clone();
            o.Surface = surface;
            o.Scenario = scenario;
            o.Theme = theme;
            o.Language = language;
            o.Width = width;
            cells.Add(Render(o));
        }
        var sheet = ContactSheet.Build(cells.Select(c => (c.Label, c.Png)).ToList(), columns);
        return new MatrixResult { Sheet = sheet, Cells = cells };
    }
}
