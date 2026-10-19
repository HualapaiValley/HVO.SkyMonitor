using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using SkiaSharp;

namespace HVO.SkyMonitor.VirtualSkyPoc;

internal static partial class Program
{
    private static void DrawSatelliteDiagnostics(string output, IReadOnlyList<PixelPoint[]> segments,
        IReadOnlyList<Linear16TransientComponent> components, string fontPath, DateTimeOffset start, double seconds, string mode)
    {
        using var typeface = SKTypeface.FromFile(fontPath) ?? throw new ArgumentException("Explicit diagnostic font required.");
        using var font = new SKFont(typeface, 13);
        if (font.GetGlyphs("Predicted | measured | synthetic").Any(glyph => glyph == 0))
            throw new ArgumentException("Diagnostic font lacks glyphs.");
        using var bitmap = SKBitmap.Decode(Path.Combine(output, "injected-03.png"));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        void Line(PixelPoint[] points, SKColor color)
        {
            if (points.Length < 2) return;
            paint.Color = color;
            using var builder = new SKPathBuilder();
            builder.MoveTo((float)points[0].X, (float)points[0].Y);
            foreach (var point in points.Skip(1)) builder.LineTo((float)point.X, (float)point.Y);
            using var path = builder.Detach();
            canvas.DrawPath(path, paint);
        }
        foreach (var segment in segments) Line(segment, SKColors.Gold);
        paint.StrokeWidth = 1.5f;
        foreach (var component in components)
            Line([new(component.StartX * 2, component.StartY * 2), new(component.EndX * 2, component.EndY * 2)], SKColors.Magenta);
        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(0, 0, 0, 220);
        canvas.DrawRect(5, 5, bitmap.Width - 10, 87, paint);
        paint.Color = SKColors.White;
        canvas.DrawText($"{mode} | {start:yyyy-MM-dd HH:mm:ss} UTC | {seconds.ToString("R", CultureInfo.InvariantCulture)} s", 12, 23, SKTextAlign.Left, font, paint);
        canvas.DrawText("Diagnostic derivative | explicit synthetic optical stimulus", 12, 43, SKTextAlign.Left, font, paint);
        paint.Color = SKColors.Gold;
        canvas.DrawText("Gold: supplied exposure segments (gaps preserved)", 12, 63, SKTextAlign.Left, font, paint);
        paint.Color = SKColors.Magenta;
        canvas.DrawText("Magenta: all measured components; no temporal direction", 12, 83, SKTextAlign.Left, font, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(Path.Combine(output, "injected-diagnostic.png"));
        encoded.SaveTo(stream);
    }

    private static void WriteSatelliteComparison(string output)
    {
        using var validation = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "validation.json")));
        var root = validation.RootElement;
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Orbital virtual-camera engineering comparison</title><style>
            :root{color-scheme:dark}body{font:16px system-ui;background:#101720;color:#e5edf6;margin:0 auto;padding:24px;max-width:1440px}
            h1{font-size:26px}p{line-height:1.5;max-width:1100px}.tag{padding:12px;background:#25354c;border-left:4px solid #e6bb58}
            .grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}.pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:16px}
            figure{margin:0;background:#172334;padding:8px}img{width:100%;height:auto;display:block}figcaption{padding:8px 0;font-size:14px}
            pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#172334;padding:16px;font-size:12px}li{margin:8px 0}
            @media(max-width:760px){.grid,.pair{grid-template-columns:1fr}body{padding:12px}}
            </style><h1>Orbital track through the actual VirtualSky camera</h1>
            """);
        var mode = root.GetProperty("canonicalInput").GetBoolean()
            ? "Canonical serialized #529 products; strict ParseTrack input validation."
            : "Independent/reference mode i: retained Seattle ISS geometry. This is not new mode-a engine output or qualification.";
        html.Append("<p class=tag>").Append(WebUtility.HtmlEncode(mode)).Append("</p>");
        html.Append("<p>Five matched control, injection and repeat captures. Every raw signal passes through the existing transient renderer and actual VirtualSky sensor/readout. PNGs use a fixed 64–4095 code range and gamma 2.2; no per-frame stretch.</p>");
        html.Append("<p><strong>Explicit synthetic profile:</strong> ").Append(WebUtility.HtmlEncode(root.GetProperty("syntheticOpticalProfile").GetRawText())).Append("</p>");
        html.Append("<p><strong>CausalProvisional:</strong> frame 3 uses frames 1 and 2 (N-2, N-1). Frames 1 and 2 report Missing on cold start. No future-frame dependency. Control candidates: ")
            .Append(root.GetProperty("controlCandidates").GetInt32()).Append("; injected candidates: ")
            .Append(root.GetProperty("injectedCandidates").GetInt32()).Append(". All five repeated raw hashes match.</p>");
        void Figure(string file, string caption)
        {
            html.Append("<figure><img alt=\"").Append(WebUtility.HtmlEncode(caption)).Append("\" src=\"data:image/png;base64,")
                .Append(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(output, file))))
                .Append("\"><figcaption>").Append(WebUtility.HtmlEncode(caption)).Append("</figcaption></figure>");
        }
        html.Append("<div class=grid>");
        for (var i = 1; i <= 5; i++)
            foreach (var name in new[] { "control", "injected", "repeat" })
                Figure($"{name}-{i:00}.png", $"{name} frame {i} — raw-derived display, no labels or path painted in");
        html.Append("</div><h2>Separate diagnostic derivative</h2><div class=pair>");
        Figure("injected-diagnostic.png", "Frame 3: gold supplied prediction, magenta every measured component. Labels never enter raw pixels.");
        Figure("injected-residual.png", "Causal residual at half resolution; fixed positive 0–500 ADU display scale.");
        html.Append("</div><div class=pair>");
        Figure("control-residual.png", "Matched control causal residual; same thresholds and masks.");
        html.Append("<div><h2>Limits</h2><ul>");
        foreach (var limitation in root.GetProperty("limitations").EnumerateArray())
            html.Append("<li>").Append(WebUtility.HtmlEncode(limitation.GetString())).Append("</li>");
        html.Append("</ul></div></div><h2>Machine evidence</h2><pre>")
            .Append(WebUtility.HtmlEncode(root.GetRawText())).Append("</pre></html>");
        File.WriteAllText(Path.Combine(output, "comparison.html"), html.ToString());
    }
}
