using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Linq;

namespace AltMate;

internal static unsafe class QuestionableQuestBridge
{
    internal static bool IsAvailable => Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
        plugin.IsLoaded && string.Equals(plugin.InternalName, "Questionable", StringComparison.OrdinalIgnoreCase));

    internal static bool IsRunning()
    {
        try
        {
            return IsAvailable && Plugin.PluginInterface
                .GetIpcSubscriber<bool>("Questionable.IsRunning").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsComplete(uint questRowId) => questRowId != 0 && QuestManager.IsQuestComplete(questRowId);

    internal static bool StartSingle(uint questRowId)
    {
        if (!IsAvailable || questRowId == 0) return false;
        // Questionable's QuestId is the low 16 bits of the Lumina Quest row id.
        var questId = ((ushort)(questRowId & 0xffff)).ToString();
        return Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.StartSingleQuest")
            .InvokeFunc(questId);
    }
}
