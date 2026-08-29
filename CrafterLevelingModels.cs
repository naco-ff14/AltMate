using System;
using System.Collections.Generic;

namespace AltMate;

public enum CrafterLevelingRoute
{
    Normal,
    Restoration,
    Collectable,
}

public enum CrafterLevelingState
{
    Idle,
    Preparing,
    MovingToBell,
    ScanningRetainers,
    WithdrawingItems,
    ReturningOldGear,
    ChangingGear,
    CraftingNormal,
    CraftingRestoration,
    CraftingCollectable,
    TurningInRestoration,
    TurningInCollectable,
    WaitingAtLevel50,
    ChangingJob,
    Paused,
    Error,
    Completed,
}

[Serializable]
public sealed class CrafterLevelingSettings
{
    public HashSet<uint> EnabledJobIds { get; set; } = new() { 8, 9, 10, 11, 12, 13, 14, 15 };
    public int TargetLevel { get; set; } = 100;
    public CrafterLevelingRoute Level50To80Route { get; set; } = CrafterLevelingRoute.Collectable;
    public bool StopAtLevel50 { get; set; } = true;
    public bool ShowMissingOnly { get; set; }
    public List<CrafterRecipePreset> RecipePresets { get; set; } = new();
    public List<CrafterGearPreset> GearPresets { get; set; } = new();
    public Dictionary<uint, int> KnownOwnedItems { get; set; } = new();
    public CrafterBellRegistration Bell { get; set; } = new();
    public List<ulong> SelectedRetainerIds { get; set; } = new();
    public Dictionary<ulong, CrafterRetainerInventoryCache> RetainerInventories { get; set; } = new();
    public CrafterLevelingProgress Progress { get; set; } = new();
    public CrafterTransferPlan TransferPlan { get; set; } = new();
}

[Serializable]
public sealed class CrafterTransferPlan
{
    public List<CrafterTransferLine> Withdrawals { get; set; } = new();
    public List<CrafterTransferLine> Returns { get; set; } = new();
    public Dictionary<uint, int> UnavailableItems { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public bool IsReady => CreatedAt != default && UnavailableItems.Count == 0;
}

[Serializable]
public sealed class CrafterTransferLine
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public bool IsGear { get; set; }
}

[Serializable]
public sealed class CrafterBellRegistration
{
    public bool IsRegistered { get; set; }
    public uint TerritoryId { get; set; }
    public uint ObjectId { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

[Serializable]
public sealed class CrafterRetainerInventoryCache
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public Dictionary<uint, int> Items { get; set; } = new();
    public DateTime ScannedAt { get; set; }
}

[Serializable]
public sealed class CrafterRecipePreset
{
    public uint JobId { get; set; }
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; }
    public CrafterLevelingRoute Route { get; set; }
    public uint RecipeId { get; set; }
    public int MaxCraftCount { get; set; }
    public int GearTier { get; set; }
    public string RequiredUnlock { get; set; } = string.Empty;
    public bool IsCatalogGenerated { get; set; }
}

[Serializable]
public sealed class CrafterGearPreset
{
    public int TierLevel { get; set; }
    public List<uint> SharedItemIds { get; set; } = new();
    public Dictionary<uint, List<uint>> JobItemIds { get; set; } = new();
}

[Serializable]
public sealed class CrafterLevelingProgress
{
    public CrafterLevelingState State { get; set; }
    public uint CurrentJobId { get; set; }
    public int NextTargetLevel { get; set; }
    public int CurrentGearTier { get; set; }
    public string LastError { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public sealed record CrafterPreparationItem(uint ItemId, string Name, int RequiredCount, int OwnedCount,
    bool IsCrystal, bool IsGear)
{
    public int MissingCount => Math.Max(0, RequiredCount - OwnedCount);
}
