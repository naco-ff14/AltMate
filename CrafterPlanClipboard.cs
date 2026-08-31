using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal static class CrafterPlanClipboard
{
    private const string FormatName = "AltMate.CrafterPlan";
    private const int CurrentVersion = 1;

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
