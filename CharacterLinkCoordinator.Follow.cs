using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;

namespace AltMate;

public sealed partial class CharacterLinkCoordinator
{
    private CombatAutomationPlan ResolveCombatPlan() => CombatAutomationPlan.Resolve(
        plugin.Configuration.UseBossModReborn, IsPluginLoaded("BossModReborn"), plugin.Configuration.BossModRole,
        plugin.Configuration.UseRotationSolverReborn,
        IsPluginLoaded("RotationSolver") || IsPluginLoaded("RotationSolverReborn"),
        IsWrathRotationActive(), IsCastingJob(Plugin.PlayerState.ClassJob.RowId));

    private void CompleteFollowPath()
    {
        var task = pendingFollowPath;
        if (task is null || !task.IsCompleted) return;
        pendingFollowPath = null;
        try
        {
            var path = task.GetAwaiter().GetResult();
            if (!vnavRecoveryActive || path.Count == 0 || followPathCancellation?.IsCancellationRequested != false)
                return;
            if (!followPathIssued && IsVnavMovementRunning())
            {
                // Another caller acquired navigation while our path was being calculated.
                vnavRecoveryActive = false;
                return;
            }
            Plugin.PluginInterface.GetIpcSubscriber<System.Collections.Generic.List<Vector3>, bool, object>("vnavmesh.Path.MoveTo")
                .InvokeAction(path, followPathFlying);
            followPathIssued = true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "追従経路の計算を完了できませんでした。");
        }
    }

    private bool combatRecovery;
    private long combatRecoveryStartedAt;

    private bool UpdateCombatFollowRecovery(LinkedCharacterState leader, DateTime now)
    {
        if (!combatBmrActive && !combatRecovery) return false;
        smoothFollow.Stop();
        if (!TryGetLeaderObject(out _, out var target) || Plugin.ObjectTable.LocalPlayer is not { } local)
        {
            StopVnavRecovery();
            followState.Enter(FollowState.AwaitingLeader);
            return true;
        }
        var distance = Vector3.Distance(local.Position, target.Position);
        var tick = Environment.TickCount64;
        if (Plugin.Condition[ConditionFlag.Casting] || Plugin.Condition[ConditionFlag.Casting87])
        {
            StopVnavRecovery();
            followState.ResetProgress();
            return true;
        }
        // Close-range positioning belongs to combat AI; only recover a separated follower.
        if (combatRecovery && (distance <= 20f || tick - combatRecoveryStartedAt >= 15000))
        {
            StopVnavRecovery();
            combatRecovery = false;
            followState.ResetProgress();
            backendRetryAt = tick + 3000;
            if (IsPluginLoaded("BossModReborn") && plugin.Configuration.UseBossModReborn)
                combatBmrActive = Plugin.CommandManager.ProcessCommand("/bmrai on");
            return true;
        }
        if (!combatRecovery && (distance <= 20f || EffectiveFollow.Recovery != true ||
            !IsVnavmeshLoaded || tick < backendRetryAt))
        {
            followState.ResetProgress();
            return true;
        }
        if (!combatRecovery && followState.NeedsRecovery(local.Position, distance, tick))
        {
            if (!Plugin.CommandManager.ProcessCommand("/bmrai off")) return true;
            combatBmrActive = false;
            combatRecovery = true;
            combatRecoveryStartedAt = tick;
        }
        if (combatRecovery)
        {
            followState.Enter(FollowState.Recovery);
            RequestVnavRecovery(target.Position, 18f, now);
            LastAction = "BMRの移動停滞を検出・vnavmeshで戦闘位置へ復帰中";
        }
        return true;
    }

    private long lastWrathCheck;
    private bool wrathRotationActive;
    private bool IsWrathRotationActive()
    {
        if (!IsPluginLoaded("WrathCombo")) return false;
        var now = Environment.TickCount64;
        if (now - lastWrathCheck < 1000) return wrathRotationActive;
        lastWrathCheck = now;
        try
        {
            wrathRotationActive = Plugin.PluginInterface
                .GetIpcSubscriber<bool>("WrathCombo.GetAutoRotationState").InvokeFunc();
        }
        catch
        {
            // Unknown rotation ownership must not start a competing engine.
            wrathRotationActive = true;
        }
        return wrathRotationActive;
    }

    private FollowOverrides EffectiveFollow => FollowOverrides.Resolve(plugin.Configuration,
        Plugin.PlayerState.ContentId, plugin.Configuration.LinkLeaderContentId);

    private void ReleaseFollowMovement()
    {
        smoothFollow.Stop();
        StopBmrFollow();
        StopVnavRecovery();
        followController.Reset();
        followState.ResetProgress();
    }

    private bool TryStartBmrFollow(LinkedCharacterState leader, DateTime now)
    {
        if (plugin.Configuration.FollowNearby || EffectiveFollow.PreferBossMod != true || !IsPluginLoaded("BossModReborn") ||
            combatAutomationActive || vnavRecoveryActive || IsVnavMovementRunning() ||
            Environment.TickCount64 < backendRetryAt)
        {
            StopBmrFollow();
            return false;
        }
        if (followBmrOwned) return true;
        if (IsWrathRotationActive() || rotationOwned) return false;
        var temporaryForbid = plugin.Configuration.BossModRole == BossModCombatRole.MovementOnly ? (bool?)true : null;
        if (!PrepareBmrActions(temporaryForbid))
        {
            backendRetryAt = Environment.TickCount64 + 3000;
            return false;
        }
        try
        {
            Plugin.CommandManager.ProcessCommand($"/bmrai follow {leader.CharacterName}");
            Plugin.CommandManager.ProcessCommand("/bmrai followoutofcombat on");
            followBmrOwned = Plugin.CommandManager.ProcessCommand("/bmrai on");
            if (!followBmrOwned)
            {
                RestoreBmrActions();
                backendRetryAt = Environment.TickCount64 + 3000;
            }
        }
        catch (Exception exception)
        {
            RestoreBmrActions();
            Plugin.Log.Verbose(exception, "BMR追従の開始に失敗しました。");
            backendRetryAt = Environment.TickCount64 + 3000;
        }
        return followBmrOwned;
    }

    private void StopBmrFollow()
    {
        if (!followBmrOwned) return;
        try
        {
            if (IsPluginLoaded("BossModReborn"))
                Plugin.CommandManager.ProcessCommand("/bmrai off");
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "BMR追従の停止に失敗しました。");
        }
        followBmrOwned = false;
        RestoreBmrActions();
    }
}
