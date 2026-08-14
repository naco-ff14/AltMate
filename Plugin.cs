using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Chat;
using Dalamud.Game.Config;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumina.Excel.Sheets;

namespace AltMate;

public sealed class Plugin : IDalamudPlugin
{
    private static Plugin? current;
    private SharedConfigurationStore? sharedConfiguration;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IAddonEventManager AddonEventManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider InteropProvider { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;

    private const string Command = "/altmate";
    private const string LegacyCommand = "/hlt";
    private readonly WindowSystem windowSystem = new("AltMate");
    private readonly MainWindow mainWindow;
    private IAddonEventHandle? confirmHandle;
    private IAddonEventHandle? resultHandle;
    private bool placardOpen;
    private string? viewedAddress;
    private DateTime? viewedEntryEnd;
    private uint viewedPlotPrice;
    private readonly HousingWardObserver? wardObserver;
    private readonly GilTracker gilTracker;
    internal CharacterLinkCoordinator CharacterLink { get; }
    internal AnimationService Animations { get; }
    internal RoleBasedFpsController RoleBasedFps { get; }
    internal string IconPath { get; }

    internal Configuration Configuration { get; }
    internal static Configuration? CurrentConfiguration => current?.Configuration;

    public Plugin()
    {
        current = this;
        IconPath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty,
            "images", "icon.png");
        Configuration = LoadConfiguration();
        if (Configuration.Version < 3)
        {
            Configuration.CycleAnchorUtc = new DateTime(2026, 8, 12, 15, 0, 0, DateTimeKind.Utc);
            Configuration.Version = 3;
            Configuration.Save();
        }
        if (Configuration.Version < 4)
        {
            foreach (var record in Configuration.Characters.Values)
                record.EnabledForDisplay = false;
            Configuration.Version = 4;
            Configuration.Save();
        }
        sharedConfiguration = new SharedConfigurationStore();
        sharedConfiguration.LoadInto(Configuration);
        if (string.IsNullOrWhiteSpace(Configuration.LocalLinkKey))
        {
            Configuration.LocalLinkKey = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            // Separate Dalamud installations must use the same local key.
            // Persist it to the mutex-protected shared document before link startup.
            sharedConfiguration.SaveMerged(Configuration, includeSharedSettings: true);
        }
        PluginInterface.SavePluginConfig(Configuration);
        ScanCharacterFolders();
        Animations = new AnimationService();
        CharacterLink = new CharacterLinkCoordinator(this);
        RoleBasedFps = new RoleBasedFpsController(this);
        wardObserver = HousingWardObserver.TryCreate(this);
        gilTracker = new GilTracker(this);
        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(Command, new CommandInfo((_, _) => mainWindow.Toggle())
        { HelpMessage = "AltMateを開く" });
        CommandManager.AddHandler(LegacyCommand, new CommandInfo((_, _) => mainWindow.Toggle())
        { HelpMessage = "AltMateを開く（旧コマンド）", ShowInHelp = false });
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += mainWindow.OpenSettings;
        ClientState.Login += OnLogin;
        ChatGui.ChatMessage += OnChatMessage;

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "HousingSignBoard", OnPlacardOpen);
        AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "HousingSignBoard", OnPlacardUpdate);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "HousingSignBoard", OnPlacardClose);
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesNoTextScroll", OnBidConfirmOpen);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectYesNoTextScroll", OnBidConfirmClose);
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnResultOpen);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectYesno", OnResultClose);
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnLinkedSelectYesnoOpen);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectYesno", OnLinkedSelectYesnoClose);

        if (ClientState.IsLoggedIn)
        {
            CheckCurrentCharacter(false);
            ShowWindowIfAttentionNeeded();
        }
    }

    private static Configuration LoadConfiguration()
    {
        if (PluginInterface.GetPluginConfig() is Configuration current)
            return current;

        try
        {
            var configDirectory = PluginInterface.ConfigFile.DirectoryName;
            if (string.IsNullOrWhiteSpace(configDirectory))
                return new Configuration();

            var legacyPath = Path.Combine(configDirectory, "HousingLotteryTracker.json");
            if (!File.Exists(legacyPath))
                return new Configuration();

            var migrated = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(legacyPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (migrated is null)
                return new Configuration();

            PluginInterface.SavePluginConfig(migrated);
            Log.Information("HousingLotteryTrackerの設定をAltMateへ移行しました。");
            return migrated;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "HousingLotteryTrackerから設定を移行できませんでした。");
            return new Configuration();
        }
    }

    internal static void SaveConfiguration(Configuration configuration)
    {
        var instance = current;
        if (instance?.sharedConfiguration is null)
        {
            PluginInterface.SavePluginConfig(configuration);
            return;
        }
        var revision = instance.sharedConfiguration.SaveMerged(configuration, includeSharedSettings: false);
        PluginInterface.SavePluginConfig(configuration);
        instance.CharacterLink?.NotifySharedConfigurationChanged(revision);
    }

    internal void SaveSharedSettings()
    {
        if (sharedConfiguration is null)
            return;
        var revision = sharedConfiguration.SaveMerged(Configuration, includeSharedSettings: true);
        PluginInterface.SavePluginConfig(Configuration);
        CharacterLink.NotifySharedConfigurationChanged(revision);
    }

    internal void CheckSharedConfiguration(long minimumRevision = 0)
    {
        if (sharedConfiguration is null)
            return;
        var changed = minimumRevision > 0
            ? sharedConfiguration.ReloadIfNewer(Configuration, minimumRevision, out _)
            : sharedConfiguration.Poll(Configuration, out _);
        if (changed)
            PluginInterface.SavePluginConfig(Configuration);
    }

    private static void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        var text = chatMessage.Message.TextValue;
        if (!text.StartsWith("[BMRAI]", StringComparison.OrdinalIgnoreCase))
            return;

        // AltMateが戦闘連携開始時に毎回適用するBMR設定の応答だけを隠す。
        // エラーやその他のBMR通知は通常どおり表示する。
        if (text.Contains("Forbidden actions in combat is now", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Follow during combat is now", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Follow during active boss module is now", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Following targets is now", StringComparison.OrdinalIgnoreCase))
            chatMessage.PreventOriginal();
    }

    private void OnLogin()
    {
        CheckCurrentCharacter(false);
        ShowWindowIfAttentionNeeded();
    }

    internal void CheckCurrentCharacter(bool openWindow)
    {
        var id = PlayerState.ContentId;
        if (!PlayerState.IsLoaded || id == 0)
            return;

        if (!Configuration.Characters.TryGetValue(id, out var record))
        {
            record = new CharacterLotteryRecord { ContentId = id };
            Configuration.Characters[id] = record;
        }

        record.CharacterName = PlayerState.CharacterName;
        record.WorldName = PlayerState.HomeWorld.Value.Name.ToString();
        record.LastCheckedAt = DateTime.Now;
        Configuration.Save();
        if (openWindow)
            mainWindow.IsOpen = true;
    }

    internal LotteryCycle GetCurrentCycle()
    {
        var cycle = LotteryCycle.Current(DateTime.Now, Configuration.CycleAnchorUtc);
        var cycleStartUtc = cycle.CycleStartsAt.ToUniversalTime();
        if (Configuration.OpenPlotsCycleStartsAtUtc is null)
        {
            Configuration.OpenPlotsCycleStartsAtUtc = cycleStartUtc;
            Configuration.Save();
        }
        else if (Math.Abs((Configuration.OpenPlotsCycleStartsAtUtc.Value - cycleStartUtc).TotalMinutes) >= 5)
        {
            Configuration.OpenPlots.Clear();
            Configuration.OpenPlotsCycleStartsAtUtc = cycleStartUtc;
            Configuration.Save();
        }
        BackfillHousingDeposits(cycle);
        return cycle;
    }

    private void BackfillHousingDeposits(LotteryCycle cycle)
    {
        var changed = false;
        foreach (var record in Configuration.Characters.Values)
        {
            // 過去周期・結果確認済み・既に金額取得済みの記録は対象外。
            if (!cycle.HasEntry(record) || record.ResultChecked || record.BidGilDeposited != 0)
                continue;

            var price = FindSavedPlotPrice(record);
            if (price == 0)
                continue;
            record.BidGilDeposited = price;
            changed = true;
        }
        if (changed)
            Configuration.Save();
    }

    internal string GetCharacterDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games", "FINAL FANTASY XIV - A Realm Reborn");

    internal int ScanCharacterFolders()
    {
        var root = GetCharacterDataDirectory();
        if (!Directory.Exists(root))
            return 0;

        var added = 0;
        foreach (var directory in Directory.EnumerateDirectories(root, "FFXIV_CHR*", SearchOption.TopDirectoryOnly))
        {
            var folderName = Path.GetFileName(directory);
            var hex = folderName["FFXIV_CHR".Length..];
            if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var contentId) ||
                contentId == 0 || Configuration.Characters.ContainsKey(contentId))
                continue;

            Configuration.Characters[contentId] = new CharacterLotteryRecord
            {
                ContentId = contentId,
                CharacterName = $"未確認 ({folderName})",
                WorldName = "—",
            };
            added++;
        }

        if (added > 0)
            Configuration.Save();
        return added;
    }

    internal unsafe void SaveWardSnapshot(HousingPortalPacket* packet)
    {
        if (packet == null || packet->HouseId.WorldId == 0)
            return;

        var houseId = packet->HouseId;
        var wardNumber = houseId.WardIndex + 1;
        var checkedAt = DateTime.Now;
        var districtName = GetDistrictName(houseId.TerritoryTypeId);
        var worldName = GetWorldName(houseId.WorldId);

        Configuration.OpenPlots.RemoveAll(x =>
            x.WorldId == houseId.WorldId &&
            x.TerritoryTypeId == houseId.TerritoryTypeId &&
            x.WardNumber == wardNumber);

        for (var index = 0; index < 60; index++)
        {
            var info = packet->HouseInfoEntries[index];
            if ((info.InfoFlags & HousingPortalPacket.HouseInfoFlags.PlotOwned) != 0)
                continue;

            Configuration.OpenPlots.Add(new OpenPlotRecord
            {
                WorldId = houseId.WorldId,
                WorldName = worldName,
                TerritoryTypeId = houseId.TerritoryTypeId,
                DistrictName = districtName,
                WardNumber = wardNumber,
                PlotNumber = index + 1,
                Size = GetPlotSize(houseId.TerritoryTypeId, index),
                Price = info.HousePrice,
                CheckedAt = checkedAt,
            });
        }

        Configuration.Save();
    }

    private static string GetDistrictName(ushort territoryTypeId)
    {
        try
        {
            var territory = DataManager.GetExcelSheet<TerritoryType>().GetRow(territoryTypeId);
            return territory.PlaceName.Value.Name.ToString();
        }
        catch { return $"エリアID {territoryTypeId}"; }
    }

    private static string GetWorldName(ushort worldId)
    {
        try { return DataManager.GetExcelSheet<World>().GetRow(worldId).Name.ToString(); }
        catch { return $"World {worldId}"; }
    }

    private static string GetPlotSize(ushort territoryTypeId, int plotIndex)
    {
        try
        {
            var landSetId = territoryTypeId switch
            {
                641 => 3u,
                979 => 4u,
                _ => (uint)(territoryTypeId - 339),
            };
            var size = DataManager.GetExcelSheet<HousingLandSet>().GetRow(landSetId).LandSet[plotIndex].PlotSize;
            return size switch { 0 => "S", 1 => "M", 2 => "L", _ => "?" };
        }
        catch { return "?"; }
    }

    internal static bool PreviewOpenPlot(OpenPlotRecord plot)
    {
        try
        {
            var sheet = DataManager.GetSubrowExcelSheet<HousingMapMarkerInfo>();
            if (plot.PlotNumber is < 1 or > 60 ||
                !sheet.TryGetSubrow(plot.TerritoryTypeId, (ushort)(plot.PlotNumber - 1), out var marker))
                return false;

            var rawX = (int)MathF.Round(marker.X * 1000f);
            var rawY = (int)MathF.Round(marker.Z * 1000f);
            return GameGui.OpenMapWithMapLink(
                new MapLinkPayload(plot.TerritoryTypeId, marker.Map.RowId, rawX, rawY));
        }
        catch (Exception exception)
        {
            Log.Error(exception, "土地の位置プレビューを開けませんでした。");
            return false;
        }
    }

    internal static bool IsLifestreamAvailable() =>
        PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.IsLoaded &&
            (plugin.InternalName.Equals("Lifestream", StringComparison.OrdinalIgnoreCase) ||
             plugin.Name.Equals("Lifestream", StringComparison.OrdinalIgnoreCase)));

    internal static bool TravelToOpenPlot(OpenPlotRecord plot)
    {
        if (!IsLifestreamAvailable())
            return false;

        var district = plot.TerritoryTypeId switch
        {
            339 => "mist",
            340 => "lavender",
            341 => "goblet",
            641 => "shirogane",
            979 => "empyreum",
            _ => null,
        };
        if (district is null)
            return false;

        CommandManager.ProcessCommand(
            $"/li {plot.WorldName} {district} {plot.WardNumber} {plot.PlotNumber}");
        return true;
    }

    private void ShowWindowIfAttentionNeeded()
    {
        if (!Configuration.Characters.TryGetValue(PlayerState.ContentId, out var record) ||
            !record.EnabledForDisplay)
            return;

        var cycle = GetCurrentCycle();
        var hasEntry = cycle.HasEntry(record);
        var needsAttention = cycle.Phase == LotteryPhase.Entry
            ? !hasEntry
            : hasEntry && !record.ResultChecked;
        if (needsAttention)
            mainWindow.OpenHousingLottery();
    }

    private unsafe void OnPlacardOpen(AddonEvent _, AddonArgs __) => placardOpen = true;

    private unsafe void OnPlacardUpdate(AddonEvent _, AddonArgs args)
    {
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || args.Addon.Address == nint.Zero)
            return;
        var addon = (AtkUnitBase*)args.Addon.Address;
        var addressNode = addon->GetTextNodeById(56);
        var detailsNode = addon->GetTextNodeById(64);
        if (addressNode is null || detailsNode is null)
            return;
        viewedAddress = addressNode->NodeText.ExtractText().Trim();
        var details = detailsNode->NodeText.ExtractText();
        viewedEntryEnd = ParseDate(details);
        viewedPlotPrice = ParseGilAmount(details);
    }

    private void OnPlacardClose(AddonEvent _, AddonArgs __)
    {
        placardOpen = false;
        viewedAddress = null;
        viewedEntryEnd = null;
        viewedPlotPrice = 0;
    }

    private unsafe void OnBidConfirmOpen(AddonEvent _, AddonArgs args)
    {
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || !placardOpen ||
            viewedEntryEnd is null || args.Addon.Address == nint.Zero)
            return;
        var addon = (AtkUnitBase*)args.Addon.Address;
        var node = addon->GetComponentNodeById(5);
        if (node is not null)
            confirmHandle = AddonEventManager.AddEvent((nint)addon, (nint)node,
                AddonEventType.ButtonClick, OnBidConfirmed);
    }

    private void OnBidConfirmClose(AddonEvent _, AddonArgs __) => RemoveHandle(ref confirmHandle);

    private unsafe void OnResultOpen(AddonEvent _, AddonArgs args)
    {
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || !placardOpen ||
            args.Addon.Address == nint.Zero)
            return;
        var addon = (AddonSelectYesno*)args.Addon.Address;
        if (addon != null && addon->YesButton != null && addon->YesButton->OwnerNode != null)
            resultHandle = AddonEventManager.AddEvent((nint)addon, (nint)addon->YesButton->OwnerNode,
                AddonEventType.ButtonClick, OnResultConfirmed);
    }

    private void OnResultClose(AddonEvent _, AddonArgs __) => RemoveHandle(ref resultHandle);

    private void OnLinkedSelectYesnoOpen(AddonEvent _, AddonArgs args) =>
        CharacterLink.OnSelectYesnoOpened(args);

    private void OnLinkedSelectYesnoClose(AddonEvent _, AddonArgs __) =>
        CharacterLink.OnSelectYesnoClosed();

    private void OnBidConfirmed(AddonEventType _, AddonEventData __)
    {
        CheckCurrentCharacter(false);
        if (!Configuration.Characters.TryGetValue(PlayerState.ContentId, out var record))
            return;
        record.PlotAddress = string.IsNullOrWhiteSpace(viewedAddress) ? "応募した土地" : viewedAddress;
        record.EntryPhaseEndsAt = viewedEntryEnd;
        record.ResultChecked = false;
        record.LastCheckedAt = DateTime.Now;
        PopulateStructuredBid(record, viewedAddress);
        record.BidGilDeposited = viewedPlotPrice != 0
            ? viewedPlotPrice
            : FindSavedPlotPrice(record);
        if (viewedEntryEnd is { } entryEnd)
            Configuration.CycleAnchorUtc = entryEnd.AddDays(-5).ToUniversalTime();
        Configuration.Save();
    }

    private uint FindSavedPlotPrice(CharacterLotteryRecord record) => Configuration.OpenPlots
        .Where(x => IsBidForPlot(record, x))
        .OrderByDescending(x => x.CheckedAt)
        .Select(x => x.Price)
        .FirstOrDefault();

    private static uint ParseGilAmount(string text)
    {
        var matches = Regex.Matches(text, @"(?<amount>\d[\d,\.\s]*)\s*(?:G|ギル)",
            RegexOptions.IgnoreCase);
        uint maximum = 0;
        foreach (Match match in matches)
        {
            var digits = Regex.Replace(match.Groups["amount"].Value, @"\D", string.Empty);
            if (uint.TryParse(digits, out var value) && value > maximum)
                maximum = value;
        }
        return maximum;
    }

    private static void PopulateStructuredBid(CharacterLotteryRecord record, string? address)
    {
        record.BidWorldId = (ushort)PlayerState.CurrentWorld.RowId;
        record.BidTerritoryTypeId = ResolveDistrictId(address) ?? (ushort)ClientState.TerritoryType;
        record.BidWardNumber = 0;
        record.BidPlotNumber = 0;

        if (string.IsNullOrWhiteSpace(address))
            return;

        var japanese = Regex.Match(address, @"第\s*(?<ward>\d+)\s*区.*?(?<plot>\d+)\s*番地");
        if (japanese.Success)
        {
            record.BidWardNumber = int.Parse(japanese.Groups["ward"].Value);
            record.BidPlotNumber = int.Parse(japanese.Groups["plot"].Value);
            return;
        }

        var english = Regex.Match(address,
            @"Plot\s+(?<plot>\d+).*?(?<ward>\d+)(?:st|nd|rd|th)?\s+Ward",
            RegexOptions.IgnoreCase);
        if (english.Success)
        {
            record.BidWardNumber = int.Parse(english.Groups["ward"].Value);
            record.BidPlotNumber = int.Parse(english.Groups["plot"].Value);
        }
    }

    internal int GetBidCount(OpenPlotRecord plot)
    {
        var cycle = GetCurrentCycle();
        return Configuration.Characters.Values.Count(record =>
            cycle.HasEntry(record) &&
            IsBidForPlot(record, plot));
    }

    private static bool IsBidForPlot(CharacterLotteryRecord record, OpenPlotRecord plot)
    {
        if (record.BidWardNumber > 0 && record.BidPlotNumber > 0)
            return record.BidWorldId == plot.WorldId &&
                   record.BidTerritoryTypeId == plot.TerritoryTypeId &&
                   record.BidWardNumber == plot.WardNumber &&
                   record.BidPlotNumber == plot.PlotNumber;

        if (!string.Equals(record.WorldName, plot.WorldName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(record.PlotAddress))
            return false;

        var ward = 0;
        var plotNumber = 0;
        var japanese = Regex.Match(record.PlotAddress, @"第\s*(?<ward>\d+)\s*区.*?(?<plot>\d+)\s*番地");
        var english = Regex.Match(record.PlotAddress,
            @"Plot\s+(?<plot>\d+).*?(?<ward>\d+)(?:st|nd|rd|th)?\s+Ward",
            RegexOptions.IgnoreCase);
        var match = japanese.Success ? japanese : english;
        if (!match.Success ||
            !int.TryParse(match.Groups["ward"].Value, out ward) ||
            !int.TryParse(match.Groups["plot"].Value, out plotNumber))
            return false;

        return ward == plot.WardNumber && plotNumber == plot.PlotNumber &&
               AddressMatchesDistrict(record.PlotAddress, plot.TerritoryTypeId);
    }

    private static bool AddressMatchesDistrict(string address, ushort territoryTypeId)
    {
        string[] names = territoryTypeId switch
        {
            339 => new[] { "ミスト", "Mist" },
            340 => new[] { "ラベンダー", "Lavender" },
            341 => new[] { "ゴブレット", "Goblet" },
            641 => new[] { "シロガネ", "Shirogane" },
            979 => new[] { "エンピレアム", "Empyreum" },
            _ => Array.Empty<string>(),
        };
        return names.Any(name => address.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static ushort? ResolveDistrictId(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (AddressMatchesDistrict(address, 339)) return 339;
        if (AddressMatchesDistrict(address, 340)) return 340;
        if (AddressMatchesDistrict(address, 341)) return 341;
        if (AddressMatchesDistrict(address, 641)) return 641;
        if (AddressMatchesDistrict(address, 979)) return 979;
        return null;
    }

    private void OnResultConfirmed(AddonEventType _, AddonEventData __)
    {
        if (Configuration.Characters.TryGetValue(PlayerState.ContentId, out var record))
        {
            record.ResultChecked = true;
            record.BidGilDeposited = 0;
            record.LastCheckedAt = DateTime.Now;
            Configuration.Save();
        }
    }

    private void RemoveHandle(ref IAddonEventHandle? handle)
    {
        if (handle is null)
            return;
        AddonEventManager.RemoveEvent(handle);
        handle = null;
    }

    private static DateTime? ParseDate(string text)
    {
        string[] formats =
        {
            "H:mm M/d/yyyy", "HH:mm M/d/yyyy", "M/d/yyyy H:mm", "M/d/yyyy HH:mm",
            "yyyy/MM/dd H:mm", "yyyy/MM/dd HH:mm", "yyyy年M月d日 H:mm", "yyyy年M月d日 HH:mm",
            "h:mm tt M/d/yyyy", "hh:mm tt M/d/yyyy",
        };
        var normalized = Regex.Replace(text.Replace("a.m.", "AM").Replace("p.m.", "PM"), @"\s+", " ");
        foreach (Match match in Regex.Matches(normalized,
                     @"(?:\d{1,2}:\d{2}\s*(?:AM|PM)?\s*\d{1,4}[年/]\d{1,2}[月/]\d{1,4}日?|\d{1,4}[年/]\d{1,2}[月/]\d{1,4}日?\s*\d{1,2}:\d{2})",
                     RegexOptions.IgnoreCase))
        {
            if (DateTime.TryParseExact(match.Value.Trim(), formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var parsed))
                return parsed;
            if (DateTime.TryParse(match.Value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
                return parsed;
        }
        return null;
    }

    public void Dispose()
    {
        RoleBasedFps.Dispose();
        CharacterLink.Dispose();
        gilTracker.Dispose();
        wardObserver?.Dispose();
        ClientState.Login -= OnLogin;
        ChatGui.ChatMessage -= OnChatMessage;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.OpenSettings;
        CommandManager.RemoveHandler(Command);
        CommandManager.RemoveHandler(LegacyCommand);
        RemoveHandle(ref confirmHandle);
        RemoveHandle(ref resultHandle);
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "HousingSignBoard", OnPlacardOpen);
        AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "HousingSignBoard", OnPlacardUpdate);
        AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "HousingSignBoard", OnPlacardClose);
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesNoTextScroll", OnBidConfirmOpen);
        AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "SelectYesNoTextScroll", OnBidConfirmClose);
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnResultOpen);
        AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "SelectYesno", OnResultClose);
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnLinkedSelectYesnoOpen);
        AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "SelectYesno", OnLinkedSelectYesnoClose);
        windowSystem.RemoveAllWindows();
        sharedConfiguration?.Dispose();
        sharedConfiguration = null;
        current = null;
    }
}
