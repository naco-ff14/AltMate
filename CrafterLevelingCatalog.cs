using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal static class CrafterLevelingCatalog
{
    internal sealed record Entry(uint JobId, int MinLevel, int MaxLevel, string JapaneseName,
        string EnglishName, int MaxCraftCount);

    internal sealed record ApplyResult(int Added, int Skipped, IReadOnlyList<string> Unresolved);

    // Phase 1 draft from the agreed leveling route. IDs are deliberately not stored; the current
    // game data is resolved by product name so the user-facing plan remains readable.
    internal static readonly IReadOnlyList<Entry> Level1To20 =
    [
        new(8, 1, 20, "メープル材", "Maple Lumber", 20),
        new(8, 1, 20, "アッシュ材", "Ash Lumber", 20),
        new(8, 1, 20, "エルム材", "Elm Lumber", 40),
        new(8, 1, 20, "ユー材", "Yew Lumber", 30),
        new(9, 1, 20, "ブロンズインゴット", "Bronze Ingot", 30),
        new(9, 1, 20, "ブロンズバゼラード", "Bronze Baselard", 15),
        new(9, 1, 20, "アイアンインゴット", "Iron Ingot", 30),
        new(9, 1, 20, "アイアンリベット", "Iron Rivets", 30),
        new(10, 1, 20, "ブロンズプレート", "Bronze Plate", 30),
        new(10, 1, 20, "アイアンプレート", "Iron Plate", 30),
        new(10, 1, 20, "イニシエートフライパン", "Initiate's Skillet", 30),
        new(10, 1, 20, "アイアンガントレット", "Iron Gauntlets", 80),
        new(11, 1, 20, "カッパーインゴット", "Copper Ingot", 1),
        new(11, 1, 20, "ラグストーン砥石", "Ragstone Whetstone", 30),
        new(11, 1, 20, "ブラスインゴット", "Brass Ingot", 30),
        new(11, 1, 20, "ブラスリング", "Brass Ring", 30),
        new(11, 1, 20, "シルバーインゴット", "Silver Ingot", 20),
        new(12, 1, 20, "レザー", "Leather", 1),
        new(12, 1, 20, "ハードレザー", "Hard Leather", 30),
        new(12, 1, 20, "アルドゴートレザー", "Aldgoat Leather", 30),
        new(12, 1, 20, "ゴートリストガード", "Goatskin Wristguards", 20),
        new(13, 1, 20, "草布", "Hempen Cloth", 30),
        new(13, 1, 20, "綿布", "Undyed Cotton Cloth", 30),
        new(13, 1, 20, "コットンキャンバス", "Cotton Canvas", 30),
        new(13, 1, 20, "デューヤーン", "Dew Thread", 10),
        new(13, 1, 20, "別珍", "Velveteen", 20),
        new(14, 1, 20, "蒸留水", "Distilled Water", 1),
        new(14, 1, 20, "ラバー", "Rubber", 10),
        new(14, 1, 20, "蜜蝋", "Beeswax", 30),
        new(14, 1, 20, "ファイアブリック", "Fire Brick", 50),
        new(14, 1, 20, "重曹", "Natron", 10),
        new(15, 1, 20, "メープルシロップ", "Maple Syrup", 1),
        new(15, 1, 20, "バター", "Butter", 30),
        new(15, 1, 20, "トマトソース", "Tomato Sauce", 30),
        new(15, 1, 20, "サイダービネガー", "Cider Vinegar", 50),
        new(15, 1, 20, "ドライプルーン", "Dried Plums", 10),
    ];

    internal static ApplyResult ApplyStandard(CrafterLevelingSettings settings)
    {
        var recipes = Plugin.DataManager.GetExcelSheet<Recipe>();
        var byName = recipes.Where(x => x.ItemResult.RowId != 0)
            .GroupBy(x => x.ItemResult.Value.Name.ToString(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.CurrentCultureIgnoreCase);
        var unresolved = new List<string>();
        var added = 0;
        var skipped = 0;

        // Replace only presets created by this catalog. User-created presets remain untouched.
        settings.RecipePresets.RemoveAll(x => x.IsCatalogGenerated);

        foreach (var jobEntries in Level1To20.Where(x => settings.EnabledJobIds.Contains(x.JobId))
                     .GroupBy(x => x.JobId))
        {
            var resolved = new List<(Entry Entry, Recipe Recipe, int RecipeLevel)>();
            foreach (var entry in jobEntries)
            {
                var matches = Find(byName, entry.JapaneseName).Concat(Find(byName, entry.EnglishName))
                    .Where(x => x.CraftType.RowId + 8 == entry.JobId)
                    .DistinctBy(x => x.RowId).ToArray();
                if (matches.Length == 0)
                {
                    unresolved.Add(entry.JapaneseName);
                    continue;
                }
                var recipe = matches[0];
                var recipeLevel = recipe.RecipeLevelTable.Value.ClassJobLevel;
                if (recipeLevel < settings.TargetLevel)
                    resolved.Add((entry, recipe, recipeLevel));
            }

            resolved.Sort((left, right) => left.RecipeLevel.CompareTo(right.RecipeLevel));
            for (var index = 0; index < resolved.Count; index++)
            {
                var (entry, recipe, recipeLevel) = resolved[index];
                if (settings.RecipePresets.Any(x => x.RecipeId == recipe.RowId && x.JobId == entry.JobId))
                {
                    skipped++;
                    continue;
                }

                var nextLevel = index + 1 < resolved.Count
                    ? resolved[index + 1].RecipeLevel
                    : Math.Min(21, settings.TargetLevel + 1);
                settings.RecipePresets.Add(new CrafterRecipePreset
                {
                    JobId = entry.JobId,
                    MinLevel = Math.Clamp(recipeLevel, 1, 20),
                    MaxLevel = Math.Clamp(nextLevel - 1, 1, Math.Min(20, settings.TargetLevel)),
                    RecipeId = recipe.RowId,
                    MaxCraftCount = entry.MaxCraftCount,
                    Route = CrafterLevelingRoute.Normal,
                    IsCatalogGenerated = true,
                });
                added++;
            }
        }

        var upperLevel = Math.Min(settings.TargetLevel, 50);
        for (var minLevel = 21; minLevel <= upperLevel; minLevel += 5)
        {
            var maxLevel = Math.Min(minLevel + 4, upperLevel);
            foreach (var jobId in settings.EnabledJobIds.Where(x => x is >= 8 and <= 15).OrderBy(x => x))
            {
                var candidate = recipes
                    .Where(x => x.ItemResult.RowId != 0 && x.CraftType.RowId + 8 == jobId)
                    .Where(x =>
                    {
                        var level = x.RecipeLevelTable.Value.ClassJobLevel;
                        var name = x.ItemResult.Value.Name.ToString().TrimStart();
                        return level <= minLevel && level >= Math.Max(1, minLevel - 4) &&
                               !name.StartsWith('†');
                    })
                    .OrderByDescending(x => x.RecipeLevelTable.Value.ClassJobLevel)
                    .ThenBy(x => x.Ingredient.Count(ingredient => ingredient.RowId != 0))
                    .ThenBy(x => x.RowId)
                    .FirstOrDefault();
                if (candidate.RowId == 0)
                {
                    unresolved.Add($"Job {jobId} Lv{minLevel}-{maxLevel}");
                    continue;
                }
                if (settings.RecipePresets.Any(x => x.RecipeId == candidate.RowId && x.JobId == jobId))
                {
                    skipped++;
                    continue;
                }
                settings.RecipePresets.Add(new CrafterRecipePreset
                {
                    JobId = jobId,
                    MinLevel = minLevel,
                    MaxLevel = maxLevel,
                    RecipeId = candidate.RowId,
                    MaxCraftCount = EstimatedCraftCount(minLevel),
                    Route = CrafterLevelingRoute.Normal,
                    IsCatalogGenerated = true,
                });
                added++;
            }
        }

        settings.RecipePresets.Sort((left, right) =>
        {
            var job = left.JobId.CompareTo(right.JobId);
            return job != 0 ? job : left.MinLevel.CompareTo(right.MinLevel);
        });

        return new ApplyResult(added, skipped, unresolved);
    }

    private static IEnumerable<Recipe> Find(Dictionary<string, Recipe[]> recipes, string name) =>
        recipes.TryGetValue(name, out var matches) ? matches : [];

    // Public leveling guides estimate about 25 restoration crafts for Lv21-41 and
    // about 20 for Lv41-53. Distribute those totals over the current five-level bands.
    private static int EstimatedCraftCount(int minLevel) => minLevel switch
    {
        21 => 7,
        26 or 31 or 36 => 6,
        41 or 46 => 10,
        _ => 20,
    };
}
