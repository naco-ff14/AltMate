using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AltMate;

internal static class ItemLocationFormatter
{
    internal static string FormatOwners(IEnumerable<StoredItemRecord> locations) => string.Join("\n",
        locations.GroupBy(x => x.OwnerKey)
            .OrderBy(x => x.First().OwnerName, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => OwnerLabel(group.First())));

    internal static string FormatGrouped(IEnumerable<StoredItemRecord> locations)
    {
        var builder = new StringBuilder();
        foreach (var owner in locations.GroupBy(x => x.OwnerKey)
                     .OrderBy(x => x.First().OwnerName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (builder.Length > 0) builder.AppendLine();
            var first = owner.First();
            builder.Append(OwnerLabel(first));
            builder.Append(Loc.IsEnglish ? $"  (total {owner.Sum(x => x.Quantity):N0})" :
                $"  （計{owner.Sum(x => x.Quantity):N0}）");
            foreach (var storage in owner.GroupBy(x => (x.StorageKind, x.StorageName, x.IsHq))
                         .OrderBy(x => x.Key.StorageKind).ThenBy(x => x.Key.StorageName,
                             StringComparer.CurrentCultureIgnoreCase))
            {
                builder.AppendLine();
                builder.Append("  └ ");
                builder.Append(storage.Key.StorageName);
                builder.Append(" ×");
                builder.Append(storage.Sum(x => x.Quantity).ToString("N0"));
                if (storage.Key.IsHq) builder.Append(" HQ");
            }
        }
        return builder.ToString();
    }

    private static string OwnerLabel(StoredItemRecord row) => string.IsNullOrWhiteSpace(row.WorldName)
        ? row.OwnerName
        : $"{row.OwnerName} @ {row.WorldName}";
}
