using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
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
    private int lastStylistLevel = -1;
    private DateTime waitUntilUtc;
    private int requestedCraftCount;
    private int requestProductCount;
    private int creditedCraftCount;
    private bool stopRequested;
    private int requestStopLevel;
    private uint pendingJobId;
    private DateTime jobChangeRequestedAtUtc;

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
        LoadQueue(settings, jobId, level);
        if (queue.Count == 0)
            return Fail(settings, Loc.L("現在レベルから目標レベルまでの製作品がありません。",
                "No recipes apply between the current and target levels."));

        index = 0;
        requestSent = false;
        artisanBecameBusy = false;
        lastStylistLevel = -1;
        requestedCraftCount = 0;
        requestProductCount = 0;
        creditedCraftCount = 0;
        stopRequested = false;
        requestStopLevel = 0;
        pendingJobId = 0;
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
        if (pendingJobId != 0)
        {
            ContinueJobChange();
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
            var artisanEndurance =
                Plugin.PluginInterface.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus").InvokeFunc();
            var activelyCrafting = Plugin.Condition[ConditionFlag.Crafting];
            var preparingToCraft = Plugin.Condition[ConditionFlag.PreparingToCraft];
            // PreparingToCraft remains set while the crafting log is open. Once an endurance
            // stop was requested, treating that flag as busy prevents the recipe transition
            // forever even though Artisan has already stopped.
            // After AltMate requested a boundary stop, Artisan can leave its endurance flag on
            // even though the character is idle. At that point only an actual synthesis must
            // finish; both the stale endurance flag and the open-log preparation flag are ignored.
            artisanBusy = stopRequested
                ? activelyCrafting
                : artisanEndurance || activelyCrafting || preparingToCraft;
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
            CreditCompletedCrafts();
            var switchLevel = requestStopLevel > 0 ? requestStopLevel : NextSwitchLevel();
            if (!stopRequested && Plugin.PlayerState.Level >= switchLevel)
            {
                try
                {
                    Plugin.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus")
                        .InvokeAction(false);
                    stopRequested = true;
                    Status = Loc.L($"Lv{switchLevel}到達：現在の製作完了後に停止します。",
                        $"Reached Lv{switchLevel}; stopping after the current craft.");
                }
                catch (Exception ex)
                {
                    Stop(Loc.L($"Artisanの連続製作を停止できませんでした：{ex.Message}",
                        $"Could not stop Artisan endurance crafting: {ex.Message}"));
                }
                return;
            }
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
            CreditCompletedCrafts();
            var settings = plugin.GetCrafterLevelingSettings();
            settings.Progress.UpdatedAt = DateTime.Now;
            plugin.Configuration.Save();
            requestSent = false;
            artisanBecameBusy = false;
            stopRequested = false;
        }

        var currentLevel = Plugin.PlayerState.Level;
        while (index + 1 < queue.Count && currentLevel >= queue[index + 1].MinLevel)
            index++;

        var settingsForGear = plugin.GetCrafterLevelingSettings();
        var stylistLevel = AvailableGearUpdateLevel(settingsForGear, Plugin.PlayerState.ClassJob.RowId, currentLevel);
        // ICE leaves RecipeNote open and lets Artisan select the next recipe itself. Updating a
        // gearset while PreparingToCraft would require closing that log, so defer Stylist until
        // the character is naturally out of crafting stance (job change/start/completion).
        if (stylistLevel > lastStylistLevel &&
            !Plugin.Condition[ConditionFlag.Crafting] &&
            !Plugin.Condition[ConditionFlag.PreparingToCraft])
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
                lastStylistLevel = stylistLevel;
                waitUntilUtc = DateTime.UtcNow.AddSeconds(2);
                Status = Loc.L($"StylistでLv{stylistLevel}までの装備へ更新します。",
                    $"Updating gear available through Lv{stylistLevel} with Stylist.");
                return;
            }
            catch (Exception ex)
            {
                Stop(Loc.L($"Stylistで装備を更新できませんでした：{ex.Message}",
                    $"Stylist could not update gear: {ex.Message}"));
                return;
            }
        }

        // Save/equip the newly available tier before leaving a job that just reached target.
        if (index >= queue.Count || currentLevel >= plugin.GetCrafterLevelingSettings().TargetLevel)
        {
            if (!BeginNextJob())
                Complete();
            return;
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
            var settings = plugin.GetCrafterLevelingSettings();
            settings.PlannedCraftCounts.TryGetValue(preset.RecipeId, out var plannedCrafts);
            settings.CompletedCraftCounts.TryGetValue(preset.RecipeId, out var completedCrafts);
            requestedCraftCount = Math.Max(1, plannedCrafts - completedCrafts);
            if (requestedCraftCount == 1 && plannedCrafts <= completedCrafts)
            {
                if (preset.Route == CrafterLevelingRoute.Restoration)
                {
                    PauseForManualTurnIn(Loc.L(
                        "予定数の復興品を製作しました。蒼天街で納品後、リストを更新して再開してください。",
                        "The planned restoration items are complete. Turn them in at the Firmament, rebuild the list, then resume."));
                    return;
                }
                requestedCraftCount = CrafterExperiencePlanner.CraftsNeededNow(preset, settings.TargetLevel);
                settings.PlannedCraftCounts[preset.RecipeId] = checked(plannedCrafts + requestedCraftCount);
            }
            requestProductCount = CrafterInventoryLocator.PlayerInventoryCount(RecipeProductId(preset.RecipeId));
            creditedCraftCount = 0;
            stopRequested = false;
            requestStopLevel = CalculateNextStopLevel(Plugin.PlayerState.Level);
            Plugin.PluginInterface.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem")
                .InvokeAction((ushort)preset.RecipeId, requestedCraftCount);
            requestSent = true;
            requestAtUtc = DateTime.UtcNow;
            Status = Loc.L(
                $"Artisanへ連続製作を依頼中：{RecipeName(preset.RecipeId)} ×{requestedCraftCount}（レシピ段階 {index + 1}/{queue.Count}・{NextSwitchLabel()}）",
                $"Sending an endurance craft to Artisan: {RecipeName(preset.RecipeId)} ×{requestedCraftCount} (recipe stage {index + 1}/{queue.Count}; {NextSwitchLabel(false)})");
        }
        catch (Exception ex)
        {
            Stop(Loc.L($"Artisanへの製作依頼に失敗しました：{ex.Message}",
                $"Failed to request crafting from Artisan: {ex.Message}"));
        }
    }

    private void PauseForManualTurnIn(string reason)
    {
        IsRunning = false;
        Status = reason;
        var settings = plugin.GetCrafterLevelingSettings();
        settings.Progress.State = CrafterLevelingState.Paused;
        settings.Progress.LastError = string.Empty;
        settings.Progress.UpdatedAt = DateTime.Now;
        plugin.Configuration.Save();
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

    private void LoadQueue(CrafterLevelingSettings settings, uint jobId, int level)
    {
        queue = settings.RecipePresets
            .Where(x => x.JobId == jobId && x.MinLevel < settings.TargetLevel && x.MaxLevel >= level)
            .OrderBy(x => x.MinLevel)
            .ThenBy(x => x.RecipeId)
            .Select(Clone)
            .ToArray();
        index = 0;
    }

    private unsafe bool BeginNextJob()
    {
        var settings = plugin.GetCrafterLevelingSettings();
        var currentJobId = Plugin.PlayerState.ClassJob.RowId;
        var targetJobId = settings.EnabledJobIds
            .Where(jobId => jobId is >= 8 and <= 15 && jobId != currentJobId)
            .OrderBy(jobId => jobId > currentJobId ? 0 : 1)
            .ThenBy(jobId => jobId)
            .FirstOrDefault(jobId => ClassJobLevel(jobId) < settings.TargetLevel);
        if (targetJobId == 0)
            return false;

        var gearsets = RaptureGearsetModule.Instance();
        if (gearsets == null)
        {
            Stop(Loc.L("ギアセット一覧を取得できませんでした。", "Could not read the gearset list."));
            return true;
        }
        var gearsetIndex = -1;
        for (var candidate = 0; candidate < gearsets->NumGearsets; candidate++)
        {
            if (!gearsets->IsValidGearset(candidate)) continue;
            var gearset = gearsets->GetGearset(candidate);
            if (gearset != null && gearset->ClassJob == targetJobId)
            {
                gearsetIndex = candidate;
                break;
            }
        }
        if (gearsetIndex < 0)
        {
            var jobName = JobName(targetJobId);
            Stop(Loc.L($"{jobName}のギアセットがありません。先に登録してください。",
                $"No gearset exists for {jobName}. Register one first."));
            return true;
        }

        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<int, bool?, bool?, object>("Stylist.UpdateGearsetIfNeededEx")
                .InvokeAction(gearsetIndex, true, true);
            pendingJobId = targetJobId;
            jobChangeRequestedAtUtc = DateTime.UtcNow;
            settings.Progress.State = CrafterLevelingState.ChangingJob;
            settings.Progress.UpdatedAt = DateTime.Now;
            plugin.Configuration.Save();
            Status = Loc.L($"次の職：{JobName(targetJobId)}へ着替え中です。",
                $"Changing to the next job: {JobName(targetJobId)}.");
            return true;
        }
        catch (Exception ex)
        {
            Stop(Loc.L($"Stylistで次の職へ着替えられませんでした：{ex.Message}",
                $"Stylist could not change to the next job: {ex.Message}"));
            return true;
        }
    }

    private void ContinueJobChange()
    {
        if (DateTime.UtcNow - jobChangeRequestedAtUtc > TimeSpan.FromSeconds(15))
        {
            Stop(Loc.L($"{JobName(pendingJobId)}への着替えがタイムアウトしました。",
                $"Timed out changing to {JobName(pendingJobId)}."));
            return;
        }
        try
        {
            if (Plugin.PluginInterface.GetIpcSubscriber<bool>("Stylist.IsBusy").InvokeFunc() ||
                Plugin.PlayerState.ClassJob.RowId != pendingJobId)
                return;
        }
        catch (Exception ex)
        {
            Stop(Loc.L($"Stylistの着替え状態を確認できませんでした：{ex.Message}",
                $"Could not check Stylist job-change state: {ex.Message}"));
            return;
        }

        var settings = plugin.GetCrafterLevelingSettings();
        var changedJobId = pendingJobId;
        pendingJobId = 0;
        LoadQueue(settings, changedJobId, Plugin.PlayerState.Level);
        requestSent = false;
        artisanBecameBusy = false;
        stopRequested = false;
        requestStopLevel = 0;
        lastStylistLevel = -1;
        settings.Progress.State = CrafterLevelingState.CraftingNormal;
        settings.Progress.CurrentJobId = changedJobId;
        settings.Progress.UpdatedAt = DateTime.Now;
        plugin.Configuration.Save();
        Status = Loc.L($"{JobName(changedJobId)}の製作を開始します。",
            $"Starting crafting for {JobName(changedJobId)}.");
    }

    private static unsafe int ClassJobLevel(uint jobId)
    {
        var playerState = PlayerState.Instance();
        var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        if (playerState == null || !sheet.TryGetRow(jobId, out var job)) return 0;
        return playerState->ClassJobLevels[job.ExpArrayIndex];
    }

    private static string JobName(uint jobId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        return sheet.TryGetRow(jobId, out var job) ? job.Abbreviation.ToString() : $"Job {jobId}";
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
        var targetLevel = requestSent && requestStopLevel > 0
            ? requestStopLevel
            : CalculateNextStopLevel(Plugin.PlayerState.Level);
        return japanese ? $"次の判定 Lv{targetLevel}" : $"next check at Lv{targetLevel}";
    }

    private int NextSwitchLevel() => requestStopLevel > 0
        ? requestStopLevel
        : CalculateNextStopLevel(Plugin.PlayerState.Level);

    private int CalculateNextStopLevel(int currentLevel)
    {
        var targetLevel = plugin.GetCrafterLevelingSettings().TargetLevel;
        var recipeLevel = index + 1 < queue.Count ? queue[index + 1].MinLevel : targetLevel;
        var gearLevel = NextGearUpdateLevel(plugin.GetCrafterLevelingSettings(),
            Plugin.PlayerState.ClassJob.RowId, currentLevel, targetLevel);
        return Math.Min(targetLevel, Math.Min(recipeLevel, gearLevel));
    }

    private static int AvailableGearUpdateLevel(CrafterLevelingSettings settings, uint jobId, int currentLevel) =>
        GearUpdateLevels(settings, jobId)
            .Where(level => level <= currentLevel)
            .DefaultIfEmpty(1)
            .Max();

    private static int NextGearUpdateLevel(CrafterLevelingSettings settings, uint jobId,
        int currentLevel, int targetLevel) =>
        GearUpdateLevels(settings, jobId)
            .Where(level => level > currentLevel && level <= targetLevel)
            .DefaultIfEmpty(targetLevel)
            .Min();

    private static IEnumerable<int> GearUpdateLevels(CrafterLevelingSettings settings, uint jobId)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var preset in settings.GearPresets.Where(x => x.TierLevel <= settings.TargetLevel))
        {
            IEnumerable<uint> itemIds = preset.SharedItemIds;
            if (preset.JobItemIds.TryGetValue(jobId, out var jobItems))
                itemIds = itemIds.Concat(jobItems);
            foreach (var itemId in itemIds.Distinct())
                if (itemSheet.TryGetRow(itemId, out var item) && item.LevelEquip > 0)
                    yield return item.LevelEquip;
        }
    }

    private void CreditCompletedCrafts()
    {
        if (!requestSent || index >= queue.Count)
            return;
        var productId = RecipeProductId(queue[index].RecipeId);
        if (productId == 0)
            return;
        var producedItems = Math.Max(0,
            CrafterInventoryLocator.PlayerInventoryCount(productId) - requestProductCount);
        var amountPerCraft = RecipeResultAmount(queue[index].RecipeId);
        var completedNow = Math.Min(requestedCraftCount, producedItems / amountPerCraft);
        if (completedNow <= creditedCraftCount)
            return;
        var increment = completedNow - creditedCraftCount;
        var settings = plugin.GetCrafterLevelingSettings();
        var recipeId = queue[index].RecipeId;
        settings.CompletedCraftCounts.TryGetValue(recipeId, out var completedCrafts);
        settings.CompletedCraftCounts[recipeId] = checked(completedCrafts + increment);
        settings.Progress.UpdatedAt = DateTime.Now;
        plugin.Configuration.Save();
        creditedCraftCount = completedNow;
    }

    private static uint RecipeProductId(uint recipeId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Recipe>();
        return sheet.TryGetRow(recipeId, out var recipe) ? recipe.ItemResult.RowId : 0;
    }

    private static int RecipeResultAmount(uint recipeId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Recipe>();
        return sheet.TryGetRow(recipeId, out var recipe) ? Math.Max(1, (int)recipe.AmountResult) : 1;
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
