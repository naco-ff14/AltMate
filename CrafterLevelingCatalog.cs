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
        new(8, 1, 20, "エルム材", "Elm Lumber", 20),
        new(8, 1, 20, "ユー材", "Yew Lumber", 20),
        new(9, 1, 20, "ブロンズインゴット", "Bronze Ingot", 20),
        new(9, 1, 20, "ブロンズバゼラード", "Bronze Baselard", 20),
        new(9, 1, 20, "アイアンインゴット", "Iron Ingot", 20),
        new(9, 1, 20, "アイアンリベット", "Iron Rivets", 20),
        new(10, 1, 20, "ブロンズプレート", "Bronze Plate", 20),
        new(10, 1, 20, "アイアンプレート", "Iron Plate", 20),
        new(10, 1, 20, "イニシエートフライパン", "Initiate's Skillet", 20),
        new(10, 1, 20, "アイアンガントレット", "Iron Gauntlets", 20),
        new(11, 1, 20, "カッパーインゴット", "Copper Ingot", 20),
        new(11, 1, 20, "ラグストーン砥石", "Ragstone Whetstone", 20),
        new(11, 1, 20, "ブラスインゴット", "Brass Ingot", 20),
        new(11, 1, 20, "ブラスリング", "Brass Ring", 20),
        new(11, 1, 20, "シルバーインゴット", "Silver Ingot", 20),
        new(12, 1, 20, "レザー", "Leather", 20),
        new(12, 1, 20, "ハードレザー", "Hard Leather", 20),
        new(12, 1, 20, "アルドゴートレザー", "Aldgoat Leather", 20),
        new(12, 1, 20, "ゴートリストガード", "Goatskin Wristguards", 20),
        new(13, 1, 20, "草布", "Hempen Cloth", 20),
        new(13, 1, 20, "綿布", "Undyed Cotton Cloth", 20),
        new(13, 1, 20, "コットンキャンバス", "Cotton Canvas", 20),
        new(13, 1, 20, "デューヤーン", "Dew Thread", 20),
        new(13, 1, 20, "別珍", "Velveteen", 20),
        new(14, 1, 20, "蒸留水", "Distilled Water", 20),
        new(14, 1, 20, "ラバー", "Rubber", 20),
        new(14, 1, 20, "蜜蝋", "Beeswax", 20),
        new(14, 1, 20, "ファイアブリック", "Fire Brick", 20),
        new(14, 1, 20, "重曹", "Natron", 20),
        new(15, 1, 20, "メープルシロップ", "Maple Syrup", 20),
        new(15, 1, 20, "バター", "Butter", 20),
        new(15, 1, 20, "トマトソース", "Tomato Sauce", 20),
        new(15, 1, 20, "サイダービネガー", "Cider Vinegar", 20),
        new(15, 1, 20, "ドライプルーン", "Dried Plums", 20),
    ];

    internal static ApplyResult ApplyLevel1To20(CrafterLevelingSettings settings)
    {
        var recipes = Plugin.DataManager.GetExcelSheet<Recipe>();
        var byName = recipes.Where(x => x.ItemResult.RowId != 0)
            .GroupBy(x => x.ItemResult.Value.Name.ToString(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.CurrentCultureIgnoreCase);
        var unresolved = new List<string>();
        var added = 0;
        var skipped = 0;

        foreach (var jobEntries in Level1To20.GroupBy(x => x.JobId))
        {
            var resolved = new List<(Entry Entry, Recipe Recipe, int RecipeLevel)>();
            foreach (var entry in jobEntries)
            {
                var matches = Find(byName, entry.JapaneseName).Concat(Find(byName, entry.EnglishName))
                    .DistinctBy(x => x.RowId).ToArray();
                if (matches.Length == 0)
                {
                    unresolved.Add(entry.JapaneseName);
                    continue;
                }
                var recipe = matches[0];
                resolved.Add((entry, recipe, recipe.RecipeLevelTable.Value.ClassJobLevel));
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

                var nextLevel = index + 1 < resolved.Count ? resolved[index + 1].RecipeLevel : 21;
                settings.RecipePresets.Add(new CrafterRecipePreset
                {
                    JobId = entry.JobId,
                    MinLevel = Math.Clamp(recipeLevel, 1, 20),
                    MaxLevel = Math.Clamp(nextLevel - 1, 1, 20),
                    RecipeId = recipe.RowId,
                    MaxCraftCount = entry.MaxCraftCount,
                    Route = CrafterLevelingRoute.Normal,
                });
                added++;
            }
        }

        return new ApplyResult(added, skipped, unresolved);
    }

    private static IEnumerable<Recipe> Find(Dictionary<string, Recipe[]> recipes, string name) =>
        recipes.TryGetValue(name, out var matches) ? matches : [];
}
