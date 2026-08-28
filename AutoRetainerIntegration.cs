using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AltMate;

/// <summary>
/// Keeps AltMate as the coordinator while AutoRetainer owns voyage UI,
/// repair, redeployment, housing travel, and character switching.
/// </summary>
internal sealed unsafe class AutoRetainerIntegration : IDisposable
{
    private enum PendingStart
    {
        None,
        CurrentCharacter,
        AllCharacters,
        ResumeAllCharacters,
    }

    private DateTime statusCacheExpiresUtc;
    private IReadOnlyList<AutoRetainerCharacterStatus> statusCache = [];
    private PendingStart pendingStart;
    private DateTime pendingDeadlineUtc;
    private DateTime nextWorkshopRequestUtc;
    private bool ownsAllCharacterCycle;

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
        return BeginWorkshopTravel(PendingStart.CurrentCharacter);
    }

    private bool InvokeSingleCharacterStart()
    {
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
        if (!IsAvailable || !Plugin.PlayerState.IsLoaded || Mode != ConfiguredMode.Submersibles)
            return false;
        return BeginWorkshopTravel(PendingStart.AllCharacters);
    }

    private bool InvokeAllCharacterStart()
    {
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
            pendingStart = PendingStart.None;
            ownsAllCharacterCycle = false;
            Plugin.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled")
                .InvokeAction(false);
            Plugin.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.AbortAllTasks")
                .InvokeAction();
            SetAutoRetainerSuppressed(false);
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

    private bool BeginWorkshopTravel(PendingStart start)
    {
        if (!IsLifestreamAvailable())
        {
            Plugin.ChatGui.PrintError(Loc.L(
                "AltMate：FC地下工房への移動にはLifestreamが必要です。",
                "AltMate: Lifestream is required to travel to the company workshop."));
            return false;
        }
        pendingStart = start;
        pendingDeadlineUtc = DateTime.UtcNow.AddMinutes(3);
        nextWorkshopRequestUtc = DateTime.MinValue;
        if (start == PendingStart.ResumeAllCharacters)
            SetAutoRetainerSuppressed(true);
        UpdateWorkshopTravel();
        return true;
    }

    private void OnLogin()
    {
        if (!ownsAllCharacterCycle || !IsMultiModeEnabled)
            return;
        BeginWorkshopTravel(PendingStart.ResumeAllCharacters);
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        if (pendingStart == PendingStart.None)
            return;
        UpdateWorkshopTravel();
    }

    private void UpdateWorkshopTravel()
    {
        if (pendingStart == PendingStart.None || !Plugin.PlayerState.IsLoaded)
            return;
        if (DateTime.UtcNow > pendingDeadlineUtc)
        {
            var resume = pendingStart == PendingStart.ResumeAllCharacters;
            pendingStart = PendingStart.None;
            if (resume)
            {
                ownsAllCharacterCycle = false;
                SetAutoRetainerSuppressed(false);
            }
            Plugin.ChatGui.PrintError(Loc.L(
                "AltMate：FC地下工房への移動が時間切れになったため停止しました。",
                "AltMate: Stopped because travel to the company workshop timed out."));
            return;
        }

        if (HousingManager.Instance()->WorkshopTerritory != null)
        {
            var completed = pendingStart;
            pendingStart = PendingStart.None;
            if (completed == PendingStart.CurrentCharacter)
                InvokeSingleCharacterStart();
            else if (completed == PendingStart.AllCharacters)
            {
                ownsAllCharacterCycle = InvokeAllCharacterStart();
            }
            else
                SetAutoRetainerSuppressed(false);
            return;
        }

        if (IsLifestreamBusy() || DateTime.UtcNow < nextWorkshopRequestUtc)
            return;
        try
        {
            // "ws" targets the FC workshop directly. It never falls back to a
            // nearby apartment or private-estate entrance.
            Plugin.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand")
                .InvokeAction("ws");
            nextWorkshopRequestUtc = DateTime.UtcNow.AddSeconds(15);
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "LifestreamでFC地下工房へ移動できませんでした。");
            nextWorkshopRequestUtc = DateTime.UtcNow.AddSeconds(5);
        }
    }

    private static bool IsLifestreamAvailable()
    {
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLifestreamBusy()
    {
        try
        {
            return Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutoRetainerSuppressed(bool suppressed)
    {
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed")
                .InvokeAction(suppressed);
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "AutoRetainerの一時停止状態を変更できませんでした。");
        }
    }

    internal string TravelStatus => pendingStart == PendingStart.None
        ? string.Empty
        : Loc.L("FC地下工房へ移動中", "Traveling to the company workshop");

    internal AutoRetainerIntegration()
    {
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ClientState.Login += OnLogin;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.ClientState.Login -= OnLogin;
        if (pendingStart == PendingStart.ResumeAllCharacters)
            SetAutoRetainerSuppressed(false);
    }
}

internal sealed record AutoRetainerCharacterStatus(
    ulong ContentId,
    string Name,
    bool? HasReadyVessel,
    bool? HasIdleVessel);
