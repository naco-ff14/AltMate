using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AltMate;

internal static class CrafterPlanClipboard
{
    private const string FormatName = "AltMate.CrafterPlan";
    private const int CurrentVersion = 1;

    public static string ExportTable(CrafterLevelingSettings settings, int minLevel, int maxLevel)
    {
        var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var jobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var text = new StringBuilder("minLv\tMaxLv\tレシピ\t制作個数\r\n");
        foreach (var preset in settings.RecipePresets
                     .Where(x => x.MinLevel <= maxLevel && x.MaxLevel >= minLevel)
                     .OrderBy(x => x.MinLevel).ThenBy(x => x.JobId))
        {
            if (!recipeSheet.TryGetRow(preset.RecipeId, out var recipe)) continue;
            text.Append(preset.MinLevel).Append('\t').Append(preset.MaxLevel).Append('\t')
                .Append(recipe.ItemResult.Value.Name).Append('\t').Append(preset.MaxCraftCount).Append("\r\n");
        }

        text.Append("\r\n装備Lv\t対象職\t装備\r\n");
        foreach (var preset in settings.GearPresets
                     .Where(x => x.TierLevel >= minLevel && x.TierLevel <= maxLevel)
                     .OrderBy(x => x.TierLevel))
        {
            foreach (var itemId in preset.SharedItemIds)
                if (itemSheet.TryGetRow(itemId, out var item))
                    text.Append(preset.TierLevel).Append("\t共通\t").Append(item.Name).Append("\r\n");
            foreach (var jobItems in preset.JobItemIds.OrderBy(x => x.Key))
            {
                var job = jobSheet.TryGetRow(jobItems.Key, out var classJob)
                    ? classJob.Abbreviation.ToString()
                    : jobItems.Key.ToString();
                foreach (var itemId in jobItems.Value)
                    if (itemSheet.TryGetRow(itemId, out var item))
                        text.Append(preset.TierLevel).Append('\t').Append(job).Append('\t')
                            .Append(item.Name).Append("\r\n");
            }
        }
        return text.ToString();
    }

    public static bool TryImportTable(string text, out List<CrafterRecipePreset> recipes,
        out List<CrafterGearPreset> gear, out string error)
    {
        recipes = new List<CrafterRecipePreset>();
        gear = new List<CrafterGearPreset>();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = Loc.L("クリップボードが空です。", "The clipboard is empty.");
            return false;
        }

        var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var jobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var gearByTier = new Dictionary<int, CrafterGearPreset>();
        var section = 0;
        var lineNumber = 0;
        foreach (var rawLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var columns = rawLine.Split('\t').Select(x => x.Trim()).ToArray();
            if (columns.Length >= 4 && columns[0].Equals("minLv", StringComparison.OrdinalIgnoreCase))
            {
                section = 1;
                continue;
            }
            if (columns.Length >= 3 && columns[0] == "装備Lv")
            {
                section = 2;
                continue;
            }

            if (section == 1)
            {
                if (columns.Length < 4 || !int.TryParse(columns[0], out var minLevel) ||
                    !int.TryParse(columns[1], out var maxLevel) || !int.TryParse(columns[3], out var count) ||
                    minLevel is < 1 or > 100 || maxLevel is < 1 or > 100 || minLevel > maxLevel || count < 1)
                    return Fail(lineNumber, out error);
                var matches = recipeSheet.Where(x => x.ItemResult.RowId != 0 &&
                        x.ItemResult.Value.Name.ToString().Equals(columns[2], StringComparison.CurrentCultureIgnoreCase) &&
                        x.CraftType.RowId + 8 is >= 8 and <= 15)
                    .ToArray();
                if (matches.Length != 1)
                {
                    error = Loc.L($"{lineNumber}行目のレシピ名を一意に特定できません：{columns[2]}",
                        $"Recipe name on line {lineNumber} is missing or ambiguous: {columns[2]}");
                    return false;
                }
                var recipe = matches[0];
                recipes.Add(new CrafterRecipePreset
                {
                    JobId = recipe.CraftType.RowId + 8,
                    MinLevel = minLevel,
                    MaxLevel = maxLevel,
                    RecipeId = recipe.RowId,
                    MaxCraftCount = count,
                    Route = columns[2].Contains("復興", StringComparison.Ordinal)
                        ? CrafterLevelingRoute.Restoration
                        : CrafterLevelingRoute.Normal,
                });
                continue;
            }

            if (section == 2)
            {
                if (columns.Length < 3 || !int.TryParse(columns[0], out var tier) || tier is < 1 or > 100)
                    return Fail(lineNumber, out error);
                var items = itemSheet.Where(x => x.Name.ToString()
                        .Equals(columns[2], StringComparison.CurrentCultureIgnoreCase)).ToArray();
                if (items.Length != 1)
                {
                    error = Loc.L($"{lineNumber}行目の装備名を一意に特定できません：{columns[2]}",
                        $"Gear name on line {lineNumber} is missing or ambiguous: {columns[2]}");
                    return false;
                }
                if (!gearByTier.TryGetValue(tier, out var preset))
                {
                    preset = new CrafterGearPreset { TierLevel = tier };
                    gearByTier[tier] = preset;
                }
                if (columns[1] is "共通" or "Shared")
                {
                    preset.SharedItemIds.Add(items[0].RowId);
                    continue;
                }
                var jobs = jobSheet.Where(x => x.RowId is >= 8 and <= 15 && x.Abbreviation.ToString()
                    .Equals(columns[1], StringComparison.OrdinalIgnoreCase)).ToArray();
                if (jobs.Length != 1)
                {
                    error = Loc.L($"{lineNumber}行目の対象職が不明です：{columns[1]}",
                        $"Unknown crafting job on line {lineNumber}: {columns[1]}");
                    return false;
                }
                if (!preset.JobItemIds.TryGetValue(jobs[0].RowId, out var itemIds))
                    preset.JobItemIds[jobs[0].RowId] = itemIds = new List<uint>();
                itemIds.Add(items[0].RowId);
                continue;
            }

            error = Loc.L("先頭行に minLv / MaxLv / レシピ / 制作個数 の見出しが必要です。",
                "The first row must contain the minLv / MaxLv / Recipe / Craft count headers.");
            return false;
        }

