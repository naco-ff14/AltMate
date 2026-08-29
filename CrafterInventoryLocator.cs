using FFXIVClientStructs.FFXIV.Client.Game;
using System.Collections.Generic;

namespace AltMate;

internal static unsafe class CrafterInventoryLocator
{
    internal static int PlayerInventoryCount(uint itemId)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null) return 0;
        var normal = inventory->GetInventoryItemCount(itemId, false, false, false);
        var highQuality = inventory->GetInventoryItemCount(itemId, true, false, false);
        return checked(normal + highQuality);
    }

    internal static IReadOnlyList<string> GetLocations(CrafterLevelingSettings settings, uint itemId)
    {
        var locations = new List<string>();
        var playerCount = PlayerInventoryCount(itemId);
        if (playerCount > 0) locations.Add(Loc.L($"手持ちバッグ ×{playerCount:N0}", $"Inventory ×{playerCount:N0}"));
        foreach (var retainerId in settings.SelectedRetainerIds)
        {
            if (!settings.RetainerInventories.TryGetValue(retainerId, out var cache) ||
                !cache.Items.TryGetValue(itemId, out var count) || count <= 0) continue;
            locations.Add($"{cache.RetainerName} ×{count:N0}");
        }
        return locations;
    }
}
