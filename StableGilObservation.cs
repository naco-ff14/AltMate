namespace AltMate;

// Require two consecutive ready samples from the same character and FC.
// Zero is a valid balance; an unavailable sample must call Reset instead.
internal sealed class StableGilObservation
{
    private ulong characterId;
    private ulong companyId;
    private uint gil;
    private bool hasSample;
    internal bool HasConfirmedContext { get; private set; }

    internal bool Observe(ulong currentCharacter, ulong currentCompany, uint currentGil,
        bool finalSample = false)
    {
        if (currentCharacter == 0 || currentCompany == 0)
        {
            Reset();
            return false;
        }
        var sameContext = hasSample && characterId == currentCharacter && companyId == currentCompany;
        var stable = sameContext && (gil == currentGil || (finalSample && HasConfirmedContext));
        HasConfirmedContext = stable || (sameContext && HasConfirmedContext);
        characterId = currentCharacter;
        companyId = currentCompany;
        gil = currentGil;
        hasSample = true;
        return stable;
    }

    internal void Reset()
    {
        hasSample = false;
        HasConfirmedContext = false;
    }
}
