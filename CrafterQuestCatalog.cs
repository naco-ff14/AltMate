using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal sealed record CrafterQuestItem(uint JobId, int Level, string QuestName, uint ItemId,
    string ItemName, int RequiredCount, bool RequiresHq);

internal static class CrafterQuestCatalog
{
    internal static IReadOnlyList<CrafterQuestItem> BuildToLevel60()
    {
        var result = new List<CrafterQuestItem>();
        foreach (var quest in Plugin.DataManager.GetExcelSheet<Quest>())
        {
            var jobId = quest.ClassJobRequired.RowId;
            if (jobId is < 8 or > 15 || quest.ClassJobLevel.Count == 0) continue;
            var level = quest.ClassJobLevel[0];
            if (level is < 1 or > 60 || !quest.QuestClassJobSupply.IsValid) continue;
            foreach (var supply in quest.QuestClassJobSupply.Value)
            {
                if (supply.Item.RowId == 0 || supply.AmountRequired == 0) continue;
                result.Add(new CrafterQuestItem(jobId, level, quest.Name.ToString(), supply.Item.RowId,
                    supply.Item.Value.Name.ToString(), supply.AmountRequired, supply.ItemHQ));
            }
        }
        return result.OrderBy(x => x.JobId).ThenBy(x => x.Level).ThenBy(x => x.ItemName).ToArray();
    }
}
