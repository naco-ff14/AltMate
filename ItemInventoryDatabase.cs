using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace AltMate;

internal enum ItemStorageKind
{
    Inventory,
    Saddlebag,
    Retainer,
    FreeCompanyChest,
    Armoire,
    GlamourDresser,
    ArmouryChest,
}

internal sealed class StoredItemRecord
{
    public string Id { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public bool IsHq { get; set; }
    public ItemStorageKind StorageKind { get; set; }
    public string OwnerKey { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public string StorageName { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public int ContainerIndex { get; set; }
    public DateTime ScannedAtUtc { get; set; }
}

internal sealed class ItemInventoryDatabase : IDisposable
{
    private const string MutexName = "Local\\AltMate.ItemInventory.v1";
    private readonly string path;
    private readonly Mutex mutex = new(false, MutexName);
    private readonly object gate = new();
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = false };
    private List<StoredItemRecord> cache = new();
    private readonly Dictionary<string, DateTime> emptySnapshotWrites = new();
    private (DateTime ModifiedUtc, long Length)? cacheStamp;

    internal ItemInventoryDatabase(string path)
    {
        this.path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Reload();
    }

    internal void ReplaceSnapshot(string ownerKey, ItemStorageKind kind, string storageKey,
        IEnumerable<StoredItemRecord> snapshot)
    {
        lock (gate)
        {
            if (!TryEnter()) return;
            try
            {
                ReadUnsafe();
                var rows = snapshot.Where(x => x.ItemId != 0 && x.Quantity > 0).ToArray();
                var previous = cache.Where(x => x.OwnerKey == ownerKey && x.StorageKind == kind &&
                                                x.StorageKey == storageKey).ToArray();
                var snapshotKey = $"{ownerKey}:{(int)kind}:{storageKey}";
                if (previous.Length == 0 && rows.Length == 0 &&
                    emptySnapshotWrites.TryGetValue(snapshotKey, out var emptyWrittenAt) &&
                    DateTime.UtcNow - emptyWrittenAt < TimeSpan.FromMinutes(1))
                    return;
                if (SameContents(previous, rows) && previous.Length > 0 &&
                    DateTime.UtcNow - previous.Max(x => x.ScannedAtUtc) < TimeSpan.FromMinutes(1))
                    return;
                cache.RemoveAll(x => x.OwnerKey == ownerKey && x.StorageKind == kind && x.StorageKey == storageKey);
                cache.AddRange(rows);
                WriteUnsafe();
                if (rows.Length == 0) emptySnapshotWrites[snapshotKey] = DateTime.UtcNow;
                else emptySnapshotWrites.Remove(snapshotKey);
            }
            catch (Exception exception)
            {
                Plugin.Log.Warning(exception, "アイテム管理DBを保存できませんでした。");
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    internal IReadOnlyList<StoredItemRecord> Search(string query)
    {
        lock (gate)
        {
            ReloadIfChanged();
            var normalized = query.Trim();
            IEnumerable<StoredItemRecord> result = cache;
            if (uint.TryParse(normalized, out var itemId))
                result = result.Where(x => x.ItemId == itemId);
            else if (normalized.Length > 0)
                result = result.Where(x => x.ItemName.Contains(normalized, StringComparison.CurrentCultureIgnoreCase));
            return result.OrderBy(x => x.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.OwnerName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.StorageKind).ThenBy(x => x.StorageName).ToArray();
        }
    }

    internal IReadOnlyList<StoredItemRecord> GetAll() => Search(string.Empty);

    private void ReloadIfChanged()
    {
        if (FileStamp() == cacheStamp) return;
        Reload();
    }

    private void Reload()
    {
        if (!TryEnter()) return;
        try { ReadUnsafe(); }
        catch (Exception exception) { Plugin.Log.Warning(exception, "アイテム管理DBを読み込めませんでした。"); }
        finally { mutex.ReleaseMutex(); }
    }

    private void ReadUnsafe()
    {
        if (!File.Exists(path))
        {
            cache = new List<StoredItemRecord>();
            cacheStamp = null;
            return;
        }
        cache = JsonSerializer.Deserialize<List<StoredItemRecord>>(File.ReadAllText(path), jsonOptions) ?? new();
        cacheStamp = FileStamp();
    }

    private void WriteUnsafe()
    {
        var temporary = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(cache, jsonOptions));
        File.Move(temporary, path, true);
        cacheStamp = FileStamp();
    }

    private (DateTime ModifiedUtc, long Length)? FileStamp()
    {
        var file = new FileInfo(path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
    }

    private bool TryEnter()
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { return true; }
    }

    private static bool SameContents(IReadOnlyCollection<StoredItemRecord> left,
        IReadOnlyCollection<StoredItemRecord> right)
    {
        var expected = left.GroupBy(x => (x.ItemId, x.IsHq))
            .ToDictionary(x => x.Key, x => x.Sum(row => row.Quantity));
        var actual = right.GroupBy(x => (x.ItemId, x.IsHq))
            .ToDictionary(x => x.Key, x => x.Sum(row => row.Quantity));
        return expected.Count == actual.Count && expected.All(x =>
            actual.TryGetValue(x.Key, out var quantity) && quantity == x.Value);
    }

    public void Dispose() => mutex.Dispose();
}
