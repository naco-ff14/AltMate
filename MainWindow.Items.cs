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
    private string itemSearch = string.Empty;
    private IReadOnlyList<StoredItemRecord> itemSearchResults = Array.Empty<StoredItemRecord>();
    private string lastItemSearch = "\0";
    private string recipeSearch = string.Empty;
    private string lastRecipeSearch = "\0";
    private int recipeCraftCount = 1;
    private uint selectedStorageRecipeId;
    private IReadOnlyList<(uint Id, string Name)> storageRecipeCandidates = Array.Empty<(uint, string)>();
    private DateTime nextItemResultRefreshUtc;

    private void DrawItems()
    {
        ImGui.TextColored(ThemeAccent, Loc.L("全キャラ アイテム管理", "All-character item storage"));
        ImGui.TextDisabled(Loc.L(
            "各保管先をゲームで読み込んだ時点の情報を保存します。FCチェストはFC単位で1回だけ集計します。",
            "Stores the latest data loaded by the game. FC chests are counted once per free company."));
        ImGui.Spacing();

        DrawRecipeStoragePlanner();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.47f, 0.65f, 0.78f, 1f),
            Loc.L("アイテムを直接検索", "Direct item search"));

        ImGui.SetNextItemWidth(MathF.Min(480 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##item-storage-search",
            Loc.L("アイテム名またはアイテムIDで検索", "Search by item name or item ID"), ref itemSearch, 120);
        if (!string.Equals(itemSearch, lastItemSearch, StringComparison.Ordinal) ||
            DateTime.UtcNow >= nextItemResultRefreshUtc)
        {
            lastItemSearch = itemSearch;
            nextItemResultRefreshUtc = DateTime.UtcNow.AddSeconds(2);
            itemSearchResults = itemSearch.Trim().Length < 1
                ? Array.Empty<StoredItemRecord>()
                : plugin.ItemInventory.Search(itemSearch);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(Loc.L($"{itemSearchResults.Select(x => x.ItemId).Distinct().Count():N0}件",
            $"{itemSearchResults.Select(x => x.ItemId).Distinct().Count():N0} items"));
        ImGui.Separator();

        if (string.IsNullOrWhiteSpace(itemSearch))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(Loc.L(
                "作りたいアイテムの素材名を入力すると、どのキャラクターのどの保管先に何個あるか表示します。",
                "Enter a material name to see which character and storage location holds it."));
            DrawItemScanGuide();
            return;
        }
        if (itemSearchResults.Count == 0)
        {
            ImGui.TextDisabled(Loc.L("保存済みデータに該当するアイテムがありません。", "No matching item in saved data."));
            DrawItemScanGuide();
            return;
        }

        foreach (var itemGroup in itemSearchResults.GroupBy(x => x.ItemId))
        {
            var first = itemGroup.First();
            var total = itemGroup.Sum(x => x.Quantity);
            if (!ImGui.CollapsingHeader($"{first.ItemName}  ×{total:N0}##stored-item-{first.ItemId}",
                    ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            if (!ImGui.BeginTable($"item-locations-{first.ItemId}", 4,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
                continue;
            ImGui.TableSetupColumn(Loc.L("キャラクター／FC", "Character / FC"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn(Loc.L("保管先", "Storage"), ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn(Loc.L("個数", "Qty"), ImGuiTableColumnFlags.WidthFixed, 72 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn(Loc.L("最終確認", "Last seen"), ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();
            foreach (var row in itemGroup.OrderBy(x => x.StorageKind).ThenBy(x => x.OwnerName))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(row.WorldName)
                    ? row.OwnerName : $"{row.OwnerName} @ {row.WorldName}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.StorageName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{row.Quantity:N0}{(row.IsHq ? " HQ" : string.Empty)}");
                ImGui.TableNextColumn();
                var local = row.ScannedAtUtc.ToLocalTime();
                var stale = DateTime.UtcNow - row.ScannedAtUtc > TimeSpan.FromDays(7);
                if (stale) ImGui.TextColored(new Vector4(0.96f, 0.68f, 0.25f, 1f), local.ToString("M/d HH:mm"));
                else ImGui.TextUnformatted(local.ToString("M/d HH:mm"));
            }
            ImGui.EndTable();
            ImGui.Spacing();
        }
    }

    private void DrawRecipeStoragePlanner()
    {
        ImGui.TextColored(new Vector4(0.47f, 0.65f, 0.78f, 1f),
            Loc.L("作りたいアイテムから素材を探す", "Find materials for a recipe"));
        ImGui.SetNextItemWidth(MathF.Min(420 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 130 * ImGuiHelpers.GlobalScale));
        ImGui.InputTextWithHint("##stored-recipe-search", Loc.L("製作アイテム名", "Crafted item name"),
            ref recipeSearch, 120);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt(Loc.L("回##stored-craft-count", "crafts##stored-craft-count"), ref recipeCraftCount, 1, 10);
        recipeCraftCount = Math.Clamp(recipeCraftCount, 1, 9999);

        if (!string.Equals(recipeSearch, lastRecipeSearch, StringComparison.Ordinal))
        {
            lastRecipeSearch = recipeSearch;
            selectedStorageRecipeId = 0;
            var term = recipeSearch.Trim();
            storageRecipeCandidates = term.Length < 1
                ? Array.Empty<(uint, string)>()
                : Plugin.DataManager.GetExcelSheet<Recipe>()
                    .Where(x => x.ItemResult.RowId != 0 &&
                                x.ItemResult.Value.Name.ToString().Contains(term, StringComparison.CurrentCultureIgnoreCase))
                    .Select(x => (x.RowId, x.ItemResult.Value.Name.ToString()))
                    .OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase)
                    .Take(20).ToArray();
            if (storageRecipeCandidates.Count == 1)
                selectedStorageRecipeId = storageRecipeCandidates[0].Id;
        }

        if (storageRecipeCandidates.Count > 0 && selectedStorageRecipeId == 0)
        {
            ImGui.SetNextItemWidth(MathF.Min(540 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
            var preview = Loc.L("レシピを選択", "Select a recipe");
            if (ImGui.BeginCombo("##stored-recipe-candidate", preview))
            {
                foreach (var candidate in storageRecipeCandidates)
                    if (ImGui.Selectable($"{candidate.Name}  (Recipe #{candidate.Id})##stored-recipe-{candidate.Id}"))
                        selectedStorageRecipeId = candidate.Id;
                ImGui.EndCombo();
            }
        }

        if (selectedStorageRecipeId == 0 ||
            !Plugin.DataManager.GetExcelSheet<Recipe>().TryGetRow(selectedStorageRecipeId, out var recipe))
            return;

        ImGui.TextUnformatted(Loc.L($"{recipe.ItemResult.Value.Name}：{recipeCraftCount:N0}回分の素材",
            $"{recipe.ItemResult.Value.Name}: materials for {recipeCraftCount:N0} crafts"));
        if (!ImGui.BeginTable("stored-recipe-materials", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn(Loc.L("素材", "Material"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn(Loc.L("必要／所持", "Need / owned"), ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("不足", "Missing"), ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("保管場所", "Locations"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableHeadersRow();
        for (var index = 0; index < recipe.Ingredient.Count; index++)
        {
            var ingredientId = recipe.Ingredient[index].RowId;
            var amount = recipe.AmountIngredient[index];
            if (ingredientId == 0 || amount == 0) continue;
            var required = checked((int)amount * recipeCraftCount);
            var locations = plugin.ItemInventory.Search(ingredientId.ToString());
            var owned = locations.Sum(x => x.Quantity);
            var missing = Math.Max(0, required - owned);
            var itemName = recipe.Ingredient[index].Value.Name.ToString();
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(itemName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{required:N0} / {owned:N0}");
            ImGui.TableNextColumn();
            if (missing > 0) ImGui.TextColored(new Vector4(0.96f, 0.42f, 0.38f, 1f), missing.ToString("N0"));
            else ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.5f, 1f), "0");
            ImGui.TableNextColumn();
            if (locations.Count == 0) ImGui.TextDisabled("—");
            else ImGui.TextWrapped(string.Join(Loc.IsEnglish ? ", " : "、", locations.Select(x =>
                $"{x.OwnerName}：{x.StorageName} ×{x.Quantity:N0}")));
        }
        ImGui.EndTable();
    }

    private static void DrawItemScanGuide()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.47f, 0.65f, 0.78f, 1f), Loc.L("更新方法", "How data updates"));
        ImGui.BulletText(Loc.L("所持品・チョコボかばん：ログイン中に自動更新", "Inventory / saddlebag: automatic while logged in"));
        ImGui.BulletText(Loc.L("リテイナー：各リテイナーを開くと更新", "Retainers: open each retainer"));
        ImGui.BulletText(Loc.L("FCチェスト：チェストを開き、各タブを読み込むと更新", "FC chest: open the chest and load its tabs"));
        ImGui.BulletText(Loc.L("愛蔵品・ミラージュドレッサー：宿屋などで画面を開くと更新", "Armoire / glamour dresser: open them at an inn or similar location"));
    }
}
