#pragma warning disable NoireLib_002 // Explicit wire/DPAPI JSON contract, not Dalamud configuration serialization.
using System.Net;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AltMate.SyncContracts;

internal sealed class SyncTransport(HttpClient client, SecureStore store)
{
    internal static bool IsProjectUrl(string input) => Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath == "/" && uri.Query == "" && uri.Fragment == "" && uri.UserInfo == "" && uri.IsDefaultPort;
    internal static bool IsSecret(string value) => Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z");
    internal async Task Pair(string projectUrl, string code, CancellationToken cancel)
    {
        if (!IsProjectUrl(projectUrl) || !IsSecret(code)) throw new InvalidDataException("Invalid project URL or pairing code.");
        using var request = Request(projectUrl, code, "{\"Action\":\"pair\"}");
        using var response = await client.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode) throw new SyncHttpException(response.StatusCode);
        var pair = JsonSerializer.Deserialize<PairingResponse>(await response.Content.ReadAsStringAsync(cancel));
        if (pair is null || pair.DeviceId == Guid.Empty || !IsSecret(pair.Token) || !IsSecret(pair.HmacKey))
            throw new InvalidDataException("Invalid pairing response.");
        store.Save("identity", new DeviceIdentity(projectUrl.TrimEnd('/'), pair.DeviceId, pair.Token, pair.HmacKey));
    }
    internal async Task<bool> Sync(string input, CancellationToken cancel)
    {
        var identity = store.Load<DeviceIdentity>("identity");
        if (!IsProjectUrl(identity.ProjectUrl)) throw new InvalidDataException("Invalid stored project URL.");
        SyncEnvelope envelope;
        if (store.Exists("pending")) envelope = store.Load<SyncEnvelope>("pending");
        else
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(input, cancel));
            envelope = Exporter.Build(document.RootElement, identity.DeviceId, Convert.FromHexString(identity.HmacKey));
            if (envelope.Changes.Count == 0) return false;
            var fingerprint = Fingerprint(envelope);
            if (store.Exists("ack") && store.Load<string>("ack") == fingerprint) return false;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
            if (bytes.Length > 1048576) throw new InvalidDataException("Snapshot exceeds the 1 MiB upload limit.");
            store.Save("pending", envelope); // Durable BEFORE sending. Retry the same event UUID after uncertain delivery.
        }
        if (envelope.DeviceId != identity.DeviceId) throw new InvalidDataException("Queue belongs to another device; revoke and pair again.");
        using var request = Request(identity.ProjectUrl, identity.Token, JsonSerializer.Serialize(envelope));
        using var response = await client.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode) throw new SyncHttpException(response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        if (!receipt.RootElement.TryGetProperty("applied", out var count) || !count.TryGetInt32(out _) ||
            !receipt.RootElement.TryGetProperty("duplicate", out var duplicate) || duplicate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Invalid upload receipt; queued event retained.");
        store.Save("ack", Fingerprint(envelope));
        store.Save("last-upload", DateTimeOffset.UtcNow);
        store.Remove("pending");
        return true;
    }
    private static string Fingerprint(SyncEnvelope envelope) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope.Changes)));
    private static HttpRequestMessage Request(string url, string token, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/functions/v1/sync");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }
}
internal sealed class SyncHttpException(HttpStatusCode status) : Exception("Sync server rejected the request.")
{
    internal HttpStatusCode Status => status;
    internal bool Retryable => status == HttpStatusCode.TooManyRequests || (int)status >= 500;
}
