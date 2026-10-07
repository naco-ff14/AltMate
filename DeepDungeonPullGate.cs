namespace AltMate;

internal sealed class DeepDungeonPullGate
{
    internal static bool AllowLeaderFollow(bool waiting, bool safetyBlocked) => waiting && !safetyBlocked;
    private bool released;
    private ulong leader;
    private uint territory;
    internal bool Update(bool enabled, bool inCombat, bool enemyArrived, ulong leaderId, uint zone)
    {
        if (leader != leaderId || territory != zone || !enabled || !inCombat) released = false;
        leader = leaderId;
        territory = zone;
        if (!enabled || !inCombat) return false;
        released |= enemyArrived;
        return !released;
    }
}
