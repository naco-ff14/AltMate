using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;
using System.Text;

namespace AltMate;

public sealed unsafe class GilTracker : IDisposable
{
    private readonly Plugin plugin;
    private DateTime lastCheckUtc;
    private readonly FreeCompanyChestSession chestSession = new();
    private readonly CompanyCreditObservation companyCreditObservation = new();
    private readonly CompanyCreditObservation creditShopObservation = new();
    private const string CompanyAddon = "FreeCompany";
    private const string CreditShopAddon = "FreeCompanyCreditShop";
    private DateTime nextChestCheckUtc;
    internal string? FreeCompanyChestStatus { get; private set; }

    public GilTracker(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.GameInventory.InventoryChangedRaw += OnInventoryChanged;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FreeCompanyChest", OnChestOpened);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnChestClosing);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, CompanyAddon, OnCreditWindowReset);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, CompanyAddon, OnCreditWindowReset);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, CreditShopAddon, OnCreditWindowReset);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, CompanyAddon, OnCompanyCreditsUpdated);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, CreditShopAddon, OnCreditShopUpdated);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, CreditShopAddon, OnCreditShopUpdated);
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        var now = DateTime.UtcNow;
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
        {
            chestSession.Reset();
            ResetCompanyCreditObservations();
            FreeCompanyChestStatus = null;
            return;
        }
        if (now >= nextChestCheckUtc)
        {
            nextChestCheckUtc = now.AddMilliseconds(100);
            CheckChest(now);
            CheckCompanyCredits();
        }
        if (now - lastCheckUtc < TimeSpan.FromSeconds(2))
            return;
        lastCheckUtc = now;

        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return;
            var contentId = Plugin.PlayerState.ContentId;
            if (!plugin.Configuration.CharacterGil.TryGetValue(contentId, out var character))
            {
                character = new CharacterGilRecord { ContentId = contentId };
                plugin.Configuration.CharacterGil[contentId] = character;
            }
            var changed = false;
            var gil = inventory->GetGil();
            if (character.Gil != gil || now - character.UpdatedAt.ToUniversalTime() > TimeSpan.FromMinutes(1))
            {
                character.CharacterName = Plugin.PlayerState.CharacterName;
                character.WorldName = Plugin.PlayerState.HomeWorld.Value.Name.ToString();
                character.Gil = gil;
                character.UpdatedAt = DateTime.Now;
                changed = true;
            }

            var retainers = RetainerManager.Instance();
            if (retainers != null && retainers->IsReady)
            {
                for (uint i = 0; i < retainers->GetRetainerCount(); i++)
                {
                    var retainer = retainers->GetRetainerBySortedIndex(i);
                    if (retainer == null || retainer->RetainerId == 0 || !retainer->Available)
                        continue;
                    var name = ReadUtf8((byte*)retainer + 0x08, 32);
                    if (!character.Retainers.TryGetValue(retainer->RetainerId, out var record) ||
                        record.Gil != retainer->Gil || record.Name != name)
                    {
                        character.Retainers[retainer->RetainerId] = new RetainerGilRecord
                        {
                            RetainerId = retainer->RetainerId,
                            Name = string.IsNullOrWhiteSpace(name) ? $"リテイナー {i + 1}" : name,
                            Gil = retainer->Gil,
                            UpdatedAt = DateTime.Now,
                        };
                        changed = true;
                    }
                }
            }

            var infoModule = InfoModule.Instance();
            var workshopInfo = infoModule == null ? null : infoModule->GetInfoProxyFreeCompany();
            if (workshopInfo != null && workshopInfo->Id != 0 &&
                HousingManager.Instance()->WorkshopTerritory != null)
            {
                if (!plugin.Configuration.FreeCompanyGil.TryGetValue(workshopInfo->Id, out var workshopFc))
                {
                    workshopFc = new FreeCompanyGilRecord
                    {
                        FreeCompanyId = workshopInfo->Id,
                        Name = string.IsNullOrWhiteSpace(workshopInfo->NameString) ? "不明なFC" : workshopInfo->NameString,
                        WorldName = Plugin.PlayerState.HomeWorld.Value.Name.ToString(),
                        LastCheckedByContentId = contentId,
                        LastCheckedByName = Plugin.PlayerState.CharacterName,
                    };
                    plugin.Configuration.FreeCompanyGil[workshopInfo->Id] = workshopFc;
                }
                else if (!string.IsNullOrWhiteSpace(workshopInfo->NameString) && workshopFc.Name != workshopInfo->NameString)
                {
                    workshopFc.Name = workshopInfo->NameString;
                    changed = true;
                }
                if (UpdateSubmarines(workshopFc, now))
                    changed = true;
                if (CaptureTreasureVoyage(workshopFc, now))
                    changed = true;
            }

            if (changed)
                plugin.Configuration.Save();
        }
        catch (Exception exception)
        {
            chestSession.Reset();
            FreeCompanyChestStatus = Loc.L("ギル情報の取得に失敗しました。保存済みの金額を表示しています。",
                "Could not read gil data. Displaying the last saved balances.");
            Plugin.Log.Verbose(exception, "ギル情報を更新できませんでした。");
        }
    }

    private void OnCreditWindowReset(AddonEvent _, AddonArgs args) =>
        (args.AddonName == CompanyAddon ? companyCreditObservation : creditShopObservation).Reset();

    private void ResetCompanyCreditObservations()
    {
        companyCreditObservation.Reset();
        creditShopObservation.Reset();
    }

    private void OnCompanyCreditsUpdated(AddonEvent type, AddonArgs args)
    {
        try
        {
            // RequestedUpdate follows array-data processing; Setup/Update polling
            // is not evidence of new credit data. See docs/fc-credit-capture.md for
            // the remaining native readiness limitations and validation steps.
            if (args.PreventOriginalRequested ||
                !TryGetCreditContext(CompanyAddon, args.Addon.Address, out var addon, out _, out var context) ||
                !TryReadCompanyCreditText(addon, out var credits))
            {
                companyCreditObservation.Reset();
                return;
            }
            companyCreditObservation.ObserveUpdate(context, credits, DateTime.UtcNow);
        }
        catch (Exception exception)
        {
            companyCreditObservation.Reset();
            Plugin.Log.Verbose(exception, "FCクレジットの更新を取得できませんでした。");
        }
    }

    private void OnCreditShopUpdated(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PostSetup) creditShopObservation.Reset();
        try
        {
            // Use the actual incoming value payload, not an unrelated update's
            // retained AtkValues. A negative signed value is unavailable, not zero.
            var values = args switch
            {
                AddonSetupArgs setup => (AtkValue*)setup.AtkValues,
                AddonRefreshArgs refresh => (AtkValue*)refresh.AtkValues,
                _ => null,
            };
            var count = args switch
            {
                AddonSetupArgs setup => setup.AtkValueCount,
                AddonRefreshArgs refresh => refresh.AtkValueCount,
                _ => 0u,
            };
            if (args.PreventOriginalRequested ||
                !TryGetCreditContext(CreditShopAddon, args.Addon.Address, out _, out _, out var context,
                    requireVisible: type != AddonEvent.PostSetup) ||
                !TryReadCreditShopValue(values, count, out var credits))
            {
                creditShopObservation.Reset();
                return;
            }
            creditShopObservation.ObserveUpdate(context, credits, DateTime.UtcNow);
        }
        catch (Exception exception)
        {
            creditShopObservation.Reset();
            Plugin.Log.Verbose(exception, "FCクレジット交換の更新を取得できませんでした。");
        }
    }

    private static bool TryGetCreditContext(string addonName, nint expectedAddon,
        out AtkUnitBase* addon, out InfoProxyFreeCompany* info, out CompanyCreditContext context,
        bool requireVisible = true)
    {
        addon = null;
        info = null;
        context = default;
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0) return false;
        var module = InfoModule.Instance();
        if (module == null || module->LocalContentId != Plugin.PlayerState.ContentId) return false;
        info = module->GetInfoProxyFreeCompany();
        if (info == null || info->Id == 0 || info->HomeWorldId == 0 ||
            info->HomeWorldId != Plugin.PlayerState.HomeWorld.RowId ||
            string.IsNullOrWhiteSpace(info->NameString)) return false;
        addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(addonName).Address;
        if (addon == null || (expectedAddon != 0 && (nint)addon != expectedAddon) ||
            !addon->IsReady || (requireVisible && !addon->IsVisible)) return false;
        var agent = AgentFreeCompany.Instance();
        if (agent == null || agent->InfoProxyFreeCompany != info) return false;
        if (addonName == CompanyAddon && agent->AddonId != addon->Id) return false;
        if (addonName == CreditShopAddon)
        {
            var agents = AgentModule.Instance();
            var shopAgent = agents == null ? null : agents->GetAgentByInternalId(AgentId.FreeCompanyCreditShop);
            if (shopAgent == null || shopAgent->AddonId != addon->Id) return false;
        }
        // A populated member proxy disagreeing with the FC proxy indicates an
        // identity transition/inspection. Do not attach that balance to this FC.
        var members = agent->InfoProxyFreeCompanyMember;
        if (members != null && members->FreeCompanyId != 0 && members->FreeCompanyId != info->Id)
            return false;
        context = new CompanyCreditContext(Plugin.PlayerState.ContentId, info->Id,
            Plugin.ClientState.TerritoryType, (nint)addon, (nint)info);
        return true;
    }

    private static bool TryReadCompanyCreditText(AtkUnitBase* addon, out uint credits)
    {
        credits = 0;
        // Node ID, not a NodeList index. Never infer credits from rank/member
        // counts or by choosing the largest number elsewhere in the window.
        var node = addon->GetNodeById(17);
        return node != null && node->Type == NodeType.Text && node->IsVisible() &&
            CompanyCreditParser.TryParse(((AtkTextNode*)node)->NodeText.ToString(), out credits);
    }

    private static bool TryReadCreditShopValue(AtkValue* values, uint count, out uint credits)
    {
        credits = 0;
        if (values == null || count <= 3) return false;
        var value = values[3];
        if (value.Type == AtkValueType.UInt)
        {
            credits = value.UInt;
            return true;
        }
        return value.Type == AtkValueType.Int && CompanyCreditParser.TryParseSigned(value.Int, out credits);
    }

    private void CheckCompanyCredits()
    {
        try
        {
            ConfirmCompanyCredits(CompanyAddon, companyCreditObservation);
            ConfirmCompanyCredits(CreditShopAddon, creditShopObservation);
        }
        catch (Exception exception)
        {
            ResetCompanyCreditObservations();
            Plugin.Log.Verbose(exception, "FCクレジットを取得できませんでした。");
        }
    }

    private void ConfirmCompanyCredits(string addonName, CompanyCreditObservation observation)
    {
        if (!TryGetCreditContext(addonName, 0, out var addon, out var info, out var context))
        {
            observation.Reset();
            return;
        }
        uint credits;
        var valid = addonName == CompanyAddon ? TryReadCompanyCreditText(addon, out credits) :
            TryReadCreditShopValue(addon->AtkValues, addon->AtkValuesCount, out credits);
        if (!observation.TryConfirm(context, valid ? credits : null, out var sample)) return;
        var exists = plugin.Configuration.FreeCompanyGil.TryGetValue(info->Id, out var fc);
        fc ??= new FreeCompanyGilRecord { FreeCompanyId = info->Id };
        if (!sample.TryApply(fc)) return;
        fc.Name = info->NameString;
        fc.WorldName = Plugin.PlayerState.HomeWorld.Value.Name.ToString();
        if (!exists) plugin.Configuration.FreeCompanyGil[info->Id] = fc;
        plugin.Configuration.Save();
    }

    private void OnChestOpened(AddonEvent _, AddonArgs __)
    {
        chestSession.Reset();
        nextChestCheckUtc = default;
    }

    private void OnChestClosing(AddonEvent _, AddonArgs args)
    {
        var now = DateTime.UtcNow;
        // Read before native UI/inventory teardown; do not wait for another periodic sample.
        CheckChest(now, finalSample: true, closingAddon: args.Addon.Address);
        chestSession.Close(now);
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
        {
            chestSession.Reset();
            return;
        }
        foreach (var change in events)
        {
            // Removed events carry the OLD item. Never interpret unload/removal as zero gil.
            if (change is not InventoryItemChangedArgs ||
                (uint)change.Item.ContainerType != (uint)InventoryType.FreeCompanyGil ||
                change.Item.ItemId != 1 || change.Item.Quantity < 0)
                continue;
            var now = DateTime.UtcNow;
            var module = InfoModule.Instance();
            var info = module == null ? null : module->GetInfoProxyFreeCompany();
            if (info != null && chestSession.CanAcceptLateUpdate(Plugin.PlayerState.ContentId,
                    info->Id, Plugin.ClientState.TerritoryType, now))
            {
                // Use the delivered inventory snapshot, not memory that may already be unloaded.
                SaveChestBalance(info, (uint)change.Item.Quantity, now);
            }
            else
                CheckChest(now, finalSample: true);
        }
    }

    private void CheckChest(DateTime now, bool finalSample = false, nint closingAddon = default)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
        {
            chestSession.Reset();
            return;
        }
        try
        {
            var chest = (AtkUnitBase*)(closingAddon != 0 ? closingAddon :
                Plugin.GameGui.GetAddonByName("FreeCompanyChest").Address);
            if (chest == null || (closingAddon == 0 && (!chest->IsVisible || !chest->IsReady)))
            {
                if (!chestSession.IsAwaitingLateUpdate(now))
                {
                    chestSession.Reset();
                    FreeCompanyChestStatus = null;
                }
                return;
            }
            var inventory = InventoryManager.Instance();
            var container = inventory == null ? null : inventory->GetInventoryContainer(InventoryType.FreeCompanyGil);
            if (container == null || !container->IsLoaded)
            {
                // During close, retain the verified identity for a delayed inventory event.
                if (closingAddon == 0) chestSession.Reset();
                FreeCompanyChestStatus = Loc.L("FCチェスト：残高の読み込み待ち（チェストの「ギル」を選択してください）。",
                    "FC chest: waiting for balance data (select Gil in the chest).");
                return;
            }
            var module = InfoModule.Instance();
            var info = module == null ? null : module->GetInfoProxyFreeCompany();
            if (info == null || info->Id == 0)
            {
                chestSession.Reset();
                FreeCompanyChestStatus = Loc.L("FC情報の取得待ち：フリーカンパニー画面を開いてください。",
                    "Waiting for FC identity: open the Free Company window.");
                return;
            }
            var gil = inventory->GetFreeCompanyGil();
            if (chestSession.ObserveOpen(Plugin.PlayerState.ContentId, info->Id,
                    Plugin.ClientState.TerritoryType, gil, finalSample))
                SaveChestBalance(info, gil, now);
            else
                FreeCompanyChestStatus = Loc.L("FCチェスト：残高を確認中", "FC chest: confirming balance");
        }
        catch (Exception exception)
        {
            chestSession.Reset();
            FreeCompanyChestStatus = Loc.L("FCチェストの残高を取得できませんでした。保存済みの金額を表示しています。",
                "Could not read the FC chest balance. Displaying the last saved value.");
            Plugin.Log.Warning(exception, "FCチェストの残高を更新できませんでした。");
        }
    }

    private void SaveChestBalance(InfoProxyFreeCompany* info, uint gil, DateTime now)
    {
        var contentId = Plugin.PlayerState.ContentId;
        var name = info->NameString;
        FreeCompanyChestStatus = Loc.L($"FCチェスト確認済み：{name} / {gil:N0} G",
            $"FC chest verified: {name} / {gil:N0} G");
        if (plugin.Configuration.FreeCompanyGil.TryGetValue(info->Id, out var fc) &&
            fc.Gil == gil && fc.GilConfirmed &&
            (string.IsNullOrWhiteSpace(name) || fc.Name == name) &&
            fc.LastCheckedByContentId == contentId &&
            now - fc.UpdatedAt.ToUniversalTime() < TimeSpan.FromMinutes(1)) return;
        fc ??= new FreeCompanyGilRecord { FreeCompanyId = info->Id };
        if (!string.IsNullOrWhiteSpace(name)) fc.Name = name;
        else if (IsGeneratedFcName(fc.Name)) fc.Name = "不明なFC";
        fc.WorldName = Plugin.PlayerState.HomeWorld.Value.Name.ToString();
        fc.Gil = gil;
        fc.GilConfirmed = true;
        fc.UpdatedAt = DateTime.Now;
        fc.LastCheckedByContentId = contentId;
        fc.LastCheckedByName = Plugin.PlayerState.CharacterName;
        plugin.Configuration.FreeCompanyGil[info->Id] = fc;
        plugin.Configuration.Save();
    }

    private static bool IsGeneratedFcName(string name)
    {
        if (!name.StartsWith("FC ", StringComparison.Ordinal) || name.Length <= 3)
            return false;
        foreach (var character in name.AsSpan(3))
            if (!Uri.IsHexDigit(character))
                return false;
        return true;
    }

    private static bool UpdateSubmarines(FreeCompanyGilRecord fc, DateTime nowUtc)
    {
        var housing = HousingManager.Instance();
        if (housing->WorkshopTerritory == null)
            return false;

        var observed = new System.Collections.Generic.Dictionary<string, SubmarineRecord>();
        var vessels = housing->WorkshopTerritory->Submersible;
        for (var index = 0; index < Math.Min(4, vessels.DataPointers.Length); index++)
        {
            var vessel = vessels.DataPointers[index].Value;
            if (vessel == null)
                continue;
            var name = ReadUtf8(vessel->Name);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            observed[name] = new SubmarineRecord
            {
                Name = name,
                ReturnTimeUnix = vessel->ReturnTime,
                RoutePointIds = vessel->CurrentExplorationPoints.ToArray().Where(x => x != 0).ToArray(),
            };
        }
        if (observed.Count == 0)
            return false;

        var changed = observed.Count != fc.Submarines.Count;
        if (!changed)
            foreach (var pair in observed)
                if (!fc.Submarines.TryGetValue(pair.Key, out var current) ||
                    (current.ReturnTimeUnix != pair.Value.ReturnTimeUnix ||
                     !current.RoutePointIds.SequenceEqual(pair.Value.RoutePointIds)))
                {
                    changed = true;
                    break;
                }
        if (!changed)
            return false;

        fc.Submarines = observed;
        fc.SubmarinesUpdatedAt = nowUtc.ToLocalTime();
        return true;
    }

    private static bool CaptureTreasureVoyage(FreeCompanyGilRecord fc, DateTime nowUtc)
    {
        if (Plugin.GameGui.GetAddonByName("AirShipExplorationResult").Address == nint.Zero)
            return false;
        var territory = HousingManager.Instance()->WorkshopTerritory;
        if (territory == null)
            return false;
        var current = territory->Submersible.DataPointers[4].Value;
        if (current == null || current->GatheredData[0].ItemIdPrimary == 0)
            return false;

        var submarineName = ReadUtf8(current->Name);
        // RegisterTime is the vessel's registration date and never changes, so using it here
        // silently limited every submarine to one lifetime revenue record. ReturnTime identifies
        // the individual voyage shown by the result window.
        var returnedAt = (uint)new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        var voyageReturnTime = current->ReturnTime != 0 ? current->ReturnTime : returnedAt;
        var id = $"{fc.FreeCompanyId:X16}:{voyageReturnTime}:{submarineName}";
        if (fc.TreasureVoyages.Any(x => x.Id == id))
            return false;

        var itemCounts = new System.Collections.Generic.Dictionary<uint, uint>();
        foreach (var gathered in current->GatheredData)
        {
            AddTreasure(gathered.ItemIdPrimary, gathered.ItemCountPrimary);
            AddTreasure(gathered.ItemIdAdditional, gathered.ItemCountAdditional);
        }

        var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        ulong total = 0;
        foreach (var pair in itemCounts)
            total += (ulong)itemSheet.GetRow(pair.Key).PriceLow * pair.Value;

        fc.TreasureVoyages.Add(new SubmarineTreasureVoyageRecord
        {
            Id = id,
            SubmarineName = submarineName,
            // The result structure does not expose a reliable departure timestamp.
            DepartedAtUnix = 0,
            ReturnedAtUnix = returnedAt,
            TreasureGil = total,
            TreasureItems = itemCounts,
        });
        fc.TreasureVoyagesUpdatedAt = nowUtc.ToLocalTime();
        return true;

        void AddTreasure(uint itemId, ushort count)
        {
            if (itemId is < 22500 or > 22507 || count == 0)
                return;
            itemCounts.TryGetValue(itemId, out var currentCount);
            itemCounts[itemId] = currentCount + count;
        }
    }

    private static string ReadUtf8(byte* pointer, int maximumLength)
    {
        var length = 0;
        while (length < maximumLength && pointer[length] != 0)
            length++;
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(pointer, length);
    }

    private static string ReadUtf8(Span<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        var length = terminator < 0 ? bytes.Length : terminator;
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes[..length]);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.GameInventory.InventoryChangedRaw -= OnInventoryChanged;
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "FreeCompanyChest", OnChestOpened);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnChestClosing);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, CompanyAddon, OnCreditWindowReset);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, CompanyAddon, OnCreditWindowReset);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, CreditShopAddon, OnCreditWindowReset);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, CompanyAddon, OnCompanyCreditsUpdated);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, CreditShopAddon, OnCreditShopUpdated);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, CreditShopAddon, OnCreditShopUpdated);
    }
}
