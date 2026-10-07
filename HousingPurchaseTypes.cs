namespace AltMate;

// Lodestone 6.3 classifications, with Dynamis/Chaos/Light changes in 7.0.
// Checked 2026-10-07. Unknown regions/wards must not inherit a guessed default.
internal static class HousingPurchaseTypes
{
    internal static string? Resolve(string dataCenter, int ward)
    {
        if (ward is < 1 or > 30) return null;
        if (dataCenter == "Materia")
            return ward <= 9 ? "FC" : ward <= 24 ? "Solo" : null;
        if (dataCenter is not ("Elemental" or "Gaia" or "Mana" or "Meteor" or
            "Aether" or "Primal" or "Crystal" or "Dynamis" or "Chaos" or "Light")) return null;
        return ward <= 6 || ward == 25 ? "FC"
            : ward is >= 21 and <= 24 || ward == 30 ? "Solo" : "FC/Solo";
    }
}
