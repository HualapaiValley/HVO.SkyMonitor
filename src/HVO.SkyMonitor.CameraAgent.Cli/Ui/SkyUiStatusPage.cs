using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;
using HVO.SkyMonitor.TerminalUi;

namespace HVO.SkyMonitor.CameraAgent.Cli.Ui;

/// <summary>
/// F1 Status: capture and the camera, the queues, storage and, on a wide terminal, LogicHost delivery and the recent
/// changes, from the operations summary read every poll interval.
/// </summary>
internal sealed class SkyUiStatusPage : SkyUiPage
{
    private readonly SkyUiFields _capture;
    private readonly SkyUiFields _queues;
    private readonly SkyUiFields _delivery;
    private readonly SkyUiFields _storage;
    private readonly SkyUiFields _changes;
    private readonly HvoPanel _capturePanel;
    private readonly HvoPanel _queuesPanel;
    private readonly HvoPanel _deliveryPanel;
    private readonly HvoPanel _storagePanel;
    private readonly HvoPanel _changesPanel;
    private int _width = 80;
    private int _height = 19;

    public SkyUiStatusPage(SkyTerminalUi ui)
        : base(ui, "Status")
    {
        _capture = new SkyUiFields(Theme, 12);
        _queues = new SkyUiFields(Theme, 12);
        _delivery = new SkyUiFields(Theme, 13);
        _storage = new SkyUiFields(Theme, 10);
        _changes = new SkyUiFields(Theme, 10);
        _capturePanel = AddPanel("Capture", _capture);
        _queuesPanel = AddPanel("Queues", _queues);
        _deliveryPanel = AddPanel("Delivery", _delivery);
        _storagePanel = AddPanel("Storage", _storage);
        _changesPanel = AddPanel("Recent changes", _changes);
        AddButton("Refresh", Ui.RefreshNow, primary: true);
    }

    public override string Help => Ui.Freshness == SkyUiFreshness.Live
        ? $"Read every {(int)Ui.Context.Host.PollInterval.TotalSeconds} s. Ctrl+R reads now."
        : "Ctrl+R reads again.";

    public override void Arrange(int width, int height, bool wide)
    {
        _width = width;
        _height = height;
        var stores = Math.Clamp(SkyJson.Items(Ui.Data.Summary.Value, "storage", "value").Count, 1, 3);
        if (wide)
        {
            var column = (width - 4) / 3;
            Place(_capturePanel, 1, 0, column, 9);
            Place(_queuesPanel, 2 + column, 0, column, 9);
            Place(_deliveryPanel, 3 + 2 * column, 0, width - 4 - 2 * column, 9);
            Place(_storagePanel, 1, 9, width - 2, stores + 2);
            var top = 9 + stores + 2;
            Place(_changesPanel, 1, top, width - 2, Math.Max(3, height - top - 2));
            _deliveryPanel.Visible = true;
            _changesPanel.Visible = true;
            PlaceButtons(Math.Max(top + 3, height - 1));
        }
        else
        {
            var left = (width - 3) / 2;
            Place(_capturePanel, 1, 0, left, 8);
            Place(_queuesPanel, 2 + left, 0, width - 3 - left, 8);
            Place(_storagePanel, 1, 8, width - 2, stores + 2);
            _deliveryPanel.Visible = false;
            _changesPanel.Visible = false;
            PlaceButtons(8 + stores + 3);
        }
    }

    public override void DataChanged()
    {
        var reading = Ui.Data.Summary;
        _capturePanel.Caption = Titled("Capture", reading);
        _queuesPanel.Caption = Titled("Queues", reading);
        _deliveryPanel.Caption = Titled("Delivery", reading);
        _storagePanel.Caption = Titled("Storage", reading);
        if (!reading.HasValue)
        {
            var pending = Ui.Connection is null ? new SkyUiField(string.Empty, "Not signed in: F5 System.", HvoTone.Warning) : Pending(reading, "the status");
            _capture.Fields = [pending];
            _queues.Fields = [];
            _delivery.Fields = [];
            _storage.Fields = [];
            _changes.Fields = [];
            return;
        }

        var summary = reading.Value;
        var now = Ui.Now;
        _capture.Fields = Capture(summary, now);
        _queues.Fields = Queues(summary);
        _delivery.Fields = Delivery(summary);
        _storage.Fields = Storage(summary);
        _changes.Fields = [.. Ui.Data.Changes.Select(change => new SkyUiField(SkyCliFormat.Time(change.At)[11..], change.State))];
        Arrange(_width, _height, Ui.Wide);
    }

    public override string Describe()
        => string.Join("\n\n", new[]
        {
            Describe(_capturePanel.Caption, _capture, 60),
            Describe(_queuesPanel.Caption, _queues, 60),
            Describe(_storagePanel.Caption, _storage, 76)
        }.Concat(Ui.Wide ? [Describe(_deliveryPanel.Caption, _delivery, 60), Describe(_changesPanel.Caption, _changes, 100)] : []));

