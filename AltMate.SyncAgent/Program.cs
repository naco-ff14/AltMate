using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AltMate.SyncContracts;

// Explicit commands only: launching without arguments never sends data.
if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("This agent requires Windows (current-user DPAPI)."); return 1; }
var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltMate", "web-sync");
var input = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltMate", "shared-config.json");
var store = new SecureStore(directory);
FileStream singleInstance;
try
{
    Directory.CreateDirectory(directory);
    singleInstance = new FileStream(Path.Combine(directory, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Another sync agent is running, or its data directory is unavailable. Stop it before pairing or changing settings."); return 2; }
using var processLock = singleInstance;
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
var transport = new SyncTransport(client, store);
try
{
    switch (args.FirstOrDefault()?.ToLowerInvariant())
    {
        case "pair" when args.Length == 2:
            if (store.Exists("identity")) { Console.Error.WriteLine("Already paired. Revoke the old device in the dashboard, then run forget before pairing again."); return 2; }
            if (!SyncTransport.IsProjectUrl(args[1])) { Console.Error.WriteLine("Use your HTTPS Supabase project URL, not the Pages website."); return 2; }
            Console.Write("Paste the one-time pairing code (hidden), then Enter: ");
            var code = ReadSecret();
            await transport.Pair(args[1], code.Trim(), cancel.Token);
            Console.WriteLine("Device paired. Automatic sync is still OFF. Use sync for one upload or watch for a foreground five-minute loop.");
            return 0;
        case "sync":
            RequireIdentity();
            Console.WriteLine(await transport.Sync(input, cancel.Token) ? "Sync acknowledged." : "No new observed data to send.");
            return 0;
        case "watch":
            RequireIdentity();
            Console.WriteLine("Sync enabled while this window is running. Interval: 5 minutes. Ctrl+C stops it. No scheduled task is installed.");
            var failures = 0;
            while (!cancel.IsCancellationRequested)
            {
                TimeSpan delay;
                try
                {
                    Console.WriteLine(await transport.Sync(input, cancel.Token) ? "Sync acknowledged." : "No new observed data to send.");
                    failures = 0;
                    delay = SyncContract.MinimumSyncInterval;
                }
                catch (SyncHttpException ex) when (ex.Retryable)
                {
                    delay = ex.Status == HttpStatusCode.TooManyRequests ? TimeSpan.FromMinutes(5) : Retry(++failures);
                    Console.WriteLine(ex.Status == HttpStatusCode.TooManyRequests
                        ? "Upload interval limit (HTTP 429). Retrying in five minutes; encrypted queued event retained."
                        : $"Server temporarily unavailable (HTTP {(int)ex.Status}); encrypted queued event retained. Retrying with backoff.");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException || ex is TaskCanceledException && !cancel.IsCancellationRequested)
                {
                    delay = Retry(++failures);
                    Console.WriteLine("Connection or source file temporarily unavailable; queued data retained. Retrying with backoff.");
                }
                await Task.Delay(delay, cancel.Token);
            }
            return 0;
        case "preview":
            // Generate a local-only secret if not paired; never send this preview manually.
            var secret = store.Exists("identity") ? Convert.FromHexString(store.Load<DeviceIdentity>("identity").HmacKey) : RandomNumberGenerator.GetBytes(32);
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(input, cancel.Token)))
            {
                var preview = Exporter.Build(doc.RootElement, Guid.Empty, secret);
                store.Save("preview", preview);
                Console.WriteLine($"Local encrypted preview: {preview.Changes.Count} entities. No network requests made.");
            }
            return 0;
        case "status":
            Console.WriteLine(store.Exists("identity") ? "Paired (credentials protected for the current Windows user)." : "Not paired.");
            Console.WriteLine(store.Exists("pending") ? "An encrypted upload is pending." : "No queued upload.");
            return 0;
        case "forget":
            foreach (var file in new[] { "identity", "pending", "ack", "preview", "last-upload" }) store.Remove(file);
            Console.WriteLine("Local credentials and queued data removed. Revoke the device separately in the web dashboard.");
            return 0;
        default:
            Console.WriteLine("AltMate Sync Agent\n  pair <https://project.supabase.co>  Register using a one-time dashboard code\n  preview  Local encrypted export only\n  sync     Explicit one-time upload\n  watch    Foreground sync every five minutes; Ctrl+C stops\n  status   Show local pairing/queue status\n  forget   Remove local credentials/queue (does not revoke server device)");
            return 0;
    }
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested) { Console.WriteLine("Sync stopped; queued data retained."); return 0; }
catch (SyncHttpException ex)
{
    Console.Error.WriteLine(ex.Status == HttpStatusCode.Unauthorized ? "Pairing code expired/used, or device revoked. Generate a new code and pair again." :
        ex.Status == HttpStatusCode.TooManyRequests ? "Rate limited. Wait five minutes; queued data retained." :
        "Sync rejected. Check the server deployment and agent version; queued data retained.");
    return 3;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or HttpRequestException or InvalidDataException or FormatException or OperationCanceledException)
{
    // Do not print raw HTTP bodies, request URLs with secrets, source contents, or exception messages.
    Console.Error.WriteLine("Sync could not complete. Check the source file, Windows account, connection and deployment. Queued data retained.");
    return 4;
}


void RequireIdentity() { if (!store.Exists("identity")) throw new InvalidDataException("Pair this device first."); }
static TimeSpan Retry(int failures) => TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(failures - 1, 5))) + Random.Shared.Next(0, 10));
static string ReadSecret()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var value = new System.Text.StringBuilder();
    for (;;)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
        if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
        else if (!char.IsControl(key.KeyChar) && value.Length < 256) value.Append(key.KeyChar);
    }
}
