using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AltMate;

internal enum ItemStorageKind { Inventory, Saddlebag, Retainer, FreeCompanyChest, Armoire, GlamourDresser, ArmouryChest }

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
    private readonly string path;
    private readonly string legacyJsonPath;
    private readonly object gate = new();
    private readonly Channel<SnapshotWrite> writes = Channel.CreateUnbounded<SnapshotWrite>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private Dictionary<SnapshotKey, StoredItemRecord[]> snapshots = new();
    private Dictionary<uint, StoredItemRecord[]> byItemId = new();
    private StoredItemRecord[] allRows = Array.Empty<StoredItemRecord>();
    private long revision;

    internal long Revision => Interlocked.Read(ref revision);

    internal ItemInventoryDatabase(string path, string legacyJsonPath)
    {
        this.path = path;
        this.legacyJsonPath = legacyJsonPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var connection = OpenConnection())
        {
            InitializeSchema(connection);
            MigrateLegacyJson(connection);
            ReplaceCache(LoadAll(connection));
        }
        worker = Task.Run(WriterLoopAsync);
    }

    internal void ReplaceSnapshot(string ownerKey, ItemStorageKind kind, string storageKey,
        IEnumerable<StoredItemRecord> snapshot)
    {
        var rows = snapshot.Where(x => x.ItemId != 0 && x.Quantity > 0).ToArray();
        var key = new SnapshotKey(ownerKey, kind, storageKey);
        lock (gate)
        {
            if (snapshots.TryGetValue(key, out var previous) && SameContents(previous, rows)) return;
            snapshots = new Dictionary<SnapshotKey, StoredItemRecord[]>(snapshots) { [key] = rows };
            RebuildIndexesUnsafe();
            Interlocked.Increment(ref revision);
        }
        writes.Writer.TryWrite(new SnapshotWrite(key, rows));
    }

    internal IReadOnlyList<StoredItemRecord> Search(string query)
    {
        var normalized = query.Trim();
        lock (gate)
        {
            if (uint.TryParse(normalized, out var itemId))
                return byItemId.TryGetValue(itemId, out var rows) ? rows : Array.Empty<StoredItemRecord>();
            if (normalized.Length == 0) return allRows;
            return allRows.Where(x => x.ItemName.Contains(normalized,
                StringComparison.CurrentCultureIgnoreCase)).ToArray();
        }
    }

    internal IReadOnlyList<StoredItemRecord> GetAll()
    {
        lock (gate) return allRows;
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            using var connection = OpenConnection();
            var dataVersion = ReadDataVersion(connection);
            while (!cancellation.IsCancellationRequested)
            {
                var wait = writes.Reader.WaitToReadAsync(cancellation.Token).AsTask();
                await Task.WhenAny(wait, Task.Delay(500, cancellation.Token)).ConfigureAwait(false);
                var pending = new Dictionary<SnapshotKey, SnapshotWrite>();
                while (writes.Reader.TryRead(out var write)) pending[write.Key] = write;
                if (pending.Count > 0)
                {
                    try { PersistBatch(connection, pending.Values); }
                    catch (Exception exception)
                    {
                        Plugin.Log.Warning(exception, "アイテム管理DBへの一括保存を再試行します。");
                        foreach (var write in pending.Values) writes.Writer.TryWrite(write);
                        await Task.Delay(500, cancellation.Token).ConfigureAwait(false);
                        continue;
                    }
                }

                // Do not replace the memory cache while a newer local snapshot is still queued.
                if (!writes.Reader.TryPeek(out _))
                {
                    var currentVersion = ReadDataVersion(connection);
                    if (currentVersion != dataVersion)
                    {
                        var expectedRevision = Revision;
                        var loadedRows = LoadAll(connection);
                        // A local scan may have changed the cache while SQLite was being read.
                        // In that case keep the local state and retry this data version next loop.
                        if (TryReplaceCache(loadedRows, expectedRevision))
                            dataVersion = currentVersion;
                    }
                }
                if (writes.Reader.Completion.IsCompleted && pending.Count == 0) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "アイテム管理DBのバックグラウンド処理に失敗しました。");
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=3000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS inventory_items (
                owner_key TEXT NOT NULL, storage_kind INTEGER NOT NULL, storage_key TEXT NOT NULL,
                item_id INTEGER NOT NULL, is_hq INTEGER NOT NULL, item_name TEXT NOT NULL,
                quantity INTEGER NOT NULL, owner_name TEXT NOT NULL, world_name TEXT NOT NULL,
                storage_name TEXT NOT NULL, container_index INTEGER NOT NULL, scanned_at_utc TEXT NOT NULL,
                PRIMARY KEY (owner_key, storage_kind, storage_key, item_id, is_hq)
            );
            CREATE INDEX IF NOT EXISTS ix_inventory_item_id ON inventory_items(item_id);
            CREATE INDEX IF NOT EXISTS ix_inventory_item_name ON inventory_items(item_name);
            """;
        command.ExecuteNonQuery();
    }

    private void PersistBatch(SqliteConnection connection, IEnumerable<SnapshotWrite> batch)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var write in batch)
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM inventory_items WHERE owner_key=$owner AND storage_kind=$kind AND storage_key=$storage";
                delete.Parameters.AddWithValue("$owner", write.Key.OwnerKey);
                delete.Parameters.AddWithValue("$kind", (int)write.Key.Kind);
                delete.Parameters.AddWithValue("$storage", write.Key.StorageKey);
                delete.ExecuteNonQuery();
            }
            foreach (var row in write.Rows)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO inventory_items
                    (owner_key, storage_kind, storage_key, item_id, is_hq, item_name, quantity,
                     owner_name, world_name, storage_name, container_index, scanned_at_utc)
                    VALUES ($owner, $kind, $storage, $item, $hq, $name, $quantity,
                            $ownerName, $world, $storageName, $container, $scanned)
                    """;
                AddRowParameters(insert, row, write.Key);
                insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    private static void AddRowParameters(SqliteCommand command, StoredItemRecord row, SnapshotKey key)
    {
        command.Parameters.AddWithValue("$owner", key.OwnerKey);
        command.Parameters.AddWithValue("$kind", (int)key.Kind);
        command.Parameters.AddWithValue("$storage", key.StorageKey);
        command.Parameters.AddWithValue("$item", (long)row.ItemId);
        command.Parameters.AddWithValue("$hq", row.IsHq ? 1 : 0);
        command.Parameters.AddWithValue("$name", row.ItemName);
        command.Parameters.AddWithValue("$quantity", row.Quantity);
        command.Parameters.AddWithValue("$ownerName", row.OwnerName);
        command.Parameters.AddWithValue("$world", row.WorldName);
        command.Parameters.AddWithValue("$storageName", row.StorageName);
        command.Parameters.AddWithValue("$container", row.ContainerIndex);
        command.Parameters.AddWithValue("$scanned", row.ScannedAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static List<StoredItemRecord> LoadAll(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_key, storage_kind, storage_key, item_id, is_hq, item_name, quantity,
                   owner_name, world_name, storage_name, container_index, scanned_at_utc FROM inventory_items
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<StoredItemRecord>();
        while (reader.Read())
        {
            var ownerKey = reader.GetString(0);
            var kind = (ItemStorageKind)reader.GetInt32(1);
            var storageKey = reader.GetString(2);
            var itemId = checked((uint)reader.GetInt64(3));
            var isHq = reader.GetInt32(4) != 0;
            rows.Add(new StoredItemRecord
            {
                Id = $"{ownerKey}:{(int)kind}:{storageKey}:{itemId}:{isHq}",
                OwnerKey = ownerKey, StorageKind = kind, StorageKey = storageKey,
                ItemId = itemId, IsHq = isHq, ItemName = reader.GetString(5), Quantity = reader.GetInt32(6),
                OwnerName = reader.GetString(7), WorldName = reader.GetString(8), StorageName = reader.GetString(9),
                ContainerIndex = reader.GetInt32(10),
                ScannedAtUtc = DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
            });
        }
        return rows;
    }

    private void ReplaceCache(IEnumerable<StoredItemRecord> rows)
    {
        var grouped = rows.GroupBy(x => new SnapshotKey(x.OwnerKey, x.StorageKind, x.StorageKey))
            .ToDictionary(x => x.Key, x => x.ToArray());
        lock (gate)
        {
            snapshots = grouped;
            RebuildIndexesUnsafe();
            Interlocked.Increment(ref revision);
        }
    }

    private bool TryReplaceCache(IEnumerable<StoredItemRecord> rows, long expectedRevision)
    {
        var grouped = rows.GroupBy(x => new SnapshotKey(x.OwnerKey, x.StorageKind, x.StorageKey))
            .ToDictionary(x => x.Key, x => x.ToArray());
        lock (gate)
        {
            if (revision != expectedRevision) return false;
            snapshots = grouped;
            RebuildIndexesUnsafe();
            Interlocked.Increment(ref revision);
            return true;
        }
    }

    private void RebuildIndexesUnsafe()
    {
        allRows = snapshots.Values.SelectMany(x => x)
            .OrderBy(x => x.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.OwnerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.StorageKind).ThenBy(x => x.StorageName).ToArray();
        byItemId = allRows.GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.ToArray());
    }

    private void MigrateLegacyJson(SqliteConnection connection)
    {
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM inventory_items";
        if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) != 0 ||
            !File.Exists(legacyJsonPath)) return;
        try
        {
            var rows = JsonSerializer.Deserialize<List<StoredItemRecord>>(File.ReadAllText(legacyJsonPath)) ?? new();
            var grouped = rows.GroupBy(x => new SnapshotKey(x.OwnerKey, x.StorageKind, x.StorageKey));
            PersistBatch(connection, grouped.Select(x => new SnapshotWrite(x.Key, x.ToArray())));
            File.Move(legacyJsonPath, legacyJsonPath + ".migrated", true);
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "旧アイテム管理JSONをSQLiteへ移行できませんでした。");
        }
    }

    private static long ReadDataVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool SameContents(IReadOnlyCollection<StoredItemRecord> left,
        IReadOnlyCollection<StoredItemRecord> right)
    {
        if (left.Count != right.Count) return false;
        var expected = left.ToDictionary(x => (x.ItemId, x.IsHq), x => x.Quantity);
        return right.All(x => expected.TryGetValue((x.ItemId, x.IsHq), out var quantity) && quantity == x.Quantity);
    }

    public void Dispose()
    {
        writes.Writer.TryComplete();
        if (!worker.Wait(TimeSpan.FromSeconds(3)))
        {
            cancellation.Cancel();
            try { worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }
        cancellation.Dispose();
    }

    private readonly record struct SnapshotKey(string OwnerKey, ItemStorageKind Kind, string StorageKey);
    private sealed record SnapshotWrite(SnapshotKey Key, StoredItemRecord[] Rows);
}
