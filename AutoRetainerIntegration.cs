using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AltMate;

/// <summary>
/// Keeps AltMate as the coordinator while AutoRetainer owns voyage UI,
/// repair, redeployment, housing travel, and character switching.
/// </summary>
internal sealed class AutoRetainerIntegration
{
    private DateTime statusCacheExpiresUtc;
    private IReadOnlyList<AutoRetainerCharacterStatus> statusCache = [];

    internal enum ConfiguredMode
    {
        Unknown = -1,
        Retainers = 0,
        Submersibles = 1,
        Everything = 2,
    }

    internal bool IsAvailable
    {
        get
        {
            try
            {
                Plugin.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.Init").InvokeAction();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal bool IsMultiModeEnabled
    {
        get
        {
            try
            {
                return Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled")
                    .InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    internal bool IsBusy
    {
        get
        {
            try
            {
                return Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy")
                    .InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    internal ConfiguredMode Mode => ReadConfiguredMode();

    internal IReadOnlyList<AutoRetainerCharacterStatus> GetCharacterStatuses()
    {
        if (DateTime.UtcNow < statusCacheExpiresUtc)
            return statusCache;
        try
        {
            var ids = Plugin.PluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs")
                .InvokeFunc();
            statusCache = ids.Select(contentId =>
            {
                var ready = InvokeNullableStatus("AutoRetainer.PluginState.AreAnyEnabledVesselsReady", contentId);
                var idle = InvokeNullableStatus("AutoRetainer.PluginState.AreAnyEnabledVesselsNotDeployed", contentId);
                var name = pluginCharacterName(contentId);
                return new AutoRetainerCharacterStatus(contentId, name, ready, idle);
            }).ToArray();
            statusCacheExpiresUtc = DateTime.UtcNow.AddSeconds(2);
            return statusCache;
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "AutoRetainerの対象キャラクターを取得できませんでした。");
            statusCache = [];
            statusCacheExpiresUtc = DateTime.UtcNow.AddSeconds(2);
            return statusCache;
        }

        static string pluginCharacterName(ulong contentId) =>
            Plugin.CurrentConfiguration?.Characters.TryGetValue(contentId, out var character) == true
                ? $"{character.CharacterName} @ {character.WorldName}"
                : $"Content ID {contentId:X16}";
    }

    internal bool StartCurrentCharacter()
    {
        if (!IsAvailable || !Plugin.PlayerState.IsLoaded)
            return false;
        try
        {
            var apiAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "AutoRetainerAPI");
            var enumType = apiAssembly?.GetType("AutoRetainerAPI.Configuration.MultiModeType");
            if (enumType == null)
                return false;
            var nullableEnum = typeof(Nullable<>).MakeGenericType(enumType);
            var argument = Activator.CreateInstance(nullableEnum, Enum.ToObject(enumType, 1));
            var getSubscriber = typeof(Dalamud.Plugin.IDalamudPluginInterface).GetMethods()
                .Single(method => method.Name == "GetIpcSubscriber" && method.GetGenericArguments().Length == 2);
            var subscriber = getSubscriber.MakeGenericMethod(nullableEnum, typeof(object))
                .Invoke(Plugin.PluginInterface, ["AutoRetainer.PluginState.EnableSingleMultiMode"]);
            subscriber?.GetType().GetMethod("InvokeAction")?.Invoke(subscriber, [argument]);
            Plugin.ChatGui.Print(Loc.L(
                "AltMate：現在のキャラクターで潜水艦処理を開始しました。",
                "AltMate: Started submersible processing for the current character."));
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "AutoRetainerの単一キャラクター潜水艦処理を開始できませんでした。");
            return false;
        }
    }

    internal bool Start()
    {
        if (!IsAvailable || Mode != ConfiguredMode.Submersibles)
            return false;
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled")
                .InvokeAction(true);
            Plugin.ChatGui.Print(Loc.L(
                "AltMate：AutoRetainerの潜水艦巡回を開始しました。",
                "AltMate: Started the AutoRetainer submersible cycle."));
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "AutoRetainerのMulti Modeを開始できませんでした。");
            return false;
        }
    }

    private static bool? InvokeNullableStatus(string channel, ulong contentId)
    {
        try
        {
            return Plugin.PluginInterface.GetIpcSubscriber<ulong, bool?>(channel).InvokeFunc(contentId);
        }
        catch
        {
            return null;
        }
    }

    private static ConfiguredMode ReadConfiguredMode()
    {
        try
        {
            var apiAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "AutoRetainerAPI");
            var enumType = apiAssembly?.GetType("AutoRetainerAPI.Configuration.MultiModeType");
            if (enumType == null)
                return ConfiguredMode.Unknown;
            var getSubscriber = typeof(Dalamud.Plugin.IDalamudPluginInterface).GetMethods()
                .Single(method => method.Name == "GetIpcSubscriber" && method.GetGenericArguments().Length == 1);
            var subscriber = getSubscriber.MakeGenericMethod(enumType)
                .Invoke(Plugin.PluginInterface, ["AutoRetainer.GetConfig.MultiModeType"]);
            var value = subscriber?.GetType().GetMethod("InvokeFunc")?.Invoke(subscriber, null);
            return value == null ? ConfiguredMode.Unknown : (ConfiguredMode)Convert.ToInt32(value);
        }
        catch
        {
            return ConfiguredMode.Unknown;
        }
    }

    internal bool Stop()
    {
        if (!IsAvailable)
            return false;
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled")
                .InvokeAction(false);
            Plugin.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.AbortAllTasks")
                .InvokeAction();
            Plugin.ChatGui.Print(Loc.L(
                "AltMate：AutoRetainerの潜水艦巡回を停止しました。",
                "AltMate: Stopped the AutoRetainer submersible cycle."));
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "AutoRetainerのMulti Modeを停止できませんでした。");
            return false;
        }
    }
}

internal sealed record AutoRetainerCharacterStatus(
    ulong ContentId,
    string Name,
    bool? HasReadyVessel,
    bool? HasIdleVessel);
