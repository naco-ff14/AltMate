using System;
using System.Collections.Generic;

namespace AltMate;

internal readonly record struct CompanyCreditContext(
    ulong CharacterId, ulong CompanyId, uint TerritoryId, nint Addon, nint InfoProxy)
{
    internal bool IsValid => CharacterId != 0 && CompanyId != 0 && Addon != 0 && InfoProxy != 0;
}

internal readonly record struct CompanyCreditSample(uint Credits, DateTime ObservedAtUtc)
{
    internal bool TryApply(FreeCompanyGilRecord record)
    {
        if (record.CompanyCreditsConfirmed &&
            (record.CompanyCredits == Credits ||
             ObservedAtUtc <= record.CompanyCreditsUpdatedAt.ToUniversalTime()))
            return false;
        record.CompanyCredits = Credits;
        record.CompanyCreditsConfirmed = true;
        record.CompanyCreditsUpdatedAt = ObservedAtUtc.ToLocalTime();
        return true;
    }
}

// A native data-update event authorizes one pending observation. Polling may only
// confirm it, never create a new observation or renew its original timestamp.
// This class does NOT establish native data readiness; the caller must do so.
internal sealed class CompanyCreditObservation
{
    private readonly Dictionary<(ulong Character, ulong Company), uint> lastObserved = new();
    private CompanyCreditContext context;
    private CompanyCreditSample? pending;
    private uint? lastEventCredits;
    internal bool HasPending => pending.HasValue;

    internal void ObserveUpdate(CompanyCreditContext current, uint credits, DateTime observedAtUtc)
    {
        if (!current.IsValid)
        {
            Reset();
            return;
        }
        if (context != current) Reset();
        context = current;
        // An intervening value witnessed in this source's own native event is
        // evidence of a transition even if polling did not confirm it in time.
        // A shared-file update or another window is not such evidence.
        var witnessedTransition = lastEventCredits is { } priorEvent && priorEvent != credits;
        lastEventCredits = credits;
        // Preserve a candidate already authorized by a witnessed transition.
        if (pending is { } repeated && repeated.Credits == credits) return;
        if (lastObserved.TryGetValue((current.CharacterId, current.CompanyId), out var previous) &&
            previous == credits && !witnessedTransition)
        {
            pending = null;
            return;
        }
        // Repeated events for an unchanged candidate must not advance its time.
        if (pending is not { } candidate || candidate.Credits != credits)
            pending = new CompanyCreditSample(credits, observedAtUtc);
    }

    internal bool TryConfirm(CompanyCreditContext current, uint? credits, out CompanyCreditSample sample)
    {
        sample = default;
        if (!current.IsValid || current != context || credits is null)
        {
            Reset();
            return false;
        }
        if (pending is not { } candidate) return false;
        pending = null;
        // An unrelated UI change cannot supply a value on behalf of an older event.
        if (candidate.Credits != credits.Value) return false;
        lastObserved[(current.CharacterId, current.CompanyId)] = credits.Value;
        sample = candidate;
        return true;
    }

    internal void Reset()
    {
        pending = null;
        context = default;
        lastEventCredits = null;
        // Keep duplicate suppression across close/reopen and logout. A stale UI
        // reopening is not evidence that a newer shared balance should be replaced.
    }
}

internal static class CompanyCreditParser
{
    // FFXIV's supported locales use ASCII digits and comma, period or space
    // grouping. Require a whole, nonnegative integer; never strip arbitrary text.
    internal static bool TryParse(string? text, out uint credits)
    {
        credits = 0;
        if (text is null) return false;
        var input = text.AsSpan().Trim();
        if (input.IsEmpty) return false;
        uint parsed = 0;
        var digitsInGroup = 0;
        char separator = '\0';
        foreach (var character in input)
        {
            if (character is >= '0' and <= '9')
            {
                var digit = (uint)(character - '0');
                if (parsed > (uint.MaxValue - digit) / 10) return false;
                parsed = parsed * 10 + digit;
                digitsInGroup++;
                continue;
            }
            var normalized = character is '\u00A0' or '\u202F' ? ' ' : character;
            if (normalized is not (',' or '.' or ' ') || digitsInGroup == 0 ||
                (separator == '\0' ? digitsInGroup > 3 : digitsInGroup != 3) ||
                (separator != '\0' && separator != normalized))
                return false;
            separator = normalized;
            digitsInGroup = 0;
        }
        if (digitsInGroup == 0 || (separator != '\0' && digitsInGroup != 3)) return false;
        credits = parsed;
        return true;
    }

    internal static bool TryParseSigned(int value, out uint credits)
    {
        credits = 0;
        if (value < 0) return false;
        credits = (uint)value;
        return true;
    }
}
