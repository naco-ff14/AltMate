using System.Numerics;
using AltMate;

internal static class FollowRegression
{
    internal static void Run()
    {
        static void Check(bool value, string description)
        {
            if (!value) throw new Exception("FAILED: " + description);
        }
        long StuckAt(int fps)
        {
            var machine = new FollowStateMachine();
            for (var frame = 0; frame < fps * 5; frame++)
            {
                var time = (long)Math.Round(frame * 1000.0 / fps);
                if (machine.NeedsRecovery(new Vector3(1, 0, 1), 30, time)) return time;
            }
            throw new Exception("Stuck recovery never triggered");
        }
        Check(Math.Abs(StuckAt(30) - StuckAt(60)) <= 34, "30/60fps stuck detection uses elapsed time");
        var state = new FollowStateMachine();
        for (var t = 0; t <= 6000; t += 100)
            Check(!state.NeedsRecovery(new Vector3(t / 200f, 0, 0), 50 - t / 200f, t), "progress does not trigger recovery");
        state.ResetProgress();
        var divergence = false;
        for (var t = 0; t <= 4000; t += 100)
            divergence |= state.NeedsRecovery(new Vector3(t / 1000f, 0, 0), 20 + t / 500f, t);
        Check(divergence, "moving but falling behind triggers recovery");
        state.Enter(FollowState.Suspended);
        Check(!state.NeedsRecovery(Vector3.One, 30, 5000), "suspension resets stall evidence");
        Check(!state.NeedsRecovery(Vector3.One, 30, 15000), "long interruption resets stall evidence");
        Check(FollowStateMachine.ClassifyTransition(true, true, true, true, false) == FollowState.Aethernet, "aethernet has priority");
        Check(FollowStateMachine.ClassifyTransition(false, true, true, true, false) == FollowState.Teleport, "teleport before territory");
        Check(FollowStateMachine.ClassifyTransition(false, false, true, true, false) == FollowState.DutyTransition, "duty entry before boundary");
        Check(FollowStateMachine.ClassifyTransition(false, false, false, false, false) == FollowState.AwaitingLeader, "missing leader waits");
        var evidence = new AethernetEvidence();
        evidence.Observe(100, Vector3.One, 0);
        evidence.Observe(100, new Vector3(100, 1, 1), 100);
        evidence.Observe(100, new Vector3(100, 1, 1), 1100);
        Check(evidence.HasJump(2100), "jump survives delayed destination and IPC rejection");
        Check(evidence.Origin == Vector3.One && evidence.OriginTerritory == 100, "source retained for approach");
        Check(!evidence.HasJump(13000), "jump evidence expires");
        evidence.Reset();
        evidence.Observe(100, Vector3.One, 0);
        evidence.Observe(100, new Vector3(100, 1, 1), 10000);
        Check(!evidence.HasJump(10000), "stale samples never infer a transfer");
        evidence.Observe(101, Vector3.One, 10100);
        Check(evidence.HasJump(10100), "territory transition retained");
        evidence.Consume();
        Check(!evidence.HasJump(10100), "accepted transfer consumed");

        Vector3 PredictAt(int fps)
        {
            var controller = new FollowController();
            FollowDecision decision = default;
            for (var frame = 0; frame <= fps; frame++)
            {
                var time = (long)Math.Round(frame * 1000.0 / fps);
                decision = controller.Update(Vector3.Zero, new Vector3(0, 0, 10 + time * .006f), 0, 5, time);
            }
            return decision.Target;
        }
        Check(Vector3.Distance(PredictAt(30), PredictAt(60)) < .01f, "30/60fps prediction is stable");
        var follow = new FollowController();
        follow.Update(Vector3.Zero, new Vector3(0, 0, 10), 0, 5, 0);
        var teleported = follow.Update(Vector3.Zero, new Vector3(0, 0, 100), 0, 5, 100);
        Check(teleported.Target.Z == 95, "teleports are not extrapolated");
        follow.Reset();
        Check(!follow.Update(Vector3.Zero, new Vector3(0, 0, 5), 0, 5, 200).ShouldMove, "arrival stops movement");
        var config = new Configuration { FollowStartDistance = 8 };
        config.CharacterFollowOverrides[2] = new FollowOverrides { Distance = 6, Recovery = false };
        config.PairFollowOverrides[FollowOverrides.PairKey(2, 1)] = new FollowOverrides { Distance = 3 };
        Check(FollowOverrides.Resolve(config, 2, 1).Distance == 3, "pair beats character distance");
        Check(FollowOverrides.Resolve(config, 2, 1).Recovery == false, "unset pair field inherits character");
        Check(FollowOverrides.Resolve(config, 2, 3).Distance == 6, "other leader uses character defaults");
        Check(FollowOverrides.Resolve(config, 4, 1).Distance == 8, "other character uses global defaults");
        Console.WriteLine("PASS: follow timing, progress/divergence, transition priority, delayed Aethernet, prediction.");
    }
}
