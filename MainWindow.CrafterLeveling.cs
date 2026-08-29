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
    private string crafterCatalogMessage = string.Empty;
    private string crafterGearMessage = string.Empty;

    private void DrawCrafterLeveling()
    {
        DrawPageTitle(Loc.L("クラフター自動レベリング", "Crafter Auto-Leveling"),
            Loc.L("8職を装備Tierごとに揃えて育成するための準備と進捗を管理します。",
                "Prepare and track tier-based leveling for all eight crafting jobs."));
        var settings = plugin.GetCrafterLevelingSettings();

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
        }

        if (ImGui.CollapsingHeader(Loc.L("レベリング製作品", "Leveling recipes"),
                ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrafterPresetEditor(settings);

        if (ImGui.CollapsingHeader(Loc.L("装備Tier", "Gear tiers"), ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrafterGearTiers(settings);

        if (ImGui.CollapsingHeader(Loc.L("リテイナー保管設定", "Retainer storage"),
                ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrafterStorageSettings(settings);

        if (ImGui.CollapsingHeader(Loc.L("準備リスト", "Preparation list"),
                ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrafterPreparationList(settings);

        if (ImGui.CollapsingHeader(Loc.L("実行状態", "Progress")))
        {
            ImGui.TextUnformatted($"{Loc.L("状態", "State")}：{settings.Progress.State}");
            if (!string.IsNullOrWhiteSpace(settings.Progress.LastError))
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.3f, 1f), settings.Progress.LastError);
            ImGui.BeginDisabled();
            ImGui.Button(Loc.L("自動レベリング開始（Phase 4以降）", "Start auto-leveling (Phase 4+)"));
            ImGui.EndDisabled();
        }
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

    private void DrawCrafterGearTiers(CrafterLevelingSettings settings)
    {
        ImGui.TextDisabled(Loc.L(
            "育成途中で着替えるクラフター装備候補を作成します。共通防具・アクセサリと、選択中の職だけの主道具・副道具を準備リストへ追加します。",
            "Creates crafting gear candidates used while leveling. Adds shared gear and tools for selected jobs to the preparation list."));
        if (ImGui.Button(Loc.L("選択職用の装備候補を作成・更新", "Create/update gear for selected jobs")))
        {
            var result = CrafterGearCatalog.BuildStandard(settings);
            crafterGearMessage = result.Missing.Count == 0
                ? Loc.L($"{result.TierCount}段階の装備リストを作成しました。",
                    $"Created {result.TierCount} gear tiers.")
                : Loc.L($"装備リストを作成しました。未解決{result.Missing.Count}件。",
                    $"Created gear tiers with {result.Missing.Count} unresolved slots.");
            SaveCrafterSettings();
        }
        if (!string.IsNullOrWhiteSpace(crafterGearMessage)) ImGui.TextWrapped(crafterGearMessage);

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var jobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        foreach (var tier in settings.GearPresets.OrderBy(x => x.TierLevel))
        {
            if (!ImGui.TreeNode($"Lv{tier.TierLevel}##crafter-gear-tier-{tier.TierLevel}")) continue;
            var shared = tier.SharedItemIds.Select(id => itemSheet.TryGetRow(id, out var item)
                    ? item.Name.ToString() : $"Item {id}")
                .GroupBy(x => x).Select(x => x.Count() > 1 ? $"{x.Key} ×{x.Count()}" : x.Key);
            ImGui.TextWrapped($"{Loc.L("共通装備", "Shared gear")}：{string.Join("、", shared)}");
            foreach (var job in tier.JobItemIds.Where(x => settings.EnabledJobIds.Contains(x.Key))
                         .OrderBy(x => x.Key))
            {
                var jobName = jobSheet.TryGetRow(job.Key, out var classJob)
                    ? classJob.Abbreviation.ToString() : $"Job {job.Key}";
                var tools = job.Value.Select(id => itemSheet.TryGetRow(id, out var item)
                    ? item.Name.ToString() : $"Item {id}");
                ImGui.BulletText($"{jobName}：{string.Join(" / ", tools)}");
            }
            ImGui.TreePop();
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

        var standardUpperLevel = Math.Min(settings.TargetLevel, 50);
        if (ImGui.Button(Loc.L($"標準Lv1～{standardUpperLevel}リストを作成",
                $"Create standard Lv1-{standardUpperLevel} list")))
        {
            var result = CrafterLevelingCatalog.ApplyStandard(settings);
            crafterCatalogMessage = result.Unresolved.Count == 0
                ? Loc.L($"{result.Added}件追加、{result.Skipped}件登録済み。",
                    $"Added {result.Added}; {result.Skipped} already registered.")
                : Loc.L($"{result.Added}件追加。未解決：{string.Join("、", result.Unresolved)}",
                    $"Added {result.Added}. Unresolved: {string.Join(", ", result.Unresolved)}");
            SaveCrafterSettings();
            var service = new CrafterPreparationService();
            crafterPreparationItems = service.Build(settings, out crafterPreparationErrors);
        }
        ImGui.SameLine();
        ImGui.TextDisabled(Loc.L(
            "Lv20までは既定リスト、Lv21～50は5レベル帯ごとにゲームデータから選定します。数量は後から調整できます。",
            "Uses the curated list through Lv20, then game data in five-level bands through Lv50. Quantities remain editable."));
        if (!string.IsNullOrWhiteSpace(crafterCatalogMessage))
            ImGui.TextWrapped(crafterCatalogMessage);

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
            .Where(x => settings.EnabledJobIds.Contains(x.Preset.JobId)).ToArray();
        if (visiblePresets.Length > 0 && ImGui.BeginTable("crafter-leveling-recipes", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            foreach (var heading in new[] { Loc.L("製作品", "Product"), Loc.L("ジョブ", "Job"),
                         Loc.L("レシピLv", "Recipe Lv"), Loc.L("使用Lv帯", "Level range"),
                         Loc.L("最大製作数", "Max crafts"), string.Empty })
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
            var craftCount = preset.MaxCraftCount;
            ImGui.SetNextItemWidth(85 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt($"##crafter-count-{index}", ref craftCount, 1, 10))
            {
                preset.MaxCraftCount = Math.Clamp(craftCount, 1, 9999);
                SaveCrafterSettings();
                var service = new CrafterPreparationService();
                crafterPreparationItems = service.Build(settings, out crafterPreparationErrors);
            }
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
        if (ImGui.Button(Loc.L("準備リスト生成", "Build preparation list")))
        {
            var service = new CrafterPreparationService();
            crafterPreparationItems = service.Build(settings, out crafterPreparationErrors);
            plugin.Configuration.Save();
        }
        ImGui.SameLine();
        var missingOnly = settings.ShowMissingOnly;
        if (ImGui.Checkbox(Loc.L("不足のみ表示", "Missing only"), ref missingOnly))
        {
            settings.ShowMissingOnly = missingOnly;
            SaveCrafterSettings();
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.L("不足している素材・装備をコピー", "Copy missing materials and gear")))
        {
            var lines = crafterPreparationItems.Where(x => x.MissingCount > 0)
                .Select(x =>
                {
                    var locations = CrafterInventoryLocator.GetLocations(settings, x.ItemId);
                    var locationText = locations.Count > 0 ? $"（所在：{string.Join(" / ", locations)}）" : string.Empty;
                    return $"{x.Name} ×{x.MissingCount}{locationText}";
                });
            ImGui.SetClipboardText($"【AltMate クラフター育成 不足品】\n\n{string.Join("\n", lines)}");
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
            Loc.L("製作用の素材・クリスタル", "Crafting materials and crystals"),
            rows.Where(x => !x.IsGear).ToArray());
        DrawCrafterPreparationTable(settings, "crafter-preparation-gear",
            Loc.L("育成途中で使用する装備", "Gear used while leveling"),
            rows.Where(x => x.IsGear).ToArray());
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
        if (!ImGui.BeginTable(id, 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, Math.Min(220, 30 + rows.Count * 24) * ImGuiHelpers.GlobalScale))) return;
        foreach (var heading in new[] { Loc.L("アイテム", "Item"), Loc.L("分類", "Type"),
                     Loc.L("必要", "Required"), Loc.L("所持", "Owned"), Loc.L("不足", "Missing"),
                     Loc.L("所在", "Location") })
            ImGui.TableSetupColumn(heading);
        ImGui.TableHeadersRow();
        foreach (var item in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Name);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Item ID: {item.ItemId}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.IsGear ? Loc.L("装備", "Gear") :
                item.IsCrystal ? Loc.L("クリスタル", "Crystal") : Loc.L("素材", "Material"));
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
        crafterPreparationItems = [];
        crafterPreparationErrors = [];
    }

}
