using Dalamud.Bindings.ImGui;

namespace AltMate;

public sealed partial class MainWindow
{
    private void DrawFollowOverrides()
    {
        if (!ImGui.CollapsingHeader(Loc.L("キャラクター・ペア別の追従設定", "Character / pair follow overrides"))) return;
        var id = Plugin.PlayerState.ContentId;
        var leader = plugin.Configuration.LinkLeaderContentId;
        if (id == 0) return;
        DrawFollowOverride(false, id, leader);
        if (leader != 0 && leader != id) DrawFollowOverride(true, id, leader);
        ImGui.TextDisabled(Loc.L("優先順：A-Bペア → このキャラクター → 共通設定", "Priority: pair > character > global"));
    }

    private void DrawFollowOverride(bool pairScope, ulong id, ulong leader)
    {
        var config = plugin.Configuration;
        var key = FollowOverrides.PairKey(id, leader);
        FollowOverrides? profile;
        var enabled = pairScope ? config.PairFollowOverrides.TryGetValue(key, out profile)
            : config.CharacterFollowOverrides.TryGetValue(id, out profile);
        ImGui.PushID(pairScope ? "pair-follow" : "character-follow");
        var changed = ImGui.Checkbox(pairScope ? Loc.L("このA-Bペアを個別設定", "Override this pair")
            : Loc.L("このキャラクターを個別設定", "Override this character"), ref enabled);
        if (changed)
        {
            if (enabled)
            {
                profile = FollowOverrides.Resolve(config, id, pairScope ? leader : 0);
                if (pairScope) config.PairFollowOverrides[key] = profile;
                else config.CharacterFollowOverrides[id] = profile;
            }
            else if (pairScope) config.PairFollowOverrides.Remove(key);
            else config.CharacterFollowOverrides.Remove(id);
        }
        if (enabled && profile is not null)
        {
            var resolved = FollowOverrides.Resolve(config, id, pairScope ? leader : 0);
            var distance = resolved.Distance ?? 5f;
            if (ImGui.SliderFloat(Loc.L("追従距離", "Follow distance"), ref distance, 1, 15, "%.1f m"))
            { profile.Distance = distance; changed = true; }
            var bmr = resolved.PreferBossMod == true;
            if (ImGui.Checkbox(Loc.L("BMRを優先", "Prefer BMR"), ref bmr))
            { profile.PreferBossMod = bmr; changed = true; }
            var recovery = resolved.Recovery == true;
            if (ImGui.Checkbox(Loc.L("vnavmeshで復旧", "Recover with vnavmesh"), ref recovery))
            { profile.Recovery = recovery; changed = true; }
            var mount = resolved.MountFallback == true;
            if (ImGui.Checkbox(Loc.L("自前マウントへ切替", "Use own mount as fallback"), ref mount))
            { profile.MountFallback = mount; changed = true; }
        }
        if (changed)
        {
            config.Save();
            plugin.CharacterLink.SettingsChanged();
        }
        ImGui.PopID();
    }
}
