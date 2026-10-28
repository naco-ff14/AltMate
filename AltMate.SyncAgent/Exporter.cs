using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AltMate.SyncContracts;

internal static class Exporter
{
    internal static void AddAll(List<EntityChange> output, JsonElement configuration,
        string property, string type, string? idField, string timeField, params string[] allowed)
    {
        if (!configuration.TryGetProperty(property, out var collection) ||
            collection.ValueKind != JsonValueKind.Object) return;

        foreach (var item in collection.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.Object) continue;
            var source = item.Value;
            var key = item.Name;
            if (idField is not null && source.TryGetProperty(idField, out var id))
                key = id.ToString();
            // Pseudonymous stable key; never send raw content IDs.
            var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type + ":" + key)));
            var fields = new Dictionary<string, JsonElement>();
            foreach (var name in allowed)
                if (source.TryGetProperty(name, out var value))
                    fields[name] = value.Clone();
            if (fields.Count == 0) continue;
            var observed = DateTimeOffset.MinValue;
            if (source.TryGetProperty(timeField, out var stamp) &&
                stamp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var parsed))
                observed = parsed.ToUniversalTime();
            if (observed == DateTimeOffset.MinValue) continue;
            output.Add(new EntityChange(type, keyHash, observed, JsonSerializer.SerializeToElement(fields)));
        }
    }
}
