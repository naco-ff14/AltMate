using System;
using System.Numerics;

namespace AltMate;

internal enum TravelJobKind
{
    OccultAethernet,
    CityAethernet,
    ZoneBoundary,
}

internal enum TravelJobState
{
    Pending,
    Running,
}

internal sealed record TravelJob(
    TravelJobKind Kind,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    uint DestinationId = 0,
    uint SourceTerritory = 0,
    uint TargetTerritory = 0,
    Vector3 TargetPosition = default)
{
    internal TravelJobState State { get; set; } = TravelJobState.Pending;
    internal int Attempts { get; set; }
}
