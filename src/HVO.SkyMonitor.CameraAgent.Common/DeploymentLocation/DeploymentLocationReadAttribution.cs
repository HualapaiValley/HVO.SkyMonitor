using System.Diagnostics;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

// Bounded diagnostics on one outer activity, never one activity/object per manifest.
// Counts include attempted leaves (including terminal enumeration/reader calls and
// failed leaves); bytes count successfully materialized inputs. Leaf intervals do
// not nest. Synchronous allocations are thread-local; async reads have elapsed
// time/bytes only. Neither may be added to inclusive process-wide span allocation.
internal sealed class DeploymentLocationReadAttribution : IDisposable
{
    internal enum Stage { Enumeration, Security, Read, Parse, Provenance, Open, Query, Unprotect, Deserialize, Validate }

    private static readonly string[] StageNames =
        ["enumeration", "security", "read", "parse", "provenance", "open", "query", "unprotect", "deserialize", "validate"];
    private readonly Activity _activity;
    private readonly Totals[] _totals = new Totals[StageNames.Length];
    private long _manifestCount;
    private long _manifestBytes;

    private DeploymentLocationReadAttribution(Activity activity) => _activity = activity;

    internal static DeploymentLocationReadAttribution? Create(Activity? activity)
        => activity?.IsAllDataRequested == true ? new(activity) : null;

    internal Leaf Measure(Stage stage, bool synchronous = true) => new(this, stage, synchronous);

    internal void AddBytes(Stage stage, long bytes) => _totals[(int)stage].Bytes += bytes;

    internal void AddManifest(long bytes)
    {
        _manifestCount++;
        _manifestBytes += bytes;
        AddBytes(Stage.Parse, bytes);
    }

    public void Dispose()
    {
        _activity.SetTag("attribution.manifest.count", _manifestCount);
        _activity.SetTag("attribution.manifest.bytes", _manifestBytes);
        for (var index = 0; index < _totals.Length; index++)
        {
            ref var total = ref _totals[index];
            if (total.Count == 0) continue;
            var prefix = "attribution." + StageNames[index];
            _activity.SetTag(prefix + ".count", total.Count);
            _activity.SetTag(prefix + ".elapsed_ms", (double)total.ElapsedTicks / Stopwatch.Frequency * 1000);
            _activity.SetTag(prefix + ".bytes", total.Bytes);
            if (total.Synchronous)
                _activity.SetTag(prefix + ".thread_allocated_bytes", total.ThreadAllocatedBytes);
        }
    }

    private struct Totals
    {
        internal long Count;
        internal long ElapsedTicks;
        internal long Bytes;
        internal long ThreadAllocatedBytes;
        internal bool Synchronous;
    }

    internal readonly struct Leaf : IDisposable
    {
        private readonly DeploymentLocationReadAttribution _owner;
        private readonly Stage _stage;
        private readonly bool _synchronous;
        private readonly long _started;
        private readonly long _allocated;

        internal Leaf(DeploymentLocationReadAttribution owner, Stage stage, bool synchronous)
        {
            _owner = owner;
            _stage = stage;
            _synchronous = synchronous;
            _allocated = synchronous ? GC.GetAllocatedBytesForCurrentThread() : 0;
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var elapsed = Stopwatch.GetTimestamp() - _started;
            var allocated = _synchronous ? GC.GetAllocatedBytesForCurrentThread() - _allocated : 0;
            ref var total = ref _owner._totals[(int)_stage];
            total.Count++;
            total.ElapsedTicks += elapsed;
            total.ThreadAllocatedBytes += allocated;
            total.Synchronous = _synchronous;
        }
    }
}
