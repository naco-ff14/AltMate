#pragma warning disable NoireLib_002 // Explicit wire/DPAPI JSON contract, not Dalamud configuration serialization.
using System.Globalization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AltMate.SyncContracts;

internal static class Exporter
{
    internal static SyncEnvelope Build(JsonElement root, Guid deviceId, byte[] hmacKey, DateTimeOffset? sourceWrittenAt = null)
    {
        if (!root.TryGetProperty("Configuration", out var config) || config.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing Configuration object.");
        long revision = root.TryGetProperty("Revision", out var rev) && rev.TryGetInt64(out var n) && n >= 0 && n <= 9007199254740991 ? n : 0;
        var changes = new List<EntityChange>();
        var snapshotAt = sourceWrittenAt ?? DateTimeOffset.UtcNow;
        AddAll(changes, config, hmacKey, "CharacterGil", "character_gil", "ContentId", "UpdatedAt", "CharacterName", "WorldName", "Gil");
        AddAll(changes, config, hmacKey, "CustomDeliveryCharacters", "custom_delivery", "ContentId", "UpdatedAt",
            "CharacterName", "WorldName", "RemainingWeeklyAllowances", "PurpleCrafterScrip", "PurpleGathererScrip", "OrangeCrafterScrip", "OrangeGathererScrip");
        AddAll(changes, config, hmacKey, "HousingDemolition", "housing_demolition", null, "UpdatedAt",
            "CharacterName", "WorldName", "EstateKind", "IsOwned", "HouseWorldId", "HouseTerritoryTypeId", "HouseWard", "HousePlot", "LastEnteredAt", "LastOwnershipCheckedAt");
        AddAll(changes, config, hmacKey, "Characters", "housing_lottery", "ContentId", "LastCheckedAt",
            "CharacterName", "WorldName", "PlotAddress", "EntryPhaseEndsAt", "ResultChecked", "BidGilDeposited", "BidWardNumber", "BidPlotNumber");
        if (config.TryGetProperty("Characters", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
        foreach (var item in profiles.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.Object) continue;
            var id = item.Value.TryGetProperty("ContentId", out var raw) ? raw.ToString() : item.Name;
            changes.Add(Change("character_profile", Key(hmacKey, "character_gil", id), snapshotAt,
                Fields(item.Value, "CharacterName", "WorldName", "EnabledForDisplay", "EnabledForDemolitionDisplay")));
        }
        if (config.TryGetProperty("CharacterGil", out var wallets) && wallets.ValueKind == JsonValueKind.Object)
        foreach (var owner in wallets.EnumerateObject())
        {
            var source = owner.Value;
            if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("Retainers", out var retainers) || retainers.ValueKind != JsonValueKind.Object) continue;
            var id = source.TryGetProperty("ContentId", out var raw) ? raw.ToString() : owner.Name;
            foreach (var retainer in retainers.EnumerateObject())
            {
                if (retainer.Value.ValueKind != JsonValueKind.Object || Time(retainer.Value, "UpdatedAt") is not { } time) continue;
                var fields = Fields(retainer.Value, "Name", "Gil");
                foreach (var field in Fields(source, "CharacterName", "WorldName")) fields[field.Key] = field.Value;
                fields["CharacterKey"] = JsonSerializer.SerializeToElement(Key(hmacKey, "character_gil", id));
                changes.Add(Change("retainer_gil", Key(hmacKey, "retainer_gil", id + ":" + retainer.Name), time, fields));
            }
        }
        if (config.TryGetProperty("OpenPlots", out var plots) && plots.ValueKind == JsonValueKind.Array)
        foreach (var plot in plots.EnumerateArray())
        {
            if (plot.ValueKind != JsonValueKind.Object || Time(plot, "CheckedAt") is not { } time) continue;
            var fields = Fields(plot, "WorldName", "DistrictName", "WardNumber", "PlotNumber", "Size", "Price", "CheckedAt");
            if (Time(config, "OpenPlotsCycleStartsAtUtc") is { } cycle) fields["CycleStartsAt"] = JsonSerializer.SerializeToElement(cycle);
            var identity = string.Join(":", new[] { "WorldId", "TerritoryTypeId", "WardNumber", "PlotNumber" }.Select(field => plot.TryGetProperty(field, out var value) ? value.ToString() : ""));
            changes.Add(Change("open_plot", Key(hmacKey, "open_plot", identity), time, fields));
        }
        if (config.TryGetProperty("FreeCompanyGil", out var companies) && companies.ValueKind == JsonValueKind.Object)
        foreach (var company in companies.EnumerateObject())
        {
            var source = company.Value;
            if (source.ValueKind != JsonValueKind.Object) continue;
            var id = source.TryGetProperty("FreeCompanyId", out var raw) ? raw.ToString() : company.Name;
            var key = Key(hmacKey, "free_company", id);
            var name = Fields(source, "Name", "WorldName");
            var observed = new[] { Time(source, "UpdatedAt"), Time(source, "CompanyCreditsUpdatedAt"), Time(source, "SubmarinesUpdatedAt") }.Max();
            if (observed.HasValue && name.Count > 0) changes.Add(Change("free_company", key, observed.Value, name));
            if (True(source, "GilConfirmed") && Time(source, "UpdatedAt") is { } gilTime && source.TryGetProperty("Gil", out _))
            {
                var fields = Fields(source, "Name", "WorldName", "Gil", "GilConfirmed");
                fields["GilUpdatedAt"] = JsonSerializer.SerializeToElement(gilTime);
                changes.Add(Change("free_company_gil", key, gilTime, fields));
            }
            if (True(source, "CompanyCreditsConfirmed") && Time(source, "CompanyCreditsUpdatedAt") is { } creditTime && source.TryGetProperty("CompanyCredits", out _))
                changes.Add(Change("free_company_credits", key, creditTime, Fields(source, "Name", "WorldName", "CompanyCredits", "CompanyCreditsConfirmed", "CompanyCreditsUpdatedAt")));
            if (Time(source, "SubmarinesUpdatedAt") is { } subTime && source.TryGetProperty("Submarines", out var subs) && subs.ValueKind == JsonValueKind.Object)
            {
            var fleetKeys = new List<string>();
            foreach (var sub in subs.EnumerateObject())
            {
                if (sub.Value.ValueKind != JsonValueKind.Object) continue;
                var fields = Fields(sub.Value, "Name", "ReturnTimeUnix");
                if (name.TryGetValue("Name", out var fcName)) fields["FreeCompanyName"] = fcName;
                if (name.TryGetValue("WorldName", out var world)) fields["WorldName"] = world;
                fields["FreeCompanyKey"] = JsonSerializer.SerializeToElement(key);
                var subKey = Key(hmacKey, "submarine", id + ":" + sub.Name);
                fleetKeys.Add(subKey);
                changes.Add(Change("submarine", subKey, subTime, fields));
            }
            var fleet = Fields(source, "Name", "WorldName");
            fleet["SubmarineKeys"] = JsonSerializer.SerializeToElement(fleetKeys.OrderBy(value => value));
            changes.Add(Change("submarine_fleet", key, subTime, fleet));
            }
        }
        var metadata = new Dictionary<string, JsonElement> { ["DataVersion"] = JsonSerializer.SerializeToElement(2) };
        if (DateValue(config, "CycleAnchorUtc") is { } anchor) metadata["CycleAnchorUtc"] = JsonSerializer.SerializeToElement(anchor);
        foreach (var pair in new[] { ("CharacterKeys", "character_profile"), ("WalletKeys", "character_gil"), ("RetainerKeys", "retainer_gil"), ("CompanyKeys", "free_company"), ("OpenPlotKeys", "open_plot"), ("HousingKeys", "housing_demolition"), ("LotteryKeys", "housing_lottery"), ("DeliveryKeys", "custom_delivery") })
            metadata[pair.Item1] = JsonSerializer.SerializeToElement(changes.Where(change => change.EntityType == pair.Item2).Select(change => change.EntityKey).OrderBy(value => value));
        changes.Add(Change("sync_metadata", Key(hmacKey, "sync_metadata", deviceId.ToString("N")), snapshotAt, metadata));
        // A single bounded durable snapshot is coalesced locally. Do not silently truncate.
        if (changes.Count > 1000) throw new InvalidDataException("Snapshot exceeds 1,000 entities; cloud upload was not attempted.");
        return new SyncEnvelope(SyncContract.CurrentSchemaVersion, deviceId, revision, Guid.NewGuid(), DateTimeOffset.UtcNow, changes);
    }

    internal static string Key(byte[] secret, string type, string identity) =>
        Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(type + ":" + identity))).ToLowerInvariant();
    internal static DateTimeOffset? Time(JsonElement source, string field)
    {
        var value = DateValue(source, field);
        return value > DateTimeOffset.UtcNow.AddMinutes(5) ? null : value;
    }
    private static DateTimeOffset? DateValue(JsonElement source, string field)
    {
        if (!source.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) ||
            parsed.Year < 2000) return null;
        return parsed.ToUniversalTime();
    }
    private static bool True(JsonElement source, string field) => source.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;
    private static EntityChange Change(string type, string key, DateTimeOffset time, Dictionary<string, JsonElement> fields) =>
        new(type, key, time, JsonSerializer.SerializeToElement(fields));
    private static Dictionary<string, JsonElement> Fields(JsonElement source, params string[] allowed)
    {
        var fields = new Dictionary<string, JsonElement>();
        foreach (var field in allowed)
        {
            if (!source.TryGetProperty(field, out var value)) continue;
            if (field.EndsWith("At", StringComparison.Ordinal))
            {
                if ((field == "EntryPhaseEndsAt" ? DateValue(source, field) : Time(source, field)) is { } time) fields[field] = JsonSerializer.SerializeToElement(time);
                else if (field is "LastEnteredAt" or "EntryPhaseEndsAt") fields[field] = JsonSerializer.SerializeToElement<object?>(null);
            }
            else if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                fields[field] = value.Clone();
        }
        return fields;
    }
    private static void AddAll(List<EntityChange> output, JsonElement config, byte[] secret, string property, string type, string? idField, string timeField, params string[] allowed)
    {
        if (!config.TryGetProperty(property, out var collection) || collection.ValueKind != JsonValueKind.Object) return;
        foreach (var item in collection.EnumerateObject())
        {
            var source = item.Value;
            if (source.ValueKind != JsonValueKind.Object || Time(source, timeField) is not { } observed) continue;
            var id = idField is not null && source.TryGetProperty(idField, out var raw) ? raw.ToString() : item.Name;
            var fields = Fields(source, allowed);
            if (idField == "ContentId") fields["CharacterKey"] = JsonSerializer.SerializeToElement(Key(secret, "character_gil", id));
            if (fields.Count > 0) output.Add(Change(type, Key(secret, type, id), observed, fields));
        }
    }
}
