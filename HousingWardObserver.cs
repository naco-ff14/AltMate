using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;

namespace AltMate;

internal sealed unsafe class HousingWardObserver : IDisposable
{
    private readonly Plugin plugin;
    private readonly Hook<ReadPacketDelegate> hook;

    private delegate void ReadPacketDelegate(AgentHousingPortal* agent, HousingPortalPacket* packet);

    private HousingWardObserver(Plugin plugin)
    {
        this.plugin = plugin;
        hook = Plugin.InteropProvider.HookFromAddress<ReadPacketDelegate>(
            (nint)AgentHousingPortal.MemberFunctionPointers.ReadPacket, Detour);
        hook.Enable();
    }

    internal static HousingWardObserver? TryCreate(Plugin plugin)
    {
        try
        {
            return new HousingWardObserver(plugin);
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "住宅区画情報の監視を開始できませんでした。");
            return null;
        }
    }

    private void Detour(AgentHousingPortal* agent, HousingPortalPacket* packet)
    {
        try
        {
            plugin.SaveWardSnapshot(packet);
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "住宅区画情報の保存に失敗しました。");
        }
        hook.Original(agent, packet);
    }

    public void Dispose() => hook.Dispose();
}
