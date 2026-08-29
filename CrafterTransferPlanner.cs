using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal static unsafe class CrafterTransferPlanner
{
    internal static CrafterTransferPlan Build(CrafterLevelingSettings settings,
        IReadOnlyList<CrafterPreparationItem> preparation)
    {
        var plan = new CrafterTransferPlan { CreatedAt = DateTime.Now };
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var item in preparation)
        {
            var remaining = Math.Max(0, item.RequiredCount - PlayerInventoryCount(item.ItemId));
            foreach (var retainerId in settings.SelectedRetainerIds)
            {
                if (remaining == 0) break;
                if (!settings.RetainerInventories.TryGetValue(retainerId, out var cache) ||
                    !cache.Items.TryGetValue(item.ItemId, out var available) || available <= 0) continue;
                var quantity = Math.Min(remaining, available);
                plan.Withdrawals.Add(new CrafterTransferLine
                {
                    RetainerId = retainerId,
                    RetainerName = cache.RetainerName,
                    ItemId = item.ItemId,
                    ItemName = item.Name,
                    Quantity = quantity,
                    IsGear = item.IsGear,
                });
                remaining -= quantity;
            }
            if (remaining > 0)
                plan.UnavailableItems[item.ItemId] = remaining;
        }

        var nextTier = settings.GearPresets.Where(x => x.TierLevel > settings.Progress.CurrentGearTier &&
                                                       x.TierLevel <= settings.TargetLevel)
            .OrderBy(x => x.TierLevel).FirstOrDefault();
        if (nextTier is not null && settings.SelectedRetainerIds.FirstOrDefault() is var returnRetainerId &&
            returnRetainerId != 0 && settings.RetainerInventories.TryGetValue(returnRetainerId, out var returnCache))
        {
            foreach (var oldTier in settings.GearPresets.Where(x => x.TierLevel <= settings.Progress.CurrentGearTier))
            foreach (var itemId in oldTier.SharedItemIds.Concat(oldTier.JobItemIds.Values.SelectMany(x => x)).Distinct())
            {
                var quantity = PlayerInventoryCount(itemId);
                if (quantity <= 0) continue;
                var name = itemSheet.TryGetRow(itemId, out var row) ? row.Name.ToString() : $"Item #{itemId}";
                plan.Returns.Add(new CrafterTransferLine
                {
                    RetainerId = returnRetainerId,
                    RetainerName = returnCache.RetainerName,
                    ItemId = itemId,
                    ItemName = name,
                    Quantity = quantity,
                    IsGear = true,
                });
            }
        }
        return plan;
    }

    internal static int PlayerInventoryCount(uint itemId)
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetInventoryItemCount(itemId, false, false, false);
    }
}
