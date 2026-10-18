using System.Globalization;
using AltMate;

internal static class CompanyCreditRegression
{
    private static readonly DateTime Start = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CompanyCreditContext Context = new(1, 42, 100, (nint)101, (nint)201);
    private static int checks;

    internal static void Run()
    {
        TestParser();
        TestEventConfirmation();
        TestWitnessedIntermediateEvents();
        TestContextAndReset();
        TestSampleApplication();
        TestSharedMerge();
        TestStaleLocalObservations();
        TestPendingMergeWithoutReload();
        TestConservativeDuplicateLimitations();
        Console.WriteLine($"PASS: {checks} company-credit checks: locale parsing, event-only confirmation, zero transitions, context/reset isolation, original timestamps, stale UI/pending protection and shared-store field preservation.");
    }

    private static void TestParser()
    {
        (string Text, uint Expected)[] valid =
        [
            ("0", 0), ("000", 0), ("1", 1), ("9", 9), ("30", 30), ("31", 31),
            ("999", 999), ("1000", 1000), ("1,000", 1000), ("12,345,678", 12_345_678),
            ("1.000", 1000), ("12.345.678", 12_345_678),
            ("1 000", 1000), ("12 345 678", 12_345_678),
            ("1\u00a0000", 1000), ("12\u00a0345\u00a0678", 12_345_678),
            ("1\u202f000", 1000), ("12\u202f345\u202f678", 12_345_678),
            ("12 345\u00a0678", 12_345_678), (" \t1,234\r\n", 1234),
            ("\u00a01\u202f234\u00a0", 1234), ("4294967295", uint.MaxValue),
            ("4,294,967,295", uint.MaxValue), ("4.294.967.295", uint.MaxValue),
            ("4 294 967 295", uint.MaxValue), ("2,147,483,648", 2_147_483_648),
        ];
        string?[] invalid =
        [
            null, "", " \t\r\n", "-1", "-0", "+1", "−1", "4294967296",
            "4,294,967,296", "4.294.967.296", "4 294 967 296", "99999999999999999999",
            "1,", ",1", "1.", ".1", "1,,000", "1..000", "1  000", "1\u00a0\u00a0000",
            "12,34", "12.50", "0.0", "1,234.567", "1.234,567", "1234,567", "1234.567",
            "1,234,56", "1 23 456", "1\t000", "1\n000", "1'000", "1_000", "1e3",
            "Credits: 1,000", "1,000 credits", "Rank 30", "30 / 100", "--", "NaN", "∞",
            "１２３", "١٢٣", "1\0", "0x10",
        ];
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "de-DE", "fr-FR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var (text, expected) in valid)
                    Require(CompanyCreditParser.TryParse(text, out var parsed) && parsed == expected,
                        $"{culture}: parse exact localized integer {text}");
                foreach (var text in invalid)
                    Require(!CompanyCreditParser.TryParse(text, out var parsed) && parsed == 0,
                        $"{culture}: reject malformed/negative/overflow text {text ?? "<null>"}");
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }

