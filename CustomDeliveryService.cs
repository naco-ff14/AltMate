using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AltMate;

internal sealed unsafe class CustomDeliveryService : IDisposable
{
    internal const uint PurpleCrafterScripId = 33913;
    internal const uint PurpleGathererScripId = 33914;
    internal const uint OrangeCrafterScripId = 41784;
    internal const uint OrangeGathererScripId = 41785;

    private readonly Plugin plugin;
    private readonly CustomDeliveryAutomation automation;
    private readonly List<CustomDeliveryNpc> npcs = new();
    private readonly List<ScripExchangeItem> exchangeItems = new();
    private DateTime lastSnapshotUtc;
    private DateTime lastRefreshUtc;
    private bool exchangeCatalogLoaded;

    internal IReadOnlyList<CustomDeliveryNpc> Npcs => npcs;
    internal IReadOnlyList<ScripExchangeItem> ExchangeItems => exchangeItems;
    internal CustomDeliveryPlan? Plan { get; private set; }
    internal CustomDeliveryAutomation Automation => automation;
    internal string LastError { get; private set; } = string.Empty;
    internal int RemainingWeeklyAllowances { get; private set; }

    internal bool IsArtisanAvailable => IsPluginLoaded("Artisan");
    internal bool IsLifestreamAvailable => Plugin.IsLifestreamAvailable();
    internal bool IsVnavmeshAvailable => IsPluginLoaded("vnavmesh");
    internal bool IsQuestionableAvailable => IsPluginLoaded("Questionable");

