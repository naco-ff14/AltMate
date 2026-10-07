using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;

namespace AltMate;

public sealed partial class CharacterLinkCoordinator
{
    private readonly DeepDungeonPullGate ddPullGate = new();
    private readonly FollowController ddRetreatFollow = new();
    private bool ddPullWaiting;

    private void UpdateDeepDungeonPull(LinkedCharacterState leader)
    {
        var enabled = Plugin.Condition[ConditionFlag.InDeepDungeon] && plugin.Configuration.CombatLinkEnabled;
        var combat = leader.InCombat || Plugin.Condition[ConditionFlag.InCombat];
        var arrived = false;
        if (enabled && combat && TryGetLeaderObject(out _, out var leaderObject))
        {
            // Count only living combatants engaging our party (or the leader's selected enemy),
            // never a passive enemy standing near the pulling position.
            var allies = Plugin.PartyList.Select(x => x.EntityId).Select(x => (ulong)x).ToHashSet();
            allies.Add(leaderObject.GameObjectId);
            if (Plugin.ObjectTable.LocalPlayer is { } local) allies.Add(local.GameObjectId);
            arrived = Plugin.ObjectTable.OfType<IBattleNpc>().Any(enemy =>
                !enemy.IsDead && enemy.IsTargetable &&
                (enemy.StatusFlags & StatusFlags.InCombat) != 0 &&
                (allies.Contains(enemy.TargetObjectId) || leaderObject.TargetObjectId == enemy.GameObjectId) &&
                Vector3.Distance(enemy.Position, leaderObject.Position) <= 5f);
        }
        ddPullWaiting = ddPullGate.Update(enabled, combat, arrived, leader.ContentId, Plugin.ClientState.TerritoryType);
        if (!ddPullWaiting) ddRetreatFollow.Reset();
    }

    private void FollowDuringDeepDungeonPull()
    {
        StopBmrFollow();
        StopVnavRecovery();
        if (IsBlocked() || plugin.Configuration.PauseLinkInCombat ||
            Plugin.Condition[ConditionFlag.Casting] || Plugin.Condition[ConditionFlag.Casting87] ||
            Plugin.ObjectTable.LocalPlayer is not { } local ||
            !TryGetLeaderObject(out _, out var leaderObject))
        {
            smoothFollow.Stop();
            LastAction = "DD釣り待ち：戦闘接近を保留";
            return;
        }
        // Stay within the leader's radius without circling to the front/back on rotation.
        var follow = ddRetreatFollow.Update(local.Position, leaderObject.Position, 0,
            EffectiveFollow.Distance ?? 5f, nearby: true);
        if (follow.ShouldMove) smoothFollow.Follow(follow.Direction, follow.Strength);
        else smoothFollow.Stop();
        LastAction = "DD釣り待ち：敵がリーダーの5m以内へ来るまで待機";
    }
}