    internal static IReadOnlyList<SkyUiField> Capture(JsonElement summary, DateTimeOffset now)
    {
        var control = SkyJson.Text(summary, "captureControl", "value", "state") ?? "unknown";
        var camera = SkyJson.Text(summary, "captureRuntime", "value", "availability") ?? "unknown";
        var last = SkyJson.Time(summary, "captureTelemetry", "value", "latestCaptureStartedUtc")
            ?? SkyJson.Time(summary, "captureRuntime", "value", "lastSucceededUtc");
        var exposure = SkyJson.Number(summary, "captureTelemetry", "value", "latestExposureMilliseconds");
        var gain = SkyJson.Number(summary, "captureTelemetry", "value", "latestGain");
        var validation = SkyJson.Text(summary, "configuration", "value", "validationStatus") ?? "unknown";
        return
        [
            new("Control", control, control == "Running" ? HvoTone.Success : HvoTone.Warning),
            new("Camera", camera, camera == "Available" ? HvoTone.Success : HvoTone.Warning),
            new("Last frame", last is { } at ? $"{SkyCliFormat.Time(at)[11..]} ({SkyCliFormat.Duration(now - at)} ago)" : "none yet"),
            new("Rate", SkyJson.Number(summary, "captureTelemetry", "value", "capturesPerMinute") is { } rate ? $"{SkyCliFormat.Number(rate, "0.0")} captures/min" : "unknown"),
            new("Exposure", exposure is { } ms ? $"{SkyCliFormat.Number(ms, "0.###")} ms" + (gain is { } g ? $", gain {SkyCliFormat.Number(g)}" : string.Empty) : "unknown"),
            new("Config", validation, validation == "Valid" ? HvoTone.Success : HvoTone.Warning)
        ];
    }

    internal static IReadOnlyList<SkyUiField> Queues(JsonElement summary)
    {
        var quarantined = new[] { "rawIngress", "captureLanes", "captureProcessing", "artifactOutbox" }
            .Sum(name => SkyJson.Integer(summary, name, "value", "quarantineCount") ?? 0);
        var central = SkyJson.Text(summary, "configuration", "value", "centralIntegration") ?? "unknown";
        return
        [
            Queue("Raw ingress", summary, "rawIngress"),
            Queue("Lanes", summary, "captureLanes"),
            Queue("Processing", summary, "captureProcessing"),
            Queue("Outbox", summary, "artifactOutbox"),
            new("Quarantined", SkyCliFormat.Count(quarantined), quarantined > 0 ? HvoTone.Danger : HvoTone.Text),
            new("LogicHost", central)
        ];
    }

    internal static IReadOnlyList<SkyUiField> Delivery(JsonElement summary)
    {
        SkyUiField Lane(string label, string section)
        {
            var availability = SkyJson.Text(summary, section, "value", "availability") ?? "unknown";
            var pending = SkyJson.Integer(summary, section, "value", "pendingCount");
            return new(label, availability + (pending is { } count ? $", {SkyCliFormat.Count(count)} pending" : string.Empty)
                + Freshness(summary, section), availability is "Available" or "Disabled" ? HvoTone.Text : HvoTone.Warning);
        }

        var acknowledged = SkyJson.Time(summary, "heartbeat", "value", "lastAcknowledgedUtc");
        return
        [
            Lane("Heartbeat", "heartbeat"),
            new("Last ack", acknowledged is { } at ? SkyCliFormat.Time(at) : "never"),
            Lane("Environment", "environmentalDelivery"),
            Lane("Evidence", "executionEvidenceExport"),
            Lane("Transients", "transientWorker"),
            Lane("Replay", "archivedReplay")
        ];
    }

    internal static IReadOnlyList<SkyUiField> Storage(JsonElement summary)
        => [.. SkyJson.Items(summary, "storage", "value").Take(3).Select(store =>
        {
            var free = SkyJson.Integer(store, "availableBytes");
            var total = SkyJson.Integer(store, "totalBytes");
            var pressure = SkyJson.Boolean(store, "isUnderPressure") == true;
            var percent = free is { } f && total is { } t && t > 0 ? $" ({SkyCliFormat.Number(100.0 * f / t, "0")}% free)" : string.Empty;
            var retention = SkyJson.Integer(store, "effectiveRetentionDays") is { } days ? $", kept {days} d" : string.Empty;
            return new SkyUiField(SkyJson.Text(store, "alias") ?? "storage",
                $"{SkyCliFormat.Bytes(free)} free of {SkyCliFormat.Bytes(total)}{percent}{retention}{(pressure ? ", UNDER PRESSURE" : string.Empty)}",
                pressure ? HvoTone.Danger : HvoTone.Text);
        })];

    private static SkyUiField Queue(string label, JsonElement summary, string section)
    {
        var pending = SkyJson.Integer(summary, section, "value", "pendingCount");
        var bytes = SkyJson.Integer(summary, section, "value", "pendingBytes");
        var retry = SkyJson.Integer(summary, section, "value", "retryCount");
        var text = $"{SkyCliFormat.Count(pending)} pending"
            + (bytes is > 0 ? $" ({SkyCliFormat.Bytes(bytes)})" : string.Empty)
            + (retry is > 0 ? $", {SkyCliFormat.Count(retry)} retrying" : string.Empty)
            + Freshness(summary, section);
        return new(label, text, retry is > 0 ? HvoTone.Warning : HvoTone.Text);
    }

    private static string Freshness(JsonElement summary, string section)
        => SkyJson.Text(summary, section, "freshness") switch
        {
            "stale" => " (stale)",
            "unknown" => " (not observed)",
            "disabled" => " (disabled)",
            _ => string.Empty
        };
}
