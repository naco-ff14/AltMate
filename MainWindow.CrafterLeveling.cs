using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AltMate;

public sealed partial class MainWindow
{
    private IReadOnlyList<CrafterPreparationItem> crafterPreparationItems = [];
    private IReadOnlyList<string> crafterPreparationErrors = [];
    private int crafterPresetJobId = 8;
    private int crafterPresetMinLevel = 1;
    private int crafterPresetMaxLevel = 20;
    private int crafterPresetRecipeId;
    private int crafterPresetCraftCount = 20;
    private string crafterRecipeSearch = string.Empty;
    private IReadOnlyList<(uint RecipeId, string ProductName)> crafterRecipeSearchResults = [];
    private string crafterListMessage = string.Empty;
    private DateTime nextCrafterInventoryRefreshUtc;
    private int crafterClipboardMinLevel = 1;
    private int crafterClipboardMaxLevel = 100;

    private void DrawCrafterLeveling()
    {
        DrawPageTitle(Loc.L("クラフター自動レベリング", "Crafter Auto-Leveling"),
            Loc.L("8職を装備Tierごとに揃えて育成するための準備と進捗を管理します。",
                "Prepare and track tier-based leveling for all eight crafting jobs."));
        var settings = plugin.GetCrafterLevelingSettings();

        if (!ImGui.BeginTabBar("crafter-leveling-tabs")) return;
        if (ImGui.BeginTabItem(Loc.L("育成・準備", "Leveling and preparation")))
        {
            DrawCrafterLevelingMain(settings);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(Loc.L("レシピ・装備データ", "Recipe and gear data")))
        {
            DrawCrafterClipboardData(settings);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawCrafterLevelingMain(CrafterLevelingSettings settings)
    {

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.09f, 0.12f, 0.17f, 0.9f));
        ImGui.BeginChild("crafter-phase-status", new Vector2(0, 64 * ImGuiHelpers.GlobalScale), true);
        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f),
            Loc.L("素材・装備の準備", "Material and gear preparation"));
        ImGui.TextWrapped(Loc.L(
            "必要数と、手持ちバッグ・選択したリテイナーごとの所在を一覧化します。アイテムの取り出しはゲーム内で手動操作してください。",
            "Lists requirements and locations across your inventory and selected retainers. Withdraw items manually in game."));
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader(Loc.L("育成設定", "Leveling settings"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawCrafterJobSelection(settings);
            if (Plugin.PlayerState.IsLoaded && Plugin.PlayerState.ClassJob.RowId is >= 8 and <= 15)
                ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f),
                    Loc.L($"現在：{Plugin.PlayerState.ClassJob.Value.Abbreviation} Lv{Plugin.PlayerState.Level}",
                        $"Current: {Plugin.PlayerState.ClassJob.Value.Abbreviation} Lv{Plugin.PlayerState.Level}"));
            var targetLevel = settings.TargetLevel;
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt(Loc.L("目標レベル", "Target level"), ref targetLevel, 1, 100))
            {
                settings.TargetLevel = targetLevel;
                SaveCrafterSettings();
            }
            var route = (int)settings.Level50To80Route;
            var routeLabels = new[]
            {
                Loc.L("通常製作", "Normal crafting"),
                Loc.L("イシュガルド復興", "Restoration"),
                Loc.L("収集品", "Collectables"),
            };
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo(Loc.L("Lv50～80方式", "Lv50-80 route"), ref route, routeLabels,
                    routeLabels.Length))
            {
                settings.Level50To80Route = (CrafterLevelingRoute)route;
                SaveCrafterSettings();
            }
            var stopAt50 = settings.StopAtLevel50;
            if (ImGui.Checkbox(Loc.L("Lv50到達時に一旦停止", "Pause at level 50"), ref stopAt50))
            {
                settings.StopAtLevel50 = stopAt50;
                SaveCrafterSettings();
            }
            var useTheCollector = settings.UseTheCollectorForRestoration;
            if (ImGui.Checkbox(Loc.L("復興品をTheCollectorで自動納品", "Turn in restoration items with TheCollector"),
                    ref useTheCollector))
            {
                settings.UseTheCollectorForRestoration = useTheCollector;
                SaveCrafterSettings();
            }
            if (useTheCollector)
            {
                var batchSize = Math.Clamp(settings.RestorationTurnInBatchSize, 1, 999);
                ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt(Loc.L("復興品の納品単位", "Restoration turn-in batch"), ref batchSize))
                {
                    settings.RestorationTurnInBatchSize = Math.Clamp(batchSize, 1, 999);
                    SaveCrafterSettings();
                }
                ImGui.TextDisabled(Loc.L(
                    "指定数を製作するたびに納品し、獲得EXPを反映して残り制作数を再計算します。",
                    "Turns in after each batch and recalculates remaining crafts from the awarded EXP."));
            }
            ImGui.TextDisabled(Loc.L(
                "TheCollector側でFirmamentモードを選択してください（Lifestream・vnavmeshが必要）。失敗時は手動納品待ちで停止します。",
                "Select Firmament mode in TheCollector (requires Lifestream and vnavmesh). Failures pause for manual turn-in."));

            ImGui.Spacing();
            if (ImGui.Button(Loc.L("リスト作成・更新", "Build/update list"),
                    new Vector2(220 * ImGuiHelpers.GlobalScale, 34 * ImGuiHelpers.GlobalScale)))
                BuildCrafterLevelingList(settings);
            ImGui.SameLine();
            ImGui.TextDisabled(Loc.L(
                "製作品・装備候補・必要素材・所持数をまとめて更新します。",
                "Updates recipes, gear, required items, and owned counts together."));
            ImGui.TextDisabled(Loc.L(
                "製作数は現在Lv・EXPから事前計算します。Lv20以降の復興品は収集価値報酬の第2段階を基準にします。",
                "Craft counts are precomputed from current level/EXP. Restoration items after Lv20 use the middle collectability reward tier."));
            ImGui.TextDisabled(Loc.L(
                "実際の報酬が第2段階と異なる場合は納品後に残数を再計算します。品質・初回・EXPバフは含めません。",
                "Remaining crafts are recalculated after turn-in when the actual reward differs. Quality, first-craft and EXP buffs are excluded."));
            if (!string.IsNullOrWhiteSpace(crafterListMessage))
                ImGui.TextWrapped(crafterListMessage);
        }

        if (ImGui.CollapsingHeader(Loc.L("製作品の詳細設定（任意）", "Recipe details (optional)")))
            DrawCrafterPresetEditor(settings);

        if (ImGui.CollapsingHeader(Loc.L("リテイナー保管設定", "Retainer storage"),
                ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrafterStorageSettings(settings);

        if (ImGui.CollapsingHeader(Loc.L("準備リスト", "Preparation list"),
                ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrafterPreparationList(settings);

    }

    private void DrawCrafterClipboardData(CrafterLevelingSettings settings)
    {
        ImGui.TextWrapped(Loc.L(
            "指定レベル帯のレシピと対象装備をJSONで移行します。キャラクターの現在Lvは参照しません。",
            "Transfer recipes and target gear in the selected level range as JSON. Character level is ignored."));
        ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(Loc.L("開始Lv", "Min Lv"), ref crafterClipboardMinLevel);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(Loc.L("終了Lv", "Max Lv"), ref crafterClipboardMaxLevel);
        crafterClipboardMinLevel = Math.Clamp(crafterClipboardMinLevel, 1, 100);
        crafterClipboardMaxLevel = Math.Clamp(crafterClipboardMaxLevel, crafterClipboardMinLevel, 100);

        var exportRecipeCount = settings.RecipePresets.Count(x =>
            x.MinLevel <= crafterClipboardMaxLevel && x.MaxLevel >= crafterClipboardMinLevel);
        var exportGearCount = settings.GearPresets.Count(x =>
            x.TierLevel >= crafterClipboardMinLevel && x.TierLevel <= crafterClipboardMaxLevel);
        ImGui.TextDisabled(Loc.L(
            $"対象：レシピ{exportRecipeCount}件・装備Tier{exportGearCount}件",
            $"Selected: {exportRecipeCount} recipes and {exportGearCount} gear tiers"));

        if (ImGui.Button(Loc.L("指定範囲をコピー", "Copy selected range")))
        {
            ImGui.SetClipboardText(CrafterPlanClipboard.Export(settings,
                crafterClipboardMinLevel, crafterClipboardMaxLevel));
            crafterListMessage = Loc.L(
                $"Lv{crafterClipboardMinLevel}～{crafterClipboardMaxLevel}のレシピ{exportRecipeCount}件・装備Tier{exportGearCount}件をコピーしました。",
                $"Copied {exportRecipeCount} recipes and {exportGearCount} gear tiers for levels {crafterClipboardMinLevel}-{crafterClipboardMaxLevel}.");
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.L("指定範囲へ登録", "Import into selected range")))
        {
            if (CrafterPlanClipboard.TryImport(ImGui.GetClipboardText(), out var importedRecipes,
                    out var importedGear, out var error))
            {
                var recipes = importedRecipes.Where(x =>
                    x.MinLevel <= crafterClipboardMaxLevel && x.MaxLevel >= crafterClipboardMinLevel).ToList();
                var gear = importedGear.Where(x =>
                    x.TierLevel >= crafterClipboardMinLevel && x.TierLevel <= crafterClipboardMaxLevel).ToList();
                settings.RecipePresets.RemoveAll(x =>
                    x.MinLevel <= crafterClipboardMaxLevel && x.MaxLevel >= crafterClipboardMinLevel);
                settings.GearPresets.RemoveAll(x =>
                    x.TierLevel >= crafterClipboardMinLevel && x.TierLevel <= crafterClipboardMaxLevel);
                settings.RecipePresets.AddRange(recipes);
                settings.GearPresets.AddRange(gear);
                settings.RecipePresets.Sort((left, right) =>
                {
                    var job = left.JobId.CompareTo(right.JobId);
                    return job != 0 ? job : left.MinLevel.CompareTo(right.MinLevel);
                });
                settings.GearPresets.Sort((left, right) => left.TierLevel.CompareTo(right.TierLevel));
                settings.CompletedCraftCounts.Clear();
                settings.PlannedCraftCounts.Clear();
                CrafterExperiencePlanner.EnsurePlans(settings);
                CrafterRetainerScanner.RefreshOwnedTotals(settings);
                var service = new CrafterPreparationService();
                crafterPreparationItems = service.Build(settings, out crafterPreparationErrors);
                nextCrafterInventoryRefreshUtc = DateTime.UtcNow.AddMilliseconds(500);
                SaveCrafterSettings();
                crafterListMessage = Loc.L(
                    $"Lv{crafterClipboardMinLevel}～{crafterClipboardMaxLevel}へレシピ{recipes.Count}件・装備Tier{gear.Count}件を登録しました。",
                    $"Imported {recipes.Count} recipes and {gear.Count} gear tiers into levels {crafterClipboardMinLevel}-{crafterClipboardMaxLevel}.");
            }
            else
            {
                crafterListMessage = error;
            }
        }
        ImGui.TextDisabled(Loc.L(
            "登録すると指定範囲に重なる既存レシピと、その範囲の装備Tierを置き換えます。",
            "Import replaces existing recipes overlapping the selected range and gear tiers within it."));
        if (!string.IsNullOrWhiteSpace(crafterListMessage))
            ImGui.TextWrapped(crafterListMessage);
    }

    private void BuildCrafterLevelingList(CrafterLevelingSettings settings)
    {
        var catalog = CrafterLevelingCatalog.ApplyStandard(settings);
        var gear = CrafterGearCatalog.BuildStandard(settings);
        settings.CompletedCraftCounts.Clear();
        settings.PlannedCraftCounts.Clear();
        CrafterRetainerScanner.RefreshOwnedTotals(settings);
        var service = new CrafterPreparationService();
        crafterPreparationItems = service.Build(settings, out crafterPreparationErrors);
        nextCrafterInventoryRefreshUtc = DateTime.UtcNow.AddMilliseconds(500);
        var activeRecipeCount = settings.RecipePresets.Count(x =>
            settings.EnabledJobIds.Contains(x.JobId) &&
            CrafterPreparationService.JobLevel(x.JobId) < settings.TargetLevel &&
            x.MinLevel < settings.TargetLevel &&
            x.MaxLevel >= CrafterPreparationService.JobLevel(x.JobId));
        crafterListMessage = catalog.Unresolved.Count == 0 && gear.Missing.Count == 0
            ? Loc.L($"リストを更新しました（現在レベル以降の製作品{activeRecipeCount}件）。不足数と所在を下で確認してください。",
                $"List updated ({activeRecipeCount} recipes from current levels onward). Review missing counts and locations below.")
            : Loc.L($"リストを更新しました。未解決：製作品{catalog.Unresolved.Count}件、装備{gear.Missing.Count}件。",
                $"List updated. Unresolved: {catalog.Unresolved.Count} recipes, {gear.Missing.Count} gear slots.");
        SaveCrafterSettings();
    }

    private void DrawCrafterStorageSettings(CrafterLevelingSettings settings)
    {
        ImGui.TextUnformatted(Loc.L("素材を保管しているリテイナー", "Retainers holding materials"));
        ImGui.TextDisabled(Loc.L(
            "一度ずつリテイナーを開くと所持品を自動スキャンします。チェックしたリテイナーだけ準備数へ合算します。",
            "Open each retainer once to scan automatically. Only checked retainers count toward preparation totals."));
        var currentContentId = Plugin.PlayerState.ContentId;
        var characterRetainers = plugin.Configuration.CharacterGil.TryGetValue(currentContentId, out var character)
            ? character.Retainers.Values
            : Enumerable.Empty<RetainerGilRecord>();
        var knownRetainers = characterRetainers
            .Select(retainer => (RetainerId: retainer.RetainerId, Name: retainer.Name))
            .Concat(settings.RetainerInventories.Values.Select(cache =>
                (RetainerId: cache.RetainerId, Name: cache.RetainerName)))
            .GroupBy(x => x.RetainerId)
            .Select(group => group.Last())
            .OrderBy(x =>
            {
                var index = settings.SelectedRetainerIds.IndexOf(x.RetainerId);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (knownRetainers.Length == 0)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                Loc.L("リテイナー情報がありません。ゲーム内でリテイナー一覧を開いてください。",
                    "No retainer data. Open the retainer list in game."));
        foreach (var retainer in knownRetainers)
        {
            var selected = settings.SelectedRetainerIds.Contains(retainer.RetainerId);
            if (ImGui.Checkbox($"{retainer.Name}##crafter-retainer-{retainer.RetainerId}", ref selected))
            {
                if (selected) settings.SelectedRetainerIds.Add(retainer.RetainerId);
                else settings.SelectedRetainerIds.Remove(retainer.RetainerId);
                CrafterRetainerScanner.RefreshOwnedTotals(settings);
                SaveCrafterSettings();
            }
            if (settings.RetainerInventories.TryGetValue(retainer.RetainerId, out var cache))
            {
                ImGui.SameLine();
                ImGui.TextDisabled(Loc.L($"{cache.Items.Count}種類 / {cache.ScannedAt:MM/dd HH:mm}",
                    $"{cache.Items.Count} items / {cache.ScannedAt:MM/dd HH:mm}"));
            }
            if (!selected) continue;
            var priorityIndex = settings.SelectedRetainerIds.IndexOf(retainer.RetainerId);
            ImGui.SameLine();
            ImGui.BeginDisabled(priorityIndex <= 0);
            if (ImGui.SmallButton($"↑##crafter-retainer-up-{retainer.RetainerId}"))
            {
                (settings.SelectedRetainerIds[priorityIndex - 1], settings.SelectedRetainerIds[priorityIndex]) =
                    (settings.SelectedRetainerIds[priorityIndex], settings.SelectedRetainerIds[priorityIndex - 1]);
                SaveCrafterSettings();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(priorityIndex >= settings.SelectedRetainerIds.Count - 1);
            if (ImGui.SmallButton($"↓##crafter-retainer-down-{retainer.RetainerId}"))
            {
                (settings.SelectedRetainerIds[priorityIndex + 1], settings.SelectedRetainerIds[priorityIndex]) =
                    (settings.SelectedRetainerIds[priorityIndex], settings.SelectedRetainerIds[priorityIndex + 1]);
                SaveCrafterSettings();
            }
            ImGui.EndDisabled();
        }
    }

    private void DrawCrafterJobSelection(CrafterLevelingSettings settings)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        for (uint jobId = 8; jobId <= 15; jobId++)
        {
            var selected = settings.EnabledJobIds.Contains(jobId);
            var label = sheet.TryGetRow(jobId, out var job) ? job.Abbreviation.ToString() : $"#{jobId}";
            if (ImGui.Checkbox($"{label}##crafter-level-job-{jobId}", ref selected))
            {
                if (selected) settings.EnabledJobIds.Add(jobId);
                else settings.EnabledJobIds.Remove(jobId);
                SaveCrafterSettings();
            }
            if (jobId != 15) ImGui.SameLine();
        }
    }

    private void DrawCrafterPresetEditor(CrafterLevelingSettings settings)
    {
        ImGui.TextDisabled(Loc.L(
            "製作品を名前で検索して登録します。必要素材はゲームデータから自動集計します。",
            "Search and register a crafted item by name. Required materials are calculated from game data."));

        ImGui.TextDisabled(Loc.L(
            "通常は変更不要です。独自の製作品を追加したい場合だけ使用してください。",
            "Usually no changes are needed. Use this only to add custom recipes."));

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##crafter-recipe-search",
            Loc.L("製作品名（例：ブロンズインゴット）", "Product name (e.g. Bronze Ingot)"),
            ref crafterRecipeSearch, 100);
        ImGui.SameLine();
        if (ImGui.Button(Loc.L("検索", "Search")))
        {
            var query = crafterRecipeSearch.Trim();
            crafterRecipeSearchResults = string.IsNullOrWhiteSpace(query)
                ? []
                : Plugin.DataManager.GetExcelSheet<Recipe>()
                    .Where(recipe => recipe.ItemResult.RowId != 0)
                    .Select(recipe => (recipe.RowId, recipe.ItemResult.Value.Name.ToString()))
                    .Where(recipe => recipe.Item2.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    .DistinctBy(recipe => recipe.RowId)
                    .OrderBy(recipe => recipe.Item2, StringComparer.CurrentCultureIgnoreCase)
                    .Take(40)
                    .ToArray();
        }
        if (crafterRecipeSearchResults.Count > 0)
        {
            ImGui.TextDisabled(Loc.L("候補を選択してください。同名品は登録後のジョブ表示で確認できます。",
                "Select a result. For duplicate names, verify the job shown after registration."));
            if (ImGui.BeginListBox("##crafter-recipe-results", new Vector2(420 * ImGuiHelpers.GlobalScale,
                    Math.Min(150, 24 + crafterRecipeSearchResults.Count * 22) * ImGuiHelpers.GlobalScale)))
            {
                foreach (var result in crafterRecipeSearchResults)
                {
                    var selected = crafterPresetRecipeId == result.RecipeId;
                    if (ImGui.Selectable($"{result.ProductName}##recipe-{result.RecipeId}", selected))
                        crafterPresetRecipeId = (int)result.RecipeId;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Loc.L($"内部Recipe ID: {result.RecipeId}",
                            $"Internal Recipe ID: {result.RecipeId}"));
                }
                ImGui.EndListBox();
            }
        }

        var craftingJobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var jobLabels = Enumerable.Range(8, 8)
            .Select(jobId => craftingJobs.TryGetRow((uint)jobId, out var job)
                ? job.Abbreviation.ToString()
                : $"Job {jobId}")
            .ToArray();
        var jobIndex = Math.Clamp(crafterPresetJobId - 8, 0, 7);
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo(Loc.L("製作職", "Crafting job"), ref jobIndex, jobLabels, jobLabels.Length))
            crafterPresetJobId = jobIndex + 8;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(Loc.L("開始Lv", "Min Lv"), ref crafterPresetMinLevel);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(Loc.L("終了Lv", "Max Lv"), ref crafterPresetMaxLevel);
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(Loc.L("最大製作数", "Max crafts"), ref crafterPresetCraftCount);
        ImGui.SameLine();
        ImGui.BeginDisabled(crafterPresetRecipeId <= 0);
        if (ImGui.Button(Loc.L("製作品を追加", "Add recipe")))
        {
            settings.RecipePresets.Add(new CrafterRecipePreset
            {
                JobId = (uint)Math.Clamp(crafterPresetJobId, 8, 15),
                MinLevel = Math.Clamp(crafterPresetMinLevel, 1, 100),
                MaxLevel = Math.Clamp(crafterPresetMaxLevel, 1, 100),
                RecipeId = (uint)Math.Max(0, crafterPresetRecipeId),
                MaxCraftCount = Math.Max(1, crafterPresetCraftCount),
                Route = CrafterLevelingRoute.Normal,
            });
            SaveCrafterSettings();
        }
        ImGui.EndDisabled();

        if (crafterPresetRecipeId > 0 &&
            Plugin.DataManager.GetExcelSheet<Recipe>().TryGetRow((uint)crafterPresetRecipeId, out var selectedRecipe))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.5f, 1f),
                Loc.L($"選択中：{selectedRecipe.ItemResult.Value.Name}",
                    $"Selected: {selectedRecipe.ItemResult.Value.Name}"));
        }

        var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();
        var jobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var visiblePresets = settings.RecipePresets.Select((preset, index) => (Preset: preset, Index: index))
            .Where(x => settings.EnabledJobIds.Contains(x.Preset.JobId) &&
                        x.Preset.MinLevel < settings.TargetLevel &&
                        CrafterPreparationService.JobLevel(x.Preset.JobId) < settings.TargetLevel &&
                        x.Preset.MaxLevel >= CrafterPreparationService.JobLevel(x.Preset.JobId)).ToArray();
        if (visiblePresets.Length > 0 && ImGui.BeginTable("crafter-leveling-recipes", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            foreach (var heading in new[] { Loc.L("製作品", "Product"), Loc.L("ジョブ", "Job"),
                         Loc.L("レシピLv", "Recipe Lv"), Loc.L("使用Lv帯", "Level range"),
                         Loc.L("完了 / 予定", "Done / planned"), string.Empty })
                ImGui.TableSetupColumn(heading);
            ImGui.TableHeadersRow();
        foreach (var entry in visiblePresets)
        {
            var preset = entry.Preset;
            var index = entry.Index;
            var productName = recipeSheet.TryGetRow(preset.RecipeId, out var recipe)
                ? recipe.ItemResult.Value.Name.ToString()
                : Loc.L("不明な製作品", "Unknown product");
            var jobName = jobSheet.TryGetRow(preset.JobId, out var job)
                ? job.Abbreviation.ToString()
                : $"Job {preset.JobId}";
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(productName);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Recipe ID: {preset.RecipeId}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(jobName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(recipeSheet.TryGetRow(preset.RecipeId, out var levelRecipe)
                ? $"Lv{levelRecipe.RecipeLevelTable.Value.ClassJobLevel}"
                : "—");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"Lv{preset.MinLevel}–{preset.MaxLevel}");
            ImGui.TableNextColumn();
            settings.CompletedCraftCounts.TryGetValue(preset.RecipeId, out var completedCrafts);
            settings.PlannedCraftCounts.TryGetValue(preset.RecipeId, out var plannedCrafts);
            ImGui.TextUnformatted($"{completedCrafts} / {plannedCrafts}");
            ImGui.TableNextColumn();
            if (!ImGui.SmallButton($"{Loc.L("削除", "Remove")}##crafter-preset-{index}")) continue;
            settings.RecipePresets.RemoveAt(index);
            SaveCrafterSettings();
        }
            ImGui.EndTable();
        }
    }

    private void DrawCrafterPreparationList(CrafterLevelingSettings settings)
    {
        // Locations are read live from the player inventory. Refresh the calculated owned and
        // missing counts on the same cadence so those columns never disagree with the location.
        if (crafterPreparationItems.Count > 0 && DateTime.UtcNow >= nextCrafterInventoryRefreshUtc)
        {
            CrafterRetainerScanner.RefreshOwnedTotals(settings);
            var service = new CrafterPreparationService();
            crafterPreparationItems = service.Build(settings, out crafterPreparationErrors);
            nextCrafterInventoryRefreshUtc = DateTime.UtcNow.AddMilliseconds(500);
        }

        var missingOnly = settings.ShowMissingOnly;
        if (ImGui.Checkbox(Loc.L("不足のみ表示", "Missing only"), ref missingOnly))
        {
            settings.ShowMissingOnly = missingOnly;
            SaveCrafterSettings();
        }
        foreach (var error in crafterPreparationErrors)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.3f, 1f),
                Loc.L($"無効な製作品：{error}", $"Invalid recipe: {error}"));
        if (settings.RecipePresets.Count == 0)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                Loc.L("レベリング製作品が未登録です。先に製作品名で検索して追加してください。",
                    "No leveling recipes are registered. Search by product name and add one first."));

        var rows = settings.ShowMissingOnly
            ? crafterPreparationItems.Where(x => x.MissingCount > 0).ToArray()
            : crafterPreparationItems.ToArray();
        DrawCrafterPreparationTable(settings, "crafter-preparation-materials",
            Loc.L("製作用の素材", "Crafting materials"),
            rows.Where(x => !x.IsGear && !x.IsCrystal).ToArray());
        DrawCrafterPreparationTable(settings, "crafter-preparation-crystals",
            Loc.L("製作用のクリスタル", "Crafting crystals"),
            rows.Where(x => x.IsCrystal).ToArray());
        DrawCrafterPreparationTable(settings, "crafter-preparation-gear",
            Loc.L("育成途中で使用する装備", "Gear used while leveling"),
            rows.Where(x => x.IsGear).ToArray());

        ImGui.Separator();
        var automation = plugin.CrafterLeveling;
        var hasList = crafterPreparationItems.Count > 0;
        var missing = crafterPreparationItems.Count(x => x.MissingCount > 0);
        ImGui.BeginDisabled(!hasList || automation.IsRunning);
        if (ImGui.Button(Loc.L("現在のクラフター職で製作開始", "Start crafting with current job"),
                new Vector2(250 * ImGuiHelpers.GlobalScale, 34 * ImGuiHelpers.GlobalScale)))
            automation.Start(settings);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!automation.IsRunning);
        if (ImGui.Button(Loc.L("停止", "Stop")))
            automation.Stop();
        ImGui.EndDisabled();
        if (!hasList)
            ImGui.TextDisabled(Loc.L("先に「リスト作成・更新」を実行してください。",
                "Build the list first."));
        else if (missing > 0)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                Loc.L($"不足品が{missing}種類あります。開始は可能ですが、素材不足のレシピで自動停止します。",
                    $"{missing} item types are missing. You can start; execution stops at the first recipe lacking materials."));
        ImGui.TextColored(automation.IsRunning
                ? new Vector4(0.4f, 0.82f, 1f, 1f)
                : new Vector4(0.7f, 0.72f, 0.75f, 1f),
            automation.Status);
    }

    private static void DrawCrafterPreparationTable(CrafterLevelingSettings settings, string id, string title,
        IReadOnlyList<CrafterPreparationItem> rows)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f), title);
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(Loc.L("該当項目なし", "No items"));
            return;
        }
        if (!ImGui.BeginTable(id, 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, Math.Min(220, 30 + rows.Count * 24) * ImGuiHelpers.GlobalScale))) return;
        foreach (var heading in new[] { Loc.L("アイテム", "Item"), Loc.L("コピー", "Copy"), Loc.L("分類", "Type"),
                     Loc.L("装備Lv", "Equip Lv"),
                     Loc.L("必要", "Required"), Loc.L("所持", "Owned"), Loc.L("不足", "Missing"),
                     Loc.L("所在", "Location") })
            ImGui.TableSetupColumn(heading);
        ImGui.TableHeadersRow();
        foreach (var item in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Name);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Item ID: {item.ItemId}");
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{Loc.L("名前", "Name")}##copy-crafter-item-{id}-{item.ItemId}"))
                ImGui.SetClipboardText(item.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.IsGear ? Loc.L("装備", "Gear") :
                item.IsCrystal ? Loc.L("クリスタル", "Crystal") : Loc.L("素材", "Material"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.IsGear ? $"Lv{item.EquipLevel}" : "—");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.RequiredCount.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.OwnedCount.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextColored(item.MissingCount > 0 ? new Vector4(1f, 0.35f, 0.3f, 1f) :
                new Vector4(0.35f, 0.9f, 0.5f, 1f), item.MissingCount.ToString("N0"));
            ImGui.TableNextColumn();
            var locations = CrafterInventoryLocator.GetLocations(settings, item.ItemId);
            ImGui.TextWrapped(locations.Count > 0
                ? string.Join(" / ", locations)
                : Loc.L("所持なし", "Not owned"));
        }
        ImGui.EndTable();
    }

    private void SaveCrafterSettings()
    {
        var settings = plugin.GetCrafterLevelingSettings();
        settings.Progress.UpdatedAt = DateTime.Now;
        plugin.Configuration.Save();
    }

}
