using System.Globalization;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// The Observatory &amp; location sky plot. Every visible object is drawn at the pixel the active rig's
/// calibrated optics place it, scaled into a 200-unit square, so the plot shows the sky the way the camera
/// images it: a horizontal flip, a roll, or a tilted boresight all appear here as they do in a capture. The
/// compass letters sit where the published cardinal landmarks meet the image-circle edge.
/// </summary>
/// <remarks>
/// A scene can hold thousands of objects, so the plot re-renders only when the scene, the highlighted table
/// page, the selection, or the drawn figures change, and each object's click handler is created once per scene
/// so an unchanged object keeps its handler across renders. The table is the keyboard path to every object;
/// the plot's objects are pointer targets only.
/// </remarks>
public sealed partial class ObservatorySkyDial : ComponentBase
{
    internal const double Centre = 100d;
    internal const double FrameRadius = 92d;
    private const double CardinalRadius = 97d;
    private const double EdgeMargin = 5d;
    private const double LabelGap = 1.8d;
    private const double LabelFlipX = 120d;

    private static readonly IReadOnlySet<string> s_empty = new HashSet<string>(StringComparer.Ordinal);
    private static readonly string[] s_directions =
        ["up", "upper right", "right", "lower right", "down", "lower left", "left", "upper left"];

    private CameraAgentSkyMapProjectionResult? _framedScene;
    private DialFrame _frame;
    private CameraAgentSkyMapObject[] _drawOrder = [];
    private Dictionary<string, CameraAgentSkyMapConstellation> _constellations = new(StringComparer.Ordinal);
    private Dictionary<string, Func<Task>> _selectHandlers = new(StringComparer.Ordinal);
    private List<CardinalLabel> _cardinals = [];
    private string? _orientation;

    private string? _renderedSelection;
    private HashSet<string> _renderedOnPage = new(StringComparer.Ordinal);
    private HashSet<string> _renderedFigures = new(StringComparer.Ordinal);
    private bool _dirty = true;

    /// <summary>The projection to plot.</summary>
    [Parameter, EditorRequired] public CameraAgentSkyMapProjectionResult Scene { get; set; } = default!;

    /// <summary>The identifiers of the objects on the table's current page, which the plot highlights.</summary>
    [Parameter] public IReadOnlySet<string> OnPage { get; set; } = s_empty;

    /// <summary>The selected object, which the plot rings and labels.</summary>
    [Parameter] public string? SelectedId { get; set; }

    /// <summary>The constellation figures to draw.</summary>
    [Parameter] public IReadOnlySet<string> Figures { get; set; } = s_empty;

    /// <summary>Raised with an object's identifier when the operator clicks it on the plot.</summary>
    [Parameter] public EventCallback<string> ObjectSelected { get; set; }

