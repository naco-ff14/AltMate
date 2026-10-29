#pragma warning disable NoireLib_002 // Explicit wire/DPAPI JSON contract, not Dalamud configuration serialization.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AltMate;

internal enum WebSyncPhase { Initializing, Disabled, Unpaired, WaitingOwner, WaitingInterval, Pairing, Paired, Uploading, Synced, NoChanges, RateLimited, Retrying, Rejected, Failed, Stopped }
internal sealed record WebSyncStatus(WebSyncPhase Phase, bool Paired = false, bool Busy = false,
    string ProjectUrl = "", DateTimeOffset? LastSuccess = null, DateTimeOffset? NextAttempt = null, bool HasPending = false);

/// <summary>Worker owns all disk/network activity. UI only posts intent and reads immutable snapshots.</summary>
internal sealed class WebSyncController : IDisposable
{
    private readonly object gate = new();
    private readonly SecureStore store;
    private readonly string input;
    private readonly HttpClient client;
    private readonly SyncTransport transport;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private CancellationTokenSource? uploadCancel;
    private FileStream? lease;
    private (string Url, string Code)? pairing;
    private bool forget;
    private bool blocked;
    private bool disposed;
    private int enabled;
    private int failures;
    private DateTimeOffset? nextAttempt;
    private DateTimeOffset? lastSuccess;
    private string projectUrl = "";
    private bool paired;
    private WebSyncStatus status = new(WebSyncPhase.Initializing);
    internal WebSyncStatus Status => Volatile.Read(ref status);
    internal bool Enabled => Volatile.Read(ref enabled) != 0;
    internal Task Completion { get; }

