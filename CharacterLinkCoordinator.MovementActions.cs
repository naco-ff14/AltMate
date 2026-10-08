using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AltMate;

public sealed partial class CharacterLinkCoordinator
{
    private readonly MovementActionSync movementActions = new();

    private void UpdateLeaderMovementObservation()
    {
        if (!IsLeader || runtimeStopped || !plugin.Configuration.LinkEnabled ||
            !plugin.Configuration.SyncJumpEnabled || IsBlocked() ||
            Plugin.ObjectTable.LocalPlayer is not { } local)
        {
            ResetMovementObservation();
            return;
        }
        // Some keyboard/controller jump inputs bypass the hotbar action path.
        // Observe upward movement at the start of jumping as a fallback. A
        // falling animation alone must not make every follower jump off a ledge.
        var jumping = Plugin.Condition[ConditionFlag.Jumping] || Plugin.Condition[ConditionFlag.Jumping61];
        if (movementActions.ObserveJump(jumping, local.Position.Y, Plugin.ClientState.TerritoryType,
                Environment.TickCount64))
            BroadcastMovementAction("jump");
    }

    private void ResetMovementObservation()
    {
        movementActions.ResetObservation();
    }

    private void BroadcastMovementAction(string? kind)
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
    }

    private unsafe void ExecuteLinkedMovementAction(LinkedCharacterState message)
    {
        var enabled = message.Kind == "jump" ? plugin.Configuration.SyncJumpEnabled : plugin.Configuration.SyncSprintEnabled;
        if (!IsLocalCharacterReady() || !MovementActionSync.CanReceive(message.Kind, enabled,
            plugin.Configuration.LinkEnabled, runtimeStopped, IsBlocked(),
            message.ContentId, plugin.Configuration.LinkLeaderContentId, Plugin.PlayerState.ContentId,
            message.TerritoryType, Plugin.ClientState.TerritoryType,
            message.WorldName, Plugin.PlayerState.CurrentWorld.Value.Name.ToString(),
            message.MovementActionSentAtUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            return;
        // Respect transfer/interaction ownership without altering any combat
        // plugin settings or interrupting casts. Unusable commands are dropped.
        if (Plugin.Condition[ConditionFlag.RidingPillion] || housingMovementActive ||
            DateTime.UtcNow < travelHandoffUntilUtc || IsLifestreamBusy() ||
            pendingInteractionTargetDataId != 0 || cityJumpConfirmed || residentialJumpConfirmed ||
            (!vnavRecoveryActive && IsVnavMovementRunning()))
            return;
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
                if (message.Mounted || Plugin.Condition[ConditionFlag.Mounted] ||
                    manager->AnimationLock > 0 ||
                    local.StatusList.Any(status => status.StatusId == 50) ||
                    manager->GetActionStatus(ActionType.GeneralAction, 4) != 0) return;
                accepted = manager->UseAction(ActionType.GeneralAction, 4);
            }
            if (accepted)
                LastAction = message.Kind == "jump" ? "リーダーに合わせてジャンプ" : "リーダーに合わせてスプリント";
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "リーダーのジャンプ・スプリントに合わせられませんでした。");
        }
    }
}
