using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;

namespace AltMate;

internal sealed unsafe class HousingDemolitionTracker : IDisposable
{
    private readonly Plugin plugin;
    private DateTime nextCheckUtc;
    private ulong lastIndoorHouseId;

    internal HousingDemolitionTracker(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
            return;
        var nowUtc = DateTime.UtcNow;
        if (nowUtc < nextCheckUtc)
            return;
        nextCheckUtc = nowUtc.AddSeconds(2);

        try
        {
            var contentId = Plugin.PlayerState.ContentId;
            var personal = HousingManager.GetOwnedHouseId(EstateType.PersonalEstate);
            var freeCompany = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate);
            var changed = UpdateOwnership(contentId, OwnedEstateKind.Personal, personal.Id) |
                          UpdateOwnership(contentId, OwnedEstateKind.FreeCompany, freeCompany.Id);

            var manager = HousingManager.Instance();
            var indoor = manager != null && manager->IndoorTerritory != null
                ? manager->GetCurrentIndoorHouseId().Id
                : 0;
            if (indoor != 0 && indoor != lastIndoorHouseId)
                changed |= RecordEntry(contentId, indoor);
            lastIndoorHouseId = indoor;

            if (changed)
                plugin.Configuration.Save();
        }
        catch (Exception exception)
        {
            Plugin.Log.Debug(exception, "住宅保持期限の確認に失敗しました。");
        }
    }

    private bool UpdateOwnership(ulong contentId, OwnedEstateKind kind, ulong houseId)
    {
        var key = HousingDemolitionRecord.Key(contentId, kind);
        if (!plugin.Configuration.HousingDemolition.TryGetValue(key, out var record))
        {
            record = new HousingDemolitionRecord { ContentId = contentId, EstateKind = kind };
            plugin.Configuration.HousingDemolition[key] = record;
        }
        var now = DateTime.Now;
        var name = Plugin.PlayerState.CharacterName;
        var world = Plugin.PlayerState.HomeWorld.Value.Name.ToString();
        var owned = houseId != 0;
        var previousHouseId = record.HouseId;
        var changed = record.CharacterName != name || record.WorldName != world ||
                      record.IsOwned != owned || record.HouseId != houseId;
        record.CharacterName = name;
        record.WorldName = world;
        record.IsOwned = owned;
        record.HouseId = houseId;
        record.LastOwnershipCheckedAt = now;
        if (changed)
        {
            // A newly acquired/different estate must be entered before its timer starts.
            if (previousHouseId != houseId || !owned)
                record.LastEnteredAt = null;
            record.UpdatedAt = now;
        }
        return changed;
    }

    private bool RecordEntry(ulong contentId, ulong indoorHouseId)
    {
        foreach (var kind in new[] { OwnedEstateKind.Personal, OwnedEstateKind.FreeCompany })
        {
            var key = HousingDemolitionRecord.Key(contentId, kind);
            if (!plugin.Configuration.HousingDemolition.TryGetValue(key, out var record) ||
                !record.IsOwned || !SameEstate(record.HouseId, indoorHouseId))
                continue;
            record.LastEnteredAt = DateTime.Now;
            record.UpdatedAt = record.LastEnteredAt.Value;
            Plugin.ChatGui.Print($"AltMate：{(kind == OwnedEstateKind.Personal ? "個人宅" : "FC宅")}の入室を記録しました。保持期限を40日に更新します。");
            return true;
        }
        return false;
    }

    private static bool SameEstate(ulong left, ulong right)
    {
        var a = (HouseId)left;
        var b = (HouseId)right;
        return a.WorldId == b.WorldId && a.TerritoryTypeId == b.TerritoryTypeId &&
               a.WardIndex == b.WardIndex && a.PlotIndex == b.PlotIndex;
    }

    public void Dispose() => Plugin.Framework.Update -= OnFrameworkUpdate;
}
