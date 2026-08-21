using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Numerics;

namespace AltMate;

public sealed partial class MainWindow
{
    private string customDeliveryExchangeFilter = string.Empty;

    private void DrawCustomDeliveries()
    {
        DrawPageTitle(Loc.L("お得意様取引", "Custom Deliveries"), Loc.L(
            "開放済みのお得意様とボーナスを比較し、実行前に納品・交換計画を確認します。",
            "Compare unlocked clients and bonuses, then review the delivery and exchange plan before starting."));

        var service = plugin.CustomDeliveries;
        DrawDeliveryDependencies(service);
        ImGui.Spacing();
        DrawDeliverySettings(service);
        ImGui.Spacing();

        if (ImGui.Button(Loc.L("NPC情報を更新", "Refresh clients")))
            service.Refresh();
        ImGui.SameLine();
        if (ImGui.Button(Loc.L("実行計画を作成", "Build execution plan")))
            service.BuildPlan();
        ImGui.SameLine();
        ImGui.TextDisabled(Loc.L($"今週の残り納品回数：{service.RemainingWeeklyAllowances}/12",
            $"Weekly deliveries remaining: {service.RemainingWeeklyAllowances}/12"));

        if (!string.IsNullOrWhiteSpace(service.LastError))
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.35f, 1f), service.LastError);

        ImGui.Spacing();
        if (ImGui.BeginTabBar("custom-delivery-tabs"))
        {
            if (ImGui.BeginTabItem(Loc.L("実行計画", "Execution Plan")))
            {
                DrawCustomDeliveryPlan(service);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Loc.L("お得意様一覧", "Clients")))
            {
                DrawCustomDeliveryClients(service);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Loc.L("キャラクター状況", "Characters")))
            {
                DrawCustomDeliveryCharacters();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawDeliveryDependencies(CustomDeliveryService service)
    {
        DrawDependency("Lifestream", service.IsLifestreamAvailable);
        ImGui.SameLine();
        DrawDependency("vnavmesh", service.IsVnavmeshAvailable);
        ImGui.SameLine();
        DrawDependency("Artisan", service.IsArtisanAvailable);
        ImGui.SameLine();
        DrawDependency("Questionable", service.IsQuestionableAvailable);
    }

    private static void DrawDependency(string name, bool connected)
    {
        ImGui.TextColored(connected
            ? new Vector4(0.38f, 0.88f, 0.52f, 1f)
            : new Vector4(0.9f, 0.47f, 0.37f, 1f),
            $"{name}: {(connected ? Loc.L("接続済", "Ready") : Loc.L("未接続", "Missing"))}");
    }

    private void DrawDeliverySettings(CustomDeliveryService service)
    {
        if (!ImGui.CollapsingHeader(Loc.L("自動処理の設定", "Automation Settings"),
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var settings = plugin.Configuration.CustomDeliverySettings;
        var changed = false;
        var jobType = (int)settings.JobType;
        var jobLabels = Loc.IsEnglish ? new[] { "Crafter", "Gatherer" } : new[] { "クラフター", "ギャザラー" };
        ImGui.SetNextItemWidth(190 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo(Loc.L("納品タイプ", "Delivery type"), ref jobType, jobLabels, jobLabels.Length))
        {
            settings.JobType = (CustomDeliveryJobType)jobType;
            changed = true;
        }

        DrawSelectedJob(settings, ref changed);

        var priority = (int)settings.ScripPreference;
        var priorityLabels = Loc.IsEnglish
            ? new[] { "Orange scrips", "Purple scrips", "Highest total" }
            : new[] { "橙貨優先", "紫貨優先", "獲得総量優先" };
        ImGui.SetNextItemWidth(190 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo(Loc.L("優先スクリップ", "Scrip priority"), ref priority,
                priorityLabels, priorityLabels.Length))
        {
            settings.ScripPreference = (CustomDeliveryScripPreference)priority;
            settings.ExchangeItemId = 0;
            changed = true;
        }

        var selectedNpc = settings.PreferredNpcId == 0
            ? Loc.L("自動選択（効率順）", "Automatic (best reward first)")
            : service.Npcs.FirstOrDefault(npc => npc.RowId == settings.PreferredNpcId)?.Name ??
              Loc.L("選択中のNPC", "Selected client");
        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo(Loc.L("対象のお得意様", "Preferred client"), selectedNpc))
        {
            if (ImGui.Selectable(Loc.L("自動選択（効率順）", "Automatic (best reward first)"),
                    settings.PreferredNpcId == 0))
            {
                settings.PreferredNpcId = 0;
                changed = true;
            }
            foreach (var npc in service.Npcs)
            {
                if (ImGui.Selectable($"{npc.Name}##delivery-npc-{npc.RowId}",
                        settings.PreferredNpcId == npc.RowId))
                {
                    settings.PreferredNpcId = npc.RowId;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }

        var prioritizeBonus = settings.PrioritizeBonus;
        if (ImGui.Checkbox(Loc.L("ボーナス対象を優先", "Prioritize bonus deliveries"), ref prioritizeBonus))
        {
            settings.PrioritizeBonus = prioritizeBonus;
            changed = true;
        }
        var weeklyLimit = settings.RunUntilWeeklyLimit;
        if (ImGui.Checkbox(Loc.L("週間上限まで続ける", "Continue until the weekly limit"), ref weeklyLimit))
        {
            settings.RunUntilWeeklyLimit = weeklyLimit;
            changed = true;
        }
        var exchange = settings.AutoExchangeEnabled;
        if (ImGui.Checkbox(Loc.L("スクリップを自動交換", "Automatically exchange scrips"), ref exchange))
        {
            settings.AutoExchangeEnabled = exchange;
            changed = true;
        }

        if (settings.AutoExchangeEnabled)
            DrawExchangeSettings(service, settings, ref changed);

        if (changed)
            plugin.SaveSharedSettings();
    }

    private static void DrawSelectedJob(CustomDeliverySettings settings, ref bool changed)
    {
        var crafting = settings.JobType == CustomDeliveryJobType.Crafter;
        var selectedId = crafting ? settings.CrafterJobId : settings.GathererJobId;
        var firstId = crafting ? 8u : 16u;
        var lastId = crafting ? 15u : 17u;
        var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var selectedName = sheet.TryGetRow(selectedId, out var current)
            ? current.Name.ToString()
            : $"#{selectedId}";
        ImGui.SetNextItemWidth(190 * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginCombo(Loc.L("使用ジョブ", "Selected job"), selectedName))
            return;

        for (var jobId = firstId; jobId <= lastId; jobId++)
        {
            if (!sheet.TryGetRow(jobId, out var job))
                continue;
            if (!ImGui.Selectable($"{job.Name}##delivery-job-{jobId}", jobId == selectedId))
                continue;
            if (crafting)
                settings.CrafterJobId = jobId;
            else
                settings.GathererJobId = jobId;
            changed = true;
        }
        ImGui.EndCombo();
    }

    private void DrawExchangeSettings(CustomDeliveryService service, CustomDeliverySettings settings,
        ref bool changed)
    {
        var threshold = settings.ExchangeThreshold;
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt(Loc.L("交換開始の所持数", "Exchange threshold"), ref threshold, 500, 3900))
        {
            settings.ExchangeThreshold = threshold;
            changed = true;
        }

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##delivery-exchange-filter", Loc.L("交換アイテムを検索", "Search exchange items"),
            ref customDeliveryExchangeFilter, 100);

        var currency = CustomDeliveryService.PreferredCurrencyId(settings);
        var selected = service.ExchangeItems.FirstOrDefault(item => item.ItemId == settings.ExchangeItemId &&
            (currency == 0 || item.CurrencyItemId == currency));
        var label = selected is null
            ? Loc.L("交換アイテムを選択", "Choose an exchange item")
            : $"{selected.Name} ({selected.Cost})";
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginCombo(Loc.L("交換アイテム", "Exchange item"), label))
            return;

        var filter = customDeliveryExchangeFilter.Trim();
        var matches = service.ExchangeItems
            .Where(item => (currency == 0 || item.CurrencyItemId == currency) &&
                (filter.Length == 0 || item.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
            .Take(filter.Length == 0 ? 80 : 250);
        foreach (var item in matches)
        {
            if (!ImGui.Selectable($"{item.Name} ({item.Cost})##exchange-{item.ItemId}-{item.CurrencyItemId}",
                    settings.ExchangeItemId == item.ItemId))
                continue;
            settings.ExchangeItemId = item.ItemId;
            changed = true;
        }
        ImGui.EndCombo();
    }

    private void DrawCustomDeliveryPlan(CustomDeliveryService service)
    {
        var plan = service.Plan;
        if (plan is null)
        {
            ImGui.TextDisabled(Loc.L("「実行計画を作成」を押すと、移動・製作・納品・交換の順序を表示します。",
                "Select Build execution plan to preview travel, crafting, gathering, delivery, and exchange."));
            return;
        }

        foreach (var warning in plan.Warnings)
            ImGui.TextColored(new Vector4(1f, 0.52f, 0.32f, 1f), warning);
        ImGui.TextUnformatted(Loc.L($"予定納品：{plan.PlannedDeliveries}回 / 今週残り：{plan.WeeklyAllowances}回",
            $"Planned deliveries: {plan.PlannedDeliveries} / Weekly remaining: {plan.WeeklyAllowances}"));

        if (ImGui.BeginTable("delivery-plan", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                new Vector2(0, 220 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn(Loc.L("行動", "Action"), ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("NPC", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.L("内容", "Details"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.L("報酬", "Reward"), ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableHeadersRow();
            for (var index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted((index + 1).ToString());
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(DeliveryStepLabel(step.Kind));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(step.Npc.Name);
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(step.ExchangeItem is { } exchange
                    ? $"{exchange.Name} ({exchange.Cost})"
                    : $"{step.Request.ItemName}{(step.Quantity > 0 ? $" ×{step.Quantity}" : string.Empty)}");
                ImGui.TableSetColumnIndex(4);
                var reward = step.Kind == CustomDeliveryStepKind.DeliverItems
                    ? string.Join(" / ", step.Request.Rewards.Select(value => $"+{value.Amount * step.Quantity}"))
                    : "—";
                if (step.Request.HasBonus && step.Kind == CustomDeliveryStepKind.DeliverItems)
                    reward += Loc.L(" ★", " ★");
                ImGui.TextUnformatted(reward);
            }
            ImGui.EndTable();
        }

        var automation = service.Automation;
        ImGui.BeginDisabled(!plan.CanExecute || automation.IsRunning);
        if (ImGui.Button(Loc.L("この計画で実行開始", "Start this plan")))
            automation.Start(plan);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!automation.IsRunning);
        if (ImGui.Button(Loc.L("停止", "Stop")))
            automation.Stop();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextColored(automation.IsRunning
            ? new Vector4(0.42f, 0.82f, 1f, 1f)
            : new Vector4(0.7f, 0.72f, 0.75f, 1f),
            automation.IsRunning
                ? $"[{automation.CurrentStepNumber}/{automation.TotalSteps}] {automation.Status}"
                : automation.Status);
    }

    private static string DeliveryStepLabel(CustomDeliveryStepKind kind) => kind switch
    {
        CustomDeliveryStepKind.BuyMaterials => Loc.L("素材購入", "Buy"),
        CustomDeliveryStepKind.CraftItems => Loc.L("製作", "Craft"),
        CustomDeliveryStepKind.GatherMaterials => Loc.L("採集", "Gather"),
        CustomDeliveryStepKind.TravelToClient => Loc.L("移動", "Travel"),
        CustomDeliveryStepKind.DeliverItems => Loc.L("納品", "Deliver"),
        CustomDeliveryStepKind.ExchangeScrip => Loc.L("交換", "Exchange"),
        _ => "—",
    };

    private static void DrawCustomDeliveryClients(CustomDeliveryService service)
    {
        if (service.Npcs.Count == 0)
        {
            ImGui.TextDisabled(Loc.L("「NPC情報を更新」で開放済みのお得意様を取得してください。",
                "Refresh clients to load unlocked custom delivery clients."));
            return;
        }

        if (!ImGui.BeginTable("delivery-clients", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                new Vector2(0, 250 * ImGuiHelpers.GlobalScale)))
            return;
        ImGui.TableSetupColumn("NPC", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Loc.L("残り", "Left"), ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn(Loc.L("クラフター", "Crafter"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Loc.L("ギャザラー", "Gatherer"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Loc.L("ボーナス", "Bonus"), ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableHeadersRow();
        foreach (var npc in service.Npcs)
        {
            var craft = npc.Requests.FirstOrDefault(request => request.JobType == CustomDeliveryJobType.Crafter);
            var gather = npc.Requests.FirstOrDefault(request => request.JobType == CustomDeliveryJobType.Gatherer);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(npc.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{npc.RemainingAllowances}/{npc.WeeklyLimit}");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(craft?.ItemName ?? "—");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(gather?.ItemName ?? "—");
            ImGui.TableSetColumnIndex(4);
            var bonus = string.Join(" / ", npc.Requests.Where(request => request.HasBonus)
                .Select(request => request.JobType == CustomDeliveryJobType.Crafter
                    ? Loc.L("クラ", "CRP") : Loc.L("ギャザ", "GAT")));
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(bonus) ? "—" : $"★ {bonus}");
        }
        ImGui.EndTable();
    }

    private void DrawCustomDeliveryCharacters()
    {
        var characters = plugin.Configuration.CustomDeliveryCharacters.Values
            .OrderBy(record => record.CharacterName).ThenBy(record => record.WorldName).ToArray();
        if (characters.Length == 0)
        {
            ImGui.TextDisabled(Loc.L("ログインしたキャラクターのお得意様状況がここに表示されます。",
                "Custom delivery status appears here after logging in with each character."));
            return;
        }

        if (!ImGui.BeginTable("delivery-characters", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                new Vector2(0, 250 * ImGuiHelpers.GlobalScale)))
            return;
        ImGui.TableSetupColumn(Loc.L("キャラクター", "Character"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Loc.L("残り", "Left"), ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn(Loc.L("クラ紫", "CRP Purple"), ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn(Loc.L("クラ橙", "CRP Orange"), ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn(Loc.L("ギャザ紫", "GAT Purple"), ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn(Loc.L("ギャザ橙", "GAT Orange"), ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableHeadersRow();
        foreach (var record in characters)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted($"{record.CharacterName} @ {record.WorldName}");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(record.RemainingWeeklyAllowances.ToString());
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(record.PurpleCrafterScrip.ToString("N0"));
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(record.OrangeCrafterScrip.ToString("N0"));
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(record.PurpleGathererScrip.ToString("N0"));
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(record.OrangeGathererScrip.ToString("N0"));
        }
        ImGui.EndTable();
    }
}
