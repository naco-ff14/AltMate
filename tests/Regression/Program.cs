using AltMate;
using System.Diagnostics;
using System.Reflection;

var directory = Path.Combine(Path.GetTempPath(), "AltMate-regression-" + Guid.NewGuid());
var mutexName = "Local\\AltMate.Regression." + Guid.NewGuid();
Directory.CreateDirectory(directory);
try
{
    using var first = new SharedConfigurationStore(directory, mutexName);
    using var second = new SharedConfigurationStore(directory, mutexName);
    var a = new Configuration();
    var b = new Configuration();
    first.LoadInto(a);
    second.LoadInto(b);

    a.FollowStartDistance = 12;
    Require(first.TrySaveMerged(a, true, out var revision), "initial save");
    // This client still has the older baseline. Its independent edit must not
    // overwrite the first client's change during the eventual deferred merge.
    b.Language = "en";
    Require(second.TrySaveMerged(b, true, out revision), "stale-baseline save");
    Require(b.FollowStartDistance == 12 && b.Language == "en", "independent settings merge");
    Require(first.ReloadIfNewer(a, revision, out _), "revision notification reload");
    Require(a.Language == "en" && a.FollowStartDistance == 12, "reload contents");

    a.CrafterLevelingCharacters[123] = new CrafterLevelingSettings();
    a.CrafterLevelingCharacters[123].GearCraftingSelections[987] = 4;
    a.CrafterLevelingCharacters[123].Progress.UpdatedAt = DateTime.Now;
    using var acquired = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var holder = new Thread(() =>
    {
        using var gate = new Mutex(false, mutexName);
        gate.WaitOne();
        acquired.Set();
        release.Wait();
        gate.ReleaseMutex();
    });
    holder.Start();
    acquired.Wait();
    try
    {
        var watch = Stopwatch.StartNew();
        Require(!first.TrySaveMerged(a, false, out _), "busy save defers");
        Require(!first.ReloadIfNewer(a, long.MaxValue, out _), "busy reload defers");
        Require(watch.Elapsed < TimeSpan.FromMilliseconds(500), "no two-second lock wait");
    }
    finally { release.Set(); holder.Join(); }
    Require(first.TrySaveMerged(a, false, out revision), "retry succeeds");
    Require(second.ReloadIfNewer(b, revision, out _), "retry notification reload");
    Require(b.CrafterLevelingCharacters[123].GearCraftingSelections[987] == 4, "pending gear quantity preserved");

    // Prime the periodic fallback, then prevent opening the file. An unchanged
    // metadata poll must neither deserialize nor emit an I/O warning.
    first.Poll(a, out _);
    typeof(SharedConfigurationStore).GetField("lastPollUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(first, DateTime.MinValue);
    var warningCount = Plugin.Log.Warnings;
    using (var lockedFile = new FileStream(Path.Combine(directory, "shared-config.json"),
               FileMode.Open, FileAccess.Read, FileShare.None))
    {
        Require(!first.Poll(a, out _), "unchanged poll");
        Require(Plugin.Log.Warnings == warningCount, "unchanged poll avoids file contents");
        Require(!first.ReloadIfNewer(a, revision, out _), "duplicate notification skips read");
        Require(Plugin.Log.Warnings == warningCount, "duplicate notification avoids file contents");
    }

    b.CrafterLevelingCharacters[123].GearCraftingSelections[987] = 15;
    b.CrafterLevelingCharacters[123].Progress.UpdatedAt = DateTime.Now.AddSeconds(1);
    Require(second.TrySaveMerged(b, false, out _), "external update");
    typeof(SharedConfigurationStore).GetField("lastPollUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(first, DateTime.MinValue);
    Require(first.Poll(a, out _), "metadata change triggers reload");
    Require(a.CrafterLevelingCharacters[123].GearCraftingSelections[987] == 15, "external quantity reflected");
    Console.WriteLine("PASS: settings merge, lock contention/retry, quantities, unchanged polling, external reload.");
}
finally
{
    // Only this test's explicitly created, unique temporary directory is removed.
    Directory.Delete(directory, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}
