using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AltMate.SyncContracts;
using AltMate;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI tests require Windows.");
if (args.Length == 3 && args[0] == "--validate-export")
{
    // Explicit, local-only compatibility check. Send JSON through a private child stdin,
    // never to disk/stdout or an external service. The validator prints counts only.
    using var source = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
    var envelope = Exporter.Build(source.RootElement, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));
    var start = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(Path.GetFullPath(args[2]));
    using var child = Process.Start(start) ?? throw new Exception("Could not start local validator.");
    await child.StandardInput.WriteAsync(JsonSerializer.Serialize(envelope)); child.StandardInput.Close();
    var summary = await child.StandardOutput.ReadToEndAsync();
    await child.WaitForExitAsync();
    if (child.ExitCode != 0) throw new Exception("Export validation failed (private values not logged). " + summary);
    Console.WriteLine(summary);
    return 0;
}
var root = Path.Combine(args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetTempPath(), "AltMateSyncTest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var input = Path.Combine(root, "synthetic-config.json");
var when = DateTimeOffset.UtcNow.AddMinutes(-1);
string Source(uint gil = 25) => JsonSerializer.Serialize(new { Revision = 10, Configuration = new {
    LocalLinkKey = "private-link-key-never-upload",
    CharacterGil = new Dictionary<string, object> { ["98765432123456789"] = new { ContentId = "98765432123456789", CharacterName = "Test Adventurer", WorldName = "World", Gil = gil, UpdatedAt = when, Retainers = new { RetainerId = "private-retainer" } } },
    FreeCompanyGil = new Dictionary<string, object> { ["12345678998765432"] = new { FreeCompanyId = "12345678998765432", Name = "Test FC", WorldName = "World", Gil = 100, GilConfirmed = true, UpdatedAt = when.AddMinutes(-5), CompanyCredits = 800, CompanyCreditsConfirmed = true, CompanyCreditsUpdatedAt = when, SubmarinesUpdatedAt = when.AddMinutes(-2), Submarines = new { submarine = new { Name = "Test Sub", ReturnTimeUnix = 1793318400u, PrivateField = "private-sub-data" } } } },
} });
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
using var doc = JsonDocument.Parse(Source());
var secret = RandomNumberGenerator.GetBytes(32);
var exported = Exporter.Build(doc.RootElement, Guid.NewGuid(), secret);
var wire = JsonSerializer.Serialize(exported);
foreach (var forbidden in new[] { "LocalLinkKey", "ContentId", "FreeCompanyId", "Retainers", "98765432123456789", "12345678998765432", "private-link", "PrivateField" })
    Check(!wire.Contains(forbidden, StringComparison.Ordinal), "Private field escaped allowlist: " + forbidden);
Check(exported.Changes.Count == 5, "Expected character, FC metadata, FC gil, credits and submarine.");
Check(exported.Changes.Single(e => e.EntityType == "free_company_gil").ObservedAt == when.AddMinutes(-5), "Gil must use its own clock.");
Check(exported.Changes.Single(e => e.EntityType == "free_company_credits").ObservedAt == when, "Credits must use their own clock.");
Check(exported.Changes.Single(e => e.EntityType == "submarine").ObservedAt == when.AddMinutes(-2), "Submarines must use their own clock.");
Check(Exporter.Key(secret, "character_gil", "id") == Exporter.Key(secret, "character_gil", "id"), "Stable key failed.");
Check(Exporter.Key(secret, "character_gil", "id") != Exporter.Key(RandomNumberGenerator.GetBytes(32), "character_gil", "id"), "Owner scoped key failed.");
using (var sentinel = JsonDocument.Parse("{\"UpdatedAt\":\"0001-01-01T00:00:00\"}"))
{
    Check(Exporter.Time(sentinel.RootElement, "UpdatedAt") is null, "Sentinel observation must be absent.");
}
foreach (var url in new[] { "http://test.supabase.co", "https://evil.test", "https://test.supabase.co/path", "https://user:pass@test.supabase.co", "https://test.supabase.co:8443" })
    Check(!SyncTransport.IsProjectUrl(url), "Unsafe URL accepted.");

var store = new SecureStore(Path.Combine(root, "secure"));
store.Save("probe", "sensitive-test-value");
Check(store.Load<string>("probe") == "sensitive-test-value", "DPAPI round trip failed.");
Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(store.DirectoryPath, "probe.bin"))).Contains("sensitive-test-value"), "Plaintext stored.");
var tampered = File.ReadAllBytes(Path.Combine(store.DirectoryPath, "probe.bin")); tampered[^1] ^= 0xFF;
File.WriteAllBytes(Path.Combine(store.DirectoryPath, "probe.bin"), tampered);
try { store.Load<string>("probe"); throw new Exception("Tampering was accepted."); } catch (CryptographicException) { checks++; }

