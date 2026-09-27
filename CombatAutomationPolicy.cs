namespace AltMate;

public enum BossModCombatRole
{
    PreserveExisting,
    MovementOnly,
    MovementAndActions,
}

internal readonly record struct CombatAutomationPlan(bool BossModMovement, bool? ForbidActions, bool RotationSolver)
{
    internal static CombatAutomationPlan Resolve(bool useBossMod, bool bossModLoaded,
        BossModCombatRole role, bool useRsr, bool rsrLoaded, bool wrathActive, bool caster)
    {
        var bmr = useBossMod && bossModLoaded;
        // Preserved BMR settings can execute actions. Never start another rotation beside it.
        var rsr = useRsr && rsrLoaded && !wrathActive &&
            (!bmr || role == BossModCombatRole.MovementOnly);
        // ForbidActions is not an exclusive rotation lock; external rotations use AltMate movement.
        var movement = bmr && !rsr && !wrathActive;
        bool? forbid = movement ? role switch
        {
            BossModCombatRole.MovementOnly => true,
            BossModCombatRole.MovementAndActions => false,
            _ => null,
        } : null;
        return new(movement, forbid, rsr);
    }
}
