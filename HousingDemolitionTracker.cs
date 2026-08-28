using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;

namespace AltMate;

internal sealed unsafe class HousingDemolitionTracker : IDisposable
{
    internal const int DemolitionPeriodDays = 45;
    private readonly Plugin plugin;
    private DateTime nextCheckUtc;
    private ulong lastIndoorHouseId;

    internal HousingDemolitionTracker(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    internal EstateSnapshot CurrentIndoorEstate => ReadCurrentIndoorEstate();

    internal bool RegisterCurrentEstate(OwnedEstateKind kind)
    {
        var estate = ReadCurrentIndoorEstate();
        var contentId = Plugin.PlayerState.ContentId;
        if (contentId == 0 || !estate.IsValid)
            return false;
        var key = HousingDemolitionRecord.Key(contentId, kind);
        if (!plugin.Configuration.HousingDemolition.TryGetValue(key, out var record))
        {
            record = new HousingDemolitionRecord { ContentId = contentId, EstateKind = kind };
            plugin.Configuration.HousingDemolition[key] = record;
        }
        var now = DateTime.Now;
        record.CharacterName = Plugin.PlayerState.CharacterName;
        record.WorldName = Plugin.PlayerState.HomeWorld.Value.Name.ToString();
        record.IsOwned = true;
        record.HouseId = estate.HouseId;
        record.HouseWorldId = estate.WorldId;
        record.HouseTerritoryTypeId = estate.TerritoryTypeId;
        record.HouseWard = estate.Ward;
        record.HousePlot = estate.Plot;
        record.LastEnteredAt = now;
        record.LastOwnershipCheckedAt = now;
        record.UpdatedAt = now;
        plugin.Configuration.Save();
        Plugin.PrintChat($"AltMate：現在のハウスを{(kind == OwnedEstateKind.Personal ? "個人宅" : "FC宅")}として登録しました。");
        return true;
    }

    internal bool UnregisterCurrentEstate(OwnedEstateKind kind)
    {
        var contentId = Plugin.PlayerState.ContentId;
        var key = HousingDemolitionRecord.Key(contentId, kind);
        if (contentId == 0 || !plugin.Configuration.HousingDemolition.TryGetValue(key, out var record))
            return false;
        record.IsOwned = false;
        record.HouseId = 0;
        record.HouseWorldId = 0;
        record.HouseTerritoryTypeId = 0;
        record.HouseWard = 0;
        record.HousePlot = 0;
        record.LastEnteredAt = null;
        record.LastOwnershipCheckedAt = DateTime.Now;
        record.UpdatedAt = record.LastOwnershipCheckedAt;
        plugin.Configuration.Save();
        return true;
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
            var estate = ReadCurrentIndoorEstate();
            if (estate.IsValid && estate.HouseId != lastIndoorHouseId &&
                RecordEntry(Plugin.PlayerState.ContentId, estate))
                plugin.Configuration.Save();
            lastIndoorHouseId = estate.HouseId;
        }
        catch (Exception exception)
        {
            Plugin.Log.Debug(exception, "住宅保持期限の確認に失敗しました。");
        }
    }

    private bool RecordEntry(ulong contentId, EstateSnapshot estate)
    {
        var changed = false;
        foreach (var kind in new[] { OwnedEstateKind.Personal, OwnedEstateKind.FreeCompany })
        {
            var key = HousingDemolitionRecord.Key(contentId, kind);
            if (!plugin.Configuration.HousingDemolition.TryGetValue(key, out var record) ||
                !record.IsOwned || !SameAddress(record, estate))
                continue;
            record.LastEnteredAt = DateTime.Now;
            record.UpdatedAt = record.LastEnteredAt.Value;
            changed = true;
            Plugin.PrintChat($"AltMate：{(kind == OwnedEstateKind.Personal ? "個人宅" : "FC宅")}の入室を記録しました。保持期限を{DemolitionPeriodDays}日に更新します。");
        }
        return changed;
    }

    private static EstateSnapshot ReadCurrentIndoorEstate()
    {
        var manager = HousingManager.Instance();
        if (manager == null || manager->IndoorTerritory == null)
            return default;
        var id = manager->GetCurrentIndoorHouseId();
        if (!IsValidHouseId(id))
            return default;
        return new EstateSnapshot(id.Id, id.WorldId, id.TerritoryTypeId,
            (byte)(id.WardIndex + 1), (byte)(id.PlotIndex + 1));
    }

    private static bool IsValidHouseId(HouseId id) =>
        id.Id is not (0 or ulong.MaxValue) && id.WorldId is not (0 or ushort.MaxValue) &&
        id.TerritoryTypeId is 339 or 340 or 341 or 641 or 979 &&
        id.WardIndex < 30 && id.PlotIndex < 60;

    private static bool SameAddress(HousingDemolitionRecord record, EstateSnapshot estate) =>
        record.HouseWorldId == estate.WorldId && record.HouseTerritoryTypeId == estate.TerritoryTypeId &&
        record.HouseWard == estate.Ward && record.HousePlot == estate.Plot;

    public void Dispose() => Plugin.Framework.Update -= OnFrameworkUpdate;

    internal readonly record struct EstateSnapshot(
        ulong HouseId, ushort WorldId, ushort TerritoryTypeId, byte Ward, byte Plot)
    {
        internal bool IsValid => HouseId != 0 && WorldId != 0 &&
                                 TerritoryTypeId is 339 or 340 or 341 or 641 or 979 &&
                                 Ward is >= 1 and <= 30 && Plot is >= 1 and <= 60;
    }
}