        gear = gearByTier.Values.OrderBy(x => x.TierLevel).ToList();
        if (recipes.Count > 0 || gear.Count > 0) return true;
        error = Loc.L("登録できる行がありません。", "No importable rows were found.");
        return false;
    }

    private static bool Fail(int lineNumber, out string error)
    {
        error = Loc.L($"{lineNumber}行目の形式が正しくありません。",
            $"Line {lineNumber} has an invalid format.");
        return false;
    }

    private sealed class Document
    {
        public string Format { get; set; } = FormatName;
        public int Version { get; set; } = CurrentVersion;
        public List<CrafterRecipePreset> Recipes { get; set; } = new();
        public List<CrafterGearPreset> Gear { get; set; } = new();
    }

    public static string Export(CrafterLevelingSettings settings, int minLevel, int maxLevel)
    {
        var document = new Document
        {
            Recipes = settings.RecipePresets
                .Where(x => x.MinLevel <= maxLevel && x.MaxLevel >= minLevel)
                .Select(Clone).ToList(),
            Gear = settings.GearPresets
                .Where(x => x.TierLevel >= minLevel && x.TierLevel <= maxLevel)
                .Select(Clone).ToList(),
        };
        return JsonConvert.SerializeObject(document, Formatting.Indented);
    }

    public static bool TryImport(string json, out List<CrafterRecipePreset> recipes,
        out List<CrafterGearPreset> gear, out string error)
    {
        recipes = new List<CrafterRecipePreset>();
        gear = new List<CrafterGearPreset>();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = Loc.L("クリップボードが空です。", "The clipboard is empty.");
            return false;
        }

        Document? document;
        try
        {
            document = JsonConvert.DeserializeObject<Document>(json);
        }
        catch (JsonException exception)
        {
            error = Loc.L($"JSONを読み取れません：{exception.Message}",
                $"Could not read JSON: {exception.Message}");
            return false;
        }

        if (document == null || document.Format != FormatName || document.Version != CurrentVersion)
        {
            error = Loc.L("AltMateの対応するクラフター設定JSONではありません。",
                "This is not a supported AltMate crafter-plan JSON document.");
            return false;
        }

        document.Recipes ??= new List<CrafterRecipePreset>();
        document.Gear ??= new List<CrafterGearPreset>();
        var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();
        foreach (var preset in document.Recipes)
        {
            if (preset.JobId is < 8 or > 15 || preset.MinLevel is < 1 or > 100 ||
                preset.MaxLevel is < 1 or > 100 || preset.MinLevel > preset.MaxLevel ||
                preset.MaxCraftCount < 1 || preset.GearTier is < 0 or > 100 ||
                !Enum.IsDefined(typeof(CrafterLevelingRoute), preset.Route) ||
                !recipeSheet.TryGetRow(preset.RecipeId, out var recipe) ||
                recipe.ItemResult.RowId == 0 || recipe.CraftType.RowId + 8 != preset.JobId)
            {
                error = Loc.L($"無効なレシピ設定があります（Recipe ID: {preset.RecipeId}）。",
                    $"An invalid recipe entry was found (Recipe ID: {preset.RecipeId}).");
                return false;
            }
        }

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var preset in document.Gear)
        {
            preset.SharedItemIds ??= new List<uint>();
            preset.JobItemIds ??= new Dictionary<uint, List<uint>>();
            var itemIds = preset.SharedItemIds.Concat(preset.JobItemIds.Values.SelectMany(x => x ?? new List<uint>()));
            if (preset.TierLevel is < 1 or > 100 || preset.JobItemIds.Keys.Any(x => x is < 8 or > 15) ||
                itemIds.Any(itemId => itemId == 0 || !itemSheet.TryGetRow(itemId, out _)))
            {
                error = Loc.L($"無効な装備設定があります（Tier: {preset.TierLevel}）。",
                    $"An invalid gear entry was found (tier: {preset.TierLevel}).");
                return false;
            }
        }

        recipes = document.Recipes.Select(Clone).ToList();
        gear = document.Gear.Select(Clone).ToList();
        return true;
    }

    private static CrafterRecipePreset Clone(CrafterRecipePreset source) => new()
    {
        JobId = source.JobId,
        MinLevel = source.MinLevel,
        MaxLevel = source.MaxLevel,
        Route = source.Route,
        RecipeId = source.RecipeId,
        MaxCraftCount = source.MaxCraftCount,
        GearTier = source.GearTier,
        RequiredUnlock = source.RequiredUnlock ?? string.Empty,
        IsCatalogGenerated = source.IsCatalogGenerated,
    };

    private static CrafterGearPreset Clone(CrafterGearPreset source) => new()
    {
        TierLevel = source.TierLevel,
        SharedItemIds = (source.SharedItemIds ?? new List<uint>()).ToList(),
        JobItemIds = (source.JobItemIds ?? new Dictionary<uint, List<uint>>())
            .ToDictionary(x => x.Key, x => (x.Value ?? new List<uint>()).ToList()),
    };
}
