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
    private readonly List<RecipePlanEntry> recipeCraftPlan = new();
    private RecipeMaterial[] recipeMaterialsCache = Array.Empty<RecipeMaterial>();
    private long recipeMaterialsCacheRevision = -1;
    private int recipeMaterialsCacheSignature;
    private IReadOnlyList<(uint Id, string Name)> storageRecipeCandidates = Array.Empty<(uint, string)>();
    private DateTime nextItemResultRefreshUtc;
    private long observedItemInventoryRevision = -1;
    private bool recipeMissingOnly;
    private string companyRecipeSearch = string.Empty;
    private string lastCompanyRecipeSearch = "\0";
    private uint selectedCompanyRecipeId;
    private bool companyMissingOnly;
    private bool companyRawMissingOnly;
    private uint selectedCompanyBreakdownItemId;
    private readonly List<CompanyCraftPlanEntry> companyCraftPlan = new();
    private CompanyMaterial[] companyMaterialsCache = Array.Empty<CompanyMaterial>();
    private CompanyMaterial[] companyRawMaterialsCache = Array.Empty<CompanyMaterial>();
    private long companyMaterialsCacheRevision = -1;
    private int companyMaterialsCacheSignature;
    private Dictionary<uint, Recipe>? normalRecipeByResultItem;
    private IReadOnlyList<(uint Id, string Name)> companyRecipeCandidates = Array.Empty<(uint, string)>();

    private void DrawItems()
    {
        var inventoryRevision = plugin.ItemInventory.Revision;
        if (inventoryRevision != observedItemInventoryRevision)
        {
            observedItemInventoryRevision = inventoryRevision;
            nextItemResultRefreshUtc = default;
        }
        ImGui.TextColored(ThemeAccent, Loc.L("全キャラ アイテム管理", "All-character item storage"));
        ImGui.TextDisabled(Loc.L(
            "各保管先をゲームで読み込んだ時点の情報を保存します。FCチェストはFC単位で1回だけ集計します。",
            "Stores the latest data loaded by the game. FC chests are counted once per free company."));
        if (ImGui.Button(Loc.L("全アイテム一覧を別ウィンドウで開く", "Open all items in a separate window")))
            plugin.ItemCatalogWindow.Open();
        ImGui.Spacing();

        DrawRecipeStoragePlanner();
        ImGui.Spacing();
        DrawCompanyCraftStoragePlanner();
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
            var itemOpen = ImGui.CollapsingHeader($"{first.ItemName}  ×{total:N0}##stored-item-{first.ItemId}",
                ImGuiTreeNodeFlags.DefaultOpen);
            CopyItemNameOnClick(first.ItemName);
            if (!itemOpen)
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

        if (selectedStorageRecipeId != 0 &&
            Plugin.DataManager.GetExcelSheet<Recipe>().TryGetRow(selectedStorageRecipeId, out var selectedRecipe))
        {
            var selectedName = selectedRecipe.ItemResult.Value.Name.ToString();
            if (ImGui.Button(Loc.L($"「{selectedName}」を制作リストに追加",
                    $"Add {selectedName} to craft list")))
            {
                var existing = recipeCraftPlan.FindIndex(x => x.RecipeId == selectedStorageRecipeId);
                if (existing >= 0) recipeCraftPlan[existing].CraftCount = checked(
                    recipeCraftPlan[existing].CraftCount + recipeCraftCount);
                else recipeCraftPlan.Add(new RecipePlanEntry(selectedStorageRecipeId, recipeCraftCount));
                selectedStorageRecipeId = 0;
                recipeSearch = string.Empty;
                lastRecipeSearch = "\0";
                recipeCraftCount = 1;
            }
        }

        if (recipeCraftPlan.Count == 0)
        {
            ImGui.TextDisabled(Loc.L("制作するアイテムを検索してリストに追加してください。",
                "Search for crafted items and add them to the list."));
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.L("制作リスト", "Craft list"));
        var removeAt = -1;
        for (var planIndex = 0; planIndex < recipeCraftPlan.Count; planIndex++)
        {
            var entry = recipeCraftPlan[planIndex];
            if (!Plugin.DataManager.GetExcelSheet<Recipe>().TryGetRow(entry.RecipeId, out var recipeRow)) continue;
            var resultName = recipeRow.ItemResult.Value.Name.ToString();
            ImGui.PushID($"recipe-plan-{entry.RecipeId}");
            DrawCopyableItemText(resultName);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(95 * ImGuiHelpers.GlobalScale);
            var crafts = entry.CraftCount;
            ImGui.InputInt(Loc.L("回", "crafts"), ref crafts, 1, 10);
            entry.CraftCount = Math.Clamp(crafts, 1, 9999);
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.L("削除", "Remove"))) removeAt = planIndex;
            ImGui.PopID();
        }
        if (removeAt >= 0) recipeCraftPlan.RemoveAt(removeAt);
        if (ImGui.SmallButton(Loc.L("すべて消去##recipe-plan-clear", "Clear all##recipe-plan-clear")))
            recipeCraftPlan.Clear();

        var planSignature = PlanSignature(recipeCraftPlan.Select(x => (x.RecipeId, x.CraftCount)));
        if (recipeMaterialsCacheRevision != observedItemInventoryRevision || recipeMaterialsCacheSignature != planSignature)
        {
            var requiredItems = new Dictionary<uint, int>();
            foreach (var entry in recipeCraftPlan)
            {
                if (!Plugin.DataManager.GetExcelSheet<Recipe>().TryGetRow(entry.RecipeId, out var recipe)) continue;
                for (var ingredientIndex = 0; ingredientIndex < recipe.Ingredient.Count; ingredientIndex++)
                {
                    var ingredient = recipe.Ingredient[ingredientIndex];
                    var amount = recipe.AmountIngredient[ingredientIndex];
                    if (ingredient.RowId == 0 || amount == 0) continue;
                    var required = checked((int)amount * entry.CraftCount);
                    requiredItems.TryGetValue(ingredient.RowId, out var current);
                    requiredItems[ingredient.RowId] = checked(current + required);
                }
            }
            recipeMaterialsCache = requiredItems.Select(pair =>
            {
                var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(pair.Key);
                var locations = plugin.ItemInventory.Search(pair.Key.ToString());
                var owned = locations.Sum(row => row.Quantity);
                return new RecipeMaterial(pair.Key, item.Name.ToString(), pair.Value, owned,
                    Math.Max(0, pair.Value - owned), locations);
            }).OrderByDescending(x => x.Missing > 0)
              .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            recipeMaterialsCacheRevision = observedItemInventoryRevision;
            recipeMaterialsCacheSignature = planSignature;
        }
        var materials = recipeMaterialsCache;
        var missingTypes = materials.Count(x => x.Missing > 0);
        ImGui.TextColored(missingTypes == 0
                ? new Vector4(0.35f, 0.9f, 0.5f, 1f)
                : new Vector4(0.96f, 0.68f, 0.25f, 1f),
            missingTypes == 0
                ? Loc.L("必要素材はすべて揃っています。", "All required materials are available.")
                : Loc.L($"{missingTypes:N0}種類の素材が不足しています。", $"{missingTypes:N0} material types are missing."));
        ImGui.SameLine();
        ImGui.Checkbox(Loc.L("不足のみ", "Missing only"), ref recipeMissingOnly);
        if (!ImGui.BeginTable("stored-recipe-materials", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn(Loc.L("素材", "Material"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn(Loc.L("必要／所持", "Need / owned"), ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("不足", "Missing"), ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("保管場所", "Locations"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableHeadersRow();
        foreach (var material in materials)
        {
            if (recipeMissingOnly && material.Missing == 0) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawCopyableItemName(material.Name, $"recipe-material-{material.ItemId}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{material.Required:N0} / {material.Owned:N0}");
            ImGui.TableNextColumn();
            if (material.Missing > 0) ImGui.TextColored(new Vector4(0.96f, 0.42f, 0.38f, 1f), material.Missing.ToString("N0"));
            else ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.5f, 1f), "0");
            ImGui.TableNextColumn();
            if (material.Locations.Count == 0) ImGui.TextDisabled("—");
            else ImGui.TextWrapped(string.Join(Loc.IsEnglish ? ", " : "、", material.Locations.Select(x =>
                $"{x.OwnerName}：{x.StorageName} ×{x.Quantity:N0}")));
        }
        ImGui.EndTable();
    }

    private static void DrawItemScanGuide()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.47f, 0.65f, 0.78f, 1f), Loc.L("更新方法", "How data updates"));
        ImGui.BulletText(Loc.L("所持品・チョコボかばん・アーマリーチェスト：ログイン中に自動更新", "Inventory / saddlebag / armoury chest: automatic while logged in"));
        ImGui.BulletText(Loc.L("リテイナー：各リテイナーを開くと更新", "Retainers: open each retainer"));
        ImGui.BulletText(Loc.L("FCチェスト：チェストを開き、各タブを読み込むと更新", "FC chest: open the chest and load its tabs"));
        ImGui.BulletText(Loc.L("愛蔵品・ミラージュドレッサー：宿屋などで画面を開くと更新", "Armoire / glamour dresser: open them at an inn or similar location"));
    }

    private void DrawCompanyCraftStoragePlanner()
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.47f, 0.65f, 0.78f, 1f),
            Loc.L("FC制作レシピから素材を探す", "Find materials for a company craft"));
        ImGui.SetNextItemWidth(MathF.Min(420 * ImGuiHelpers.GlobalScale,
            ImGui.GetContentRegionAvail().X - 130 * ImGuiHelpers.GlobalScale));
        ImGui.InputTextWithHint("##company-recipe-search",
            Loc.L("飛空艇・潜水艦パーツ、ハウス外装など", "Airship/submersible parts, house exteriors, etc."),
            ref companyRecipeSearch, 120);
        if (!string.Equals(companyRecipeSearch, lastCompanyRecipeSearch, StringComparison.Ordinal))
        {
            lastCompanyRecipeSearch = companyRecipeSearch;
            selectedCompanyRecipeId = 0;
            var term = companyRecipeSearch.Trim();
            companyRecipeCandidates = term.Length < 1
                ? Array.Empty<(uint, string)>()
                : Plugin.DataManager.GetExcelSheet<CompanyCraftSequence>()
                    .Where(x => x.ResultItem.RowId != 0 &&
                                x.ResultItem.Value.Name.ToString().Contains(term,
                                    StringComparison.CurrentCultureIgnoreCase))
                    .Select(x => (x.RowId, x.ResultItem.Value.Name.ToString()))
                    .OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase)
                    .Take(30).ToArray();
            if (companyRecipeCandidates.Count == 1)
                selectedCompanyRecipeId = companyRecipeCandidates[0].Id;
        }

        if (companyRecipeCandidates.Count > 0 && selectedCompanyRecipeId == 0)
        {
            ImGui.SetNextItemWidth(MathF.Min(540 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
            if (ImGui.BeginCombo("##company-recipe-candidate", Loc.L("FC制作レシピを選択", "Select a company craft")))
            {
                foreach (var candidate in companyRecipeCandidates)
                    if (ImGui.Selectable($"{candidate.Name}  (#{candidate.Id})##company-recipe-{candidate.Id}"))
                        selectedCompanyRecipeId = candidate.Id;
                ImGui.EndCombo();
            }
        }

        if (selectedCompanyRecipeId != 0 &&
            Plugin.DataManager.GetExcelSheet<CompanyCraftSequence>().TryGetRow(selectedCompanyRecipeId, out var selected))
        {
            if (ImGui.Button(Loc.L($"「{selected.ResultItem.Value.Name}」を計画に追加",
                    $"Add {selected.ResultItem.Value.Name} to plan")))
            {
                var existing = companyCraftPlan.FindIndex(x => x.SequenceId == selectedCompanyRecipeId);
                if (existing >= 0) companyCraftPlan[existing].Quantity++;
                else companyCraftPlan.Add(new CompanyCraftPlanEntry(selectedCompanyRecipeId, 1));
                selectedCompanyRecipeId = 0;
                companyRecipeSearch = string.Empty;
                lastCompanyRecipeSearch = "\0";
            }
        }

        if (companyCraftPlan.Count == 0)
        {
            ImGui.TextDisabled(Loc.L("製作するパーツや外装を検索して計画に追加してください。",
                "Search for parts or exteriors and add them to the plan."));
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(Loc.L("作成リスト", "Craft plan"));
        var removeAt = -1;
        for (var index = 0; index < companyCraftPlan.Count; index++)
        {
            var entry = companyCraftPlan[index];
            if (!Plugin.DataManager.GetExcelSheet<CompanyCraftSequence>().TryGetRow(entry.SequenceId, out var row))
                continue;
            ImGui.PushID($"company-plan-{entry.SequenceId}");
            DrawCopyableItemText(row.ResultItem.Value.Name.ToString());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(95 * ImGuiHelpers.GlobalScale);
            var quantity = entry.Quantity;
            ImGui.InputInt(Loc.L("個", "items"), ref quantity, 1, 10);
            entry.Quantity = Math.Clamp(quantity, 1, 9999);
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.L("削除", "Remove"))) removeAt = index;
            ImGui.PopID();
        }
        if (removeAt >= 0) companyCraftPlan.RemoveAt(removeAt);
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.L("すべて消去##company-plan-clear", "Clear all##company-plan-clear")))
            companyCraftPlan.Clear();

        var companyPlanSignature = PlanSignature(companyCraftPlan.Select(x => (x.SequenceId, x.Quantity)));
        if (companyMaterialsCacheRevision != observedItemInventoryRevision ||
            companyMaterialsCacheSignature != companyPlanSignature)
        {
            var requiredItems = new Dictionary<uint, int>();
            foreach (var planEntry in companyCraftPlan)
            {
                if (!Plugin.DataManager.GetExcelSheet<CompanyCraftSequence>().TryGetRow(planEntry.SequenceId, out var sequence))
                    continue;
                foreach (var partRef in sequence.CompanyCraftPart)
                {
                    if (partRef.RowId == 0) continue;
                    foreach (var processRef in partRef.Value.CompanyCraftProcess)
                    {
                        if (processRef.RowId == 0) continue;
                        var process = processRef.Value;
                        for (var supplyIndex = 0; supplyIndex < process.SupplyItem.Count; supplyIndex++)
                        {
                            var supplyRef = process.SupplyItem[supplyIndex];
                            if (supplyRef.RowId == 0 || supplyRef.Value.Item.RowId == 0) continue;
                            var quantity = checked((int)process.SetQuantity[supplyIndex] *
                                (int)process.SetsRequired[supplyIndex] * planEntry.Quantity);
                            if (quantity <= 0) continue;
                            requiredItems.TryGetValue(supplyRef.Value.Item.RowId, out var current);
                            requiredItems[supplyRef.Value.Item.RowId] = checked(current + quantity);
                        }
                    }
                }
            }
            companyMaterialsCache = requiredItems.Select(pair =>
            {
                var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(pair.Key);
                var locations = plugin.ItemInventory.Search(pair.Key.ToString());
                var owned = locations.Sum(row => row.Quantity);
                return new CompanyMaterial(pair.Key, item.Name.ToString(), ClassifyCompanyMaterial(item), pair.Value, owned,
                    Math.Max(0, pair.Value - owned), locations);
            }).OrderByDescending(x => x.Missing > 0)
              .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

            var rawRequired = new Dictionary<uint, int>();
            foreach (var material in companyMaterialsCache.Where(x => x.Missing > 0))
                ExpandToRawMaterials(material.ItemId, material.Missing, rawRequired, new HashSet<uint>());
            companyRawMaterialsCache = rawRequired.Select(pair =>
            {
                var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(pair.Key);
                var locations = plugin.ItemInventory.Search(pair.Key.ToString());
                var owned = locations.Sum(x => x.Quantity);
                return new CompanyMaterial(pair.Key, item.Name.ToString(), ClassifyCompanyMaterial(item), pair.Value, owned,
                    Math.Max(0, pair.Value - owned), locations);
            }).OrderByDescending(x => x.Missing > 0)
              .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            companyMaterialsCacheRevision = observedItemInventoryRevision;
            companyMaterialsCacheSignature = companyPlanSignature;
        }
        var materials = companyMaterialsCache;
        var rawMaterials = companyRawMaterialsCache;
        var missingTypes = materials.Count(x => x.Missing > 0);
        ImGui.TextColored(missingTypes == 0
                ? new Vector4(0.35f, 0.9f, 0.5f, 1f)
                : new Vector4(0.96f, 0.68f, 0.25f, 1f),
            missingTypes == 0
                ? Loc.L("全工程の必要素材が揃っています。", "All phase materials are available.")
                : Loc.L($"{missingTypes:N0}種類の素材が不足しています。",
                    $"{missingTypes:N0} material types are missing."));
        ImGui.SameLine();
        ImGui.Checkbox(Loc.L("不足のみ##company-missing-only", "Missing only##company-missing-only"),
            ref companyMissingOnly);

        if (!ImGui.CollapsingHeader(Loc.L("納品素材（中間素材・フレーム）", "Supply materials (intermediates / frames)"),
                ImGuiTreeNodeFlags.DefaultOpen))
            return;
        if (!ImGui.BeginTable("company-recipe-materials", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn(Loc.L("素材", "Material"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn(Loc.L("区分", "Type"), ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("必要／所持", "Need / owned"), ImGuiTableColumnFlags.WidthFixed,
            105 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("不足", "Missing"), ImGuiTableColumnFlags.WidthFixed,
            70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("保管場所", "Locations"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableHeadersRow();
        foreach (var material in materials)
        {
            if (companyMissingOnly && material.Missing == 0) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{material.Name}##company-material-{material.ItemId}",
                    selectedCompanyBreakdownItemId == material.ItemId))
            {
                selectedCompanyBreakdownItemId = material.ItemId;
                ImGui.SetClipboardText(material.Name);
            }
            DrawCopyItemTooltip();
            ImGui.TableNextColumn();
            ImGui.TextDisabled(material.Type);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{material.Required:N0} / {material.Owned:N0}");
            ImGui.TableNextColumn();
            if (material.Missing > 0)
                ImGui.TextColored(new Vector4(0.96f, 0.42f, 0.38f, 1f), material.Missing.ToString("N0"));
            else ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.5f, 1f), "0");
            ImGui.TableNextColumn();
            if (material.Locations.Count == 0) ImGui.TextDisabled("—");
            else ImGui.TextWrapped(string.Join(Loc.IsEnglish ? ", " : "、", material.Locations.Select(x =>
                $"{x.OwnerName}：{x.StorageName} ×{x.Quantity:N0}")));
        }
        ImGui.EndTable();

        if (selectedCompanyBreakdownItemId != 0)
            DrawCompanyMaterialBreakdown(selectedCompanyBreakdownItemId,
                materials.FirstOrDefault(x => x.ItemId == selectedCompanyBreakdownItemId)?.Missing ?? 0);

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader(Loc.L("一次素材まで分解", "Expanded to raw materials"),
                ImGuiTreeNodeFlags.DefaultOpen))
            return;
        ImGui.TextDisabled(Loc.L(
            "納品素材の不足分を通常レシピで再帰展開しています。中間素材の在庫は上の表で差し引き済みです。",
            "Recursively expands missing supply materials through normal recipes. Intermediate stock is already deducted above."));
        ImGui.SameLine();
        ImGui.Checkbox(Loc.L("不足のみ##company-raw-missing", "Missing only##company-raw-missing"), ref companyRawMissingOnly);
        DrawCompanyMaterialTable("company-raw-materials", rawMaterials, companyRawMissingOnly);
    }

    private Dictionary<uint, Recipe> NormalRecipes => normalRecipeByResultItem ??=
        Plugin.DataManager.GetExcelSheet<Recipe>()
            .Where(x => x.ItemResult.RowId != 0 && x.AmountResult > 0)
            .GroupBy(x => x.ItemResult.RowId)
            .ToDictionary(x => x.Key, x => x.First());

    private bool HasNormalRecipe(uint itemId) => NormalRecipes.ContainsKey(itemId);

    private string ClassifyCompanyMaterial(Item item)
    {
        var name = item.Name.ToString();
        var category = item.ItemUICategory.RowId == 0 ? string.Empty : item.ItemUICategory.Value.Name.ToString();
        if (category.Contains("クリスタル", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("Crystal", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("Shard", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("Cluster", StringComparison.OrdinalIgnoreCase))
            return Loc.L("クリスタル類", "Crystal");
        if (name.Contains("フレーム", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Frame", StringComparison.OrdinalIgnoreCase))
            return Loc.L("フレーム", "Frame");
        return HasNormalRecipe(item.RowId)
            ? Loc.L("中間素材", "Intermediate")
            : Loc.L("一次素材", "Raw");
    }

    private void ExpandToRawMaterials(uint itemId, int quantity, Dictionary<uint, int> totals, HashSet<uint> path)
    {
        if (quantity <= 0) return;
        if (!NormalRecipes.TryGetValue(itemId, out var recipe) || !path.Add(itemId))
        {
            totals.TryGetValue(itemId, out var current);
            totals[itemId] = checked(current + quantity);
            return;
        }
        var crafts = checked((quantity + (int)recipe.AmountResult - 1) / (int)recipe.AmountResult);
        for (var index = 0; index < recipe.Ingredient.Count; index++)
        {
            var ingredient = recipe.Ingredient[index];
            var amount = recipe.AmountIngredient[index];
            if (ingredient.RowId == 0 || amount == 0) continue;
            ExpandToRawMaterials(ingredient.RowId, checked(crafts * (int)amount), totals, path);
        }
        path.Remove(itemId);
    }

    private void DrawCompanyMaterialBreakdown(uint itemId, int missing)
    {
        if (!NormalRecipes.TryGetValue(itemId, out var recipe) || missing <= 0) return;
        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRow(itemId);
        if (!ImGui.TreeNode(Loc.L($"{item.Name} の内訳（不足 {missing:N0}）##company-breakdown-{itemId}",
                $"{item.Name} breakdown (missing {missing:N0})##company-breakdown-{itemId}"))) return;
        var direct = new Dictionary<uint, int>();
        var crafts = (missing + (int)recipe.AmountResult - 1) / (int)recipe.AmountResult;
        for (var index = 0; index < recipe.Ingredient.Count; index++)
        {
            var ingredient = recipe.Ingredient[index];
            if (ingredient.RowId != 0 && recipe.AmountIngredient[index] != 0)
                direct[ingredient.RowId] = crafts * (int)recipe.AmountIngredient[index];
        }
        foreach (var pair in direct)
        {
            var ingredientName = Plugin.DataManager.GetExcelSheet<Item>().GetRow(pair.Key).Name.ToString();
            ImGui.Bullet();
            ImGui.SameLine();
            DrawCopyableItemName($"{ingredientName} ×{pair.Value:N0}",
                $"breakdown-material-{itemId}-{pair.Key}", ingredientName);
        }
        ImGui.TreePop();
    }

    private void DrawCompanyMaterialTable(string id, IReadOnlyList<CompanyMaterial> materials, bool missingOnly)
    {
        if (!ImGui.BeginTable(id, 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn(Loc.L("素材", "Material"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn(Loc.L("区分", "Type"), ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("必要／所持", "Need / owned"), ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("不足", "Missing"), ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("保管場所", "Locations"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableHeadersRow();
        foreach (var material in materials)
        {
            if (missingOnly && material.Missing == 0) continue;
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            DrawCopyableItemName(material.Name, $"raw-material-{material.ItemId}");
            ImGui.TableNextColumn(); ImGui.TextDisabled(material.Type);
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{material.Required:N0} / {material.Owned:N0}");
            ImGui.TableNextColumn();
            if (material.Missing > 0) ImGui.TextColored(new Vector4(0.96f, 0.42f, 0.38f, 1f), material.Missing.ToString("N0"));
            else ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.5f, 1f), "0");
            ImGui.TableNextColumn();
            if (material.Locations.Count == 0) ImGui.TextDisabled("—");
            else ImGui.TextWrapped(string.Join(Loc.IsEnglish ? ", " : "、", material.Locations.Select(x =>
                $"{x.OwnerName}：{x.StorageName} ×{x.Quantity:N0}")));
        }
        ImGui.EndTable();
    }

    private static void DrawCopyableItemName(string name, string id, string? clipboardName = null)
    {
        if (ImGui.Selectable($"{name}##{id}")) ImGui.SetClipboardText(clipboardName ?? name);
        DrawCopyItemTooltip();
    }

    private static void DrawCopyableItemText(string name)
    {
        ImGui.TextUnformatted(name);
        CopyItemNameOnClick(name);
    }

    private static void CopyItemNameOnClick(string name)
    {
        if (ImGui.IsItemClicked()) ImGui.SetClipboardText(name);
        DrawCopyItemTooltip();
    }

    private static void DrawCopyItemTooltip()
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.L("クリックでアイテム名をコピー", "Click to copy item name"));
    }

    private static int PlanSignature(IEnumerable<(uint Id, int Quantity)> entries)
    {
        var hash = new HashCode();
        foreach (var entry in entries)
        {
            hash.Add(entry.Id);
            hash.Add(entry.Quantity);
        }
        return hash.ToHashCode();
    }

    private sealed class CompanyCraftPlanEntry(uint sequenceId, int quantity)
    {
        public uint SequenceId { get; } = sequenceId;
        public int Quantity { get; set; } = quantity;
    }

    private sealed class RecipePlanEntry(uint recipeId, int craftCount)
    {
        public uint RecipeId { get; } = recipeId;
        public int CraftCount { get; set; } = craftCount;
    }

    private sealed record RecipeMaterial(uint ItemId, string Name, int Required, int Owned, int Missing,
        IReadOnlyList<StoredItemRecord> Locations);

    private sealed record CompanyMaterial(uint ItemId, string Name, string Type, int Required, int Owned, int Missing,
        IReadOnlyList<StoredItemRecord> Locations);
}
