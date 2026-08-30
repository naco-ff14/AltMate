using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

/// <summary>
/// Builds a material-safe craft plan from the character's current level/EXP.
/// Quality, first-craft bonuses and EXP buffs are deliberately ignored so the
/// preparation list does not underestimate the materials needed before starting.
/// </summary>
internal static class CrafterExperiencePlanner
{
    // Index = player level - apparent recipe level. Values are percentages.
    private static readonly int[] LevelDifferenceModifiers =
        [100, 96, 92, 88, 84, 80, 75, 70, 65, 60, 55, 45, 35, 25, 20, 18, 16, 15, 14, 13, 12, 10];

    // First-completion EXP by apparent recipe level (1-100). Normal synthesis base EXP is
    // floor(value / 3), matching the game's current crafting EXP lookup table.
    private static readonly int[] FirstCompletionExperience =
    [
        540, 582, 630, 795, 996, 1050, 1176, 1263, 1356, 1437, 1629, 1725, 1875, 1917, 2067,
        2241, 2409, 2556, 2700, 2841, 3045, 3240, 3429, 3612, 3783, 4383, 4683, 5199, 5511,
        5745, 6216, 6948, 7452, 7980, 8568, 9492, 10164, 10773, 11502, 12555, 13203, 13851,
        14499, 15147, 15795, 17334, 18549, 19764, 20979, 27786, 31500, 34800, 37791, 41571,
        45198, 48669, 51969, 52200, 52680, 52992, 55875, 58656, 61689, 65724, 66498, 66693,
        66900, 67410, 67530, 68244, 68250, 70074, 72552, 77865, 83079, 89211, 95982, 103551,
        111990, 134820, 139553, 154280, 157261, 175221, 182593, 202532, 208955, 226719,
        230926, 239004, 305279, 340725, 350860, 383995, 397220, 438995, 449528, 489453,
        499569, 522414,
    ];

    internal static void EnsurePlans(CrafterLevelingSettings settings)
    {
        var recipes = Plugin.DataManager.GetExcelSheet<Recipe>();
        foreach (var jobId in settings.EnabledJobIds.Where(x => x is >= 8 and <= 15))
        {
            var (level, experience) = JobProgress(jobId);
            if (level >= settings.TargetLevel)
                continue;

            foreach (var preset in settings.RecipePresets
                         .Where(x => x.JobId == jobId && x.MinLevel < settings.TargetLevel)
                         .OrderBy(x => x.MinLevel).ThenBy(x => x.RecipeId))
            {
                if (level >= settings.TargetLevel)
                    break;
                var stopLevel = Math.Min(settings.TargetLevel, preset.MaxLevel + 1);
                if (level >= stopLevel || !recipes.TryGetRow(preset.RecipeId, out var recipe))
                    continue;

                if (settings.PlannedCraftCounts.ContainsKey(preset.RecipeId))
                {
                    // Existing plans are kept while crafting. Live completed counts reduce the
                    // remaining material total without moving the original starting baseline.
                    SimulateExistingPlan(recipe, preset.Route, settings.PlannedCraftCounts[preset.RecipeId],
                        ref level, ref experience, stopLevel);
                    continue;
                }

                var count = CraftsToLevel(recipe, preset.Route, ref level, ref experience, stopLevel);
                settings.PlannedCraftCounts[preset.RecipeId] = count;
            }
        }
    }

    internal static int CraftsNeededNow(CrafterRecipePreset preset, int targetLevel)
    {
        if (!Plugin.DataManager.GetExcelSheet<Recipe>().TryGetRow(preset.RecipeId, out var recipe))
            return 1;
        var (level, experience) = JobProgress(preset.JobId);
        var stopLevel = Math.Min(targetLevel, preset.MaxLevel + 1);
        return Math.Max(1, CraftsToLevel(recipe, preset.Route, ref level, ref experience, stopLevel));
    }

    private static int CraftsToLevel(Recipe recipe, CrafterLevelingRoute route, ref int level,
        ref int experience, int stopLevel)
    {
        var count = 0;
        while (level < stopLevel && count < 100_000)
        {
            var gained = ExperiencePerCraft(recipe, route, level);
            if (gained <= 0)
                return Math.Max(1, count);
            experience = checked(experience + gained);
            AdvanceLevels(ref level, ref experience, stopLevel);
            count++;
        }
        return Math.Max(1, count);
    }

    private static void SimulateExistingPlan(Recipe recipe, CrafterLevelingRoute route, int count,
        ref int level, ref int experience, int stopLevel)
    {
        for (var index = 0; index < count && level < stopLevel; index++)
        {
            experience = checked(experience + ExperiencePerCraft(recipe, route, level));
            AdvanceLevels(ref level, ref experience, stopLevel);
        }
    }

    private static void AdvanceLevels(ref int level, ref int experience, int stopLevel)
    {
        var growth = Plugin.DataManager.GetExcelSheet<ParamGrow>();
        while (level < stopLevel && growth.TryGetRow((uint)level, out var row) && row.ExpToNext > 0 &&
               experience >= row.ExpToNext)
        {
            experience -= row.ExpToNext;
            level++;
        }
    }

    private static int ExperiencePerCraft(Recipe recipe, CrafterLevelingRoute route, int playerLevel)
    {
        var recipeLevel = (int)recipe.RecipeLevelTable.Value.ClassJobLevel;
        if (recipeLevel < 1 || recipeLevel > FirstCompletionExperience.Length)
            return 0;
        var difference = Math.Clamp(playerLevel - recipeLevel, 0, LevelDifferenceModifiers.Length - 1);
        var normalBase = FirstCompletionExperience[recipeLevel - 1] / 3;
        var synthesisExperience = normalBase * LevelDifferenceModifiers[difference] / 100;
        return checked(synthesisExperience +
                       (route == CrafterLevelingRoute.Restoration ? RestorationTurnInExperience(recipe) : 0));
    }

    private static int RestorationTurnInExperience(Recipe recipe)
    {
        var itemId = recipe.ItemResult.RowId;
        var rewards = Plugin.DataManager.GetExcelSheet<HWDCrafterSupplyReward>();
        foreach (var supply in Plugin.DataManager.GetExcelSheet<HWDCrafterSupply>())
        foreach (var entry in supply.HWDCrafterSupplyParams)
        {
            if (entry.ItemTradeIn.RowId != itemId)
                continue;
            var rewardId = entry.BaseCollectableRewardPostPhase.RowId != 0
                ? entry.BaseCollectableRewardPostPhase.RowId
                : entry.BaseCollectableReward.RowId;
            if (rewardId != 0 && rewards.TryGetRow(rewardId, out var reward))
                return checked((int)reward.ExpReward);
        }
        return 0;
    }

    private static unsafe (int Level, int Experience) JobProgress(uint jobId)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return (1, 0);
        var playerState = PlayerState.Instance();
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        if (playerState == null || !jobs.TryGetRow(jobId, out var job))
            return (1, 0);
        return (Math.Max(1, (int)playerState->ClassJobLevels[job.ExpArrayIndex]),
            Math.Max(0, (int)playerState->ClassJobExperience[job.ExpArrayIndex]));
    }
}
