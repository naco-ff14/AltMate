using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal sealed class CrafterLevelingAutomation : IDisposable
{
    private readonly Plugin plugin;
    private IReadOnlyList<CrafterRecipePreset> queue = [];
    private int index;
    private bool requestSent;
    private bool artisanBecameBusy;
    private DateTime requestAtUtc;
    private int lastStylistTier = -1;
    private DateTime waitUntilUtc;

    internal bool IsRunning { get; private set; }
    internal string Status { get; private set; } = Loc.L("待機中", "Idle");
    internal int Current => IsRunning ? Math.Min(index + 1, queue.Count) : 0;
    internal int Total => queue.Count;

    internal CrafterLevelingAutomation(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    internal bool Start(CrafterLevelingSettings settings)
    {
        if (IsRunning)
            return false;
        if (!CustomDeliveryService.IsPluginLoaded("Artisan"))
            return Fail(settings, Loc.L("Artisanが読み込まれていません。", "Artisan is not loaded."));
        if (!CustomDeliveryService.IsPluginLoaded("Stylist"))
            return Fail(settings, Loc.L("装備更新に必要なStylistが読み込まれていません。",
                "Stylist is required for gear updates but is not loaded."));
        if (plugin.CustomDeliveries.Automation.IsRunning)
            return Fail(settings, Loc.L("お得意様取引の自動処理を先に停止してください。",
                "Stop custom-delivery automation first."));

        var jobId = Plugin.PlayerState.ClassJob.RowId;
        if (jobId is < 8 or > 15 || !settings.EnabledJobIds.Contains(jobId))
            return Fail(settings, Loc.L("選択したクラフター職に着替えてから開始してください。",
                "Switch to one of the selected crafting jobs before starting."));

        var level = Plugin.PlayerState.Level;
        queue = settings.RecipePresets
            .Where(x => x.JobId == jobId && x.MinLevel < settings.TargetLevel && x.MaxLevel >= level)
            .OrderBy(x => x.MinLevel)
            .ThenBy(x => x.RecipeId)
            .Select(Clone)
            .ToArray();
        if (queue.Count == 0)
            return Fail(settings, Loc.L("現在レベルから目標レベルまでの製作品がありません。",
                "No recipes apply between the current and target levels."));

        index = 0;
        requestSent = false;
        artisanBecameBusy = false;
        lastStylistTier = -1;
        waitUntilUtc = DateTime.MinValue;
        IsRunning = true;
        settings.Progress.State = CrafterLevelingState.CraftingNormal;
        settings.Progress.CurrentJobId = jobId;
        settings.Progress.LastError = string.Empty;
        settings.Progress.UpdatedAt = DateTime.Now;
        plugin.Configuration.Save();
        Status = Loc.L("Artisanへ最初の製作を依頼します。", "Sending the first craft to Artisan.");
        return true;
    }

    internal void Stop(string? reason = null)
    {
        if (!IsRunning && reason is null)
            return;
        IsRunning = false;
        Status = reason ?? Loc.L("停止しました。", "Stopped.");
        var settings = plugin.GetCrafterLevelingSettings();
        settings.Progress.State = reason is null ? CrafterLevelingState.Paused : CrafterLevelingState.Error;
        settings.Progress.LastError = reason ?? string.Empty;
        settings.Progress.UpdatedAt = DateTime.Now;
        plugin.Configuration.Save();
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus")
                .InvokeAction(false);
        }
        catch
        {
            // Artisan may already be idle or may not expose a stop endpoint in this version.
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning)
            return;
        if (DateTime.UtcNow < waitUntilUtc)
            return;
        if (!Plugin.PlayerState.IsLoaded)
        {
            Stop(Loc.L("ログアウトしたため停止しました。", "Stopped because the character logged out."));
            return;
        }
        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.Unconscious])
        {
            Stop(Loc.L("戦闘状態を検出したため停止しました。", "Stopped because combat was detected."));
            return;
        }

        bool artisanBusy;
        try
        {
            artisanBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus").InvokeFunc() ||
                          Plugin.Condition[ConditionFlag.Crafting] ||
                          Plugin.Condition[ConditionFlag.PreparingToCraft];
        }
        catch (Exception ex)
        {
            Stop(Loc.L($"Artisanとの通信に失敗しました：{ex.Message}",
                $"Artisan communication failed: {ex.Message}"));
            return;
        }

        if (artisanBusy)
        {
            artisanBecameBusy = true;
            Status = Loc.L(
                $"製作中：{RecipeName(queue[index].RecipeId)}（レシピ段階 {index + 1}/{queue.Count}・{NextSwitchLabel()}）",
                $"Crafting: {RecipeName(queue[index].RecipeId)} (recipe stage {index + 1}/{queue.Count}; {NextSwitchLabel(false)})");
            return;
        }

        if (requestSent)
        {
            if (!artisanBecameBusy)
            {
                if (DateTime.UtcNow - requestAtUtc > TimeSpan.FromSeconds(10))
                    Stop(Loc.L("Artisanが製作を開始できませんでした。素材・装備・レシピ解放状況を確認してください。",
                        "Artisan could not start. Check materials, gear, and recipe unlocks."));
                return;
            }
            var settings = plugin.GetCrafterLevelingSettings();
            var completedRecipeId = queue[index].RecipeId;
            settings.CompletedCraftCounts.TryGetValue(completedRecipeId, out var completedCrafts);
            settings.CompletedCraftCounts[completedRecipeId] = checked(completedCrafts + 1);
            settings.Progress.UpdatedAt = DateTime.Now;
            plugin.Configuration.Save();
            requestSent = false;
            artisanBecameBusy = false;
        }

        var currentLevel = Plugin.PlayerState.Level;
        while (index + 1 < queue.Count && currentLevel >= queue[index + 1].MinLevel)
            index++;
        if (index >= queue.Count || currentLevel >= plugin.GetCrafterLevelingSettings().TargetLevel)
        {
            Complete();
            return;
        }

        var stylistTier = CrafterGearCatalog.TierLevels.Where(x => x <= currentLevel).DefaultIfEmpty(1).Max();
        if (stylistTier > lastStylistTier)
        {
            try
            {
                var stylistBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Stylist.IsBusy").InvokeFunc();
                if (stylistBusy)
                {
                    Status = Loc.L("Stylistで装備更新中です。", "Stylist is updating gear.");
                    return;
                }
                Plugin.PluginInterface.GetIpcSubscriber<bool?, bool?, object>("Stylist.UpdateCurrentGearsetEx")
                    .InvokeAction(true, true);
                lastStylistTier = stylistTier;
                waitUntilUtc = DateTime.UtcNow.AddSeconds(2);
                Status = Loc.L($"StylistでLv{stylistTier}装備へ更新します。",
                    $"Updating to Lv{stylistTier} gear with Stylist.");
                return;
            }
            catch (Exception ex)
            {
                Stop(Loc.L($"Stylistで装備を更新できませんでした：{ex.Message}",
                    $"Stylist could not update gear: {ex.Message}"));
                return;
            }
        }

        var preset = queue[index];
        var missingIngredients = MissingIngredients(preset.RecipeId);
        if (missingIngredients.Count > 0)
        {
            Stop(Loc.L($"素材不足のため停止しました：{string.Join("、", missingIngredients)}",
                $"Stopped because materials are missing: {string.Join(", ", missingIngredients)}"));
            return;
        }
        if (preset.RecipeId > ushort.MaxValue)
        {
            Stop(Loc.L($"Artisanに渡せないレシピIDです：{preset.RecipeId}",
                $"Recipe ID is not supported by Artisan: {preset.RecipeId}"));
            return;
        }
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem")
                .InvokeAction((ushort)preset.RecipeId, 1);
            requestSent = true;
            requestAtUtc = DateTime.UtcNow;
            Status = Loc.L(
                $"Artisanへ1個ずつ依頼中：{RecipeName(preset.RecipeId)}（レシピ段階 {index + 1}/{queue.Count}・{NextSwitchLabel()}）",
                $"Sending one craft at a time to Artisan: {RecipeName(preset.RecipeId)} (recipe stage {index + 1}/{queue.Count}; {NextSwitchLabel(false)})");
        }
        catch (Exception ex)
        {
            Stop(Loc.L($"Artisanへの製作依頼に失敗しました：{ex.Message}",
                $"Failed to request crafting from Artisan: {ex.Message}"));
        }
    }

    private void Complete()
    {
        IsRunning = false;
        Status = Loc.L("現在のクラフター職の製作が完了しました。", "Crafting for the current job is complete.");
        var settings = plugin.GetCrafterLevelingSettings();
        settings.Progress.State = CrafterLevelingState.Completed;
        settings.Progress.LastError = string.Empty;
        settings.Progress.UpdatedAt = DateTime.Now;
        plugin.Configuration.Save();
    }

    private bool Fail(CrafterLevelingSettings settings, string reason)
    {
        Status = reason;
        settings.Progress.State = CrafterLevelingState.Error;
        settings.Progress.LastError = reason;
        settings.Progress.UpdatedAt = DateTime.Now;
        plugin.Configuration.Save();
        return false;
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
        RequiredUnlock = source.RequiredUnlock,
        IsCatalogGenerated = source.IsCatalogGenerated,
    };

    private static string RecipeName(uint recipeId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Recipe>();
        return sheet.TryGetRow(recipeId, out var recipe)
            ? recipe.ItemResult.Value.Name.ToString()
            : $"Recipe #{recipeId}";
    }

    private string NextSwitchLabel(bool japanese = true)
    {
        var targetLevel = index + 1 < queue.Count
            ? queue[index + 1].MinLevel
            : plugin.GetCrafterLevelingSettings().TargetLevel;
        return japanese ? $"次の切替 Lv{targetLevel}" : $"next change at Lv{targetLevel}";
    }

    private static IReadOnlyList<string> MissingIngredients(uint recipeId)
    {
        var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (!recipeSheet.TryGetRow(recipeId, out var recipe))
            return [$"Recipe #{recipeId}"];
        var missing = new List<string>();
        for (var index = 0; index < recipe.Ingredient.Count; index++)
        {
            var itemId = recipe.Ingredient[index].RowId;
            var required = recipe.AmountIngredient[index];
            if (itemId == 0 || required == 0) continue;
            var owned = CrafterInventoryLocator.PlayerInventoryCount(itemId);
            if (owned >= required) continue;
            var name = itemSheet.TryGetRow(itemId, out var item) ? item.Name.ToString() : $"Item #{itemId}";
            missing.Add($"{name} {owned}/{required}");
        }
        return missing;
    }

    public void Dispose() => Plugin.Framework.Update -= OnFrameworkUpdate;
}
