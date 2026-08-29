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
    private string crafterStorageMessage = string.Empty;

    private void DrawCrafterLeveling()
    {
        DrawPageTitle(Loc.L("クラフター自動レベリング", "Crafter Auto-Leveling"),
            Loc.L("8職を装備Tierごとに揃えて育成するための準備と進捗を管理します。",
                "Prepare and track tier-based leveling for all eight crafting jobs."));
        var settings = plugin.Configuration.CrafterLeveling;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.09f, 0.12f, 0.17f, 0.9f));
        ImGui.BeginChild("crafter-phase-status", new Vector2(0, 64 * ImGuiHelpers.GlobalScale), true);
        ImGui.TextColored(new Vector4(0.4f, 0.82f, 1f, 1f),
            Loc.L("Phase 1：準備リスト・Preset基盤", "Phase 1: Preparation list and preset foundation"));
        ImGui.TextWrapped(Loc.L(
            "自動操作はまだ無効です。実Recipe IDと装備Item IDを登録して準備内容を検証してから、次Phaseでリテイナー・Artisan連携を有効化します。",
            "Automation is disabled until real recipe and gear IDs are validated; retainer and Artisan integration follows in the next phases."));
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

        if (ImGui.CollapsingHeader(Loc.L("Recipe Preset", "Recipe presets"),
                ImGuiTreeNodeFlags.DefaultOpen))
            DrawCrafterPresetEditor(settings);

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

        ImGui.Separator();
        ImGui.TextUnformatted(Loc.L("検索対象リテイナー（上から検索）", "Retainers to scan (priority order)"));
        ImGui.TextDisabled(Loc.L(
            "一度ずつリテイナーを開くと所持品を自動スキャンします。チェックしたリテイナーだけ準備数へ合算します。",
            "Open each retainer once to scan automatically. Only checked retainers count toward preparation totals."));
        var knownRetainers = plugin.Configuration.CharacterGil.Values
            .SelectMany(character => character.Retainers.Values)
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
            "素材数はRecipe IDと最大製作数からゲームデータを使って自動展開します。",
            "Materials are expanded from game data using Recipe ID and maximum craft count."));
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(Loc.L("ジョブID", "Job ID"), ref crafterPresetJobId);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(Loc.L("開始Lv", "Min Lv"), ref crafterPresetMinLevel);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(Loc.L("終了Lv", "Max Lv"), ref crafterPresetMaxLevel);
        ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Recipe ID", ref crafterPresetRecipeId);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(Loc.L("最大製作数", "Max crafts"), ref crafterPresetCraftCount);
        ImGui.SameLine();
        if (ImGui.Button(Loc.L("Preset追加", "Add preset")))
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

        for (var index = 0; index < settings.RecipePresets.Count; index++)
        {
            var preset = settings.RecipePresets[index];
            ImGui.BulletText($"Job {preset.JobId} / Lv{preset.MinLevel}-{preset.MaxLevel} / Recipe {preset.RecipeId} × {preset.MaxCraftCount}");
            ImGui.SameLine();
            if (!ImGui.SmallButton($"{Loc.L("削除", "Remove")}##crafter-preset-{index}")) continue;
            settings.RecipePresets.RemoveAt(index--);
            SaveCrafterSettings();
        }
    }

    private void DrawCrafterPreparationList(CrafterLevelingSettings settings)
    {
        if (ImGui.Button(Loc.L("準備リスト生成", "Build preparation list")))
        {
            var service = new CrafterPreparationService();
            crafterPreparationItems = service.Build(settings, out crafterPreparationErrors);
        }
        ImGui.SameLine();
        var missingOnly = settings.ShowMissingOnly;
        if (ImGui.Checkbox(Loc.L("不足のみ表示", "Missing only"), ref missingOnly))
        {
            settings.ShowMissingOnly = missingOnly;
            SaveCrafterSettings();
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.L("不足品をコピー", "Copy missing items")))
        {
            var lines = crafterPreparationItems.Where(x => x.MissingCount > 0)
                .Select(x => $"{x.Name} ×{x.MissingCount}");
            ImGui.SetClipboardText($"【AltMate クラフター育成 不足品】\n\n{string.Join("\n", lines)}");
        }

        foreach (var error in crafterPreparationErrors)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.3f, 1f),
                Loc.L($"無効なPreset：{error}", $"Invalid preset: {error}"));
        if (settings.RecipePresets.Count == 0)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f),
                Loc.L("Recipe Presetが未登録です。準備完了にはなりません。",
                    "No recipe presets are registered; preparation cannot complete."));

        var rows = settings.ShowMissingOnly
            ? crafterPreparationItems.Where(x => x.MissingCount > 0).ToArray()
            : crafterPreparationItems.ToArray();
        if (!ImGui.BeginTable("crafter-preparation", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, 260 * ImGuiHelpers.GlobalScale))) return;
        foreach (var heading in new[] { "ID", Loc.L("アイテム", "Item"), Loc.L("分類", "Type"),
                     Loc.L("必要", "Required"), Loc.L("所持", "Owned"), Loc.L("不足", "Missing") })
            ImGui.TableSetupColumn(heading);
        ImGui.TableHeadersRow();
        foreach (var item in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.ItemId.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Name);
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
        plugin.Configuration.CrafterLeveling.Progress.UpdatedAt = DateTime.Now;
        plugin.SaveSharedSettings();
        crafterPreparationItems = [];
        crafterPreparationErrors = [];
    }
}