var token = new string('a', 64); var hmac = new string('b', 64); var id = Guid.NewGuid();
var handler = new FakeHandler();
using var client = new HttpClient(handler);
var transport = new SyncTransport(client, store);
handler.Handle = async request => {
    Check(request.RequestUri!.AbsoluteUri == "https://test.supabase.co/functions/v1/sync", "Wrong sync endpoint.");
    Check(request.Headers.Authorization?.Parameter == new string('c', 64), "Wrong pairing credential.");
    Check(await request.Content!.ReadAsStringAsync() == "{\"Action\":\"pair\"}", "Pairing must not send configuration.");
    return Json(new { DeviceId = id, Token = token, HmacKey = hmac });
};
await transport.Pair("https://test.supabase.co", new string('c', 64), CancellationToken.None);
Check(store.Load<DeviceIdentity>("identity").DeviceId == id, "Pairing identity not saved.");
await File.WriteAllTextAsync(input, Source());
string? pendingWire = null;
handler.Handle = async request => {
    Check(request.Headers.Authorization?.Parameter == token, "Wrong upload credential.");
    pendingWire = await request.Content!.ReadAsStringAsync();
    throw new HttpRequestException("simulated uncertain delivery");
};
try { await transport.Sync(input, CancellationToken.None); throw new Exception("Expected connection failure."); } catch (HttpRequestException) { checks++; }
Check(store.Exists("pending"), "Failed send must retain durable queue.");
Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(store.DirectoryPath, "pending.bin"))).Contains("Test Adventurer"), "Queue data stored as plaintext.");
File.Delete(input); // An offline queue must remain sendable without the current source.
handler.Handle = async request => {
    Check(await request.Content!.ReadAsStringAsync() == pendingWire, "Retry changed the event ID/payload.");
    return Json(new { duplicate = true, applied = 0 });
};
Check(await transport.Sync(input, CancellationToken.None), "Duplicate acknowledgement not accepted.");
Check(!store.Exists("pending"), "Acknowledged queue was not removed.");
await File.WriteAllTextAsync(input, Source());
handler.Handle = _ => throw new Exception("Unchanged data should not call the server.");
Check(!await transport.Sync(input, CancellationToken.None), "Unchanged data sent again.");
await File.WriteAllTextAsync(input, Source(99));
handler.Handle = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
try { await transport.Sync(input, CancellationToken.None); throw new Exception("Unauthorized upload accepted."); }
catch (SyncHttpException ex) { Check(!ex.Retryable && ex.Status == HttpStatusCode.Unauthorized, "Revocation should stop automatic retry."); }
Check(store.Exists("pending"), "Rejected queue must not disappear.");
handler.Handle = _ => Task.FromResult(Json(new { unexpected = true }));
try { await transport.Sync(input, CancellationToken.None); throw new Exception("Invalid receipt accepted."); } catch (InvalidDataException) { checks++; }
Check(store.Exists("pending"), "Invalid receipt discarded queue.");
// The production plugin controller runs here without Dalamud or a live game.
static async Task WaitFor(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (!condition()) { if (DateTime.UtcNow >= deadline) throw new Exception("Controller timed out."); await Task.Delay(10); }
}
SecureStore NewPairedStore(string name)
{
    var value = new SecureStore(Path.Combine(root, name));
    value.Save("identity", new DeviceIdentity("https://test.supabase.co", id, token, hmac));
    return value;
}
await File.WriteAllTextAsync(input, Source(500));
{
    var saved = NewPairedStore("controller-off"); var calls = 0;
    var fake = new FakeHandler { Handle = _ => { Interlocked.Increment(ref calls); return Task.FromResult(Json(new { duplicate = false, applied = 5 })); } };
    using var controller = new WebSyncController(false, saved.DirectoryPath, input, new HttpClient(fake));
    await WaitFor(() => controller.Status.Phase == WebSyncPhase.Disabled && controller.Status.Paired);
    controller.SyncNow(); await Task.Delay(50);
    Check(calls == 0, "Disabled plugin must not upload, including manual requests.");
    controller.SetEnabled(true);
    await WaitFor(() => controller.Status.Phase == WebSyncPhase.Synced);
    Check(calls == 1 && controller.Status.LastSuccess.HasValue, "Enable did not upload/record success.");
    controller.SyncNow(); await Task.Delay(50);
    Check(calls == 1, "Manual request bypassed five-minute interval.");
    controller.SetEnabled(false);
    await WaitFor(() => controller.Status.Phase == WebSyncPhase.Disabled);
    // OFF releases the file lock without blocking the UI.
    using var lease = new FileStream(Path.Combine(saved.DirectoryPath, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Check(lease.CanWrite, "Disabling did not release the owner lease.");
    controller.Dispose(); await controller.Completion;
}
{
    var saved = NewPairedStore("controller-contention"); var calls = 0;
    using var blocker = new FileStream(Path.Combine(saved.DirectoryPath, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var fake = new FakeHandler { Handle = _ => { Interlocked.Increment(ref calls); return Task.FromResult(Json(new { duplicate = false, applied = 5 })); } };
    using var controller = new WebSyncController(true, saved.DirectoryPath, input, new HttpClient(fake));
    await WaitFor(() => controller.Status.Phase == WebSyncPhase.WaitingOwner);
    Check(calls == 0, "A second client uploaded while another owner held the lease.");
    blocker.Dispose(); controller.SyncNow();
    await WaitFor(() => controller.Status.Phase == WebSyncPhase.Synced);
    Check(calls == 1, "Waiting client failed to take ownership after release.");
    controller.Dispose(); await controller.Completion;
}
{
    var saved = NewPairedStore("controller-rate-limit"); var calls = 0; var clock = new AdjustableClock();
    var fake = new FakeHandler { Handle = _ => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Json(new { duplicate = false, applied = 5 })) };
    using var controller = new WebSyncController(true, saved.DirectoryPath, input, new HttpClient(fake), clock);
    await WaitFor(() => controller.Status.Phase == WebSyncPhase.RateLimited);
    Check(controller.Status.HasPending && controller.Status.NextAttempt == clock.GetUtcNow().AddMinutes(5), "429 must retain queue and show its own retry state.");
    controller.SyncNow(); await Task.Delay(50); Check(calls == 1, "Rate limit was bypassed.");
    clock.Advance(TimeSpan.FromMinutes(6)); controller.SyncNow();
    await WaitFor(() => controller.Status.Phase == WebSyncPhase.Synced);
    Check(calls == 2 && !controller.Status.HasPending, "Queued upload was not retried after cooldown.");
    controller.Dispose(); await controller.Completion;
}
{
    var saved = NewPairedStore("controller-cancel");
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var fake = new FakeHandler { HandleCancelable = async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Json(new { duplicate = false, applied = 5 }); } };
    using var controller = new WebSyncController(true, saved.DirectoryPath, input, new HttpClient(fake));
    await started.Task.WaitAsync(TimeSpan.FromSeconds(8));
    var timer = Stopwatch.StartNew(); controller.Dispose();
    Check(timer.Elapsed < TimeSpan.FromSeconds(1), "Dispose blocked the game/UI thread on network activity.");
    await controller.Completion.WaitAsync(TimeSpan.FromSeconds(8));
    Check(saved.Exists("pending"), "Unloading discarded an in-flight queued event.");
    using var lease = new FileStream(Path.Combine(saved.DirectoryPath, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Check(lease.CanWrite, "Unloading failed to release the lease.");
}
{
    var saved = new SecureStore(Path.Combine(root, "controller-pair"));
    var fake = new FakeHandler { Handle = _ => Task.FromResult(Json(new { DeviceId = id, Token = token, HmacKey = hmac })) };
    using var controller = new WebSyncController(false, saved.DirectoryPath, input, new HttpClient(fake));
    await WaitFor(() => controller.Status.Phase == WebSyncPhase.Disabled);
    Check(controller.Pair("https://test.supabase.co", new string('c', 64)), "Pair command rejected while OFF.");
    await WaitFor(() => controller.Status.Phase == WebSyncPhase.Paired);
    Check(saved.Exists("identity") && !saved.Exists("pending"), "Pairing should not enable uploads.");
    Check(controller.Forget(), "Forget command rejected while OFF.");
    await WaitFor(() => !controller.Status.Paired && !saved.Exists("identity"));
    controller.Dispose(); await controller.Completion;
}
Console.WriteLine($"Sync agent/plugin: {checks} checks passed (synthetic data; privacy, DPAPI, retries, opt-in, ownership, cooldown and unload cancellation).");
return 0;

static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
internal sealed class FakeHandler : HttpMessageHandler
{
    internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Handle { get; set; } = _ => throw new NotImplementedException();
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? HandleCancelable { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => HandleCancelable is { } action ? action(request, cancellationToken) : Handle(request);
}
internal sealed class AdjustableClock : TimeProvider
{
    private long ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    internal void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
}
