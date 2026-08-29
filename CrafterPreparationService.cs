using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal sealed class CrafterPreparationService
{
    internal IReadOnlyList<CrafterPreparationItem> Build(CrafterLevelingSettings settings,
        out IReadOnlyList<string> errors)
    {
        var required = new Dictionary<uint, (int Count, bool Crystal, bool Gear)>();
        var problems = new List<string>();
        var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();

        foreach (var preset in settings.RecipePresets.Where(x =>
                     settings.EnabledJobIds.Contains(x.JobId) && x.MaxLevel <= settings.TargetLevel &&
                     JobLevel(x.JobId) < settings.TargetLevel && x.MaxLevel >= JobLevel(x.JobId)))
        {
            if (preset.RecipeId == 0 || preset.MaxCraftCount <= 0 ||
                !recipeSheet.TryGetRow(preset.RecipeId, out var recipe))
            {
                problems.Add($"Recipe #{preset.RecipeId} ({preset.JobId}, Lv{preset.MinLevel}-{preset.MaxLevel})");
                continue;
            }

            if (!settings.PlannedCraftCounts.TryGetValue(preset.RecipeId, out var plannedCrafts))
            {
                plannedCrafts = RemainingCraftCount(preset, JobLevel(preset.JobId));
                settings.PlannedCraftCounts[preset.RecipeId] = plannedCrafts;
            }
            settings.CompletedCraftCounts.TryGetValue(preset.RecipeId, out var completedCrafts);
            var craftCount = Math.Max(0, plannedCrafts - completedCrafts);
            for (var index = 0; index < recipe.Ingredient.Count; index++)
            {
                var itemId = recipe.Ingredient[index].RowId;
                var amount = recipe.AmountIngredient[index];
                if (itemId == 0 || amount == 0)
                    continue;
                Add(required, itemId, checked((int)amount * craftCount), IsCrystal(itemId), false);
            }
        }

        foreach (var gear in settings.GearPresets.Where(x => x.TierLevel <= settings.TargetLevel))
        {
            foreach (var itemId in gear.SharedItemIds.Where(x => x != 0))
                Add(required, itemId, 1, false, true);
            foreach (var job in gear.JobItemIds.Where(x => settings.EnabledJobIds.Contains(x.Key)))
                foreach (var itemId in job.Value.Where(x => x != 0))
                    Add(required, itemId, 1, false, true);
        }

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        errors = problems;
        return required.Select(pair =>
            {
                var name = itemSheet.TryGetRow(pair.Key, out var item) ? item.Name.ToString() : $"Item #{pair.Key}";
                settings.KnownOwnedItems.TryGetValue(pair.Key, out var retainerOwned);
                var owned = checked(retainerOwned + CrafterInventoryLocator.PlayerInventoryCount(pair.Key));
                var equipLevel = pair.Value.Gear && itemSheet.TryGetRow(pair.Key, out var gearItem)
                    ? gearItem.LevelEquip
                    : 0;
                return new CrafterPreparationItem(pair.Key, name, pair.Value.Count, owned,
                    pair.Value.Crystal, pair.Value.Gear, equipLevel);
            })
            .OrderByDescending(x => x.MissingCount > 0)
            .ThenBy(x => x.IsGear)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void Add(Dictionary<uint, (int Count, bool Crystal, bool Gear)> required, uint itemId,
        int count, bool crystal, bool gear)
    {
        required.TryGetValue(itemId, out var current);
        required[itemId] = (checked(current.Count + count), current.Crystal || crystal, current.Gear || gear);
    }

    private static bool IsCrystal(uint itemId) => itemId is >= 2 and <= 19;

    private static int RemainingCraftCount(CrafterRecipePreset preset, int jobLevel)
    {
        if (jobLevel <= preset.MinLevel)
            return preset.MaxCraftCount;
        var totalLevels = Math.Max(1, preset.MaxLevel - preset.MinLevel + 1);
        var remainingLevels = Math.Max(1, preset.MaxLevel - jobLevel + 1);
        return Math.Max(1, (int)Math.Ceiling(preset.MaxCraftCount * remainingLevels / (double)totalLevels));
    }

    private static unsafe int JobLevel(uint jobId)
    {
        if (!Plugin.PlayerState.IsLoaded) return 0;
        if (Plugin.PlayerState.ClassJob.RowId == jobId) return Plugin.PlayerState.Level;
        var playerState = PlayerState.Instance();
        var classJobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        if (playerState == null || !classJobs.TryGetRow(jobId, out var job)) return 0;
        return playerState->ClassJobLevels[job.ExpArrayIndex];
    }
}
