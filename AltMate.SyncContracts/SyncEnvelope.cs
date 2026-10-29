#pragma warning disable NoireLib_002 // Explicit wire/DPAPI JSON contract, not Dalamud configuration serialization.
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AltMate.SyncContracts;

public sealed record SyncEnvelope(
    int SchemaVersion,
    Guid DeviceId,
    long Revision,
    Guid EventId,
    DateTimeOffset CapturedAt,
    IReadOnlyList<EntityChange> Changes);

public sealed record EntityChange(
    string EntityType,
    string EntityKey,
    DateTimeOffset ObservedAt,
    JsonElement Data);

public static class SyncContract
{
    public const int CurrentSchemaVersion = 1;
    public static readonly TimeSpan MinimumSyncInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
}
