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
    private string crafterStorageMessage = string.Empty;
    private string crafterTransferMessage = string.Empty;
    private bool crafterTransferMessageIsError;
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
            Loc.L("Phase 3：取得・返却計画", "Phase 3: Withdrawal and return planning"));
        ImGui.TextWrapped(Loc.L(
            "必要数・プレイヤー所持数・リテイナー在庫から安全な移動計画を作成します。実際のアイテム移動は計画検証後に有効化します。",
            "Builds a safe transfer plan from requirements, player inventory, and retainer caches. Item movement remains disabled until validation is complete."));
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

        if (ImGui.CollapsingHeader(Loc.L("最初に設定：リテイナーベル", "First: Summoning bell"),
                ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrafterBellRegistration(settings);

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

        if (ImGui.CollapsingHeader(Loc.L("取得・返却計画", "Transfer plan"),
                ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrafterTransferPlan(settings);

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

    private void DrawCrafterTransferPlan(CrafterLevelingSettings settings)
    {
        var bellReady = settings.Bell.IsRegistered &&
                        settings.Bell.TerritoryId == Plugin.ClientState.TerritoryType &&
                        Plugin.ObjectTable.LocalPlayer is { } local &&
                        Vector3.Distance(local.Position,
                            new Vector3(settings.Bell.X, settings.Bell.Y, settings.Bell.Z)) <= 6f;
        ImGui.TextColored(settings.Bell.IsRegistered
                ? new Vector4(0.35f, 0.9f, 0.5f, 1f)
                : new Vector4(1f, 0.35f, 0.3f, 1f),
            settings.Bell.IsRegistered ? Loc.L("✓ ベル登録済み", "✓ Bell registered") :
                Loc.L("✕ ベル未登録", "✕ Bell not registered"));
        ImGui.SameLine();
        ImGui.TextColored(bellReady ? new Vector4(0.35f, 0.9f, 0.5f, 1f) :
                new Vector4(1f, 0.72f, 0.2f, 1f),
            bellReady ? Loc.L("現在ベル付近", "Near the bell") : Loc.L("現在ベルから離れています", "Not near the bell"));

        ImGui.BeginDisabled(crafterPreparationItems.Count == 0 || settings.SelectedRetainerIds.Count == 0);
        if (ImGui.Button(Loc.L("取得・返却計画を生成", "Build transfer plan")))
        {
            settings.TransferPlan = CrafterTransferPlanner.Build(settings, crafterPreparationItems);
            settings.Progress.State = CrafterLevelingState.Preparing;
            settings.Progress.UpdatedAt = DateTime.Now;
            plugin.Configuration.Save();
        }
        ImGui.EndDisabled();
        if (crafterPreparationItems.Count == 0)
            ImGui.TextDisabled(Loc.L("先に準備リストを生成してください。", "Build the preparation list first."));

        var plan = settings.TransferPlan;
        if (!string.IsNullOrWhiteSpace(CrafterTransferExecutor.StatusJapanese))
            ImGui.TextColored(CrafterTransferExecutor.StatusIsError
                    ? new Vector4(1f, 0.35f, 0.3f, 1f)
                    : new Vector4(0.35f, 0.9f, 0.5f, 1f),
                Loc.L(CrafterTransferExecutor.StatusJapanese, CrafterTransferExecutor.StatusEnglish));
        if (!string.IsNullOrWhiteSpace(CrafterBellAutomation.StatusJapanese))
            ImGui.TextColored(CrafterBellAutomation.StatusIsError
                    ? new Vector4(1f, 0.35f, 0.3f, 1f)
                    : new Vector4(0.35f, 0.9f, 0.5f, 1f),
                Loc.L(CrafterBellAutomation.StatusJapanese, CrafterBellAutomation.StatusEnglish));
        if (plan.CreatedAt == default)
        {
            ImGui.TextDisabled(Loc.L(
                "計画未生成。現在の準備リストとリテイナー在庫を確認してから生成してください。",
                "No plan generated. Verify the current preparation list and retainer inventory first."));
            return;
        }
        ImGui.TextUnformatted(Loc.L($"計画作成：{plan.CreatedAt:MM/dd HH:mm:ss}",
            $"Created: {plan.CreatedAt:MM/dd HH:mm:ss}"));
        ImGui.TextColored(plan.IsReady ? new Vector4(0.35f, 0.9f, 0.5f, 1f) :
                new Vector4(1f, 0.35f, 0.3f, 1f),
            plan.IsReady ? Loc.L("取得可能", "Ready") :
                Loc.L($"在庫不足 {plan.UnavailableItems.Count}種類", $"Unavailable items: {plan.UnavailableItems.Count}"));

        ImGui.BeginDisabled(plan.Withdrawals.Count == 0 || CrafterTransferExecutor.IsRunning ||
                            CrafterBellAutomation.IsRunning);
        if (ImGui.Button(Loc.L("ベルから全リテイナー分を自動取得", "Auto-withdraw from all retainers")))
        {
            var result = CrafterBellAutomation.Begin(settings, plan.Withdrawals);
            crafterTransferMessage = Loc.L(result.JapaneseMessage, result.EnglishMessage);
            crafterTransferMessageIsError = !result.Success;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(Loc.L("登録ベル付近から、計画順にリテイナーを呼び出します。",
            "Calls retainers in plan order while near the registered bell."));
        if (plan.Withdrawals.Count == 0)
        {
            var unscanned = settings.SelectedRetainerIds.Count(id =>
                !settings.RetainerInventories.ContainsKey(id));
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f), unscanned > 0
                ? Loc.L($"自動取得できません：選択中のリテイナー{unscanned}人の所持品が未確認です。一度ずつ開いて再スキャンしてください。",
                    $"Cannot auto-withdraw: {unscanned} selected retainers have not been scanned. Open each once and rebuild the plan.")
                : plan.UnavailableItems.Count > 0
                    ? Loc.L("自動取得できません：選択したリテイナーに取得可能な不足品がありません。在庫・選択リテイナー・準備リストを確認してください。",
                        "Cannot auto-withdraw: no missing items are available from selected retainers. Check inventory, selection, and preparation list.")
                    : Loc.L("自動取得するアイテムがありません。", "There are no items to withdraw."));
        }

        ImGui.BeginDisabled(plan.Withdrawals.Count == 0 || CrafterTransferExecutor.IsRunning ||
                            CrafterBellAutomation.IsRunning);
        if (ImGui.Button(Loc.L("現在のリテイナー分を連続取得", "Withdraw all from current retainer")))
        {
            var result = CrafterTransferExecutor.BeginBatch(plan.Withdrawals);
            crafterTransferMessage = Loc.L(result.JapaneseMessage, result.EnglishMessage);
            crafterTransferMessageIsError = !result.Success;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(Loc.L("対象リテイナーの所持品画面を開いてから実行してください。",
            "Open the target retainer inventory before running."));
        if (!string.IsNullOrWhiteSpace(crafterTransferMessage))
            ImGui.TextColored(crafterTransferMessageIsError
                ? new Vector4(1f, 0.35f, 0.3f, 1f)
                : new Vector4(0.35f, 0.9f, 0.5f, 1f), crafterTransferMessage);

        if (ImGui.BeginTable("crafter-transfer-plan", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, 220 * ImGuiHelpers.GlobalScale)))
        {
            foreach (var heading in new[] { Loc.L("処理", "Action"), Loc.L("リテイナー", "Retainer"),
                         Loc.L("アイテム", "Item"), "ID", Loc.L("数量", "Qty") })
                ImGui.TableSetupColumn(heading);
            ImGui.TableHeadersRow();
            foreach (var line in plan.Returns.Concat(plan.Withdrawals))
            {
                var returning = plan.Returns.Contains(line);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(returning ? Loc.L("返却", "Return") : Loc.L("取得", "Withdraw"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.RetainerName);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.ItemName);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.ItemId.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.Quantity.ToString("N0"));
            }
            ImGui.EndTable();
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

    private void DrawCrafterBellRegistration(CrafterLevelingSettings settings)
    {
        ImGui.TextDisabled(Loc.L(
            "自動取得で使うベルです。ゲーム内でベルをターゲットしてから一度だけ登録してください。",
            "This bell is used for automatic withdrawals. Target it in game and register it once."));
        ImGui.TextUnformatted(Loc.L("使用するリテイナーベル", "Summoning bell"));
        if (settings.Bell.IsRegistered)
        {
            ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.5f, 1f),
                $"{settings.Bell.ObjectName} / Territory {settings.Bell.TerritoryId} / " +
                $"({settings.Bell.X:F1}, {settings.Bell.Y:F1}, {settings.Bell.Z:F1})");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                Loc.L("未登録", "Not registered"));
        }
        if (ImGui.Button(Loc.L("ターゲット中のベルを登録", "Register targeted bell")))
        {
            var target = Plugin.TargetManager.Target;
            var name = target?.Name.ToString() ?? string.Empty;
            if (target is null || (!name.Contains("ベル", StringComparison.OrdinalIgnoreCase) &&
                                   !name.Contains("呼び鈴", StringComparison.OrdinalIgnoreCase) &&
                                   !name.Contains("bell", StringComparison.OrdinalIgnoreCase)))
            {
                crafterStorageMessage = Loc.L("リテイナーベルをターゲットしてから登録してください。",
                    "Target a summoning bell before registering it.");
            }
            else
            {
                settings.Bell = new CrafterBellRegistration
                {
                    IsRegistered = true,
                    TerritoryId = Plugin.ClientState.TerritoryType,
                    ObjectId = target.BaseId,
                    ObjectName = name,
                    X = target.Position.X,
                    Y = target.Position.Y,
                    Z = target.Position.Z,
                };
                crafterStorageMessage = Loc.L("ベルを登録しました。", "Bell registered.");
                SaveCrafterSettings();
            }
        }
        if (settings.Bell.IsRegistered)
        {
            ImGui.SameLine();
            if (ImGui.Button(Loc.L("登録解除", "Clear registration")))
            {
                settings.Bell = new CrafterBellRegistration();
                SaveCrafterSettings();
            }
        }
        if (!string.IsNullOrWhiteSpace(crafterStorageMessage))
            ImGui.TextWrapped(crafterStorageMessage);

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
            InvalidateCrafterTransferPlan(settings);
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
                .Select(x => $"{x.Name} ×{x.MissingCount}");
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
        DrawCrafterPreparationTable("crafter-preparation-materials", Loc.L("製作用の素材・クリスタル", "Crafting materials and crystals"),
            rows.Where(x => !x.IsGear).ToArray());
        DrawCrafterPreparationTable("crafter-preparation-gear", Loc.L("育成途中で使用する装備", "Gear used while leveling"),
            rows.Where(x => x.IsGear).ToArray());
    }

    private static void DrawCrafterPreparationTable(string id, string title,
        IReadOnlyList<CrafterPreparationItem> rows)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f), title);
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(Loc.L("該当項目なし", "No items"));
            return;
        }
        if (!ImGui.BeginTable(id, 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, Math.Min(220, 30 + rows.Count * 24) * ImGuiHelpers.GlobalScale))) return;
        foreach (var heading in new[] { Loc.L("アイテム", "Item"), Loc.L("分類", "Type"),
                     Loc.L("必要", "Required"), Loc.L("所持", "Owned"), Loc.L("不足", "Missing") })
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
        }
        ImGui.EndTable();
    }

    private void SaveCrafterSettings()
    {
        var settings = plugin.GetCrafterLevelingSettings();
        InvalidateCrafterTransferPlan(settings);
        settings.Progress.UpdatedAt = DateTime.Now;
        plugin.Configuration.Save();
        crafterPreparationItems = [];
        crafterPreparationErrors = [];
    }

    private static void InvalidateCrafterTransferPlan(CrafterLevelingSettings settings)
    {
        settings.TransferPlan = new CrafterTransferPlan();
        if (settings.Progress.State is CrafterLevelingState.Preparing or
            CrafterLevelingState.WithdrawingItems or CrafterLevelingState.ReturningOldGear)
            settings.Progress.State = CrafterLevelingState.Idle;
    }
}
