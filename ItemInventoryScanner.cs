using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace AltMate;

internal sealed unsafe class ItemInventoryScanner : IDisposable
{
    private static readonly InventoryType[] PlayerContainers =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
        InventoryType.Inventory4, InventoryType.Crystals,
    };
    private static readonly InventoryType[] SaddlebagContainers =
    {
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    };
    private static readonly InventoryType[] ArmouryContainers =
    {
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal,
    };
    private static readonly InventoryType[] RetainerContainers =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    };
    private static readonly InventoryType[] FcContainers =
    {
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5, InventoryType.FreeCompanyCrystals,
    };

    private readonly Plugin plugin;
    private DateTime nextScanUtc;

    internal ItemInventoryScanner(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextScanUtc) return;
        nextScanUtc = now.AddSeconds(5);
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0) return;
        try
        {
            ScanCharacterContainers(now);
            ScanRetainer(now);
            ScanFreeCompanyChest(now);
            ScanArmoire(now);
            ScanGlamourDresser(now);
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "アイテム保管場所の読み取りに失敗しました。");
        }
    }

    private void ScanCharacterContainers(DateTime now)
    {
        var ownerKey = $"character:{Plugin.PlayerState.ContentId}";
        SaveContainers(ownerKey, Plugin.PlayerState.CharacterName,
            Plugin.PlayerState.HomeWorld.Value.Name.ToString(), ItemStorageKind.Inventory,
            Loc.L("所持品", "Inventory"), PlayerContainers, now, requireAll: true);
        SaveContainers(ownerKey, Plugin.PlayerState.CharacterName,
            Plugin.PlayerState.HomeWorld.Value.Name.ToString(), ItemStorageKind.Saddlebag,
            Loc.L("チョコボかばん", "Chocobo saddlebag"), SaddlebagContainers, now,
            requireAll: false, splitContainers: true);
        SaveContainers(ownerKey, Plugin.PlayerState.CharacterName,
            Plugin.PlayerState.HomeWorld.Value.Name.ToString(), ItemStorageKind.ArmouryChest,
            Loc.L("アーマリーチェスト", "Armoury chest"), ArmouryContainers, now,
            requireAll: false, splitContainers: true);
    }

    private void ScanRetainer(DateTime now)
    {
        var manager = RetainerManager.Instance();
        var active = manager == null ? null : manager->GetActiveRetainer();
        if (active == null || active->RetainerId == 0) return;
        SaveContainers($"retainer:{active->RetainerId}", $"{Plugin.PlayerState.CharacterName} / {active->NameString}",
            Plugin.PlayerState.HomeWorld.Value.Name.ToString(), ItemStorageKind.Retainer,
            active->NameString, RetainerContainers, now, requireAll: true);
    }

    private void ScanFreeCompanyChest(DateTime now)
    {
        var module = InfoModule.Instance();
        var info = module == null ? null : module->GetInfoProxyFreeCompany();
        if (info == null || info->Id == 0) return;
        SaveContainers($"fc:{info->Id}", string.IsNullOrWhiteSpace(info->NameString) ? Loc.L("不明なFC", "Unknown FC") : info->NameString,
            Plugin.PlayerState.HomeWorld.Value.Name.ToString(), ItemStorageKind.FreeCompanyChest,
            Loc.L("FCチェスト", "FC chest"), FcContainers, now, requireAll: false, splitContainers: true);
    }

    private void ScanArmoire(DateTime now)
    {
        var state = UIState.Instance();
        if (state == null || !state->Cabinet.IsCabinetLoaded()) return;
        var sheet = Plugin.DataManager.GetExcelSheet<CabinetSheet>();
        var rows = new List<StoredItemRecord>();
        foreach (var cabinet in sheet)
        {
            if (!state->Cabinet.IsItemInCabinet(cabinet.RowId) || cabinet.Item.RowId == 0) continue;
            rows.Add(CreateRecord(cabinet.Item.RowId, 1, false, ItemStorageKind.Armoire,
                $"character:{Plugin.PlayerState.ContentId}", Plugin.PlayerState.CharacterName,
                Plugin.PlayerState.HomeWorld.Value.Name.ToString(), Loc.L("愛蔵品キャビネット", "Armoire"), 0, now));
        }
        SaveSnapshot($"character:{Plugin.PlayerState.ContentId}", ItemStorageKind.Armoire, "armoire", rows);
    }

    private void ScanGlamourDresser(DateTime now)
    {
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded) return;
        var counts = manager->PrismBoxItemIds.ToArray().Where(x => x != 0)
            .GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        var storage = Loc.L("ミラージュドレッサー", "Glamour dresser");
        var ownerKey = $"character:{Plugin.PlayerState.ContentId}";
        SaveSnapshot(ownerKey, ItemStorageKind.GlamourDresser, "glamour-dresser",
            counts.Select(x => CreateRecord(x.Key, x.Value, false, ItemStorageKind.GlamourDresser,
                ownerKey, Plugin.PlayerState.CharacterName, Plugin.PlayerState.HomeWorld.Value.Name.ToString(),
                storage, 0, now)));
    }

    private void SaveContainers(string ownerKey, string ownerName, string worldName, ItemStorageKind kind,
        string storageName, InventoryType[] containerTypes, DateTime now, bool requireAll, bool splitContainers = false)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null) return;
        var loaded = new List<InventoryType>();
        foreach (var type in containerTypes)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container != null && container->IsLoaded) loaded.Add(type);
        }
        if (loaded.Count == 0 || (requireAll && loaded.Count != containerTypes.Length)) return;

        if (splitContainers)
        {
            foreach (var type in loaded)
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null) continue;
                var index = Array.IndexOf(containerTypes, type) + 1;
                var pageName = SplitStorageName(kind, type, index);
                SaveSnapshot(ownerKey, kind, $"container:{(uint)type}", Aggregate(ReadContainer(container, kind, ownerKey,
                    ownerName, worldName, pageName, index, now)));
            }
            return;
        }

        var combined = new List<StoredItemRecord>();
        foreach (var type in loaded)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container != null)
                combined.AddRange(ReadContainer(container, kind, ownerKey, ownerName, worldName,
                    storageName, Array.IndexOf(containerTypes, type) + 1, now));
        }
        SaveSnapshot(ownerKey, kind, "combined", Aggregate(combined));
    }

    private List<StoredItemRecord> ReadContainer(InventoryContainer* container, ItemStorageKind kind,
        string ownerKey, string ownerName, string worldName, string storageName, int containerIndex, DateTime now)
    {
        var rows = new List<StoredItemRecord>();
        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);
            if (slot == null || slot->ItemId == 0 || slot->Quantity == 0) continue;
            rows.Add(CreateRecord(slot->ItemId, (int)slot->Quantity,
                slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), kind, ownerKey, ownerName,
                worldName, storageName, containerIndex, now));
        }
        return rows;
    }

    private static IEnumerable<StoredItemRecord> Aggregate(IEnumerable<StoredItemRecord> source) =>
        source.GroupBy(x => (x.ItemId, x.IsHq)).Select(group =>
        {
            var row = group.First();
            row.Quantity = group.Sum(x => x.Quantity);
            return row;
        });

    private static string SplitStorageName(ItemStorageKind kind, InventoryType type, int index)
    {
        if (kind == ItemStorageKind.FreeCompanyChest)
            return type == InventoryType.FreeCompanyCrystals
                ? Loc.L("FCチェスト・クリスタル", "FC chest - crystals")
                : Loc.L($"FCチェスト 第{index}タブ", $"FC chest tab {index}");
        if (kind == ItemStorageKind.ArmouryChest)
            return type switch
            {
                InventoryType.ArmoryMainHand => Loc.L("アーマリーチェスト・メインアーム", "Armoury - main hand"),
                InventoryType.ArmoryOffHand => Loc.L("アーマリーチェスト・サブアーム", "Armoury - off hand"),
                InventoryType.ArmoryHead => Loc.L("アーマリーチェスト・頭防具", "Armoury - head"),
                InventoryType.ArmoryBody => Loc.L("アーマリーチェスト・胴防具", "Armoury - body"),
                InventoryType.ArmoryHands => Loc.L("アーマリーチェスト・手防具", "Armoury - hands"),
                InventoryType.ArmoryWaist => Loc.L("アーマリーチェスト・帯防具", "Armoury - waist"),
                InventoryType.ArmoryLegs => Loc.L("アーマリーチェスト・脚防具", "Armoury - legs"),
                InventoryType.ArmoryFeets => Loc.L("アーマリーチェスト・足防具", "Armoury - feet"),
                InventoryType.ArmoryEar => Loc.L("アーマリーチェスト・耳飾り", "Armoury - earrings"),
                InventoryType.ArmoryNeck => Loc.L("アーマリーチェスト・首飾り", "Armoury - necklace"),
                InventoryType.ArmoryWrist => Loc.L("アーマリーチェスト・腕輪", "Armoury - bracelets"),
                InventoryType.ArmoryRings => Loc.L("アーマリーチェスト・指輪", "Armoury - rings"),
                InventoryType.ArmorySoulCrystal => Loc.L("アーマリーチェスト・ソウルクリスタル", "Armoury - soul crystals"),
                _ => Loc.L($"アーマリーチェスト {index}", $"Armoury {index}"),
            };
        return type switch
        {
            InventoryType.SaddleBag1 => Loc.L("チョコボかばん 1", "Chocobo saddlebag 1"),
            InventoryType.SaddleBag2 => Loc.L("チョコボかばん 2", "Chocobo saddlebag 2"),
            InventoryType.PremiumSaddleBag1 => Loc.L("プレミアムチョコボかばん 1", "Premium saddlebag 1"),
            InventoryType.PremiumSaddleBag2 => Loc.L("プレミアムチョコボかばん 2", "Premium saddlebag 2"),
            _ => Loc.L($"保管先 {index}", $"Storage {index}"),
        };
    }

    private StoredItemRecord CreateRecord(uint itemId, int quantity, bool isHq, ItemStorageKind kind,
        string ownerKey, string ownerName, string worldName, string storageName, int containerIndex, DateTime now)
    {
        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(itemId);
        return new StoredItemRecord
        {
            Id = $"{ownerKey}:{(int)kind}:{storageName}:{itemId}:{isHq}", ItemId = itemId,
            ItemName = item.Name.ToString(), Quantity = quantity, IsHq = isHq, StorageKind = kind,
            OwnerKey = ownerKey, OwnerName = ownerName, WorldName = worldName, StorageName = storageName,
            ContainerIndex = containerIndex, ScannedAtUtc = now,
        };
    }

    private void SaveSnapshot(string ownerKey, ItemStorageKind kind, string storageKey,
        IEnumerable<StoredItemRecord> rows)
    {
        var snapshot = rows.ToArray();
        foreach (var row in snapshot) row.StorageKey = storageKey;
        plugin.ItemInventory.ReplaceSnapshot(ownerKey, kind, storageKey, snapshot);
    }

    public void Dispose() => Plugin.Framework.Update -= OnFrameworkUpdate;
}
