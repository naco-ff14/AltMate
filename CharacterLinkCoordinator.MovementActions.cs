using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AltMate;

public sealed partial class CharacterLinkCoordinator
{
    private readonly MovementActionSync movementActions = new();
    public string MovementSyncStatus { get; private set; } = "ジャンプ・スプリント連携待ち";

    private void UpdateLeaderMovementObservation()
    {
        if (!IsLeader || runtimeStopped || !plugin.Configuration.LinkEnabled ||
            IsBlocked() ||
            Plugin.ObjectTable.LocalPlayer is not { } local)
        {
            ResetMovementObservation();
            return;
        }
        // Some keyboard/controller jump inputs bypass the hotbar action path.
        // Observe upward movement at the start of jumping as a fallback. A
        // falling animation alone must not make every follower jump off a ledge.
        var jumping = Plugin.Condition[ConditionFlag.Jumping] || Plugin.Condition[ConditionFlag.Jumping61];
        if (plugin.Configuration.SyncJumpEnabled)
        {
            if (movementActions.ObserveJump(jumping, local.Position.Y, Plugin.ClientState.TerritoryType,
                Environment.TickCount64))
                BroadcastMovementAction("jump");
        }
        else movementActions.ResetJumpObservation();

        // BMR may consume a hotbar request into its manual queue and return
        // false, then execute through UseActionLocation rather than UseAction.
        // Observe the actual Sprint effect as well, independently of jump sync.
        if (plugin.Configuration.SyncSprintEnabled)
        {
            if (movementActions.ObserveSprint(local.StatusList.Any(status => status.StatusId == 50),
                    Plugin.ClientState.TerritoryType))
                BroadcastMovementAction("sprint", "効果検出");
        }
        else movementActions.ResetSprintObservation();
    }

    private void ResetMovementObservation()
    {
        movementActions.ResetObservation();
    }

    private void BroadcastMovementAction(string? kind, string source = "操作監視")
    {
        if (kind is null || sender is null || !IsLocalCharacterReady() ||
            !plugin.Configuration.LinkEnabled || runtimeStopped || !IsLeader || IsBlocked() ||
            (kind == "jump" ? !plugin.Configuration.SyncJumpEnabled : !plugin.Configuration.SyncSprintEnabled))
            return;
        if (!movementActions.TryMarkSent(kind, Environment.TickCount64)) return;
        var message = new LinkedCharacterState
        {
            Protocol = CurrentProtocol,
            Sequence = NextSequence(),
            LinkKey = plugin.Configuration.LocalLinkKey,
            Kind = kind,
            ContentId = Plugin.PlayerState.ContentId,
            WorldName = Plugin.PlayerState.CurrentWorld.Value.Name.ToString(),
            TerritoryType = Plugin.ClientState.TerritoryType,
            Mounted = Plugin.Condition[ConditionFlag.Mounted],
            MovementActionSentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message);
        lock (senderLock)
        {
            // The same sequence makes local UDP repeats idempotent at reception.
            for (var i = 0; i < 3; i++)
                sender?.Send(bytes, bytes.Length, new System.Net.IPEndPoint(Group, Port));
        }
        SetMovementSyncStatus(kind, $"リーダーの{(kind == "jump" ? "ジャンプ" : "スプリント")}を送信（{source}）");
    }

    private void SetMovementSyncStatus(string kind, string status)
    {
        MovementSyncStatus = status;
        if (kind == "sprint") Plugin.Log.Information($"[AltMate MovementSync] {status}");
    }

    private unsafe void ExecuteLinkedMovementAction(LinkedCharacterState message)
    {
        var enabled = message.Kind == "jump" ? plugin.Configuration.SyncJumpEnabled : plugin.Configuration.SyncSprintEnabled;
        if (message.ContentId == Plugin.PlayerState.ContentId ||
            message.ContentId != plugin.Configuration.LinkLeaderContentId) return;
        if (!IsLocalCharacterReady() || !MovementActionSync.CanReceive(message.Kind, enabled,
            plugin.Configuration.LinkEnabled, runtimeStopped, IsBlocked(),
            message.ContentId, plugin.Configuration.LinkLeaderContentId, Plugin.PlayerState.ContentId,
            message.TerritoryType, Plugin.ClientState.TerritoryType,
            message.WorldName, Plugin.PlayerState.CurrentWorld.Value.Name.ToString(),
            message.MovementActionSentAtUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            SetMovementSyncStatus(message.Kind, "移動アクションを見送り：連携設定・停止・操作不可・別エリア・通知期限を確認してください");
            return;
        }
        // Respect transfer/interaction ownership without altering any combat
        // plugin settings or interrupting casts. Unusable commands are dropped.
        if (Plugin.Condition[ConditionFlag.RidingPillion] || housingMovementActive ||
            DateTime.UtcNow < travelHandoffUntilUtc || IsLifestreamBusy() ||
            pendingInteractionTargetDataId != 0 || cityJumpConfirmed || residentialJumpConfirmed ||
            (!vnavRecoveryActive && IsVnavMovementRunning()))
        {
            SetMovementSyncStatus(message.Kind, "移動アクションを見送り：相乗り・移動連携・他の自動移動が処理中");
            return;
        }
        var local = Plugin.ObjectTable.LocalPlayer;
        var manager = ActionManager.Instance();
        if (local is null || local.Address == nint.Zero || manager == null) return;
        try
        {
            bool accepted;
            if (message.Kind == "jump")
            {
                if (message.Mounted != Plugin.Condition[ConditionFlag.Mounted] ||
                    ((Character*)local.Address)->IsJumping()) return;
                // Jump also serves as takeoff/ascent while mounted.
                accepted = manager->UseAction(ActionType.GeneralAction, 2);
            }
            else
            {
                if (message.Mounted || Plugin.Condition[ConditionFlag.Mounted])
                {
                    SetMovementSyncStatus("sprint", "スプリントを見送り：マウント中");
                    return;
                }
                if (local.StatusList.Any(status => status.StatusId == 50))
                {
                    SetMovementSyncStatus("sprint", "スプリントを見送り：すでに効果中");
                    return;
                }
                var sprintActionId = ActionManager.GetSpellIdForAction(ActionType.GeneralAction, 4);
                var actionStatus = manager->GetActionStatus(ActionType.Action, sprintActionId, local.GameObjectId);
                if (manager->AnimationLock > 0 || sprintActionId == 0 || actionStatus != 0)
                {
                    SetMovementSyncStatus("sprint", $"スプリントを見送り：使用不可（状態 {actionStatus}、硬直 {manager->AnimationLock:F2}s）");
                    return;
                }
                // Already checked availability: use the immediate execution
                // mode so BMR's manual queue cannot swallow this relay request.
                accepted = manager->UseAction(ActionType.GeneralAction, 4, local.GameObjectId,
                    mode: ActionManager.UseActionMode.Queue);
            }
            if (accepted)
            {
                LastAction = message.Kind == "jump" ? "リーダーに合わせてジャンプ" : "リーダーに合わせてスプリント";
                SetMovementSyncStatus(message.Kind, LastAction);
            }
            else SetMovementSyncStatus(message.Kind, "移動アクションを見送り：ゲームまたは他プラグインが要求を受け付けませんでした");
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "リーダーのジャンプ・スプリントに合わせられませんでした。");
        }
    }
}
