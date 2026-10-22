using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli.Ui;

/// <summary>Whether what the interface shows is current, as its header badge says it.</summary>
internal enum SkyUiFreshness
{
    /// <summary>No address or key: nothing can be read until someone signs in.</summary>
    NotSignedIn,

    /// <summary>Nothing has been read yet.</summary>
    Connecting,

    /// <summary>The last read succeeded, recently.</summary>
    Live,

    /// <summary>CameraAgent answered the last read with a failure, or nothing was read for too long: values are last known.</summary>
    Stale,

    /// <summary>CameraAgent could not be reached: values are last known.</summary>
    Offline,

    /// <summary>CameraAgent refused the key (HTTP 401): nothing more can be read with it.</summary>
    SignedOut
}

/// <summary>
/// One document the interface reads and when: the last one read (shown as last known after a failed read), when it was
/// read, and why the latest read failed, when it did.
/// </summary>
internal sealed class SkyUiReading<T>
{
    public T? Value { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    /// <summary>Why the latest read failed; null when it succeeded or none was made.</summary>
    public string? Failure { get; private set; }

    /// <summary>The <c>--json</c> kind of the latest failure (<c>Unreachable</c>, <c>SignedOut</c>, …).</summary>
    public string? FailureKind { get; private set; }

    public bool HasValue => ReadAt is not null;

    /// <summary>True when a value is shown but the latest read of it failed.</summary>
    public bool IsLastKnown => HasValue && Failure is not null;

    public void Succeeded(T value, DateTimeOffset at)
    {
        Value = value;
        ReadAt = at;
        Failure = null;
        FailureKind = null;
    }

    public void Failed(string kind, string message)
    {
        FailureKind = kind;
        Failure = message;
    }

    public void Clear()
    {
        Value = default;
        ReadAt = null;
        Failure = null;
        FailureKind = null;
    }
}

/// <summary>Everything the interface has read from CameraAgent.</summary>
internal sealed class SkyUiData
{
    public SkyUiReading<JsonElement> Summary { get; } = new();

    public SkyUiReading<SkyInfo> Info { get; } = new();

    public SkyUiReading<SkySettingsForm> Settings { get; } = new();

    public SkyUiReading<JsonElement> Rigs { get; } = new();

    public SkyUiReading<JsonElement> Schedule { get; } = new();

    /// <summary>State lines from the status reads, newest first: the status page's recent changes on a wide terminal.</summary>
    public List<(DateTimeOffset At, string State)> Changes { get; } = [];

    /// <summary>How many state lines are kept.</summary>
    public const int ChangesKept = 20;

    public void Clear()
    {
        Summary.Clear();
        Info.Clear();
        Settings.Clear();
        Rigs.Clear();
        Schedule.Clear();
        Changes.Clear();
    }

    /// <summary>Records a status read's state line when it differs from the last.</summary>
    public void Record(DateTimeOffset at, string state)
    {
        if (Changes.Count > 0 && Changes[0].State == state)
        {
            return;
        }

        Changes.Insert(0, (at, state));
        if (Changes.Count > ChangesKept)
        {
            Changes.RemoveAt(Changes.Count - 1);
        }
    }
}
