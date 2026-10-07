using System;
using Dalamud.Game.ClientState.Conditions;
namespace AltMate;
public sealed partial class CharacterLinkCoordinator
{
    private readonly DeepDungeonPullGate ddPullGate = new();
    private bool ddPullWaiting;
    private void UpdateDeepDungeonPull(LinkedCharacterState leader)
    {
        ddPullWaiting = ddPullGate.Update(
            Plugin.Condition[ConditionFlag.InDeepDungeon] && plugin.Configuration.CombatLinkEnabled,
            leader.InCombat || Plugin.Condition[ConditionFlag.InCombat],
            plugin.Configuration.DeepDungeonCombatDelaySeconds,
            leader.ContentId, Plugin.ClientState.TerritoryType, Environment.TickCount64);
    }
}