    internal CustomDeliveryService(Plugin plugin)
    {
        this.plugin = plugin;
        automation = new CustomDeliveryAutomation(plugin, this);
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    internal void Refresh()
    {
        LastError = string.Empty;
        npcs.Clear();
        Plan = null;

        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
            return;

        try
        {
            var manager = SatisfactionSupplyManager.Instance();
            var currencies = CurrencyManager.Instance();
            if (manager == null || currencies == null)
                return;

            RemainingWeeklyAllowances = Math.Max(0, manager->GetRemainingAllowances());
            var npcSheet = Plugin.DataManager.GetExcelSheet<SatisfactionNpc>();
            var supplySheet = Plugin.DataManager.GetSubrowExcelSheet<SatisfactionSupply>();
            var rewardSheet = Plugin.DataManager.GetExcelSheet<SatisfactionSupplyReward>();
            var bonusSheet = Plugin.DataManager.GetExcelSheet<SatisfactionBonusGuarantee>();
            var bonusRowId = ResolveBonusRowId(manager, (uint)bonusSheet.Count);
            SatisfactionBonusGuarantee bonusRow = default;
            var hasBonusRow = bonusRowId >= 0 && bonusSheet.TryGetRow((uint)bonusRowId, out bonusRow);

            foreach (var npc in npcSheet)
            {
                if (npc.RowId == 0 || npc.Npc.RowId == 0)
                    continue;

                var index = (int)npc.RowId - 1;
                if (index < 0 || index >= manager->SatisfactionRanks.Length)
                    continue;

                var rank = manager->SatisfactionRanks[index];
                var unlocked = npc.QuestRequired.RowId == 0 || QuestManager.IsQuestComplete(npc.QuestRequired.RowId);
                if (!unlocked || rank == 0 || rank >= npc.SatisfactionNpcParams.Count)
                    continue;

                var remaining = Math.Max(0, npc.DeliveriesPerWeek - manager->UsedAllowances[index]);
                var supplyId = (uint)npc.SatisfactionNpcParams[rank].SupplyIndex;
                if (supplyId == 0 || !supplySheet.TryGetRow(supplyId, out var supplies))
                    continue;

                var requested = CalculateRequestedSubrows(supplies, supplyId, manager->SupplySeed);
                var entries = new List<CustomDeliveryRequest>();
                for (var slot = 0; slot < 2; slot++)
                {
                    if (requested[slot] < 0 || requested[slot] >= supplies.Count)
                        continue;

                    var selected = supplies[requested[slot]];
                    var guaranteedBonus = rank == 5 && hasBonusRow &&
                        (slot == 0 ? bonusRow.BonusDoH.Contains((byte)npc.RowId)
                                   : bonusRow.BonusDoL.Contains((byte)npc.RowId));
                    if (guaranteedBonus && !selected.IsBonus)
                    {
                        for (var subrow = 0; subrow < supplies.Count; subrow++)
                        {
                            var alternative = supplies[subrow];
                            if (alternative.Slot == selected.Slot && alternative.IsBonus)
                            {
                                selected = alternative;
                                break;
                            }
                        }
                    }

                    if (selected.Item.RowId == 0 || !rewardSheet.TryGetRow(selected.Reward.RowId, out var reward))
                        continue;

                    var rewards = new List<CustomDeliveryReward>();
                    foreach (var entry in reward.SatisfactionSupplyRewardData)
                    {
                        if (entry.RewardCurrency == 0 || entry.QuantityHigh == 0)
                            continue;
                        var currencyId = currencies->GetItemIdBySpecialId((byte)entry.RewardCurrency);
                        if (currencyId == 0)
                            continue;
                        var amount = (int)entry.QuantityHigh * reward.BonusMultiplier / 100;
                        rewards.Add(new CustomDeliveryReward(currencyId, amount));
                    }

                    entries.Add(new CustomDeliveryRequest(
                        slot == 0 ? CustomDeliveryJobType.Crafter : CustomDeliveryJobType.Gatherer,
                        slot,
                        selected.Item.RowId,
                        selected.Item.Value.Name.ToString(),
                        selected.IsBonus || guaranteedBonus,
                        selected.CollectabilityHigh,
                        rewards));
                }

                var level = npc.Level.Value;
                npcs.Add(new CustomDeliveryNpc(
                    npc.RowId,
                    npc.Npc.RowId,
                    npc.Npc.Value.Singular.ToString(),
                    npc.Level.Value.Territory.RowId,
                    new Vector3(level.X, level.Y, level.Z),
                    rank,
                    remaining,
                    npc.DeliveriesPerWeek,
                    entries));
            }

            if (!exchangeCatalogLoaded)
                LoadExchangeCatalog();
            SaveCharacterSnapshot(manager, currencies);
            lastRefreshUtc = DateTime.UtcNow;
        }
        catch (Exception exception)
        {
            LastError = Loc.L("お得意様データを取得できませんでした。", "Unable to read custom delivery data.");
            Plugin.Log.Error(exception, "お得意様データを取得できませんでした。");
        }
    }

    internal CustomDeliveryPlan BuildPlan()
    {
        Refresh();
        var settings = plugin.Configuration.CustomDeliverySettings;
        var warnings = new List<string>();
        var preferredCurrency = PreferredCurrencyId(settings);
        var candidatePairs = npcs
            .Where(npc => npc.RemainingAllowances > 0 &&
                (settings.PreferredNpcId == 0 || settings.PreferredNpcId == npc.RowId))
            .SelectMany(npc => npc.Requests
                .Where(request => request.JobType == settings.JobType)
                .Select(request => (Npc: npc, Request: request)))
            .OrderByDescending(pair => settings.PrioritizeBonus && pair.Request.HasBonus)
            .ThenByDescending(pair => Score(pair.Request, settings, preferredCurrency))
            .ThenByDescending(pair => pair.Request.Rewards.Sum(reward => reward.Amount))
            .ThenBy(pair => pair.Npc.Name)
            .ToList();

        if (RemainingWeeklyAllowances <= 0)
            warnings.Add(Loc.L("今週の納品回数を使い切っています。", "No weekly delivery allowances remain."));
        if (candidatePairs.Count == 0)
            warnings.Add(Loc.L("納品できる開放済みのお得意様が見つかりません。", "No unlocked client can accept the selected delivery type."));
        if (!IsLifestreamAvailable)
            warnings.Add(Loc.L("Lifestreamが読み込まれていません。", "Lifestream is not loaded."));
        if (!IsVnavmeshAvailable)
            warnings.Add(Loc.L("vnavmeshが読み込まれていません。", "vnavmesh is not loaded."));
        if (settings.JobType == CustomDeliveryJobType.Crafter && !IsArtisanAvailable)
            warnings.Add(Loc.L("Artisanが読み込まれていません。", "Artisan is not loaded."));
        if (settings.JobType == CustomDeliveryJobType.Gatherer && !IsQuestionableAvailable)
            warnings.Add(Loc.L("ギャザラー自動採集にはQuestionableが必要です。", "Questionable is required for automatic gathering."));

        var exchanges = new Dictionary<uint, ScripExchangeItem>();
        if (settings.AutoExchangeEnabled)
        {
            foreach (var currency in new[] { PurpleCurrencyId(settings.JobType), OrangeCurrencyId(settings.JobType) })
            {
                var configuredId = currency == PurpleCurrencyId(settings.JobType)
                    ? settings.PurpleExchangeItemId
                    : settings.OrangeExchangeItemId;
                if (configuredId == 0 && settings.ExchangeItemId != 0)
                    configuredId = settings.ExchangeItemId;
                var exchange = exchangeItems.FirstOrDefault(item => item.ItemId == configuredId &&
                    item.CurrencyItemId == currency);
                if (exchange is not null)
                    exchanges[currency] = exchange;
            }
            var rewardedCurrencies = candidatePairs.SelectMany(pair => pair.Request.Rewards)
                .Select(reward => reward.CurrencyItemId).Distinct();
            foreach (var currency in rewardedCurrencies.Where(currency => !exchanges.ContainsKey(currency)))
                warnings.Add(Loc.L($"{ItemName(currency)}の交換アイテムを選択してください。",
                    $"Select an exchange item for {ItemName(currency)}."));
        }

        var steps = new List<CustomDeliveryPlanStep>();
        var remaining = RemainingWeeklyAllowances;
        var projectedScrip = new Dictionary<uint, int>
        {
            [PurpleCurrencyId(settings.JobType)] = (int)CurrencyCount(PurpleCurrencyId(settings.JobType)),
            [OrangeCurrencyId(settings.JobType)] = (int)CurrencyCount(OrangeCurrencyId(settings.JobType)),
        };
        foreach (var (npc, request) in candidatePairs)
        {
            if (remaining <= 0)
                break;
            var count = Math.Min(remaining, npc.RemainingAllowances);
            if (!settings.RunUntilWeeklyLimit)
                count = Math.Min(count, npc.RemainingAllowances);
            if (count <= 0)
                continue;

            foreach (var reward in request.Rewards)
            {
                if (!projectedScrip.TryGetValue(reward.CurrencyItemId, out var balance) ||
                    !settings.AutoExchangeEnabled ||
                    !exchanges.TryGetValue(reward.CurrencyItemId, out var exchange) ||
                    balance + reward.Amount * count < settings.ExchangeThreshold ||
                    balance < exchange.Cost)
                    continue;

                steps.Add(new CustomDeliveryPlanStep(CustomDeliveryStepKind.ExchangeScrip,
                    npc, request, 0, exchange));
                projectedScrip[reward.CurrencyItemId] = balance % Math.Max(1, exchange.Cost);
            }

            steps.Add(new CustomDeliveryPlanStep(
                settings.JobType == CustomDeliveryJobType.Crafter
                    ? CustomDeliveryStepKind.BuyMaterials
                    : CustomDeliveryStepKind.GatherMaterials,
                npc, request, count));
            if (settings.JobType == CustomDeliveryJobType.Crafter)
                steps.Add(new CustomDeliveryPlanStep(CustomDeliveryStepKind.CraftItems, npc, request, count));
            for (var delivery = 0; delivery < count; delivery++)
            {
                foreach (var reward in request.Rewards)
                {
                    if (!projectedScrip.TryGetValue(reward.CurrencyItemId, out var balance))
                        continue;
                    projectedScrip[reward.CurrencyItemId] = balance + reward.Amount;
                }
                steps.Add(new CustomDeliveryPlanStep(CustomDeliveryStepKind.TravelToClient, npc, request, 1));
                steps.Add(new CustomDeliveryPlanStep(CustomDeliveryStepKind.DeliverItems, npc, request, 1));
            }
            remaining -= count;
            if (!settings.RunUntilWeeklyLimit)
                break;
        }

        Plan = new CustomDeliveryPlan(DateTime.UtcNow, settings.JobType,
            RemainingWeeklyAllowances, steps, warnings);
        return Plan;
    }

    internal static uint PreferredCurrencyId(CustomDeliverySettings settings) =>
        (settings.JobType, settings.ScripPreference) switch
        {
            (CustomDeliveryJobType.Crafter, CustomDeliveryScripPreference.Orange) => OrangeCrafterScripId,
            (CustomDeliveryJobType.Crafter, CustomDeliveryScripPreference.Purple) => PurpleCrafterScripId,
            (CustomDeliveryJobType.Gatherer, CustomDeliveryScripPreference.Orange) => OrangeGathererScripId,
            (CustomDeliveryJobType.Gatherer, CustomDeliveryScripPreference.Purple) => PurpleGathererScripId,
            _ => 0,
        };

    internal static uint PurpleCurrencyId(CustomDeliveryJobType jobType) =>
        jobType == CustomDeliveryJobType.Crafter ? PurpleCrafterScripId : PurpleGathererScripId;

    internal static uint OrangeCurrencyId(CustomDeliveryJobType jobType) =>
        jobType == CustomDeliveryJobType.Crafter ? OrangeCrafterScripId : OrangeGathererScripId;

    internal static uint CurrencyCount(uint currencyId)
    {
        var currencies = CurrencyManager.Instance();
        return currencies == null || currencyId == 0 ? 0 : currencies->GetItemCount(currencyId);
    }

    internal static string ItemName(uint itemId)
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<Item>().GetRow(itemId).Name.ToString();
        }
        catch
        {
            return $"#{itemId}";
        }
    }

    internal static bool IsPluginLoaded(string internalName) =>
        Plugin.PluginInterface.InstalledPlugins.Any(installed => installed.IsLoaded &&
            (installed.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase) ||
             installed.Name.Equals(internalName, StringComparison.OrdinalIgnoreCase)));

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
            return;

        var now = DateTime.UtcNow;
        if (now - lastSnapshotUtc >= TimeSpan.FromSeconds(15))
        {
            lastSnapshotUtc = now;
            try
            {
                var manager = SatisfactionSupplyManager.Instance();
                var currencies = CurrencyManager.Instance();
                if (manager != null && currencies != null)
                    SaveCharacterSnapshot(manager, currencies);
            }
            catch (Exception exception)
            {
                Plugin.Log.Verbose(exception, "お得意様のキャラクター状況を更新できませんでした。");
            }
        }

        automation.Update(now);
    }

    private void SaveCharacterSnapshot(SatisfactionSupplyManager* manager, CurrencyManager* currencies)
    {
        var id = Plugin.PlayerState.ContentId;
        if (!plugin.Configuration.CustomDeliveryCharacters.TryGetValue(id, out var record))
        {
            record = new CustomDeliveryCharacterRecord { ContentId = id };
            plugin.Configuration.CustomDeliveryCharacters[id] = record;
        }

        var remaining = Math.Max(0, manager->GetRemainingAllowances());
        var purpleCrafter = currencies->GetItemCount(PurpleCrafterScripId);
        var purpleGatherer = currencies->GetItemCount(PurpleGathererScripId);
        var orangeCrafter = currencies->GetItemCount(OrangeCrafterScripId);
        var orangeGatherer = currencies->GetItemCount(OrangeGathererScripId);
        var changed = record.RemainingWeeklyAllowances != remaining ||
            record.PurpleCrafterScrip != purpleCrafter || record.PurpleGathererScrip != purpleGatherer ||
            record.OrangeCrafterScrip != orangeCrafter || record.OrangeGathererScrip != orangeGatherer ||
            string.IsNullOrWhiteSpace(record.CharacterName) || record.CharacterName == "不明";
        RemainingWeeklyAllowances = remaining;
        if (!changed)
            return;

        record.CharacterName = Plugin.PlayerState.CharacterName;
        record.WorldName = Plugin.PlayerState.HomeWorld.Value.Name.ToString();
        record.RemainingWeeklyAllowances = remaining;
        record.PurpleCrafterScrip = purpleCrafter;
        record.PurpleGathererScrip = purpleGatherer;
        record.OrangeCrafterScrip = orangeCrafter;
        record.OrangeGathererScrip = orangeGatherer;
        record.UpdatedAt = DateTime.Now;
        plugin.Configuration.Save();
    }

    private void LoadExchangeCatalog()
    {
        exchangeCatalogLoaded = true;
        var seen = new HashSet<(uint Item, uint Currency)>();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var inclusionSheet = Plugin.DataManager.GetExcelSheet<InclusionShop>();
        var seriesSheet = Plugin.DataManager.GetSubrowExcelSheet<InclusionShopSeries>();
        var accessibleShops = new HashSet<uint>();
        foreach (var inclusion in inclusionSheet)
        {
            foreach (var category in inclusion.Category)
            {
                if (category.RowId == 0 ||
                    !seriesSheet.TryGetRow(category.Value.InclusionShopSeries.RowId, out var series))
                    continue;
                for (var index = 0; index < series.Count; index++)
                    accessibleShops.Add(series[index].SpecialShop.RowId);
            }
        }

        foreach (var shop in Plugin.DataManager.GetExcelSheet<SpecialShop>()
                     .OrderByDescending(shop => accessibleShops.Contains(shop.RowId)))
        {
            foreach (var merchandise in shop.Item)
            {
                foreach (var payment in merchandise.ItemCosts)
                {
                    var currency = NormalizeCurrency(payment.ItemCost.RowId);
                    if (currency == 0 || payment.CurrencyCost == 0)
                        continue;
                    foreach (var received in merchandise.ReceiveItems)
                    {
                        var id = received.Item.RowId;
                        if (id == 0 || !seen.Add((id, currency)) || !itemSheet.TryGetRow(id, out var item))
                            continue;
                        exchangeItems.Add(new ScripExchangeItem(id, item.Name.ToString(), currency,
                            (int)payment.CurrencyCost, shop.RowId));
                    }
                }
            }
        }

        exchangeItems.Sort((left, right) => string.Compare(left.Name, right.Name,
            StringComparison.CurrentCultureIgnoreCase));
    }

    private static uint NormalizeCurrency(uint value) => value switch
    {
        2 or PurpleCrafterScripId => PurpleCrafterScripId,
        4 or PurpleGathererScripId => PurpleGathererScripId,
        6 or OrangeCrafterScripId => OrangeCrafterScripId,
        7 or OrangeGathererScripId => OrangeGathererScripId,
        _ => 0,
    };

    private static int Score(CustomDeliveryRequest request, CustomDeliverySettings settings, uint preferredCurrency)
    {
        if (settings.ScripPreference == CustomDeliveryScripPreference.HighestTotal || preferredCurrency == 0)
            return request.Rewards.Sum(reward => reward.Amount);
        return request.Rewards.FirstOrDefault(reward => reward.CurrencyItemId == preferredCurrency)?.Amount ?? 0;
    }

    private static int ResolveBonusRowId(SatisfactionSupplyManager* manager, uint rowCount)
    {
        if (manager->BonusGuaranteeRowId != byte.MaxValue)
            return manager->BonusGuaranteeRowId;
        if (rowCount == 0)
            return -1;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + manager->TimeAdjustmentForBonusGuarantee;
        var weeks = (now - 1657008000L) / 604800L;
        return (int)(weeks % rowCount);
    }

    private static int[] CalculateRequestedSubrows(
        Lumina.Excel.SubrowCollection<SatisfactionSupply> rows, uint supplyId, uint seed)
    {
        var result = new[] { -1, -1, -1 };
        var first = 0x03CEA65Cu * supplyId ^ 0x1A0DD20Eu * seed;
        var second = 0xDF585D5Du * supplyId ^ 0x3057656Eu * seed;
        var third = 0xED69E442u * supplyId ^ 0x2202EA5Au * seed;
        var fourth = 0xAEFC3901u * supplyId ^ 0xE70723F6u * seed;
        var previous = first;

        for (var slot = 1; slot <= 3; slot++)
        {
            var total = 0;
            for (var index = 0; index < rows.Count; index++)
                if (rows[index].Slot == slot)
                    total += rows[index].ProbabilityPercent;
            if (total <= 0)
                continue;

            var transformed = previous ^ previous << 11;
            first = third;
            third = fourth;
            previous = second;
            fourth ^= transformed ^ (transformed ^ fourth >> 11) >> 8;
            second = first;

            var roll = fourth % (uint)total;
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index].Slot != slot)
                    continue;
                if (roll < rows[index].ProbabilityPercent)
                {
                    result[slot - 1] = index;
                    break;
                }
                roll -= rows[index].ProbabilityPercent;
            }
        }

        return result;
    }

    public void Dispose()
    {
        automation.Stop();
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }
}

