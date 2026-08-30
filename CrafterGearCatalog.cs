using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal static class CrafterGearCatalog
{
    internal static readonly int[] TierLevels = [20, 21, 41, 53, 63, 71, 81, 91, 100];
    internal sealed record Result(int TierCount, IReadOnlyList<string> Missing);

    internal static Result BuildStandard(CrafterLevelingSettings settings)
    {
        var items = Plugin.DataManager.GetExcelSheet<Item>()
            .Where(x => x.LevelEquip > 0 && x.EquipSlotCategory.RowId != 0 &&
                        !string.IsNullOrWhiteSpace(x.Name.ToString()) &&
                        !IsLegacyItem(x))
            .ToArray();
        var missing = new List<string>();
        foreach (var tier in TierLevels.Where(x => x <= settings.TargetLevel))
        {
            var preset = new CrafterGearPreset { TierLevel = tier };
            foreach (var slot in Enumerable.Range(0, 9))
            {
                var selected = Best(items.Where(x => x.LevelEquip <= tier && HasCraftingStats(x) &&
                                                      IsSharedSlot(x, slot) &&
                                                      AllowsAllCrafters(x.ClassJobCategory.Value)));
                if (selected.RowId != 0)
                {
                    preset.SharedItemIds.Add(selected.RowId);
                    if (slot == 8) preset.SharedItemIds.Add(selected.RowId); // Two rings.
                }
                else missing.Add($"Lv{tier} shared slot {slot}");
            }
            foreach (var jobId in settings.EnabledJobIds.Where(x => x is >= 8 and <= 15).OrderBy(x => x))
            {
                var tools = new List<uint>();
                foreach (var offHand in new[] { false, true })
                {
                    var selected = Best(items.Where(x => x.LevelEquip <= tier && HasCraftingStats(x) &&
                                                          IsToolSlot(x, offHand) &&
                                                          AllowsJob(x.ClassJobCategory.Value, jobId)));
                    if (selected.RowId != 0) tools.Add(selected.RowId);
                    else missing.Add($"Lv{tier} Job {jobId} {(offHand ? "offhand" : "mainhand")}");
                }
                preset.JobItemIds[jobId] = tools;
            }
            settings.GearPresets.RemoveAll(x => x.TierLevel == tier);
            settings.GearPresets.Add(preset);
        }
        settings.GearPresets.Sort((left, right) => left.TierLevel.CompareTo(right.TierLevel));
        return new Result(settings.GearPresets.Count, missing);
    }

    private static Item Best(IEnumerable<Item> candidates) => candidates
        .OrderByDescending(x => x.LevelEquip)
        .ThenByDescending(x => x.LevelItem.RowId)
        .ThenByDescending(x => x.RowId)
        .FirstOrDefault();

    private static bool IsLegacyItem(Item item) =>
        item.Name.ToString().TrimStart().StartsWith('†');

    private static bool HasCraftingStats(Item item)
    {
        for (var index = 0; index < item.BaseParam.Count; index++)
            if (item.BaseParam[index].RowId is 11 or 70 or 71 && item.BaseParamValue[index] > 0)
                return true;
        return false;
    }

    private static bool IsToolSlot(Item item, bool offHand)
    {
        var slot = item.EquipSlotCategory.Value;
        return offHand ? slot.OffHand > 0 : slot.MainHand > 0;
    }

    private static bool IsSharedSlot(Item item, int slotIndex)
    {
        var slot = item.EquipSlotCategory.Value;
        return slotIndex switch
        {
            0 => slot.Head > 0,
            1 => slot.Body > 0,
            2 => slot.Gloves > 0,
            3 => slot.Legs > 0,
            4 => slot.Feet > 0,
            5 => slot.Ears > 0,
            6 => slot.Neck > 0,
            7 => slot.Wrists > 0,
            8 => slot.FingerL > 0 || slot.FingerR > 0,
            _ => false,
        };
    }

    private static bool AllowsAllCrafters(ClassJobCategory category) =>
        category.CRP && category.BSM && category.ARM && category.GSM &&
        category.LTW && category.WVR && category.ALC && category.CUL;

    private static bool AllowsJob(ClassJobCategory category, uint jobId) => jobId switch
    {
        8 => category.CRP, 9 => category.BSM, 10 => category.ARM, 11 => category.GSM,
        12 => category.LTW, 13 => category.WVR, 14 => category.ALC, 15 => category.CUL,
        _ => false,
    };
}
