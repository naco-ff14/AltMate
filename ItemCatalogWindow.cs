using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AltMate;

internal sealed class ItemCatalogWindow : Window
{
    private const int PageSize = 200;
    private readonly Plugin plugin;
    private string search = string.Empty;
    private string ownerFilter = string.Empty;
    private ItemStorageKind? storageFilter;
    private DateTime nextRefreshUtc;
    private long observedInventoryRevision = -1;
    private int page;
    private StoredItemRecord[] rows = Array.Empty<StoredItemRecord>();

    internal ItemCatalogWindow(Plugin plugin)
        : base($"{Loc.L("AltMate 全アイテム一覧", "AltMate All Items")}###AltMateItemCatalog")
    {
        this.plugin = plugin;
        Size = new Vector2(1100, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 420) };
    }

    internal void Open()
    {
        IsOpen = true;
        RequestFocus = true;
        nextRefreshUtc = default;
    }

    public override void Draw()
    {
        var revision = plugin.ItemInventory.Revision;
        if (revision != observedInventoryRevision || DateTime.UtcNow >= nextRefreshUtc)
        {
            rows = plugin.ItemInventory.GetAll().ToArray();
            observedInventoryRevision = revision;
            nextRefreshUtc = DateTime.UtcNow.AddSeconds(2);
        }

        var changed = false;
        ImGui.SetNextItemWidth(330 * ImGuiHelpers.GlobalScale);
        changed |= ImGui.InputTextWithHint("##all-item-search",
            Loc.L("アイテム名またはID", "Item name or ID"), ref search, 120);
        ImGui.SameLine();
        changed |= DrawOwnerFilter();
        ImGui.SameLine();
        changed |= DrawStorageFilter();
        if (changed) page = 0;

        var filtered = FilteredRows();
        var groups = filtered.GroupBy(x => x.ItemId)
            .Select(group => new ItemGroup(group.Key, group.First().ItemName, group.Sum(x => x.Quantity),
                group.OrderByDescending(x => x.ScannedAtUtc).ToArray()))
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var pageCount = Math.Max(1, (groups.Length + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pageCount - 1);

        ImGui.TextDisabled(Loc.L($"{groups.Length:N0}種類 / 合計 {groups.Sum(x => x.Quantity):N0}個",
            $"{groups.Length:N0} item types / {groups.Sum(x => x.Quantity):N0} total"));
        ImGui.SameLine();
        DrawPager(pageCount);
        ImGui.Separator();

        if (!ImGui.BeginTable("all-items-table", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, -1)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.L("アイテム", "Item"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(Loc.L("合計", "Total"), ImGuiTableColumnFlags.WidthFixed, 72 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn(Loc.L("キャラクター／FC", "Character / FC"), ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn(Loc.L("保管先内訳", "Storage details"), ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn(Loc.L("最終確認", "Last seen"), ImGuiTableColumnFlags.WidthFixed, 105 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var item in groups.Skip(page * PageSize).Take(PageSize))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{item.Name}##catalog-item-{item.ItemId}"))
                ImGui.SetClipboardText(item.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.L($"クリックでアイテム名をコピー\nItem ID: {item.ItemId}",
                    $"Click to copy item name\nItem ID: {item.ItemId}"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Quantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextWrapped(ItemLocationFormatter.FormatOwners(item.Locations));
            ImGui.TableNextColumn();
            ImGui.TextWrapped(ItemLocationFormatter.FormatGrouped(item.Locations));
            ImGui.TableNextColumn();
            var newest = item.Locations.Max(x => x.ScannedAtUtc);
            var label = newest.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
            if (DateTime.UtcNow - newest > TimeSpan.FromDays(7))
                ImGui.TextColored(new Vector4(0.96f, 0.68f, 0.25f, 1f), label);
            else ImGui.TextUnformatted(label);
        }
        ImGui.EndTable();
    }

    private IEnumerable<StoredItemRecord> FilteredRows()
    {
        IEnumerable<StoredItemRecord> result = rows;
        var term = search.Trim();
        if (term.Length > 0)
            result = uint.TryParse(term, out var itemId)
                ? result.Where(x => x.ItemId == itemId)
                : result.Where(x => x.ItemName.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        if (ownerFilter.Length > 0) result = result.Where(x => x.OwnerKey == ownerFilter);
        if (storageFilter is { } kind) result = result.Where(x => x.StorageKind == kind);
        return result;
    }

    private bool DrawOwnerFilter()
    {
        var changed = false;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var preview = ownerFilter.Length == 0
            ? Loc.L("全キャラクター／FC", "All characters / FCs")
            : rows.FirstOrDefault(x => x.OwnerKey == ownerFilter)?.OwnerName ?? ownerFilter;
        if (ImGui.BeginCombo("##all-item-owner", preview))
        {
            if (ImGui.Selectable(Loc.L("全キャラクター／FC", "All characters / FCs"), ownerFilter.Length == 0))
            { ownerFilter = string.Empty; changed = true; }
            foreach (var owner in rows.GroupBy(x => x.OwnerKey)
                         .Select(x => (Key: x.Key, Name: x.First().OwnerName))
                         .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                if (ImGui.Selectable($"{owner.Name}##owner-{owner.Key}", ownerFilter == owner.Key))
                { ownerFilter = owner.Key; changed = true; }
            ImGui.EndCombo();
        }
        return changed;
    }

    private bool DrawStorageFilter()
    {
        var changed = false;
        ImGui.SetNextItemWidth(190 * ImGuiHelpers.GlobalScale);
        var preview = storageFilter is { } kind ? StorageLabel(kind) : Loc.L("全保管先", "All storage");
        if (ImGui.BeginCombo("##all-item-storage", preview))
        {
            if (ImGui.Selectable(Loc.L("全保管先", "All storage"), storageFilter is null))
            { storageFilter = null; changed = true; }
            foreach (var value in Enum.GetValues<ItemStorageKind>())
                if (ImGui.Selectable($"{StorageLabel(value)}##storage-{value}", storageFilter == value))
                { storageFilter = value; changed = true; }
            ImGui.EndCombo();
        }
        return changed;
    }

    private void DrawPager(int pageCount)
    {
        ImGui.BeginDisabled(page == 0);
        if (ImGui.SmallButton("◀##all-items-prev")) page--;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted($"{page + 1:N0} / {pageCount:N0}");
        ImGui.SameLine();
        ImGui.BeginDisabled(page >= pageCount - 1);
        if (ImGui.SmallButton("▶##all-items-next")) page++;
        ImGui.EndDisabled();
    }

    private static string StorageLabel(ItemStorageKind kind) => kind switch
    {
        ItemStorageKind.Inventory => Loc.L("所持品", "Inventory"),
        ItemStorageKind.Saddlebag => Loc.L("チョコボかばん", "Saddlebag"),
        ItemStorageKind.ArmouryChest => Loc.L("アーマリーチェスト", "Armoury chest"),
        ItemStorageKind.Retainer => Loc.L("リテイナー", "Retainer"),
        ItemStorageKind.FreeCompanyChest => Loc.L("FCチェスト", "FC chest"),
        ItemStorageKind.Armoire => Loc.L("愛蔵品", "Armoire"),
        ItemStorageKind.GlamourDresser => Loc.L("ミラージュドレッサー", "Glamour dresser"),
        _ => kind.ToString(),
    };

    private sealed record ItemGroup(uint ItemId, string Name, int Quantity, StoredItemRecord[] Locations);
}