internal sealed record CustomDeliveryNpc(
    uint RowId,
    uint ResidentId,
    string Name,
    uint TerritoryId,
    Vector3 Position,
    int Rank,
    int RemainingAllowances,
    int WeeklyLimit,
    IReadOnlyList<CustomDeliveryRequest> Requests);

internal sealed record CustomDeliveryRequest(
    CustomDeliveryJobType JobType,
    int Slot,
    uint ItemId,
    string ItemName,
    bool HasBonus,
    ushort Collectability,
    IReadOnlyList<CustomDeliveryReward> Rewards);

internal sealed record CustomDeliveryReward(uint CurrencyItemId, int Amount);

internal sealed record ScripExchangeItem(
    uint ItemId,
    string Name,
    uint CurrencyItemId,
    int Cost,
    uint SpecialShopId);

internal enum CustomDeliveryStepKind
{
    BuyMaterials,
    CraftItems,
    GatherMaterials,
    TravelToClient,
    DeliverItems,
    ExchangeScrip,
}

internal sealed record CustomDeliveryPlanStep(
    CustomDeliveryStepKind Kind,
    CustomDeliveryNpc Npc,
    CustomDeliveryRequest Request,
    int Quantity,
    ScripExchangeItem? ExchangeItem = null);

internal sealed record CustomDeliveryPlan(
    DateTime CreatedAtUtc,
    CustomDeliveryJobType JobType,
    int WeeklyAllowances,
    IReadOnlyList<CustomDeliveryPlanStep> Steps,
    IReadOnlyList<string> Warnings)
{
    internal bool CanExecute => Steps.Count > 0 && Warnings.Count == 0;
    internal int PlannedDeliveries => Steps.Where(step =>
        step.Kind == CustomDeliveryStepKind.DeliverItems).Sum(step => step.Quantity);
}
