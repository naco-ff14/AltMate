using AltMate;

internal static class CombatRegression
{
    internal static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAILED: " + message);
        }
        foreach (var role in Enum.GetValues<BossModCombatRole>())
        for (var flags = 0; flags < 64; flags++)
        {
            bool Flag(int bit) => (flags & (1 << bit)) != 0;
            var plan = CombatAutomationPlan.Resolve(Flag(0), Flag(1), role,
                Flag(2), Flag(3), Flag(4), Flag(5));
            Check(!(plan.BossModMovement && plan.RotationSolver), "one rotation owner without relying on ForbidActions");
            Check(!Flag(4) || (!plan.BossModMovement && !plan.RotationSolver), "Wrath owns actions");
            Check(plan.ForbidActions is null || plan.BossModMovement, "setting change requires BMR ownership");
            Check(role != BossModCombatRole.PreserveExisting || plan.ForbidActions is null, "default never changes ForbidActions");
            Check(!plan.BossModMovement || role != BossModCombatRole.MovementOnly || plan.ForbidActions == true, "explicit movement role");
            Check(!plan.BossModMovement || role != BossModCombatRole.MovementAndActions || plan.ForbidActions == false, "explicit actions role");
        }
        Check(CombatAutomationPlan.Resolve(true, true, BossModCombatRole.PreserveExisting, true, false, false, true)
            == new CombatAutomationPlan(true, null, false), "BMR existing settings preserved");
        Check(CombatAutomationPlan.Resolve(true, true, BossModCombatRole.MovementAndActions, true, true, false, true)
            == new CombatAutomationPlan(true, false, false), "explicit BMR actions suppress RSR");
        Check(CombatAutomationPlan.Resolve(true, true, BossModCombatRole.MovementOnly, true, true, false, false)
            == new CombatAutomationPlan(false, null, true), "RSR uses AltMate movement without changing BMR");

        var setting = new TemporaryBooleanSetting();
        bool? current = false;
        var writes = 0;
        bool Write(bool value) { writes++; current = value; return true; }
        Check(setting.Acquire(null, () => throw new Exception("default read"), _ => throw new Exception("default write")), "default does not touch BMR");
        Check(setting.Acquire(true, () => current, Write) && current == true, "explicit movement temporarily sets ON");
        Check(setting.Release(() => current, Write) && current == false && writes == 2, "OFF restored after stop");
        current = true;
        Check(setting.Acquire(false, () => current, Write), "explicit actions temporarily sets OFF");
        Check(setting.Release(() => current, Write) && current == true, "original ON restored too");
        current = false;
        Check(setting.Acquire(true, () => current, Write), "acquire for manual edit test");
        current = false;
        var before = writes;
        Check(setting.Release(() => current, Write) && writes == before, "manual edit is not overwritten");
        Check(!setting.Acquire(true, () => null, Write) && writes == before, "unreadable state causes no write");
        Check(setting.Acquire(true, () => current, Write), "acquire for unavailable restore");
        Check(!setting.Release(() => null, Write) && setting.Pending, "failed read retains restoration record");
        Check(setting.Release(() => current, Write) && current == false, "restoration can retry");
        Check(!setting.Acquire(true, () => current, _ => false), "rejected change fails acquisition");
        Check(setting.Release(() => current, Write), "unchanged rejected setting safely released");
        Check(setting.Acquire(true, () => current, Write), "acquire for write failure");
        Check(!setting.Release(() => current, _ => false) && setting.Pending, "failed restoration write retained");
        Check(!setting.Acquire(null, () => null, Write), "pending restoration blocks a new session");
        Check(setting.Release(() => current, Write), "retry clears pending restoration");
        Console.WriteLine("PASS: 192 combat role combinations, default preservation, exclusive engines, temporary change and restoration failures/manual edits.");
    }
}
