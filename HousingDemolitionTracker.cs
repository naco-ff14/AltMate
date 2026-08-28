using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
            var changed = false;
            // コンテンツ内では所有住宅APIが無効値を返すため、正常取得済みの情報を維持する。
            if (!IsBoundByDuty())
            {
                var personal = GetOwnedEstate(EstateType.PersonalEstate);
                var freeCompany = GetOwnedEstate(EstateType.FreeCompanyEstate);
                changed = UpdateOwnership(contentId, OwnedEstateKind.Personal, personal) |
                          UpdateOwnership(contentId, OwnedEstateKind.FreeCompany, freeCompany);
            }

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

    private static bool IsBoundByDuty() =>
        Plugin.Condition[ConditionFlag.BoundByDuty] ||
        Plugin.Condition[ConditionFlag.BoundByDuty56] ||
        Plugin.Condition[ConditionFlag.BoundByDuty95];

    private static EstateSnapshot GetOwnedEstate(EstateType type)
    {
        var telepo = Telepo.Instance();
        if (telepo != null)
        {
            for (var index = 0; index < telepo->TeleportList.Count; index++)
            {
                var info = telepo->TeleportList[index];
                if (info.EstateType != type || !IsValidHouseId(info.HouseId))
                    continue;
                var territory = info.HouseId.TerritoryTypeId is 339 or 340 or 341 or 641 or 979
                    ? info.HouseId.TerritoryTypeId : info.TerritoryId;
                var ward = info.Ward > 0 ? info.Ward : (byte)(info.HouseId.WardIndex + 1);
                var plot = info.Plot > 0 ? info.Plot : (byte)(info.HouseId.PlotIndex + 1);
                return new EstateSnapshot(info.HouseId.Id, info.HouseId.WorldId, territory, ward, plot);
            }
        }

        var owned = HousingManager.GetOwnedHouseId(type);
        return IsValidHouseId(owned)
            ? new EstateSnapshot(owned.Id, owned.WorldId, owned.TerritoryTypeId,
                (byte)(owned.WardIndex + 1), (byte)(owned.PlotIndex + 1))
            : default;
    }

    private static bool IsValidHouseId(HouseId id) =>
        id.Id is not (0 or ulong.MaxValue) &&
        id.WorldId is not (0 or ushort.MaxValue) &&
        id.TerritoryTypeId is 339 or 340 or 341 or 641 or 979 &&
        id.WardIndex < 30 && id.PlotIndex < 60;

    private bool UpdateOwnership(ulong contentId, OwnedEstateKind kind, EstateSnapshot estate)
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
        var houseId = estate.HouseId;
        var owned = estate.IsValid;
        var previousHouseId = record.HouseId;
        var changed = record.CharacterName != name || record.WorldName != world ||
                      record.IsOwned != owned || record.HouseId != houseId ||
                      record.HouseWorldId != estate.WorldId ||
                      record.HouseTerritoryTypeId != estate.TerritoryTypeId ||
                      record.HouseWard != estate.Ward || record.HousePlot != estate.Plot;
        record.CharacterName = name;
        record.WorldName = world;
        record.IsOwned = owned;
        record.HouseId = houseId;
        record.HouseWorldId = estate.WorldId;
        record.HouseTerritoryTypeId = estate.TerritoryTypeId;
        record.HouseWard = estate.Ward;
        record.HousePlot = estate.Plot;
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

    private readonly record struct EstateSnapshot(
        ulong HouseId, ushort WorldId, ushort TerritoryTypeId, byte Ward, byte Plot)
    {
        internal bool IsValid => HouseId != 0 && WorldId != 0 && TerritoryTypeId != 0 &&
                                 Ward is >= 1 and <= 30 && Plot is >= 1 and <= 60;
    }
}
