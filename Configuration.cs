using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace AltMate;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;
    public Dictionary<ulong, CharacterLotteryRecord> Characters { get; set; } = new();
    public List<OpenPlotRecord> OpenPlots { get; set; } = new();
    public Dictionary<ulong, CharacterGilRecord> CharacterGil { get; set; } = new();
    public Dictionary<ulong, CustomDeliveryCharacterRecord> CustomDeliveryCharacters { get; set; } = new();
    public Dictionary<ulong, FreeCompanyGilRecord> FreeCompanyGil { get; set; } = new();
    public CustomDeliverySettings CustomDeliverySettings { get; set; } = new();
    public DateTime? OpenPlotsCycleStartsAtUtc { get; set; }
    // 2026-08-13 00:00 JST: beginning of an entry period.
    public DateTime CycleAnchorUtc { get; set; } = new(2026, 8, 12, 15, 0, 0, DateTimeKind.Utc);
    public bool LinkEnabled { get; set; }
    public ulong LinkLeaderContentId { get; set; }
    public bool AutoFollowEnabled { get; set; } = true;
    public bool AutoRidePillionEnabled { get; set; } = true;
    public bool MountRouletteFallbackEnabled { get; set; } = true;
    public bool AutoAcceptPartyInviteEnabled { get; set; } = true;
    public bool PauseLinkInCombat { get; set; } = true;
    public float FollowStartDistance { get; set; } = 5f;
    public bool VnavmeshStuckRecoveryEnabled { get; set; } = true;
    public bool SyncLeaderInteractionEnabled { get; set; }
    public bool CombatLinkEnabled { get; set; }
    public bool UseBossModReborn { get; set; } = true;
    public bool UseRotationSolverReborn { get; set; } = true;
    public float CombatStopDelaySeconds { get; set; } = 3f;
    public bool OccultAethernetSyncEnabled { get; set; } = true;
    public bool SyncReturnEnabled { get; set; } = true;
    public bool SyncDutyCommenceEnabled { get; set; } = true;
    public bool SyncTeleportInvitationEnabled { get; set; } = true;
    public bool SyncRegularTeleportEnabled { get; set; } = true;
    public bool SyncCityAethernetEnabled { get; set; } = true;
    public bool SyncResidentialAethernetEnabled { get; set; } = true;
    public bool SyncZoneBoundaryEnabled { get; set; } = true;
    public bool SyncFreeCompanyEstateEnabled { get; set; } = true;
    public bool AutoOpenNearbyTreasureEnabled { get; set; } = true;
    public bool RoleBasedFpsEnabled { get; set; }
    public int LeaderFpsLimit { get; set; } = 60;
    public int FollowerFpsLimit { get; set; } = 30;
    public string Language { get; set; } = "ja";
    public string LocalLinkKey { get; set; } = string.Empty;
    public bool WindowCompactMode { get; set; }
    public int LastMainSection { get; set; }
    public int LastHousingSection { get; set; }

    public void Save() => Plugin.SaveConfiguration(this);
}

[Serializable]
public sealed class CustomDeliverySettings
{
    public CustomDeliveryJobType JobType { get; set; } = CustomDeliveryJobType.Crafter;
    public CustomDeliveryScripPreference ScripPreference { get; set; } = CustomDeliveryScripPreference.Orange;
    public uint PreferredNpcId { get; set; }
    public bool PrioritizeBonus { get; set; } = true;
    public bool AutoExchangeEnabled { get; set; }
    public uint ExchangeItemId { get; set; }
    public int ExchangeThreshold { get; set; } = 3500;
    public bool RunUntilWeeklyLimit { get; set; } = true;
    public uint CrafterJobId { get; set; } = 8;
    public uint GathererJobId { get; set; } = 16;
}

public enum CustomDeliveryJobType
{
    Crafter,
    Gatherer,
}

public enum CustomDeliveryScripPreference
{
    Orange,
    Purple,
    HighestTotal,
}

