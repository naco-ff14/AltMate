using AltMate;

internal static class MovementActionRegression
{
    internal static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Movement action sync: " + message);
        }
        Check(MovementActionSync.Classify(true, false, 2) == "jump", "general jump");
        Check(MovementActionSync.Classify(true, false, 4) == "sprint", "general sprint");
        Check(MovementActionSync.Classify(false, true, 3) == "sprint", "action sprint");
        Check(MovementActionSync.Classify(false, true, 2) is null, "unrelated action excluded");
        var relay = new MovementActionSync();
        Check(relay.TryMarkSent("sprint", 0), "first sprint");
        Check(!relay.TryMarkSent("sprint", 1), "nested general/action call deduplicated");
        Check(relay.TryMarkSent("jump", 1), "separate actions independent");
        Check(!relay.TryMarkSent("jump", 200), "jump observation does not repeat hook");
        Check(relay.TryMarkSent("jump", 251), "later jump accepted");
        Check(!relay.TryMarkSent("teleport", 500), "no arbitrary action forwarding");
        foreach (var interval in new[] { 16, 33 })
        {
            var observation = new MovementActionSync();
            Check(!observation.ObserveJump(false, 0, 10, 0), "baseline before jump");
            Check(!observation.ObserveJump(true, 0, 10, interval), "condition precedes height change");
            Check(observation.ObserveJump(true, 0.1f, 10, interval * 2), "jump detected at 30/60 FPS");
            Check(!observation.ObserveJump(true, 0.2f, 10, interval * 3), "airborne state not repeated");
            observation.ObserveJump(false, 0, 10, 1000);
            Check(!observation.ObserveJump(true, -0.1f, 10, 1033), "falling off ledge ignored");
            Check(!observation.ObserveJump(true, -0.2f, 10, 1066), "fall continues ignored");
            observation.ResetObservation();
            Check(!observation.ObserveJump(true, 0.5f, 10, 2000), "resuming in midair is baseline only");
            Check(!observation.ObserveJump(true, 0.7f, 10, 2033), "no resumed jump replay");
            Check(!observation.ObserveJump(true, 10, 11, 2066), "new area resets height baseline");
        }

        static bool CanReceive(bool enabled = true, bool linked = true, bool stopped = false,
            bool blocked = false, ulong source = 1, ulong leader = 1, ulong local = 2,
            uint territory = 10, string world = "Ridill", long sentAt = 10000, string kind = "jump") =>
            MovementActionSync.CanReceive(kind, enabled, linked, stopped, blocked,
                source, leader, local, territory, 10, world, "Ridill", sentAt, 10000);
        Check(CanReceive(), "selected leader event accepted");
        Check(CanReceive(kind: "sprint"), "sprint accepted");
        Check(CanReceive(world: "ridill"), "world matching ignores case");
        Check(!CanReceive(enabled: false), "individual option off");
        Check(!CanReceive(linked: false), "link off");
        Check(!CanReceive(stopped: true), "emergency stop");
        Check(!CanReceive(blocked: true), "casting/loading blocks action");
        Check(!CanReceive(source: 3), "non-leader ignored");
        Check(!CanReceive(leader: 3), "leader change discards old event");
        Check(!CanReceive(local: 1), "leader does not replay own event");
        Check(!CanReceive(territory: 11), "different territory ignored");
        Check(!CanReceive(world: "Ifrit"), "different world ignored");
        Check(!CanReceive(sentAt: 0), "old protocol lacks timestamp");
        Check(CanReceive(sentAt: 9250), "TTL boundary accepted");
        Check(!CanReceive(sentAt: 9249), "delayed event dropped");
        Check(!CanReceive(sentAt: 10101), "future timestamp ignored");
        Check(!CanReceive(kind: "emote"), "whitelist enforced");
        Console.WriteLine("PASS: jump/sprint classification, 30/60 FPS jump detection, falling exclusion, duplicate suppression, stop/leader/world/area guards, event expiry.");
    }
}
