namespace HVO.SkyMonitor.Astronomy;

/// <summary>One query's monotonic work counters; owned by its sequential numerical execution.</summary>
internal sealed class OrbitalWorkBudget
{
    private readonly OrbitalQueryBounds bounds;
    private readonly CancellationToken cancellationToken;
    private readonly TimeProvider time;
    private readonly long started;
    private OrbitalPredictionReason reason;

    internal OrbitalWorkBudget(OrbitalQueryBounds bounds, CancellationToken cancellationToken, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        bounds.Validate();
        this.bounds = bounds;
        this.cancellationToken = cancellationToken;
        this.time = time ?? TimeProvider.System;
        started = this.time.GetTimestamp();
    }

    internal int Evaluations { get; private set; }
    internal int Samples { get; private set; }
    internal int Tracks { get; private set; }
    internal int Passes { get; private set; }
    internal double ElapsedMilliseconds => time.GetElapsedTime(started, time.GetTimestamp()).TotalMilliseconds;
    internal OrbitalPredictionReason Reason
    {
        get
        {
            // Deadline takes precedence if final preparation outlives an earlier work limit.
            if (ElapsedMilliseconds > bounds.MaximumElapsedMilliseconds) reason = OrbitalPredictionReason.Deadline;
            return reason;
        }
    }

    internal bool CanContinue()
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Reason == OrbitalPredictionReason.None;
    }

    internal bool TryEvaluation()
    {
        if (!CanContinue()) return false;
        if (Evaluations == bounds.MaximumEvaluations) return Stop(OrbitalPredictionReason.EvaluationBudget);
        Evaluations++;
        return true;
    }

    internal bool TrySample()
    {
        if (!CanContinue()) return false;
        if (Samples == bounds.MaximumSamples) return Stop(OrbitalPredictionReason.SampleBudget);
        Samples++;
        return true;
    }

    internal bool TryTrack()
    {
        if (!CanContinue()) return false;
        if (Tracks == bounds.MaximumTracks) return Stop(OrbitalPredictionReason.ResultBudget);
        Tracks++;
        return true;
    }

    internal bool TryPass()
    {
        if (!CanContinue()) return false;
        if (Passes == bounds.MaximumPasses) return Stop(OrbitalPredictionReason.ResultBudget);
        Passes++;
        return true;
    }

    /// <summary>Sequential owner returns an output reservation when no product was created.</summary>
    internal void ReleaseUnusedTrack()
    {
        if (Tracks == 0) throw new InvalidOperationException("No track reservation can be released.");
        Tracks--;
    }

    private bool Stop(OrbitalPredictionReason failure)
    {
        reason = failure;
        return false;
    }
}
