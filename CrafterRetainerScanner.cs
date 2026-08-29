using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;

namespace AltMate;

internal sealed unsafe class CrafterRetainerScanner : IDisposable
{
    private static readonly InventoryType[] RetainerContainers =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    };

    private readonly Plugin plugin;
    private DateTime lastCheckUtc;
    private ulong lastRetainerId;
    private int stableLoadedChecks;

    internal CrafterRetainerScanner(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now - lastCheckUtc < TimeSpan.FromSeconds(1)) return;
        lastCheckUtc = now;
        try
        {
            var retainers = RetainerManager.Instance();
            var inventory = InventoryManager.Instance();
            var active = retainers == null ? null : retainers->GetActiveRetainer();
            if (active == null || active->RetainerId == 0 || inventory == null)
            {
                lastRetainerId = 0;
                stableLoadedChecks = 0;
                return;
            }

            var allLoaded = true;
            foreach (var type in RetainerContainers)
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded) { allLoaded = false; break; }
            }
            if (!allLoaded)
            {
                stableLoadedChecks = 0;
                return;
            }
            stableLoadedChecks = active->RetainerId == lastRetainerId ? stableLoadedChecks + 1 : 1;
            lastRetainerId = active->RetainerId;
            if (stableLoadedChecks < 2) return;

            var items = new Dictionary<uint, int>();
            foreach (var type in RetainerContainers)
            {
                var container = inventory->GetInventoryContainer(type);
                for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
                {
                    var slot = container->GetInventorySlot(slotIndex);
                    if (slot == null || slot->ItemId == 0 || slot->Quantity == 0) continue;
                    items.TryGetValue(slot->ItemId, out var count);
                    items[slot->ItemId] = checked(count + (int)slot->Quantity);
                }
            }

            var settings = plugin.GetCrafterLevelingSettings();
            var name = active->NameString;
            var hadPrevious = settings.RetainerInventories.TryGetValue(active->RetainerId, out var previous);
            var inventoryChanged = !hadPrevious || !DictionariesEqual(previous!.Items, items);
            if (!inventoryChanged && now - previous!.ScannedAt.ToUniversalTime() < TimeSpan.FromMinutes(1))
                return;
            settings.RetainerInventories[active->RetainerId] = new CrafterRetainerInventoryCache
            {
                RetainerId = active->RetainerId,
                RetainerName = string.IsNullOrWhiteSpace(name) ? $"Retainer {active->RetainerId:X}" : name,
                Items = items,
                ScannedAt = DateTime.Now,
            };
            RefreshOwnedTotals(settings);
            if (inventoryChanged && settings.SelectedRetainerIds.Contains(active->RetainerId))
            {
                if (settings.Progress.State is CrafterLevelingState.Preparing or
                    CrafterLevelingState.WithdrawingItems or CrafterLevelingState.ReturningOldGear)
                    settings.Progress.State = CrafterLevelingState.Idle;
            }
            settings.Progress.UpdatedAt = DateTime.Now;
            plugin.Configuration.Save();
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "クラフター用リテイナー所持品を読み取れませんでした。");
        }
    }

    internal static void RefreshOwnedTotals(CrafterLevelingSettings settings)
    {
        settings.KnownOwnedItems.Clear();
        foreach (var retainerId in settings.SelectedRetainerIds)
        {
            if (!settings.RetainerInventories.TryGetValue(retainerId, out var cache)) continue;
            foreach (var item in cache.Items)
            {
                settings.KnownOwnedItems.TryGetValue(item.Key, out var current);
                settings.KnownOwnedItems[item.Key] = checked(current + item.Value);
            }
        }
    }

    private static bool DictionariesEqual(Dictionary<uint, int> left, Dictionary<uint, int> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
            if (!right.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
        return true;
    }

    public void Dispose() => Plugin.Framework.Update -= OnFrameworkUpdate;
}
