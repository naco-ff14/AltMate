using Dalamud.Game;
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
        new(10, 1, 20, "イニシエートスキレット", "Initiate's Skillet", 30),
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
        // Use the actual Japanese client item names. The previous translated labels were not
        // present in the Japanese sheet, so these stages could be dropped and Undyed Cotton
        // Cloth was incorrectly stretched all the way to the target level.
        new(13, 1, 20, "綿帆布", "Cotton Canvas", 30),
        new(13, 1, 20, "露糸", "Dew Thread", 10),
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

    // The Lv20/Lv40 Grade 4 restoration recipes are intentionally explicit. Choosing an
    // arbitrary recipe near the player's level can select furnishings or obsolete gear.
    internal static readonly IReadOnlyList<Entry> RestorationLevel21To50 =
    [
        new(8, 21, 40, "第四次復興用の合板", "Grade 4 Skybuilders' Plywood", 25),
        new(9, 21, 40, "第四次復興用の合金", "Grade 4 Skybuilders' Alloy", 25),
        new(10, 21, 40, "第四次復興用の金属板", "Grade 4 Skybuilders' Steel Plate", 25),
        new(11, 21, 40, "第四次復興用の地金", "Grade 4 Skybuilders' Ingot", 25),
        new(12, 21, 40, "第四次復興用のなめし革", "Grade 4 Skybuilders' Leather", 25),
        new(13, 21, 40, "第四次復興用の荒縄", "Grade 4 Skybuilders' Rope", 25),
        new(14, 21, 40, "第四次復興用のインク", "Grade 4 Skybuilders' Ink", 25),
        new(15, 21, 40, "第四次復興用のヘンプミルク", "Grade 4 Skybuilders' Hemp Milk", 25),
        new(8, 41, 50, "第四次復興用の木箱", "Grade 4 Skybuilders' Crate", 20),
        new(9, 41, 50, "第四次復興用の鉄釘", "Grade 4 Skybuilders' Nails", 20),
        new(10, 41, 50, "第四次復興用のリベット", "Grade 4 Skybuilders' Rivets", 20),
        new(11, 41, 50, "第四次復興用の鉄環", "Grade 4 Skybuilders' Rings", 20),
        new(12, 41, 50, "第四次復興用の革紐", "Grade 4 Skybuilders' Leather Straps", 20),
        new(13, 41, 50, "第四次復興用の生地", "Grade 4 Skybuilders' Cloth", 20),
        new(14, 41, 50, "第四次復興用の植物油", "Grade 4 Skybuilders' Plant Oil", 20),
        new(15, 41, 50, "第四次復興用のセサミクッキー", "Grade 4 Skybuilders' Sesame Cookie", 20),
    ];

    internal static ApplyResult ApplyStandard(CrafterLevelingSettings settings)
    {
        var recipes = Plugin.DataManager.GetExcelSheet<Recipe>();
        // Product names are localized. Include the English sheet as a stable fallback so a
        // translated catalog typo cannot silently remove an entire leveling stage.
        var localizedRecipes = recipes.Concat(
            Plugin.DataManager.GetExcelSheet<Recipe>(ClientLanguage.English));
        var byName = localizedRecipes.Where(x => x.ItemResult.RowId != 0)
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

        foreach (var entry in RestorationLevel21To50
                     .Where(x => settings.EnabledJobIds.Contains(x.JobId) && x.MinLevel < settings.TargetLevel))
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
            if (settings.RecipePresets.Any(x => x.RecipeId == recipe.RowId && x.JobId == entry.JobId))
            {
                skipped++;
                continue;
            }

            var maxLevel = Math.Min(entry.MaxLevel, settings.TargetLevel - 1);
            var fullLevelCount = entry.MaxLevel - entry.MinLevel + 1;
            var selectedLevelCount = maxLevel - entry.MinLevel + 1;
            settings.RecipePresets.Add(new CrafterRecipePreset
            {
                JobId = entry.JobId,
                MinLevel = entry.MinLevel,
                MaxLevel = maxLevel,
                RecipeId = recipe.RowId,
                MaxCraftCount = Math.Max(1,
                    (int)Math.Ceiling(entry.MaxCraftCount * selectedLevelCount / (double)fullLevelCount)),
                Route = CrafterLevelingRoute.Restoration,
                RequiredUnlock = "Towards the Firmament",
                IsCatalogGenerated = true,
            });
            added++;
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

}
