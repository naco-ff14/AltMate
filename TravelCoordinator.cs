using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

/// <summary>
/// Arbitrates linked travel work. Only the highest-priority live job may move
/// the follower: Occult Aethernet, then city Aethernet, then a zone boundary.
/// </summary>
internal sealed class TravelCoordinator
{
    private readonly Dictionary<TravelJobKind, TravelJob> jobs = new();

    internal TravelJob? ActiveJob { get; private set; }

    internal void Schedule(TravelJob job)
    {
        jobs[job.Kind] = job;
        SelectActive(DateTime.UtcNow);
    }

    internal bool CanRun(TravelJobKind kind, DateTime now)
    {
        SelectActive(now);
        var active = ActiveJob;
        if (active?.Kind != kind)
            return false;
        active.State = TravelJobState.Running;
        return true;
    }

    internal void RecordAttempt(TravelJobKind kind)
    {
        if (jobs.TryGetValue(kind, out var job))
            job.Attempts++;
    }

    internal void Complete(TravelJobKind kind)
    {
        jobs.Remove(kind);
        ActiveJob = null;
        SelectActive(DateTime.UtcNow);
    }

    internal void Cancel(TravelJobKind kind)
    {
        jobs.Remove(kind);
        if (ActiveJob?.Kind == kind)
            ActiveJob = null;
        SelectActive(DateTime.UtcNow);
    }

    internal void Reset()
    {
        jobs.Clear();
        ActiveJob = null;
    }

    private void SelectActive(DateTime now)
    {
        foreach (var expired in jobs.Where(x => now > x.Value.ExpiresAtUtc)
                     .Select(x => x.Key).ToArray())
            jobs.Remove(expired);

        ActiveJob = jobs.Values
            .OrderBy(x => Priority(x.Kind))
            .ThenBy(x => x.CreatedAtUtc)
            .FirstOrDefault();
    }

    private static int Priority(TravelJobKind kind) => kind switch
    {
        TravelJobKind.OccultAethernet => 0,
        TravelJobKind.CityAethernet => 1,
        TravelJobKind.ZoneBoundary => 2,
        _ => int.MaxValue,
    };
}
