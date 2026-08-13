using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;

namespace AltMate;

internal sealed unsafe class HousingWardObserver : IDisposable
{
    private const string ReadPacketSignature =
        "40 55 53 41 54 41 55 41 57 48 8D AC 24 ?? ?? ?? ?? B8";

    private readonly Plugin plugin;
    private readonly Hook<ReadPacketDelegate> hook;

    private delegate void ReadPacketDelegate(AgentHousingPortal* agent, HousingPortalPacket* packet);

    private HousingWardObserver(Plugin plugin)
    {
        this.plugin = plugin;
        hook = Plugin.InteropProvider.HookFromSignature<ReadPacketDelegate>(ReadPacketSignature, Detour);
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
        hook.Original(agent, packet);
        try
        {
            plugin.SaveWardSnapshot(packet);
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "住宅区画情報の保存に失敗しました。");
        }
    }

    public void Dispose() => hook.Dispose();
}
