using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;

namespace AltMate;

internal static unsafe class CrafterInventoryLocator
{
    private static readonly InventoryType[] PlayerBagContainers =
    {
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.Crystals,
    };

    internal static int PlayerInventoryCount(uint itemId)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null) return 0;
        var bags = 0;
        foreach (var type in PlayerBagContainers)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId != itemId || slot->Quantity == 0) continue;
                bags = checked(bags + (int)slot->Quantity);
            }
        }
        var equipped = 0;
        var equippedContainer = inventory->GetInventoryContainer(InventoryType.EquippedItems);
        if (equippedContainer != null && equippedContainer->IsLoaded)
        {
            for (var slotIndex = 0; slotIndex < equippedContainer->Size; slotIndex++)
            {
                var slot = equippedContainer->GetInventorySlot(slotIndex);
                if (slot != null && slot->ItemId == itemId)
                    equipped = checked(equipped + Math.Max(1, (int)slot->Quantity));
            }
        }
        return checked(bags + equipped);
    }

    internal static int PlayerInventoryCount(uint itemId, short minimumCollectability)
    {
        var inventory = InventoryManager.Instance();
        return inventory == null
            ? 0
            : inventory->GetInventoryItemCount(itemId, false, false, false, minimumCollectability);
    }

    internal static int PlayerInventoryCount(uint itemId, bool hqOnly)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null) return 0;
        var hq = inventory->GetInventoryItemCount(itemId, true, false, false);
        return hqOnly
            ? hq
            : checked(hq + inventory->GetInventoryItemCount(itemId, false, false, false));
    }

    internal static IReadOnlyList<string> GetQuestLocations(CrafterLevelingSettings settings, uint itemId,
        bool hqOnly)
    {
        var locations = new List<string>();
        var bags = PlayerInventoryCount(itemId, hqOnly);
        if (bags > 0)
            locations.Add(hqOnly
                ? Loc.L($"手持ちバッグ HQ ×{bags:N0}", $"Inventory HQ ×{bags:N0}")
                : Loc.L($"手持ちバッグ ×{bags:N0}", $"Inventory ×{bags:N0}"));
        foreach (var retainerId in settings.SelectedRetainerIds)
        {
            if (!settings.RetainerInventories.TryGetValue(retainerId, out var cache) ||
                !cache.Items.TryGetValue(itemId, out var count) || count <= 0) continue;
            locations.Add(hqOnly
                ? Loc.L($"{cache.RetainerName} ×{count:N0}（HQ未判定）",
                    $"{cache.RetainerName} ×{count:N0} (quality unknown)")
                : $"{cache.RetainerName} ×{count:N0}");
        }
        return locations;
    }

    internal static bool TryDiscardFirstBelowCollectability(uint itemId, int minimumCollectability)
    {
        var inventory = InventoryManager.Instance();
        var context = AgentInventoryContext.Instance();
        if (inventory == null || context == null)
            return false;
        foreach (var type in PlayerBagContainers)
        {
            if (type == InventoryType.Crystals)
                continue;
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;
            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId != itemId || slot->Quantity == 0 ||
                    slot->GetCollectability() >= minimumCollectability)
                    continue;
                context->DiscardItem(slot, type, slotIndex, 0, -1);
                return true;
            }
        }
        return false;
    }

    internal static IReadOnlyList<string> GetLocations(CrafterLevelingSettings settings, uint itemId)
    {
        var locations = new List<string>();
        var playerCount = PlayerInventoryCount(itemId);
        if (playerCount > 0) locations.Add(Loc.L($"手持ち・装備中 ×{playerCount:N0}", $"Inventory/equipped ×{playerCount:N0}"));
        foreach (var retainerId in settings.SelectedRetainerIds)
        {
            if (!settings.RetainerInventories.TryGetValue(retainerId, out var cache) ||
                !cache.Items.TryGetValue(itemId, out var count) || count <= 0) continue;
            locations.Add($"{cache.RetainerName} ×{count:N0}");
        }
        return locations;
    }
}
