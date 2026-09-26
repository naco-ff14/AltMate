using System;
using System.Numerics;

namespace AltMate;

internal enum FollowState
{
    Idle, Following, CatchUp, Recovery, AwaitingLeader, Aethernet, Teleport,
    TerritoryTransition, DutyTransition, Suspended, Mounted, Flying,
}

// No game or IPC dependencies: elapsed time and observations drive every decision.
internal sealed class FollowStateMachine
{
    internal FollowState State { get; private set; }
    private long sampleAt = -1;
    private Vector3 samplePosition;
    private float sampleDistance;
    private long stalledAt = -1;
    private long divergingAt = -1;

    internal void Enter(FollowState state)
    {
        State = state;
        if (state is not (FollowState.Following or FollowState.CatchUp or FollowState.Recovery))
            ResetProgress();
    }

    internal bool NeedsRecovery(Vector3 position, float distance, long now)
    {
        if (!float.IsFinite(distance) || !float.IsFinite(position.LengthSquared()) || distance <= 2)
        {
            ResetProgress();
            return false;
        }
        if (sampleAt < 0 || now < sampleAt || now - sampleAt > 3000)
        {
            ResetProgress();
            Sample(position, distance, now);
            return false;
        }
        if (now - sampleAt < 500) return false;
        var elapsed = (now - sampleAt) / 1000f;
        var speed = Vector3.Distance(position, samplePosition) / elapsed;
        var growing = (distance - sampleDistance) / elapsed > 1f;
        stalledAt = speed < 0.3f ? (stalledAt < 0 ? sampleAt : stalledAt) : -1;
        divergingAt = growing ? (divergingAt < 0 ? sampleAt : divergingAt) : -1;
        Sample(position, distance, now);
        return (stalledAt >= 0 && now - stalledAt >= 2000) ||
               (distance >= 15 && divergingAt >= 0 && now - divergingAt >= 2500);
    }

    internal void ResetProgress()
    {
        sampleAt = stalledAt = divergingAt = -1;
    }

    private void Sample(Vector3 position, float distance, long now)
    {
        sampleAt = now;
        samplePosition = position;
        sampleDistance = distance;
    }

    internal static FollowState ClassifyTransition(bool aethernet, bool teleport,
        bool duty, bool territory, bool visible) =>
        aethernet ? FollowState.Aethernet : teleport ? FollowState.Teleport :
        duty ? FollowState.DutyTransition : territory ? FollowState.TerritoryTransition :
        !visible ? FollowState.AwaitingLeader : FollowState.Following;
}

// A jump and the destination ID can arrive in different network packets.
internal sealed class AethernetEvidence
{
    private long jumpUntil;
    private uint territory;
    private Vector3 previous;
    private long observedAt = -1;
    internal Vector3 Origin { get; private set; }
    internal uint OriginTerritory { get; private set; }
    internal void Observe(uint zone, Vector3 position, long now)
    {
        if (!float.IsFinite(position.LengthSquared())) { Reset(); return; }
        if (observedAt >= 0 && now >= observedAt && now - observedAt <= 3000 &&
            (zone != territory || Vector3.Distance(position, previous) >= 12))
        {
            Origin = previous;
            OriginTerritory = territory;
            jumpUntil = now + 12000;
        }
        previous = position;
        territory = zone;
        observedAt = now;
    }
    internal bool HasJump(long now) => jumpUntil > now;
    internal void Reset() { observedAt = -1; jumpUntil = 0; }
    internal void Consume() => jumpUntil = 0;
}