    /// <summary>
    /// Describes where north and east fall on the plot, such as "north is up and east is left", or returns
    /// null when the rig publishes no cardinal landmarks.
    /// </summary>
    internal static string? DescribeOrientation(CameraAgentSkyMapGeometry geometry)
    {
        var frame = DialFrame.From(geometry);
        var north = geometry.Cardinals.FirstOrDefault(static cardinal => cardinal.Name == "North");
        var east = geometry.Cardinals.FirstOrDefault(static cardinal => cardinal.Name == "East");
        if (north is not { PixelX: { } northX, PixelY: { } northY } || east is not { PixelX: { } eastX, PixelY: { } eastY })
        {
            return null;
        }

        return $"north is {Direction(northX - frame.CentreX, northY - frame.CentreY)} and east is {Direction(eastX - frame.CentreX, eastY - frame.CentreY)}";
    }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_framedScene, Scene))
        {
            _framedScene = Scene;
            _frame = DialFrame.From(Scene.Geometry);
            // Stars first, so the brighter solar-system bodies are drawn over any star they cover.
            _drawOrder = [
                .. Scene.Objects.Where(static item => item.Kind == "Star"),
                .. Scene.Objects.Where(static item => item.Kind != "Star")];
            _constellations = Scene.Constellations.ToDictionary(
                static item => item.ConstellationId, StringComparer.Ordinal);
            _selectHandlers = new Dictionary<string, Func<Task>>(StringComparer.Ordinal);
            _cardinals = CreateCardinals();
            _orientation = DescribeOrientation(Scene.Geometry);
            _dirty = true;
        }

        if (!string.Equals(_renderedSelection, SelectedId, StringComparison.Ordinal))
        {
            _renderedSelection = SelectedId;
            _dirty = true;
        }

        if (!_renderedOnPage.SetEquals(OnPage))
        {
            _renderedOnPage = new HashSet<string>(OnPage, StringComparer.Ordinal);
            _dirty = true;
        }

        if (!_renderedFigures.SetEquals(Figures))
        {
            _renderedFigures = new HashSet<string>(Figures, StringComparer.Ordinal);
            _dirty = true;
        }
    }

    protected override bool ShouldRender()
    {
        var render = _dirty;
        _dirty = false;
        return render;
    }

    private string AriaLabel => _orientation is null
        ? "Sky plot of the visible objects as the camera images them. The rig publishes no compass landmarks, so north and east are not marked."
        : $"Sky plot of the visible objects as the camera images them: {_orientation}.";

    private string OrientationNote => _orientation is null
        ? "The plot shows the image as the camera records it. This rig's aperture publishes no compass landmarks, so N, E, S and W are not marked."
        : $"The plot shows the image as the camera records it, so {_orientation}.";

    private CameraAgentSkyMapObject? Selected
        => SelectedId is null ? null : Scene.Objects.FirstOrDefault(item => item.Id == SelectedId);

    private IEnumerable<CameraAgentSkyMapConstellation> DrawnFigures
        => Figures.Order(StringComparer.Ordinal)
            .Select(id => _constellations.GetValueOrDefault(id))
            .OfType<CameraAgentSkyMapConstellation>();

    private Func<Task> SelectHandler(string id)
    {
        if (!_selectHandlers.TryGetValue(id, out var handler))
        {
            handler = () => ObjectSelected.InvokeAsync(id);
            _selectHandlers[id] = handler;
        }

        return handler;
    }

    private (double X, double Y) Map(double pixelX, double pixelY) => _frame.Map(pixelX, pixelY);

    private string Points(CameraAgentSkyMapPolyline line)
    {
        var builder = new StringBuilder(line.Points.Count * 14);
        foreach (var point in line.Points)
        {
            var (x, y) = Map(point.X, point.Y);
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Number(x)).Append(',').Append(Number(y));
        }

        return builder.ToString();
    }

    private string ObjectClass(CameraAgentSkyMapObject item) => (item.Kind == "Star", _renderedOnPage.Contains(item.Id)) switch
    {
        (true, false) => "site-dial-object",
        (true, true) => "site-dial-object on-page",
        (false, false) => "site-dial-object solar",
        (false, true) => "site-dial-object solar on-page"
    };

    private static double Radius(CameraAgentSkyMapObject item) => item.Kind switch
    {
        "Star" => Math.Clamp(2.6d - 0.3d * item.Magnitude, 0.5d, 3.2d),
        "Sun" => 4d,
        "Moon" => 3.6d,
        _ => Math.Clamp(2.6d - 0.3d * item.Magnitude, 2.2d, 3.2d)
    };

    // Labels sit beside the object on the side with more room and never leave the plot vertically, so a
    // name near an edge is still inside the viewBox.
    private static Label LabelFor(double x, double y, double radius)
    {
        var right = x <= LabelFlipX;
        return new Label(
            right ? x + radius + LabelGap : x - radius - LabelGap,
            Math.Clamp(y + 2.4d, EdgeMargin + 3d, 200d - EdgeMargin + 2d),
            right ? "start" : "end");
    }

    private List<CardinalLabel> CreateCardinals()
    {
        var labels = new List<CardinalLabel>(4);
        foreach (var cardinal in Scene.Geometry.Cardinals)
        {
            if (cardinal is not { PixelX: { } pixelX, PixelY: { } pixelY } || cardinal.Name.Length == 0)
            {
                continue;
            }

            var (x, y) = Map(pixelX, pixelY);
            var length = Math.Sqrt((x - Centre) * (x - Centre) + (y - Centre) * (y - Centre));
            if (length < 1e-6)
            {
                continue;
            }

            labels.Add(new CardinalLabel(
                cardinal.Name[..1],
                cardinal.Name,
                Math.Clamp(Centre + (x - Centre) / length * CardinalRadius, EdgeMargin, 200d - EdgeMargin),
                Math.Clamp(Centre + (y - Centre) / length * CardinalRadius, EdgeMargin, 200d - EdgeMargin)));
        }

        return labels;
    }

    private static string Direction(double dx, double dy)
    {
        // Screen angle clockwise from up; image y grows downward.
        var angle = (Math.Atan2(dx, -dy) * 180d / Math.PI + 360d) % 360d;
        return s_directions[(int)Math.Round(angle / 45d, MidpointRounding.AwayFromZero) % s_directions.Length];
    }

    private static string Number(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private readonly record struct Label(double X, double Y, string Anchor);

    private sealed record CardinalLabel(string Letter, string Name, double X, double Y);

    /// <summary>
    /// Maps image pixels into the plot. A circular aperture puts the image circle on the plot's outer ring; a
    /// rectangular one fits the longer image side across it. The sensor rectangle is also mapped, so a circle
    /// the sensor crops is drawn cropped.
    /// </summary>
    private readonly record struct DialFrame(
        double CentreX,
        double CentreY,
        double Scale,
        bool Circular,
        double Left,
        double Top,
        double Right,
        double Bottom)
    {
        public static DialFrame From(CameraAgentSkyMapGeometry geometry)
        {
            var width = Math.Max(1, geometry.WidthPixels);
            var height = Math.Max(1, geometry.HeightPixels);
            var circular = geometry.ImageCircleRadiusPixels is > 0;
            var centreX = circular ? geometry.PrincipalPointX : width / 2d;
            var centreY = circular ? geometry.PrincipalPointY : height / 2d;
            var scale = FrameRadius / (circular ? geometry.ImageCircleRadiusPixels!.Value : Math.Max(width, height) / 2d);
            return new DialFrame(
                centreX,
                centreY,
                scale,
                circular,
                Centre + (0 - centreX) * scale,
                Centre + (0 - centreY) * scale,
                Centre + (width - centreX) * scale,
                Centre + (height - centreY) * scale);
        }

        public (double X, double Y) Map(double pixelX, double pixelY)
            => (Centre + (pixelX - CentreX) * Scale, Centre + (pixelY - CentreY) * Scale);

        /// <summary>True when the sensor edge cuts into the image circle.</summary>
        public bool Cropped => Circular && (
            Left > Centre - FrameRadius + 0.01 ||
            Top > Centre - FrameRadius + 0.01 ||
            Right < Centre + FrameRadius - 0.01 ||
            Bottom < Centre + FrameRadius - 0.01);
    }
}
