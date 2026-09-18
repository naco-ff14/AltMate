using System;

namespace AltMate;

internal sealed class FreeCompanyChestSession
{
    private readonly StableGilObservation observation = new();
    private ulong characterId;
    private ulong companyId;
    private uint territoryId;
    private DateTime closedUntilUtc;

    internal bool ObserveOpen(ulong character, ulong company, uint territory, uint gil, bool finalSample)
    {
        if (territoryId != territory) observation.Reset();
        characterId = character;
        companyId = company;
        territoryId = territory;
        closedUntilUtc = default;
        return observation.Observe(character, company, gil, finalSample);
    }

    internal void Close(DateTime now) => closedUntilUtc = observation.HasConfirmedContext
        ? now.AddSeconds(3) : default;

    internal bool CanAcceptLateUpdate(ulong character, ulong company, uint territory, DateTime now) =>
        observation.HasConfirmedContext && closedUntilUtc != default && now < closedUntilUtc &&
        character == characterId && company == companyId && territory == territoryId;

    internal bool IsAwaitingLateUpdate(DateTime now) => closedUntilUtc != default && now < closedUntilUtc;

    internal void Reset()
    {
        observation.Reset();
        closedUntilUtc = default;
        characterId = 0;
        companyId = 0;
    }
}
