using System;
namespace AltMate;
internal sealed class DeepDungeonPullGate
{
    private long? started;
    private bool released;
    private ulong leader;
    private uint territory;
    internal double RemainingSeconds { get; private set; }
    internal bool Update(bool enabled, bool inCombat, float seconds, ulong leaderId, uint zone, long now)
    {
        if (leader != leaderId || territory != zone || !enabled || !inCombat)
        { started = null; released = false; }
        leader = leaderId;
        territory = zone;
        RemainingSeconds = 0;
        if (!enabled || !inCombat || released) return false;
        started ??= now;
        var delay = float.IsFinite(seconds) ? Math.Clamp(seconds, 0, 15) : 3;
        RemainingSeconds = Math.Max(0, delay - (now - started.Value) / 1000.0);
        if (RemainingSeconds == 0) released = true;
        return !released;
    }
}
