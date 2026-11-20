using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace AltMate;

internal static class ItemLocationFormatter
{
    private static readonly ConditionalWeakTable<IReadOnlyList<StoredItemRecord>, Labels> Cache = new();

    internal static string FormatOwners(IReadOnlyList<StoredItemRecord> locations) =>
        Cache.GetValue(locations, static rows => new Labels(rows)).Owners;

    internal static string FormatGrouped(IReadOnlyList<StoredItemRecord> locations)
    {
        var labels = Cache.GetValue(locations, static rows => new Labels(rows));
        return Loc.IsEnglish ? labels.English : labels.Japanese;
    }

    private sealed class Labels(IReadOnlyList<StoredItemRecord> rows)
    {
        internal string Owners { get; } = BuildOwners(rows);
        internal string English { get; } = BuildGrouped(rows, true);
        internal string Japanese { get; } = BuildGrouped(rows, false);
    }

    private static string BuildOwners(IEnumerable<StoredItemRecord> locations) => string.Join("\n",
        locations.GroupBy(x => x.OwnerKey)
            .OrderBy(x => x.First().OwnerName, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => OwnerLabel(group.First())));

    private static string BuildGrouped(IEnumerable<StoredItemRecord> locations, bool english)
    {
        var builder = new StringBuilder();
        foreach (var owner in locations.GroupBy(x => x.OwnerKey)
                     .OrderBy(x => x.First().OwnerName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (builder.Length > 0) builder.AppendLine();
            var first = owner.First();
            builder.Append(OwnerLabel(first));
            builder.Append(english ? $"  (total {owner.Sum(x => x.Quantity):N0})" :
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