    internal WebSyncController(bool enabled, string? directory = null, string? input = null,
        HttpClient? client = null, TimeProvider? clock = null)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltMate");
        store = new SecureStore(directory ?? Path.Combine(root, "web-sync"));
        this.input = input ?? Path.Combine(root, "shared-config.json");
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        this.clock = clock ?? TimeProvider.System;
        this.enabled = enabled ? 1 : 0;
        transport = new SyncTransport(this.client, store);
        Completion = Task.Run(Run);
    }

    internal void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (disposed) return;
            Volatile.Write(ref enabled, value ? 1 : 0);
            blocked = false;
            if (!value) uploadCancel?.Cancel();
        }
        Signal();
    }
    internal void SyncNow()
    {
        lock (gate) { if (disposed || !Enabled) return; blocked = false; }
        Signal();
    }
    internal bool Pair(string url, string code)
    {
        if (!SyncTransport.IsProjectUrl(url) || !SyncTransport.IsSecret(code)) return false;
        lock (gate)
        {
            if (disposed || Enabled || pairing.HasValue || Status.Busy) return false;
            pairing = (url, code);
        }
        Signal(); return true;
    }
    internal bool Forget()
    {
        lock (gate)
        {
            if (disposed || Enabled || Status.Busy || pairing.HasValue) return false;
            forget = true;
        }
        Signal(); return true;
    }
    private void Signal()
    {
        try { wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }
    private void Publish(WebSyncPhase phase, bool busy = false) => Volatile.Write(ref status,
        new WebSyncStatus(phase, paired, busy, projectUrl, lastSuccess, nextAttempt, store.Exists("pending")));

    private void ReadIdentity()
    {
        paired = store.Exists("identity");
        projectUrl = paired ? store.Load<DeviceIdentity>("identity").ProjectUrl : "";
        lastSuccess = store.Exists("last-upload") ? store.Load<DateTimeOffset>("last-upload") : null;
    }
    private bool Acquire()
    {
        if (lease is not null) return true;
        Directory.CreateDirectory(store.DirectoryPath);
        try { lease = new FileStream(Path.Combine(store.DirectoryPath, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return false; }
        return true;
    }
    private void Release() { lease?.Dispose(); lease = null; }

    private async Task Run()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try { await Step(); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (OperationCanceledException) when (!Enabled) { Release(); Publish(WebSyncPhase.Disabled); }
                catch (SyncHttpException ex) when (ex.Retryable)
                {
                    nextAttempt = clock.GetUtcNow().Add(ex.Status == HttpStatusCode.TooManyRequests
                        ? TimeSpan.FromMinutes(5) : Backoff());
                    Publish(ex.Status == HttpStatusCode.TooManyRequests ? WebSyncPhase.RateLimited : WebSyncPhase.Retrying);
                }
                catch (SyncHttpException) { lock (gate) blocked = true; nextAttempt = null; Publish(WebSyncPhase.Rejected); }
                catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or OperationCanceledException)
                {
                    nextAttempt = clock.GetUtcNow().Add(Backoff()); Publish(WebSyncPhase.Retrying);
                }
                catch (Exception) { lock (gate) blocked = true; nextAttempt = null; Publish(WebSyncPhase.Failed); }
                // Do not retain the lease for disabled setup operations.
                if (!Enabled || !paired) Release();
                await wake.WaitAsync(TimeSpan.FromSeconds(1), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            Release(); client.Dispose(); Publish(WebSyncPhase.Stopped);
            wake.Dispose(); lifetime.Dispose();
        }
    }
    private TimeSpan Backoff() => TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(failures++, 5))));

    private async Task Step()
    {
        (string Url, string Code)? pair;
        bool clear;
        lock (gate) { pair = pairing; pairing = null; clear = forget; forget = false; }
        if (!Enabled && pair is null && !clear)
        {
            Release(); ReadIdentity(); nextAttempt = null;
            // Preserve the outcome of a pairing/setup operation while disabled.
            if (Status.Phase is not (WebSyncPhase.Paired or WebSyncPhase.Rejected or WebSyncPhase.Failed or WebSyncPhase.WaitingOwner)) Publish(WebSyncPhase.Disabled);
            return;
        }
        if (!Acquire()) { Publish(WebSyncPhase.WaitingOwner); return; }
        if (clear)
        {
            foreach (var file in new[] { "identity", "pending", "ack", "last-upload", "preview" }) store.Remove(file);
            paired = false; projectUrl = ""; lastSuccess = null; nextAttempt = null;
            Publish(WebSyncPhase.Unpaired); return;
        }
        ReadIdentity();
        if (lastSuccess is { } last && last.AddMinutes(5) > clock.GetUtcNow() &&
            (nextAttempt is null || last.AddMinutes(5) > nextAttempt)) nextAttempt = last.AddMinutes(5);
        if (pair is { } request)
        {
            if (paired) { Publish(WebSyncPhase.Paired); return; }
            Publish(WebSyncPhase.Pairing, busy: true);
            // A pairing code is one-use. Do not auto-retry an uncertain claim.
            try { await transport.Pair(request.Url, request.Code, lifetime.Token); }
            catch (SyncHttpException) { Publish(WebSyncPhase.Rejected); return; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or CryptographicException || ex is OperationCanceledException && !lifetime.IsCancellationRequested)
            { Publish(WebSyncPhase.Failed); return; }
            ReadIdentity(); Publish(WebSyncPhase.Paired); return;
        }
        if (!paired) { Publish(WebSyncPhase.Unpaired); return; }
        lock (gate) { if (blocked) return; }
        if (nextAttempt > clock.GetUtcNow())
        {
            if (Status.Phase is WebSyncPhase.WaitingOwner or WebSyncPhase.Initializing or WebSyncPhase.Disabled or WebSyncPhase.Unpaired or WebSyncPhase.Paired)
                Publish(WebSyncPhase.WaitingInterval);
            return; // Manual sync also respects the server interval.
        }
        // A request while disabled never uploads, even if a UI command was already queued.
        lock (gate)
        {
            if (!Enabled) return;
            uploadCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        }
        try
        {
            Publish(WebSyncPhase.Uploading, busy: true);
            var sent = await transport.Sync(input, uploadCancel.Token);
            ReadIdentity(); failures = 0; nextAttempt = clock.GetUtcNow().AddMinutes(5);
            Publish(sent ? WebSyncPhase.Synced : WebSyncPhase.NoChanges);
        }
        finally
        {
            lock (gate) { uploadCancel.Dispose(); uploadCancel = null; }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; pairing = null; Volatile.Write(ref enabled, 0);
            lifetime.Cancel(); uploadCancel?.Cancel();
        }
        Signal(); // Cancellation/lease cleanup happens on the worker, never a blocking UI wait.
    }
}
