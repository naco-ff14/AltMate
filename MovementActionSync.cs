using System;

namespace AltMate;

// Movement commands are one-shot events. Never retain one for a later cooldown
// or replay it after loading, resuming, or changing the selected leader.
internal sealed class MovementActionSync
{
    internal const long MaximumAgeMilliseconds = 750;
    private long lastJumpAt = long.MinValue;
    private long lastSprintAt = long.MinValue;
    private bool jumpObservationReady;
    private bool wasJumping;
    private float previousHeight;
    private long jumpObservationUntil;
    private uint observedTerritory;

    internal bool ObserveJump(bool jumping, float height, uint territory, long now)
    {
        if (!float.IsFinite(height) || territory == 0)
        {
            ResetObservation();
            return false;
        }
        if (!jumpObservationReady || observedTerritory != territory)
        {
            jumpObservationReady = true;
            observedTerritory = territory;
            wasJumping = jumping;
            previousHeight = height;
            jumpObservationUntil = 0;
            return false;
        }
        if (jumping && !wasJumping) jumpObservationUntil = now + 150;
        var startedJump = jumping && jumpObservationUntil > now && height > previousHeight + 0.01f;
        if (startedJump || !jumping) jumpObservationUntil = 0;
        wasJumping = jumping;
        previousHeight = height;
        return startedJump;
    }

    internal void ResetObservation()
    {
        jumpObservationReady = false;
        jumpObservationUntil = 0;
    }

    internal static string? Classify(bool generalAction, bool regularAction, uint id) =>
        generalAction && id == 2 ? "jump" :
        (generalAction && id == 4) || (regularAction && id == 3) ? "sprint" : null;

    internal bool TryMarkSent(string kind, long now)
    {
        if (kind is not ("jump" or "sprint")) return false;
        ref var last = ref (kind == "jump" ? ref lastJumpAt : ref lastSprintAt);
        // General Sprint can invoke Action 3 inside UseAction. Both hooks may
        // report the same accepted action; emit a single command for that press.
        if (last != long.MinValue && now - last < 250) return false;
        last = now;
        return true;
    }

    internal static bool CanReceive(string kind, bool enabled, bool linkEnabled, bool stopped,
        bool blocked, ulong sourceId, ulong leaderId, ulong localId,
        uint sourceTerritory, uint localTerritory, string sourceWorld, string localWorld,
        long sentAt, long now) =>
        kind is "jump" or "sprint" && enabled && linkEnabled && !stopped && !blocked &&
        sourceId != 0 && sourceId == leaderId && sourceId != localId &&
        sourceTerritory != 0 && sourceTerritory == localTerritory &&
        !string.IsNullOrWhiteSpace(sourceWorld) &&
        sourceWorld.Equals(localWorld, StringComparison.OrdinalIgnoreCase) &&
        sentAt > 0 && sentAt <= now + 100 && sentAt >= now - MaximumAgeMilliseconds;
}