[Serializable]
public sealed class CustomDeliveryCharacterRecord
{
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = "不明";
    public string WorldName { get; set; } = "不明";
    public int RemainingWeeklyAllowances { get; set; }
    public uint PurpleCrafterScrip { get; set; }
    public uint PurpleGathererScrip { get; set; }
    public uint OrangeCrafterScrip { get; set; }
    public uint OrangeGathererScrip { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[Serializable]
public sealed class CharacterGilRecord
{
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = "不明";
    public string WorldName { get; set; } = "不明";
    public uint Gil { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Dictionary<ulong, RetainerGilRecord> Retainers { get; set; } = new();
}

[Serializable]
public sealed class RetainerGilRecord
{
    public ulong RetainerId { get; set; }
    public string Name { get; set; } = "不明なリテイナー";
    public uint Gil { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[Serializable]
public sealed class FreeCompanyGilRecord
{
    public ulong FreeCompanyId { get; set; }
    public string Name { get; set; } = "不明なFC";
    public string WorldName { get; set; } = "不明";
    public uint Gil { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ulong LastCheckedByContentId { get; set; }
    public string LastCheckedByName { get; set; } = "不明";
    public DateTime SubmarinesUpdatedAt { get; set; }
    public Dictionary<string, SubmarineRecord> Submarines { get; set; } = new();
    public DateTime TreasureVoyagesUpdatedAt { get; set; }
    public List<SubmarineTreasureVoyageRecord> TreasureVoyages { get; set; } = new();
}

[Serializable]
public sealed class SubmarineRecord
{
    public string Name { get; set; } = "不明な潜水艦";
    public uint ReturnTimeUnix { get; set; }
    public byte[] RoutePointIds { get; set; } = [];
}

[Serializable]
public sealed class SubmarineTreasureVoyageRecord
{
    public string Id { get; set; } = string.Empty;
    public string SubmarineName { get; set; } = "不明な潜水艦";
    public uint DepartedAtUnix { get; set; }
    public uint ReturnedAtUnix { get; set; }
    public ulong TreasureGil { get; set; }
    public Dictionary<uint, uint> TreasureItems { get; set; } = new();
}

[Serializable]
public sealed class OpenPlotRecord
{
    public ushort WorldId { get; set; }
    public string WorldName { get; set; } = "不明";
    public ushort TerritoryTypeId { get; set; }
    public string DistrictName { get; set; } = "不明";
    public int WardNumber { get; set; }
    public int PlotNumber { get; set; }
    public string Size { get; set; } = "不明";
    public uint Price { get; set; }
    public DateTime CheckedAt { get; set; }
}

[Serializable]
public sealed class CharacterLotteryRecord
{
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = "不明なキャラクター";
    public string WorldName { get; set; } = "不明";
    public string? PlotAddress { get; set; }
    public DateTime? EntryPhaseEndsAt { get; set; }
    public bool ResultChecked { get; set; }
    public DateTime LastCheckedAt { get; set; }
    public bool EnabledForDisplay { get; set; }
    public ushort BidWorldId { get; set; }
    public ushort BidTerritoryTypeId { get; set; }
    public int BidWardNumber { get; set; }
    public int BidPlotNumber { get; set; }
    public uint BidGilDeposited { get; set; }
}

public enum LotteryPhase
{
    Entry,
    Results,
}

public readonly record struct LotteryCycle(
    LotteryPhase Phase,
    DateTime CycleStartsAt,
    DateTime EntryEndsAt,
    DateTime ResultsEndAt)
{
    public static LotteryCycle Current(DateTime now, DateTime anchorUtc)
    {
        var anchor = anchorUtc.Kind == DateTimeKind.Utc ? anchorUtc : anchorUtc.ToUniversalTime();
        var nowUtc = now.ToUniversalTime();
        var cycleIndex = Math.Floor((nowUtc - anchor).TotalDays / 9d);
        var startUtc = anchor.AddDays(cycleIndex * 9d);
        var entryEndUtc = startUtc.AddDays(5);
        var resultsEndUtc = startUtc.AddDays(9);
        return new LotteryCycle(
            nowUtc < entryEndUtc ? LotteryPhase.Entry : LotteryPhase.Results,
            startUtc.ToLocalTime(), entryEndUtc.ToLocalTime(), resultsEndUtc.ToLocalTime());
    }

    public bool HasEntry(CharacterLotteryRecord record) =>
        record.EntryPhaseEndsAt is { } end && Math.Abs((end - EntryEndsAt).TotalMinutes) < 5;
}
