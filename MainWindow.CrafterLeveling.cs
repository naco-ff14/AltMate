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
    private string crafterClipboardMessage = string.Empty;
    private IReadOnlyList<CrafterQuestItem> crafterQuestItems = [];
    private string crafterQuestMessage = string.Empty;

    private void DrawCrafterLeveling()
    {
        DrawPageTitle(Loc.L("クラフター自動レベリング", "Crafter Auto-Leveling"),
            Loc.L("育成条件、必要アイテム、製作をまとめて管理します。",
                "Manage leveling plans, required items, and crafting."));
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
        if (ImGui.BeginTabItem(Loc.L("クラフタークエスト", "Crafter quests")))
        {
            DrawCrafterQuestItems(settings);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawCrafterLevelingMain(CrafterLevelingSettings settings)
    {
        if (Plugin.PlayerState.IsLoaded && Plugin.PlayerState.ClassJob.RowId is >= 8 and <= 15)
            ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f),
                Loc.L($"現在：{Plugin.PlayerState.ClassJob.Value.Abbreviation} Lv{Plugin.PlayerState.Level}",
                    $"Current: {Plugin.PlayerState.ClassJob.Value.Abbreviation} Lv{Plugin.PlayerState.Level}"));
        var targetLevel = settings.TargetLevel;
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt(Loc.L("目標レベル", "Target level"), ref targetLevel, 1, 100))
        {
            settings.TargetLevel = targetLevel;
            SaveCrafterSettings();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) BuildCrafterLevelingList(settings);
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.L("再計算", "Refresh"))) BuildCrafterLevelingList(settings);
        DrawCrafterExecutionControls(settings);
        if (!string.IsNullOrWhiteSpace(crafterListMessage)) ImGui.TextWrapped(crafterListMessage);
        ImGui.Separator();

        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f), Loc.L("不足アイテム", "Required items"));
        DrawCrafterPreparationList(settings);

        if (ImGui.CollapsingHeader(Loc.L("詳細設定", "Advanced settings")))
        {
            ImGui.TextUnformatted(Loc.L("対象職", "Jobs"));
            DrawCrafterJobSelection(settings);
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
                BuildCrafterLevelingList(settings);
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
                    "TheCollector：Firmamentモード（Lifestream・vnavmeshが必要）",
                    "TheCollector: Firmament mode (requires Lifestream and vnavmesh)"));
            }

            ImGui.Separator();
            ImGui.TextUnformatted(Loc.L("素材の保管場所", "Material storage"));
            DrawCrafterStorageSettings(settings);
            if (ImGui.TreeNode(Loc.L("レシピを手動編集", "Edit recipes manually")))
            {
                DrawCrafterPresetEditor(settings);
                ImGui.TreePop();
            }
        }
    }

    private void DrawCrafterExecutionControls(CrafterLevelingSettings settings)
    {
        var automation = plugin.CrafterLeveling;
        var hasList = crafterPreparationItems.Count > 0;
        var missing = crafterPreparationItems.Count(x => !x.IsGear && x.MissingCount > 0);
        ImGui.BeginDisabled(!hasList || automation.IsRunning);
        if (ImGui.Button(Loc.L("製作開始", "Start crafting"),
                new Vector2(180 * ImGuiHelpers.GlobalScale, 34 * ImGuiHelpers.GlobalScale)))
            automation.Start(settings);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!automation.IsRunning);
        if (ImGui.Button(Loc.L("停止", "Stop"),
                new Vector2(100 * ImGuiHelpers.GlobalScale, 34 * ImGuiHelpers.GlobalScale)))
            automation.Stop();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextColored(automation.IsRunning
                ? new Vector4(0.4f, 0.82f, 1f, 1f)
                : new Vector4(0.7f, 0.72f, 0.75f, 1f),
            automation.Status);
        if (!hasList)
            ImGui.TextDisabled(Loc.L("先に「準備リストを更新」を実行してください。",
                "Update the preparation list first."));
        else if (missing > 0)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                Loc.L($"不足：{missing}種類（不足レシピで自動停止）",
                    $"Missing: {missing} types (stops at the first affected recipe)"));
    }

    private void DrawCrafterClipboardData(CrafterLevelingSettings settings)
    {
        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f), Loc.L("対象レベル", "Level range"));
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
            $"現在Lvに関係なく、この範囲を扱います（レシピ{exportRecipeCount}件・装備Tier{exportGearCount}件）。",
            $"Uses this range regardless of current level ({exportRecipeCount} recipes, {exportGearCount} gear tiers)."));

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f), Loc.L("クリップボード", "Clipboard"));
        if (ImGui.Button(Loc.L("表をコピー", "Copy table")))
        {
            ImGui.SetClipboardText(CrafterPlanClipboard.ExportTable(settings,
                crafterClipboardMinLevel, crafterClipboardMaxLevel));
            crafterClipboardMessage = Loc.L(
                $"Lv{crafterClipboardMinLevel}～{crafterClipboardMaxLevel}のレシピ{exportRecipeCount}件・装備Tier{exportGearCount}件をコピーしました。",
                $"Copied {exportRecipeCount} recipes and {exportGearCount} gear tiers for levels {crafterClipboardMinLevel}-{crafterClipboardMaxLevel}.");
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.L("表から登録", "Import table")))
        {
            if (CrafterPlanClipboard.TryImportTable(ImGui.GetClipboardText(), out var importedRecipes,
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
                crafterClipboardMessage = Loc.L(
                    $"Lv{crafterClipboardMinLevel}～{crafterClipboardMaxLevel}へレシピ{recipes.Count}件・装備Tier{gear.Count}件を登録しました。",
                    $"Imported {recipes.Count} recipes and {gear.Count} gear tiers into levels {crafterClipboardMinLevel}-{crafterClipboardMaxLevel}.");
            }
            else
            {
                crafterClipboardMessage = error;
            }
        }
        ImGui.TextDisabled(Loc.L(
            "登録：指定範囲だけ置換／範囲外は保持",
            "Import: replace selected range / keep everything outside it"));
        if (!string.IsNullOrWhiteSpace(crafterClipboardMessage))
            ImGui.TextWrapped(crafterClipboardMessage);

        ImGui.Spacing();
        ImGui.Separator();
        DrawCrafterClipboardRecipeTable(settings);
        ImGui.Spacing();
        DrawCrafterClipboardGearTable(settings);
    }

    private void DrawCrafterQuestItems(CrafterLevelingSettings settings)
    {
        if (crafterQuestItems.Count == 0) crafterQuestItems = CrafterQuestCatalog.BuildToLevel60();
        ImGui.TextDisabled(Loc.L("Lv60までのクラフタークエスト納品物", "Crafter quest turn-ins through level 60"));
        ImGui.TextDisabled(Loc.L(
            "必要品を手持ちバッグに揃えると、Questionableへ渡して受注から納品まで自動進行できます。",
            "Once all required items are in your inventory, Questionable can automate the quest through turn-in."));
        if (!QuestionableQuestBridge.IsAvailable)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                Loc.L("Questionableが読み込まれていません。", "Questionable is not loaded."));
        if (!string.IsNullOrWhiteSpace(crafterQuestMessage)) ImGui.TextWrapped(crafterQuestMessage);
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var questionableRunning = QuestionableQuestBridge.IsRunning();
        if (!ImGui.BeginTable("crafter-quest-items", 11,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, -1))) return;
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 45 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("職", "Job"), ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("クエスト", "Quest"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn(Loc.L("アイテム", "Item"), ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn(Loc.L("コピー", "Copy"), ImGuiTableColumnFlags.WidthFixed, 72 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("必要", "Required"), ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("条件", "Condition"), ImGuiTableColumnFlags.WidthFixed, 145 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("所持", "Owned"), ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("状態", "Status"), ImGuiTableColumnFlags.WidthFixed, 75 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("所在", "Location"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn(Loc.L("実行", "Run"), ImGuiTableColumnFlags.WidthFixed, 92 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach (var row in crafterQuestItems)
        {
            // Quest readiness is intentionally based on the player's bags only. Retainers are location hints.
            var owned = CrafterInventoryLocator.PlayerInventoryCount(row.ItemId, row.RequiresHq);
            var enough = owned >= row.RequiredCount;
            var questRows = crafterQuestItems.Where(x => x.QuestId == row.QuestId).ToArray();
            var readyInBags = questRows.All(x =>
                CrafterInventoryLocator.PlayerInventoryCount(x.ItemId, x.RequiresHq) >= x.RequiredCount);
            var complete = QuestionableQuestBridge.IsComplete(row.QuestId);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Level.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(jobs.TryGetRow(row.JobId, out var job) ? job.Abbreviation.ToString() : row.JobId.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.QuestName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.ItemName + (row.RequiresHq ? " HQ" : string.Empty));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{Loc.L("コピー", "Copy")}##copy-quest-item-{row.JobId}-{row.Level}-{row.ItemId}"))
                ImGui.SetClipboardText(row.ItemName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.RequiredCount.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Condition);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(owned.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextColored(enough ? new Vector4(0.35f, 0.9f, 0.5f, 1f) : new Vector4(1f, 0.35f, 0.3f, 1f),
                enough ? Loc.L("所持済み", "Ready") : Loc.L("不足", "Missing"));
            ImGui.TableNextColumn();
            var locations = CrafterInventoryLocator.GetQuestLocations(settings, row.ItemId, row.RequiresHq);
            ImGui.TextWrapped(locations.Count > 0 ? string.Join(" / ", locations) : Loc.L("所持なし", "Not owned"));
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(row.QuestId == 0 || complete || !readyInBags || questionableRunning || !QuestionableQuestBridge.IsAvailable);
            if (ImGui.SmallButton($"{Loc.L("自動クリア", "Auto clear")}##quest-{row.QuestId}-{row.ItemId}"))
            {
                try
                {
                    crafterQuestMessage = QuestionableQuestBridge.StartSingle(row.QuestId)
                        ? Loc.L($"「{row.QuestName}」をQuestionableへ渡しました。",
                            $"Sent '{row.QuestName}' to Questionable.")
                        : Loc.L("Questionableがクエストを受け付けませんでした。対象クエストの対応状況を確認してください。",
                            "Questionable rejected the quest. Check whether the quest is supported.");
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Failed to start crafter quest through Questionable");
                    crafterQuestMessage = Loc.L("Questionable連携に失敗しました。", "Questionable integration failed.");
                }
            }
            ImGui.EndDisabled();
            if (complete) ImGui.TextDisabled(Loc.L("完了済み", "Complete"));
            else if (!readyInBags && enough) ImGui.TextDisabled(Loc.L("手持ちへ移動", "Move to bags"));
        }
        ImGui.EndTable();
    }

    private void DrawCrafterClipboardRecipeTable(CrafterLevelingSettings settings)
    {
        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f), Loc.L("レシピ", "Recipes"));
        var recipeSheet = Plugin.DataManager.GetExcelSheet<Recipe>();
        var rows = settings.RecipePresets
            .Where(x => x.MinLevel <= crafterClipboardMaxLevel && x.MaxLevel >= crafterClipboardMinLevel)
            .OrderBy(x => x.MinLevel).ThenBy(x => x.JobId).ToArray();
        var jobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        if (!ImGui.BeginTable("crafter-clipboard-recipes", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;
        foreach (var heading in new[] { "minLv", "MaxLv", Loc.L("対象職", "Job"), Loc.L("方式", "Method"),
                     Loc.L("レシピ", "Recipe"), Loc.L("制作個数", "Craft count") })
            ImGui.TableSetupColumn(heading);
        ImGui.TableHeadersRow();
        foreach (var preset in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(preset.MinLevel.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(preset.MaxLevel.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(jobSheet.TryGetRow(preset.JobId, out var job)
                ? job.Abbreviation.ToString()
                : preset.JobId.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(preset.Route switch
            {
                CrafterLevelingRoute.Restoration => Loc.L("復興", "Restoration"),
                CrafterLevelingRoute.Collectable => Loc.L("収集品", "Collectable"),
                _ => Loc.L("通常製作", "Normal"),
            });
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(recipeSheet.TryGetRow(preset.RecipeId, out var recipe)
                ? recipe.ItemResult.Value.Name.ToString()
                : $"Recipe ID {preset.RecipeId}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(preset.MaxCraftCount.ToString());
        }
        ImGui.EndTable();
    }

    private void DrawCrafterClipboardGearTable(CrafterLevelingSettings settings)
    {
        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f), Loc.L("対象装備", "Target gear"));
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var jobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        if (!ImGui.BeginTable("crafter-clipboard-gear", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;
        foreach (var heading in new[] { Loc.L("装備Lv", "Gear Lv"), Loc.L("対象職", "Job"),
                     Loc.L("部位", "Slot"), Loc.L("装備", "Gear") })
            ImGui.TableSetupColumn(heading);
        ImGui.TableHeadersRow();
        foreach (var preset in settings.GearPresets
                     .Where(x => x.TierLevel >= crafterClipboardMinLevel && x.TierLevel <= crafterClipboardMaxLevel)
                     .OrderBy(x => x.TierLevel))
        {
            foreach (var itemId in preset.SharedItemIds)
                DrawGearRow(preset.TierLevel, Loc.L("共通", "Shared"), itemId);
            foreach (var jobItems in preset.JobItemIds.OrderBy(x => x.Key))
            {
                var job = jobSheet.TryGetRow(jobItems.Key, out var classJob)
                    ? classJob.Abbreviation.ToString()
                    : jobItems.Key.ToString();
                foreach (var itemId in jobItems.Value) DrawGearRow(preset.TierLevel, job, itemId);
            }
        }
        ImGui.EndTable();
        return;

        void DrawGearRow(int level, string job, uint itemId)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(level.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(job);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(itemSheet.TryGetRow(itemId, out var slotItem)
                ? CrafterPlanClipboard.GearSlot(slotItem)
                : "—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(itemSheet.TryGetRow(itemId, out var item)
                ? item.Name.ToString()
                : $"Item ID {itemId}");
        }
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
            ? Loc.L($"更新完了：製作品{activeRecipeCount}件",
                $"Updated: {activeRecipeCount} recipes")
            : Loc.L($"リストを更新しました。未解決：製作品{catalog.Unresolved.Count}件、装備{gear.Missing.Count}件。",
                $"List updated. Unresolved: {catalog.Unresolved.Count} recipes, {gear.Missing.Count} gear slots.");
        SaveCrafterSettings();
    }

    private void DrawCrafterStorageSettings(CrafterLevelingSettings settings)
    {
        ImGui.TextDisabled(Loc.L(
            "使用するリテイナーを選択（所持品はゲーム内で開いた時に更新）",
            "Select retainers to include (inventory updates when opened in game)"));
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
                BuildCrafterLevelingList(settings);
            }
            if (jobId != 15) ImGui.SameLine();
        }
    }

    private void DrawCrafterPresetEditor(CrafterLevelingSettings settings)
    {
        ImGui.TextDisabled(Loc.L(
            "通常は編集不要です。追加する製作品を検索してください。",
            "Usually no edits are needed. Search for a product only when adding one."));

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

        DrawCrafterProducts(settings);
        var materialRows = crafterPreparationItems.Where(x => !x.IsGear &&
            (!settings.ShowMissingOnly || x.MissingCount > 0)).ToArray();
        DrawCrafterPreparationTable(settings, "crafter-preparation-materials",
            Loc.L("製作用の素材", "Crafting materials"),
            materialRows.Where(x => !x.IsCrystal).ToArray(), false);
        DrawCrafterPreparationTable(settings, "crafter-preparation-crystals",
            Loc.L("製作用のクリスタル", "Crafting crystals"),
            materialRows.Where(x => x.IsCrystal).ToArray(), false);
        DrawCrafterPreparationTable(settings, "crafter-preparation-gear",
            Loc.L("育成途中で使用する装備", "Gear used while leveling"),
            crafterPreparationItems.Where(x => x.IsGear).ToArray(), true);

    }

    private static void DrawCrafterProducts(CrafterLevelingSettings settings)
    {
        var recipes = Plugin.DataManager.GetExcelSheet<Recipe>();
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var rows = settings.RecipePresets.Where(x =>
                settings.EnabledJobIds.Contains(x.JobId) &&
                CrafterPreparationService.JobLevel(x.JobId) < settings.TargetLevel &&
                x.MaxLevel >= CrafterPreparationService.JobLevel(x.JobId) && x.MinLevel < settings.TargetLevel)
            .OrderBy(x => x.JobId).ThenBy(x => x.MinLevel).ToArray();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f), Loc.L("製作品", "Products"));
        if (rows.Length == 0)
        {
            ImGui.TextDisabled(Loc.L("該当項目なし", "No items"));
            return;
        }
        if (!ImGui.BeginTable("crafter-products", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn(Loc.L("完成品", "Product"), ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn(Loc.L("コピー", "Copy"), ImGuiTableColumnFlags.WidthFixed,
            72 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("制作Lv", "Recipe Lv"), ImGuiTableColumnFlags.WidthFixed,
            72 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("対象職", "Job"), ImGuiTableColumnFlags.WidthFixed,
            68 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("残り制作数", "Crafts remaining"), ImGuiTableColumnFlags.WidthFixed,
            100 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach (var preset in rows)
        {
            var name = recipes.TryGetRow(preset.RecipeId, out var recipe)
                ? recipe.ItemResult.Value.Name.ToString()
                : $"Recipe ID {preset.RecipeId}";
            settings.PlannedCraftCounts.TryGetValue(preset.RecipeId, out var planned);
            settings.CompletedCraftCounts.TryGetValue(preset.RecipeId, out var completed);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(name);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{Loc.L("コピー", "Copy")}##copy-product-{preset.JobId}-{preset.RecipeId}-{preset.MinLevel}"))
                ImGui.SetClipboardText(name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(recipes.TryGetRow(preset.RecipeId, out var levelRecipe)
                ? $"Lv{levelRecipe.RecipeLevelTable.Value.ClassJobLevel}"
                : "—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(jobs.TryGetRow(preset.JobId, out var job)
                ? job.Abbreviation.ToString()
                : preset.JobId.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(Math.Max(0, planned - completed).ToString("N0"));
        }
        ImGui.EndTable();
    }

    private static void DrawCrafterPreparationTable(CrafterLevelingSettings settings, string id, string title,
        IReadOnlyList<CrafterPreparationItem> rows, bool gearTable)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f), title);
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(Loc.L("該当項目なし", "No items"));
            return;
        }
        var columnCount = gearTable ? 5 : 6;
        if (!ImGui.BeginTable(id, columnCount,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, Math.Min(220, 30 + rows.Count * 24) * ImGuiHelpers.GlobalScale))) return;
        ImGui.TableSetupColumn(Loc.L("アイテム", "Item"), ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn(Loc.L("コピー", "Copy"), ImGuiTableColumnFlags.WidthFixed,
            72 * ImGuiHelpers.GlobalScale);
        if (gearTable)
        {
            ImGui.TableSetupColumn(Loc.L("装備Lv", "Equip Lv"), ImGuiTableColumnFlags.WidthFixed,
                72 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn(Loc.L("所持", "Owned"), ImGuiTableColumnFlags.WidthFixed,
                70 * ImGuiHelpers.GlobalScale);
        }
        else
        {
            foreach (var heading in new[] { Loc.L("必要", "Required"), Loc.L("所持", "Owned"),
                         Loc.L("不足", "Missing") })
                ImGui.TableSetupColumn(heading, ImGuiTableColumnFlags.WidthFixed,
                    70 * ImGuiHelpers.GlobalScale);
        }
        ImGui.TableSetupColumn(Loc.L("所在", "Location"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableHeadersRow();
        foreach (var item in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Name);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Item ID: {item.ItemId}");
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{Loc.L("コピー", "Copy")}##copy-crafter-item-{id}-{item.ItemId}"))
                ImGui.SetClipboardText(item.Name);
            if (gearTable)
            {
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"Lv{item.EquipLevel}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.OwnedCount.ToString("N0"));
            }
            else
            {
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.RequiredCount.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.OwnedCount.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextColored(item.MissingCount > 0 ? new Vector4(1f, 0.35f, 0.3f, 1f) :
                    new Vector4(0.35f, 0.9f, 0.5f, 1f), item.MissingCount.ToString("N0"));
            }
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
