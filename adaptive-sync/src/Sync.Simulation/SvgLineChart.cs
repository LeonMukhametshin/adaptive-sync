using System.Globalization;
using System.Net;
using System.Text;

namespace Sync.Simulation;

public sealed record ChartSeries(string Name, IReadOnlyList<(double X, Estimate Y)> Points);

public static class SvgLineChart
{
    private const string Surface = "#fcfcfb";
    private const string TextPrimary = "#0b0b0b";
    private const string TextSecondary = "#52514e";
    private const string Grid = "#e4e3df";
    private const string Axis = "#b9b8b2";

    private static readonly string[] Colors = { "#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948" };
    private static readonly string[] Shapes = { "circle", "square", "triangle", "diamond", "cross", "circle", "square", "triangle" };

    public static string Render(string title, string xAxis, string yAxis, bool percent, IReadOnlyList<ChartSeries> series)
    {
        const double width = 760, height = 440, left = 64, right = 24, top = 92, bottom = 64;
        var plotW = width - left - right;
        var plotH = height - top - bottom;

        var xs = series.SelectMany(s => s.Points.Select(p => p.X)).Distinct().OrderBy(x => x).ToList();
        var xMin = xs.First();
        var xMax = xs.Last() == xMin ? xMin + 1 : xs.Last();
        var yValues = series.SelectMany(s => s.Points).Where(p => !double.IsNaN(p.Y.Mean))
            .SelectMany(p => new[] { p.Y.Mean - p.Y.Ci95, p.Y.Mean + p.Y.Ci95 }).ToList();
        var yMax = percent ? 100 : NiceCeiling(yValues.DefaultIfEmpty(1).Max());
        const double yMin = 0;

        double X(double x) => left + (x - xMin) / (xMax - xMin) * plotW;
        double Y(double y) => top + plotH - (Math.Clamp(y, yMin, yMax) - yMin) / (yMax - yMin) * plotH;

        var sb = new StringBuilder();
        sb.AppendLine(Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\">"));
        sb.AppendLine(Invariant($"<rect width=\"{width}\" height=\"{height}\" fill=\"{Surface}\"/>"));
        sb.AppendLine(Invariant($"<text x=\"{left}\" y=\"28\" font-size=\"16\" font-weight=\"600\" fill=\"{TextPrimary}\">{Escape(title)}</text>"));

        var legendX = left;
        for (var i = 0; i < series.Count; i++)
        {
            var color = Colors[i % Colors.Length];
            sb.AppendLine(Invariant($"<line x1=\"{legendX}\" y1=\"54\" x2=\"{legendX + 22}\" y2=\"54\" stroke=\"{color}\" stroke-width=\"2\" stroke-linecap=\"round\"/>"));
            sb.Append(Marker(Shapes[i % Shapes.Length], legendX + 11, 54, color));
            sb.AppendLine(Invariant($"<text x=\"{legendX + 28}\" y=\"58\" font-size=\"12\" fill=\"{TextSecondary}\">{Escape(series[i].Name)}</text>"));
            legendX += 40 + series[i].Name.Length * 7;
        }

        for (var t = 0; t <= 5; t++)
        {
            var value = yMin + (yMax - yMin) * t / 5;
            var y = Y(value);
            sb.AppendLine(Invariant($"<line x1=\"{left}\" y1=\"{y:0.##}\" x2=\"{left + plotW}\" y2=\"{y:0.##}\" stroke=\"{(t == 0 ? Axis : Grid)}\" stroke-width=\"1\"/>"));
            sb.AppendLine(Invariant($"<text x=\"{left - 8}\" y=\"{y + 4:0.##}\" font-size=\"11\" text-anchor=\"end\" fill=\"{TextSecondary}\">{value:0.##}</text>"));
        }
        foreach (var x in xs)
            sb.AppendLine(Invariant($"<text x=\"{X(x):0.##}\" y=\"{top + plotH + 18}\" font-size=\"11\" text-anchor=\"middle\" fill=\"{TextSecondary}\">{x:0.##}</text>"));
        sb.AppendLine(Invariant($"<text x=\"{left + plotW / 2}\" y=\"{height - 16}\" font-size=\"12\" text-anchor=\"middle\" fill=\"{TextSecondary}\">{Escape(xAxis)}</text>"));
        sb.AppendLine(Invariant($"<text x=\"16\" y=\"{top + plotH / 2}\" font-size=\"12\" text-anchor=\"middle\" fill=\"{TextSecondary}\" transform=\"rotate(-90 16 {top + plotH / 2})\">{Escape(yAxis)}</text>"));

        for (var i = 0; i < series.Count; i++)
        {
            var color = Colors[i % Colors.Length];
            var points = series[i].Points.Where(p => !double.IsNaN(p.Y.Mean)).OrderBy(p => p.X).ToList();
            foreach (var p in points.Where(p => p.Y.Ci95 > 0))
            {
                sb.AppendLine(Invariant($"<line x1=\"{X(p.X):0.##}\" y1=\"{Y(p.Y.Mean - p.Y.Ci95):0.##}\" x2=\"{X(p.X):0.##}\" y2=\"{Y(p.Y.Mean + p.Y.Ci95):0.##}\" stroke=\"{color}\" stroke-width=\"1\" opacity=\"0.6\"/>"));
            }
            if (points.Count > 1)
            {
                var path = string.Join(" ", points.Select((p, j) => Invariant($"{(j == 0 ? "M" : "L")}{X(p.X):0.##},{Y(p.Y.Mean):0.##}")));
                sb.AppendLine(Invariant($"<path d=\"{path}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>"));
            }
            foreach (var p in points)
            {
                sb.Append("<g>");
                sb.Append(Invariant($"<title>{Escape(series[i].Name)}: {p.X:0.##} → {p.Y.Mean:0.0} ± {p.Y.Ci95:0.0}</title>"));
                sb.Append(Marker(Shapes[i % Shapes.Length], X(p.X), Y(p.Y.Mean), color));
                sb.AppendLine("</g>");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string Marker(string shape, double x, double y, string color)
    {
        const double r = 4.5;
        var ring = $"stroke=\"{Surface}\" stroke-width=\"2\" fill=\"{color}\"";
        return shape switch
        {
            "square" => Invariant($"<rect x=\"{x - r:0.##}\" y=\"{y - r:0.##}\" width=\"{2 * r}\" height=\"{2 * r}\" {ring}/>"),
            "triangle" => Invariant($"<path d=\"M{x:0.##},{y - r - 1:0.##} L{x + r + 1:0.##},{y + r:0.##} L{x - r - 1:0.##},{y + r:0.##} Z\" {ring}/>"),
            "diamond" => Invariant($"<path d=\"M{x:0.##},{y - r - 1:0.##} L{x + r + 1:0.##},{y:0.##} L{x:0.##},{y + r + 1:0.##} L{x - r - 1:0.##},{y:0.##} Z\" {ring}/>"),
            "cross" => Invariant($"<path d=\"M{x - r:0.##},{y - r:0.##} L{x + r:0.##},{y + r:0.##} M{x + r:0.##},{y - r:0.##} L{x - r:0.##},{y + r:0.##}\" stroke=\"{color}\" stroke-width=\"2.5\" stroke-linecap=\"round\"/>"),
            _ => Invariant($"<circle cx=\"{x:0.##}\" cy=\"{y:0.##}\" r=\"{r}\" {ring}/>"),
        };
    }

    private static double NiceCeiling(double value)
    {
        if (value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
        {
            if (step * magnitude >= value) return step * magnitude;
        }
        return 10 * magnitude;
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
