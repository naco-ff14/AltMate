using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal sealed record CrafterGearCraftingCandidate(uint RecipeId, uint ItemId, string Name, uint JobId);

internal static class CrafterGearCraftingService
{
    internal static IReadOnlyList<CrafterGearCraftingCandidate> Candidates()
    {
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        return Plugin.DataManager.GetExcelSheet<Recipe>()
            .Where(recipe => recipe.ItemResult.RowId != 0 &&
                             items.TryGetRow(recipe.ItemResult.RowId, out var item) &&
                             item.LevelEquip == 100 && item.EquipSlotCategory.RowId != 0 &&
                             recipe.CraftType.RowId + 8 is >= 8 and <= 15)
            .Select(recipe => new CrafterGearCraftingCandidate(recipe.RowId, recipe.ItemResult.RowId,
                recipe.ItemResult.Value.Name.ToString(), recipe.CraftType.RowId + 8))
            .OrderBy(x => x.JobId).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<CrafterPreparationItem> Materials(CrafterLevelingSettings settings,
        IReadOnlyDictionary<uint, int>? selections = null)
    {
        CrafterRetainerScanner.RefreshOwnedTotals(settings);
        var recipes = Plugin.DataManager.GetExcelSheet<Recipe>();
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        var required = new Dictionary<uint, int>();
        foreach (var selection in (selections ?? settings.GearCraftingSelections).Where(x => x.Value > 0))
        {
            if (!recipes.TryGetRow(selection.Key, out var recipe)) continue;
            var resultAmount = Math.Max(1, (int)recipe.AmountResult);
            var crafts = (selection.Value + resultAmount - 1) / resultAmount;
            for (var index = 0; index < recipe.Ingredient.Count; index++)
            {
                var itemId = recipe.Ingredient[index].RowId;
                var amount = recipe.AmountIngredient[index];
                if (itemId == 0 || amount == 0) continue;
                required.TryGetValue(itemId, out var current);
                required[itemId] = checked(current + (int)amount * crafts);
            }
        }

        return required.Select(pair =>
            {
                var name = items.TryGetRow(pair.Key, out var item) ? item.Name.ToString() : $"Item #{pair.Key}";
                settings.KnownOwnedItems.TryGetValue(pair.Key, out var retainerOwned);
                var owned = checked(retainerOwned + CrafterInventoryLocator.PlayerInventoryCount(pair.Key));
                return new CrafterPreparationItem(pair.Key, name, pair.Value, owned,
                    pair.Key is >= 2 and <= 19, false, 0);
            })
            .OrderByDescending(x => x.MissingCount > 0)
            .ThenBy(x => x.IsCrystal)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
