using System.Text.Json;
using AltMate.SyncContracts;

// Local-only exporter prototype. No network calls or credentials are used.
var input = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "AltMate", "shared-config.json");
var output = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "AltMate", "web-sync-preview.json");

if (!File.Exists(input))
{
    Console.Error.WriteLine("AltMate shared configuration not found.");
    return 1;
}

try
{
    var json = await File.ReadAllTextAsync(input);
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    if (!root.TryGetProperty("Configuration", out var config))
    {
        Console.Error.WriteLine("Missing Configuration object.");
        return 2;
    }

    long revision = root.TryGetProperty("Revision", out var rev) && rev.TryGetInt64(out var n) ? n : 0;
    var changes = new List<EntityChange>();
    Exporter.AddAll(changes, config, "CharacterGil", "character_gil", "ContentId", "UpdatedAt",
        "CharacterName", "WorldName", "Gil");
    Exporter.AddAll(changes, config, "CustomDeliveryCharacters", "custom_delivery", "ContentId", "UpdatedAt",
        "CharacterName", "WorldName", "RemainingWeeklyAllowances",
        "PurpleCrafterScrip", "PurpleGathererScrip", "OrangeCrafterScrip", "OrangeGathererScrip");
    Exporter.AddAll(changes, config, "HousingDemolition", "housing_demolition", null, "UpdatedAt",
        "CharacterName", "WorldName", "EstateKind", "IsOwned", "HouseWorldId",
        "HouseTerritoryTypeId", "HouseWard", "HousePlot", "LastEnteredAt", "LastOwnershipCheckedAt");
    Exporter.AddAll(changes, config, "FreeCompanyGil", "free_company", "FreeCompanyId", "UpdatedAt",
        "Name", "WorldName", "CompanyCredits", "CompanyCreditsConfirmed", "CompanyCreditsUpdatedAt");

    var envelope = new SyncEnvelope(SyncContract.CurrentSchemaVersion, Guid.Empty, revision,
        Guid.NewGuid(), DateTimeOffset.UtcNow, changes);
    var serialized = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, serialized);
    Console.WriteLine($"Local preview: {changes.Count} entities -> {output}");
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine($"Export failed: {ex.Message}");
    return 3;
}