        // Numeric magnitude cannot establish which native field supplied a value.
        // A genuine balance may be zero or as small as a company's rank.
        for (uint value = 0; value <= 30; value++)
            Require(CompanyCreditParser.TryParse(value.ToString(CultureInfo.InvariantCulture), out var parsed) && parsed == value,
                $"zero/small/rank-sized balance {value} is not filtered by magnitude");
        foreach (var value in new[] { 0, 1, 30, int.MaxValue })
            Require(CompanyCreditParser.TryParseSigned(value, out var parsed) && parsed == (uint)value,
                $"signed nonnegative value {value}");
        foreach (var value in new[] { -1, -30, int.MinValue })
            Require(!CompanyCreditParser.TryParseSigned(value, out _),
                $"signed negative {value} is unavailable, never a successful zero clamp");
    }

    private static void TestEventConfirmation()
    {
        var observation = new CompanyCreditObservation();
        for (var i = 0; i < 3; i++)
        {
            Require(!observation.TryConfirm(Context, 0, out _), "polling zero without an event cannot confirm");
            Require(!observation.TryConfirm(Context, 1000, out _), "polling nonzero without an event cannot confirm");
        }
        var record = new FreeCompanyGilRecord();
        var sequence = new uint[] { 1000, 0, 7 };
        for (var i = 0; i < sequence.Length; i++)
        {
            var value = sequence[i];
            var eventTime = Start.AddMinutes(i);
            observation.ObserveUpdate(Context, value, eventTime);
            observation.ObserveUpdate(Context, value, eventTime.AddSeconds(5));
            Require(observation.TryConfirm(Context, value, out var sample), "event plus matching sample confirms transition");
            Require(sample.Credits == value && sample.ObservedAtUtc == eventTime,
                "repeated pending events retain original value and observation time");
            Require(sample.TryApply(record), "nonzero to zero to nonzero transition applies");
            Require(record.CompanyCredits == value && record.CompanyCreditsConfirmed &&
                record.CompanyCreditsUpdatedAt.ToUniversalTime() == eventTime, "confirmed transition retains event timestamp");
            Require(!observation.TryConfirm(Context, value, out _), "event is consumed exactly once");
            observation.ObserveUpdate(Context, value, eventTime.AddSeconds(10));
            Require(!observation.TryConfirm(Context, value, out _), "unchanged data event cannot restamp");
        }

        observation.ObserveUpdate(Context, 80, Start.AddMinutes(3));
        Require(!observation.TryConfirm(Context, null, out _), "unavailable sample cancels pending event");
        Require(!observation.TryConfirm(Context, 80, out _), "availability returning does not recreate event");
        observation.ObserveUpdate(Context, 80, Start.AddMinutes(4));
        Require(observation.TryConfirm(Context, 80, out _), "new event recovers after unavailable sample");

        observation.ObserveUpdate(Context, 90, Start.AddMinutes(5));
        Require(!observation.TryConfirm(Context, 100, out _), "poll cannot substitute a different value for pending event");
        Require(!observation.TryConfirm(Context, 90, out _), "mismatched confirmation consumes the old event");
        observation.ObserveUpdate(Context, 100, Start.AddMinutes(6));
        Require(observation.TryConfirm(Context, 100, out _), "new event authorizes the replacement value");

        observation.ObserveUpdate(Context, 110, Start.AddMinutes(7));
        observation.ObserveUpdate(Context, 120, Start.AddMinutes(8));
        Require(observation.TryConfirm(Context, 120, out var replacement) &&
            replacement.ObservedAtUtc == Start.AddMinutes(8), "changed event replaces both pending value and event time");
        observation.ObserveUpdate(Context, 130, Start.AddMinutes(9));
        observation.ObserveUpdate(Context, 120, Start.AddMinutes(10));
        Require(!observation.TryConfirm(Context, 130, out _), "return event replaces intermediate candidate; poll cannot confirm the superseded value");

        var firstZero = new CompanyCreditObservation();
        firstZero.ObserveUpdate(Context, 0, Start);
        Require(firstZero.TryConfirm(Context, 0, out var zero) && zero.Credits == 0,
            "first zero is allowed when caller supplies a qualified event; helper does not prove native readiness");
    }

    private static void TestWitnessedIntermediateEvents()
    {
        var observation = new CompanyCreditObservation();
        Require(!observation.HasPending, "new observer has no pending event");
        var record = new FreeCompanyGilRecord();
        Require(Confirm(observation, Context, 100, Start).TryApply(record), "seed same-source event history at100");
        Require(new CompanyCreditSample(200, Start.AddMinutes(1)).TryApply(record), "remote intermediate balance arrives first");
        observation.ObserveUpdate(Context, 200, Start.AddMinutes(2));
        Require(observation.HasPending, "own intermediate event is pending before polling");
        observation.ObserveUpdate(Context, 100, Start.AddMinutes(3));
        observation.ObserveUpdate(Context, 100, Start.AddMinutes(4));
        observation.ObserveUpdate(Context, 100, Start.AddMinutes(5));
        Require(observation.HasPending, "repeated return events retain an authorized pending sample");
        Require(observation.TryConfirm(Context, 100, out var returned), "witnessed intermediate event authorizes return before intermediate confirmation");
        Require(returned.Credits == 100 && returned.ObservedAtUtc == Start.AddMinutes(3),
            "return sample retains first return-event time despite repeated events");
        Require(!observation.HasPending, "confirmation consumes witnessed return candidate");
        Require(returned.TryApply(record) && record.CompanyCredits == 100 &&
            record.CompanyCreditsUpdatedAt.ToUniversalTime() == Start.AddMinutes(3),
            "same-source witnessed return can replace older remote intermediate balance");
        observation.ObserveUpdate(Context, 100, Start.AddMinutes(6));
        Require(!observation.TryConfirm(Context, 100, out _), "confirmed return does not authorize later unchanged restamps");

        var unchangedRecord = new FreeCompanyGilRecord();
        var unchanged = new CompanyCreditObservation();
        Require(Confirm(unchanged, Context, 100, Start).TryApply(unchangedRecord), "seed record already equal to returning value");
        unchanged.ObserveUpdate(Context, 200, Start.AddMinutes(1));
        unchanged.ObserveUpdate(Context, 100, Start.AddMinutes(2));
        Require(unchanged.TryConfirm(Context, 100, out var equalReturn), "witnessed return can confirm without changing saved scalar");
        Require(!equalReturn.TryApply(unchangedRecord) && unchangedRecord.CompanyCreditsUpdatedAt.ToUniversalTime() == Start,
            "witnessed event history cannot restamp an unchanged saved balance");

        (string Reason, Action<CompanyCreditObservation> Invalidate)[] invalidations =
        [
            ("close/logoff Reset", value => value.Reset()),
            ("invalid event", value => value.ObserveUpdate(Context with { Addon = 0 }, 200, Start.AddMinutes(2))),
            ("unavailable confirmation", value => value.TryConfirm(Context, null, out _)),
            ("invalid confirmation", value => value.TryConfirm(Context with { InfoProxy = 0 }, 200, out _)),
            ("changed confirmation context", value => value.TryConfirm(Context with { TerritoryId = 101 }, 200, out _)),
        ];
        foreach (var (reason, invalidate) in invalidations)
        {
            var isolated = new CompanyCreditObservation();
            Confirm(isolated, Context, 100, Start);
            isolated.ObserveUpdate(Context, 200, Start.AddMinutes(1));
            invalidate(isolated);
            Require(!isolated.HasPending, $"{reason} clears intermediate pending event");
            isolated.ObserveUpdate(Context, 100, Start.AddMinutes(3));
            Require(!isolated.TryConfirm(Context, 100, out _), $"{reason} removes intermediate evidence; historical100 stays suppressed");
            var retried = Confirm(isolated, Context, 200, Start.AddMinutes(4));
            Require(retried.Credits == 200, $"{reason} permits new valid200 event after invalidation");

            // Retry200 directly after invalidation too, without a100 event between.
            var directRetry = new CompanyCreditObservation();
            Confirm(directRetry, Context, 100, Start);
            directRetry.ObserveUpdate(Context, 200, Start.AddMinutes(1));
            invalidate(directRetry);
            var direct = Confirm(directRetry, Context, 200, Start.AddMinutes(4));
            Require(direct.ObservedAtUtc == Start.AddMinutes(4), $"{reason} same200 retry uses new event time");
        }
        foreach (var changed in new[]
        {
            Context with { TerritoryId = 101 }, Context with { Addon = (nint)102 }, Context with { InfoProxy = (nint)202 },
        })
        {
            var isolated = new CompanyCreditObservation();
            Confirm(isolated, Context, 100, Start);
            isolated.ObserveUpdate(Context, 200, Start.AddMinutes(1));
            isolated.ObserveUpdate(changed, 100, Start.AddMinutes(2));
            Require(!isolated.TryConfirm(changed, 100, out _), "context switch cannot import old context's unconfirmed intermediate evidence");
            Confirm(isolated, changed, 200, Start.AddMinutes(3));
        }
    }

    private static void TestContextAndReset()
    {
        (string Name, CompanyCreditContext Value)[] switches =
        [
            ("character", Context with { CharacterId = 2 }),
            ("FC", Context with { CompanyId = 43 }),
            ("territory", Context with { TerritoryId = 101 }),
            ("addon", Context with { Addon = (nint)102 }),
            ("proxy", Context with { InfoProxy = (nint)202 }),
        ];
        foreach (var (name, changed) in switches)
        {
            var observation = new CompanyCreditObservation();
            observation.ObserveUpdate(Context, 100, Start);
            Require(!observation.TryConfirm(changed, 100, out _), $"{name} switch rejects old pending context");
            Require(!observation.TryConfirm(Context, 100, out _), $"{name} switch consumes old context even if it returns");
            observation.ObserveUpdate(changed, 100, Start.AddSeconds(1));
            Require(observation.TryConfirm(changed, 100, out var sample) && sample.ObservedAtUtc == Start.AddSeconds(1),
                $"{name} switch can be confirmed by its own event");

            var directSwitch = new CompanyCreditObservation();
            directSwitch.ObserveUpdate(Context, 200, Start);
            directSwitch.ObserveUpdate(changed, 200, Start.AddSeconds(2));
            Require(directSwitch.TryConfirm(changed, 200, out var switched) && switched.ObservedAtUtc == Start.AddSeconds(2),
                $"{name} event switch cannot inherit previous context's pending timestamp");
        }
        CompanyCreditContext[] invalid =
        [
            default, Context with { CharacterId = 0 }, Context with { CompanyId = 0 },
            Context with { Addon = 0 }, Context with { InfoProxy = 0 },
        ];
        foreach (var context in invalid)
        {
            var observation = new CompanyCreditObservation();
            observation.ObserveUpdate(Context, 100, Start);
            observation.ObserveUpdate(context, 200, Start.AddSeconds(1));
            Require(!observation.TryConfirm(Context, 100, out _), "invalid event context clears previous candidate");
            Require(!observation.TryConfirm(context, 200, out _), "invalid context cannot confirm");
            observation.ObserveUpdate(Context, 100, Start);
            Require(!observation.TryConfirm(context, 100, out _), "invalid confirmation context clears pending event");
            Require(!observation.TryConfirm(Context, 100, out _), "returning valid context still needs new event");
        }
        foreach (var reason in new[] { "close", "logoff" })
        {
            var observation = new CompanyCreditObservation();
            Confirm(observation, Context, 100, Start);
            observation.ObserveUpdate(Context, 200, Start.AddSeconds(1));
            observation.Reset();
            Require(!observation.TryConfirm(Context, 200, out _), $"{reason} Reset clears pending event");
            observation.ObserveUpdate(Context, 100, Start.AddSeconds(2));
            Require(!observation.TryConfirm(Context, 100, out _), $"{reason} Reset retains unchanged-value suppression");
            var changed = Confirm(observation, Context, 200, Start.AddSeconds(3));
            Require(changed.Credits == 200, $"{reason} Reset permits genuine changed event");
        }

        var identities = new CompanyCreditObservation();
        Confirm(identities, Context, 50, Start);
        foreach (var changed in switches.Select(x => x.Value))
        {
            identities.ObserveUpdate(changed, 50, Start.AddSeconds(1));
            var separateIdentity = changed.CharacterId != Context.CharacterId || changed.CompanyId != Context.CompanyId;
            Require(identities.TryConfirm(changed, 50, out _) == separateIdentity,
                "duplicate suppression is keyed by character and FC, not transient UI context");
        }
        identities.ObserveUpdate(Context, 50, Start.AddMinutes(1));
        Require(!identities.TryConfirm(Context, 50, out _), "returning character/FC does not refresh an unchanged balance");
    }

    private static void TestSampleApplication()
    {
        var record = new FreeCompanyGilRecord();
        Require(new CompanyCreditSample(0, Start).TryApply(record), "unconfirmed empty balance becomes confirmed zero");
        Require(record.CompanyCreditsConfirmed && record.CompanyCreditsUpdatedAt.ToUniversalTime() == Start,
            "initial zero stores confirmation and event time");
        Require(!new CompanyCreditSample(0, Start.AddHours(1)).TryApply(record), "same value never restamps record");
        Require(!new CompanyCreditSample(10, Start).TryApply(record), "equal-timestamp conflicting sample is rejected");
        Require(!new CompanyCreditSample(10, Start.AddSeconds(-1)).TryApply(record), "older conflicting sample is rejected");
        Require(new CompanyCreditSample(10, Start.AddSeconds(1)).TryApply(record), "newer different value applies");
        Require(record.CompanyCredits == 10 && record.CompanyCreditsUpdatedAt.ToUniversalTime() == Start.AddSeconds(1),
            "apply stores source event time rather than confirmation time");
    }

    private static void TestSharedMerge()
    {
        WithStores((first, second, a, b) =>
        {
            var observation = new CompanyCreditObservation();
            var zero = Confirm(observation, Context, 0, Start.AddMinutes(2));
            Require(zero.TryApply(a.FreeCompanyGil[42]), "confirmed zero replaces seed credits");
            Require(first.TrySaveMerged(a, false, out _), "persist confirmed zero company credits");

            // The other client still holds old nonzero credits while independently
            // updating chest gil, submarines and voyage history.
            var remote = b.FreeCompanyGil[42];
            remote.Gil = 2_000_000;
            remote.UpdatedAt = Start.AddMinutes(3).ToLocalTime();
            remote.LastCheckedByContentId = 2;
            remote.LastCheckedByName = "Other character";
            remote.SubmarinesUpdatedAt = Start.AddMinutes(4).ToLocalTime();
            remote.Submarines["Second"] = new SubmarineRecord
            {
                Name = "Second", ReturnTimeUnix = 123_456, RoutePointIds = [4, 5],
            };
            remote.TreasureVoyagesUpdatedAt = Start.AddMinutes(5).ToLocalTime();
            remote.TreasureVoyages.Add(new SubmarineTreasureVoyageRecord
            {
                Id = "second-voyage", SubmarineName = "Second", DepartedAtUnix = 100,
                ReturnedAtUnix = 200, TreasureGil = 60_000, TreasureItems = new() { [502] = 3 },
            });
            Require(second.TrySaveMerged(b, false, out var revision), "merge stale credits with newer independent FC fields");
            AssertCredits(b, 0, Start.AddMinutes(2), "stale nonzero cannot resurrect after confirmed zero");
            Require(first.ReloadIfNewer(a, revision, out _), "reload shared confirmed zero and independent field updates");
            AssertCredits(a, 0, Start.AddMinutes(2), "confirmed zero survives shared reload");
            AssertIndependentFields(a.FreeCompanyGil[42], updated: true);
            AssertIndependentFields(b.FreeCompanyGil[42], updated: true);

            var replenished = Confirm(observation, Context, 7, Start.AddMinutes(6));
            Require(replenished.TryApply(a.FreeCompanyGil[42]), "credits can recover from confirmed zero to small nonzero");
            Require(first.TrySaveMerged(a, false, out _), "persist replenished small balance");
            Require(second.TrySaveMerged(b, false, out revision), "stale zero client saves later");
            AssertCredits(b, 7, Start.AddMinutes(6), "stale zero cannot erase newer nonzero credits");
            Require(first.ReloadIfNewer(a, revision, out _), "reload merged replenished credits");
            AssertCredits(a, 7, Start.AddMinutes(6), "nonzero to zero to nonzero persists across both stores");
            AssertIndependentFields(a.FreeCompanyGil[42], updated: true);
        });
    }

    private static void TestStaleLocalObservations()
    {
        WithStores((first, second, a, b) =>
        {
            var localUi = new CompanyCreditObservation();
            Confirm(localUi, Context, 90_000, Start);
            var pendingUi = new CompanyCreditObservation();
            pendingUi.ObserveUpdate(Context, 120_000, Start.AddMinutes(1));
            // Repeated events, even after the remote update's time, must retain
            // the original pending timestamp and cannot acquire new authority.
            pendingUi.ObserveUpdate(Context, 120_000, Start.AddMinutes(3));
            Require(new CompanyCreditSample(0, Start.AddMinutes(2)).TryApply(b.FreeCompanyGil[42]), "remote confirms zero");
            Require(second.TrySaveMerged(b, false, out var revision), "remote publishes zero while local event waits");
            Require(first.ReloadIfNewer(a, revision, out _), "local receives newer remote zero");
            AssertCredits(a, 0, Start.AddMinutes(2), "newer remote zero is visible locally");

            Require(pendingUi.TryConfirm(Context, 120_000, out var stalePending), "old pending event may finish confirmation");
            Require(stalePending.ObservedAtUtc == Start.AddMinutes(1), "pending confirmation keeps pre-remote event time");
            Require(!stalePending.TryApply(a.FreeCompanyGil[42]), "old pending event cannot overwrite newer remote zero");
            foreach (var reopen in new[] { false, true })
            {
                if (reopen) localUi.Reset();
                localUi.ObserveUpdate(Context, 90_000, Start.AddMinutes(reopen ? 5 : 4));
                Require(!localUi.TryConfirm(Context, 90_000, out _),
                    "unchanged local UI cannot restamp after remote zero, including close/reopen");
            }
            pendingUi.Reset();
            pendingUi.ObserveUpdate(Context, 120_000, Start.AddMinutes(6));
            Require(!pendingUi.TryConfirm(Context, 120_000, out _), "rejected stale candidate also stays suppressed after reopen");
            Require(first.TrySaveMerged(a, false, out revision), "save after rejected local observations");
            Require(second.ReloadIfNewer(b, revision, out _), "remote reloads after stale UI checks");
            AssertCredits(a, 0, Start.AddMinutes(2), "stale UI never alters local zero or its timestamp");
            AssertCredits(b, 0, Start.AddMinutes(2), "stale UI never alters persisted zero or its timestamp");
            AssertIndependentFields(a.FreeCompanyGil[42], updated: false);
            AssertIndependentFields(b.FreeCompanyGil[42], updated: false);
        });
    }

    private static void TestPendingMergeWithoutReload()
    {
        WithStores((first, second, a, b) =>
        {
            var observation = new CompanyCreditObservation();
            observation.ObserveUpdate(Context, 120_000, Start.AddMinutes(1));
            Require(new CompanyCreditSample(0, Start.AddMinutes(2)).TryApply(b.FreeCompanyGil[42]),
                "second client confirms zero before first client reloads");
            Require(second.TrySaveMerged(b, false, out _), "publish zero without notifying stale first client");
            observation.ObserveUpdate(Context, 120_000, Start.AddMinutes(3));
            Require(observation.TryConfirm(Context, 120_000, out var pending), "stale client confirms its pending event");
            Require(pending.ObservedAtUtc == Start.AddMinutes(1), "stale client still uses original event time");
            Require(pending.TryApply(a.FreeCompanyGil[42]), "pending event may update a stale local snapshot");
            Require(first.TrySaveMerged(a, false, out var revision), "merge stale local candidate against newer shared zero");
            AssertCredits(a, 0, Start.AddMinutes(2), "save merges newer remote zero back into stale local snapshot");
            Require(second.ReloadIfNewer(b, revision, out _), "remote reads post-candidate merged file");
            AssertCredits(b, 0, Start.AddMinutes(2), "stale candidate never replaces newer persisted remote zero");
            AssertIndependentFields(a.FreeCompanyGil[42], updated: false);
            AssertIndependentFields(b.FreeCompanyGil[42], updated: false);
        });
    }

    private static void TestConservativeDuplicateLimitations()
    {
        // These assertions deliberately describe an unresolved conservative failure:
        // a generic native event with the same historical scalar cannot distinguish
        // stale UI from a genuine return to that balance after an unseen remote change.
        // Do not interpret this passing regression as correct native freshness handling.
        WithStores((first, second, a, b) =>
        {
            var localUi = new CompanyCreditObservation();
            Confirm(localUi, Context, 90_000, Start);
            Require(new CompanyCreditSample(200_000, Start.AddMinutes(1)).TryApply(b.FreeCompanyGil[42]),
                "remote publishes an intermediate balance the local UI did not observe");
            Require(second.TrySaveMerged(b, false, out var revision), "persist unseen intermediate balance");
            Require(first.ReloadIfNewer(a, revision, out _), "shared record receives intermediate balance without UI observation");
            foreach (var reopen in new[] { false, true })
            {
                if (reopen) localUi.Reset();
                localUi.ObserveUpdate(Context, 90_000, Start.AddMinutes(reopen ? 3 : 2));
                Require(!localUi.TryConfirm(Context, 90_000, out _),
                    "KNOWN LIMITATION: genuine return to historical local balance is indistinguishable from stale UI, including after reopen");
                AssertCredits(a, 200_000, Start.AddMinutes(1),
                    "KNOWN LIMITATION: newer shared intermediate balance remains despite genuine same-historical-value event");
            }
            Require(first.TrySaveMerged(a, false, out revision), "save while conservative suppression is in effect");
            Require(second.ReloadIfNewer(b, revision, out _), "remote reloads conservatively retained balance");
            AssertCredits(b, 200_000, Start.AddMinutes(1), "KNOWN LIMITATION: suppressed historical balance does not reach shared file");

            var intermediate = Confirm(localUi, Context, 200_000, Start.AddMinutes(4));
            Require(!intermediate.TryApply(a.FreeCompanyGil[42]), "observing shared intermediate locally does not restamp equal saved balance");
            var returned = Confirm(localUi, Context, 90_000, Start.AddMinutes(5));
            Require(returned.TryApply(a.FreeCompanyGil[42]), "after source observes intermediate value, return to earlier balance can apply");
            Require(first.TrySaveMerged(a, false, out revision), "persist return after intermediate source observation");
            Require(second.ReloadIfNewer(b, revision, out _), "remote receives return after intermediate source observation");
            AssertCredits(b, 90_000, Start.AddMinutes(5), "intermediate local observation bounds duplicate suppression limitation");
            AssertIndependentFields(a.FreeCompanyGil[42], updated: false);
            AssertIndependentFields(b.FreeCompanyGil[42], updated: false);
        });

        // Production keeps separate main-window and exchange observation instances.
        var main = new CompanyCreditObservation();
        var shop = new CompanyCreditObservation();
        var record = new FreeCompanyGilRecord();
        Require(Confirm(main, Context, 100, Start).TryApply(record), "main source initially records 100");
        var shopContext = Context with { Addon = (nint)102 };
        Require(Confirm(shop, shopContext, 200, Start.AddMinutes(1)).TryApply(record), "shop source records intermediate 200");
        main.ObserveUpdate(Context, 100, Start.AddMinutes(2));
        Require(!main.TryConfirm(Context, 100, out _),
            "KNOWN LIMITATION: main100, shop200, fresh main100 is suppressed without main observing200");
        Require(record.CompanyCredits == 200 && record.CompanyCreditsUpdatedAt.ToUniversalTime() == Start.AddMinutes(1),
            "KNOWN LIMITATION: cross-source historical return retains intermediate saved value");
        Require(!Confirm(main, Context, 200, Start.AddMinutes(3)).TryApply(record), "main catches up without restamping shop's 200");
        Require(Confirm(main, Context, 100, Start.AddMinutes(4)).TryApply(record), "main can return to100 after seeing200 itself");
        Console.WriteLine("KNOWN LIMITATION: conservative per-source duplicate suppression can miss a genuine return to its historical balance after an unseen remote/other-source change. Native credit-specific freshness remains unverified.");
    }

    private static CompanyCreditSample Confirm(CompanyCreditObservation observation, CompanyCreditContext context,
        uint credits, DateTime eventTime)
    {
        observation.ObserveUpdate(context, credits, eventTime);
        Require(observation.TryConfirm(context, credits, out var sample), $"confirm event for {credits} credits");
        Require(sample.Credits == credits && sample.ObservedAtUtc == eventTime, "confirmed sample preserves exact event");
        return sample;
    }

    private static void WithStores(Action<SharedConfigurationStore, SharedConfigurationStore, Configuration, Configuration> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AltMate-credit-regression-" + Guid.NewGuid());
        var mutex = "Local\\AltMate.CreditRegression." + Guid.NewGuid();
        try
        {
            using var first = new SharedConfigurationStore(directory, mutex);
            using var second = new SharedConfigurationStore(directory, mutex);
            var a = new Configuration();
            first.LoadInto(a);
            a.FreeCompanyGil[42] = new FreeCompanyGilRecord
            {
                FreeCompanyId = 42, Name = "Credit FC", WorldName = "Ridill", Gil = 1_000_000,
                GilConfirmed = true, UpdatedAt = Start.ToLocalTime(),
                LastCheckedByContentId = 1, LastCheckedByName = "First character",
                CompanyCredits = 90_000, CompanyCreditsConfirmed = true, CompanyCreditsUpdatedAt = Start.ToLocalTime(),
                SubmarinesUpdatedAt = Start.ToLocalTime(),
                Submarines = new()
                {
                    ["First"] = new SubmarineRecord { Name = "First", ReturnTimeUnix = 12_345, RoutePointIds = [1, 2, 3] },
                },
                TreasureVoyagesUpdatedAt = Start.ToLocalTime(),
                TreasureVoyages =
                [
                    new SubmarineTreasureVoyageRecord
                    {
                        Id = "first-voyage", SubmarineName = "First", DepartedAtUnix = 10,
                        ReturnedAtUnix = 20, TreasureGil = 50_000, TreasureItems = new() { [501] = 2 },
                    },
                ],
            };
            Require(first.TrySaveMerged(a, false, out _), "seed actual shared configuration file");
            var b = new Configuration();
            second.LoadInto(b);
            AssertCredits(b, 90_000, Start, "second store reads persisted nonzero credit seed");
            test(first, second, a, b);
        }
        finally
        {
            // Remove only this test's unique temporary directory, after disposing stores.
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertCredits(Configuration configuration, uint value, DateTime eventTime, string message)
    {
        var record = configuration.FreeCompanyGil[42];
        Require(record.CompanyCreditsConfirmed && record.CompanyCredits == value &&
            record.CompanyCreditsUpdatedAt.ToUniversalTime() == eventTime, message);
    }

    private static void AssertIndependentFields(FreeCompanyGilRecord record, bool updated)
    {
        Require(record.FreeCompanyId == 42 && record.Name == "Credit FC" && record.WorldName == "Ridill", "FC identity is preserved");
        Require(record.GilConfirmed && record.Gil == (updated ? 2_000_000u : 1_000_000u) &&
            record.UpdatedAt.ToUniversalTime() == (updated ? Start.AddMinutes(3) : Start), "chest gil and its independent timestamp are preserved");
        Require(record.LastCheckedByContentId == (updated ? 2ul : 1ul) &&
            record.LastCheckedByName == (updated ? "Other character" : "First character"), "chest observer metadata is preserved");
        Require(record.SubmarinesUpdatedAt.ToUniversalTime() == (updated ? Start.AddMinutes(4) : Start) &&
            record.Submarines.Count == (updated ? 2 : 1), "submarine collection and independent timestamp are preserved");
        var submarine = record.Submarines["First"];
        Require(submarine.Name == "First" && submarine.ReturnTimeUnix == 12_345 && submarine.RoutePointIds.SequenceEqual(new byte[] { 1, 2, 3 }),
            "existing submarine payload survives credit merge");
        Require(record.TreasureVoyagesUpdatedAt.ToUniversalTime() == (updated ? Start.AddMinutes(5) : Start) &&
            record.TreasureVoyages.Count == (updated ? 2 : 1), "voyage history and independent timestamp are preserved");
        var voyage = record.TreasureVoyages.Single(x => x.Id == "first-voyage");
        Require(voyage.SubmarineName == "First" && voyage.DepartedAtUnix == 10 && voyage.ReturnedAtUnix == 20 &&
            voyage.TreasureGil == 50_000 && voyage.TreasureItems.Count == 1 && voyage.TreasureItems[501] == 2,
            "existing voyage payload survives credit merge");
        if (updated)
        {
            submarine = record.Submarines["Second"];
            Require(submarine.Name == "Second" && submarine.ReturnTimeUnix == 123_456 && submarine.RoutePointIds.SequenceEqual(new byte[] { 4, 5 }),
                "independent newer submarine update survives credit merge");
            voyage = record.TreasureVoyages.Single(x => x.Id == "second-voyage");
            Require(voyage.SubmarineName == "Second" && voyage.DepartedAtUnix == 100 && voyage.ReturnedAtUnix == 200 &&
                voyage.TreasureGil == 60_000 && voyage.TreasureItems.Count == 1 && voyage.TreasureItems[502] == 3,
                "independent newer voyage update survives credit merge");
        }
    }

    private static void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception("Company credits: " + message);
    }
}
