using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Numerics;

namespace AltMate;

internal sealed unsafe class CustomDeliveryAutomation
{
    private readonly Plugin plugin;
    private readonly CustomDeliveryService deliveries;
    private CustomDeliveryPlan? activePlan;
    private int currentStep;
    private int stepPhase;
    private int startingInventory;
    private int startingAllowances;
    private uint startingCurrency;
    private DateTime stepStartedUtc;
    private DateTime nextActionUtc;
    private DateTime lastTravelRequestUtc;
    private VendorLocation? currentVendor;

    internal bool IsRunning { get; private set; }
    internal string Status { get; private set; } = "待機中";
    internal int CurrentStepNumber => currentStep + 1;
    internal int TotalSteps => activePlan?.Steps.Count ?? 0;

    internal CustomDeliveryAutomation(Plugin plugin, CustomDeliveryService deliveries)
    {
        this.plugin = plugin;
        this.deliveries = deliveries;
    }

    internal bool Start(CustomDeliveryPlan plan)
    {
        if (IsRunning || !plan.CanExecute)
            return false;

        activePlan = plan;
        currentStep = 0;
        IsRunning = true;
        BeginStep();
        Plugin.ChatGui.Print(Loc.L("AltMate：お得意様取引の自動処理を開始しました。",
            "AltMate: Started automatic custom deliveries."));
        return true;
    }

    internal void Stop(string? reason = null)
    {
        if (!IsRunning && reason is null)
            return;

        IsRunning = false;
        Status = reason ?? Loc.L("停止済み", "Stopped");
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction();
        }
        catch
        {
            // Movement may already have stopped or vnavmesh may not be installed.
        }

        if (deliveries.IsQuestionableAvailable)
        {
            try
            {
                Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.Stop")
                    .InvokeFunc("AltMate");
            }
            catch
            {
                // Questionable may not currently own an active gathering task.
            }
        }
    }

    internal void Update(DateTime now)
    {
        if (!IsRunning || activePlan is null || now < nextActionUtc)
            return;
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
        {
            Stop(Loc.L("ログアウトしたため停止しました。", "Stopped because the character logged out."));
            return;
        }
        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.Unconscious])
        {
            Stop(Loc.L("戦闘または戦闘不能を検出したため停止しました。",
                "Stopped because combat or incapacitation was detected."));
            return;
        }
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
            return;

        nextActionUtc = now.AddMilliseconds(400);
        var step = activePlan.Steps[currentStep];
        var timeout = step.Kind switch
        {
            CustomDeliveryStepKind.GatherMaterials => TimeSpan.FromMinutes(20),
            CustomDeliveryStepKind.CraftItems => TimeSpan.FromMinutes(12),
            _ => TimeSpan.FromMinutes(5),
        };
        if (now - stepStartedUtc > timeout)
        {
            Stop(Loc.L($"処理が時間切れになりました：{Describe(step)}",
                $"The current operation timed out: {Describe(step)}"));
            return;
        }

        try
        {
            switch (step.Kind)
            {
                case CustomDeliveryStepKind.BuyMaterials:
                    UpdatePurchase(step, now);
                    break;
                case CustomDeliveryStepKind.CraftItems:
                    UpdateCrafting(step, now);
                    break;
                case CustomDeliveryStepKind.GatherMaterials:
                    UpdateGathering(step, now);
                    break;
                case CustomDeliveryStepKind.TravelToClient:
                    if (MoveTo(step.Npc.TerritoryId, step.Npc.Position, now))
                        CompleteStep();
                    break;
                case CustomDeliveryStepKind.DeliverItems:
                    UpdateDeliveries(step, now);
                    break;
                case CustomDeliveryStepKind.ExchangeScrip:
                    UpdateExchange(step, now);
                    break;
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "お得意様自動処理に失敗しました：{Step}", step.Kind);
            Stop(Loc.L($"自動処理に失敗しました：{exception.Message}",
                $"Automatic processing failed: {exception.Message}"));
        }
    }

    private void UpdatePurchase(CustomDeliveryPlanStep step, DateTime now)
    {
        var recipe = FindRecipe(step.Request.ItemId, plugin.Configuration.CustomDeliverySettings.CrafterJobId);
        if (recipe is null || recipe.Value.Ingredient.Count == 0)
        {
            Stop(Loc.L("選択したクラフターの製作レシピが見つかりません。",
                "No recipe exists for the selected crafting job."));
            return;
        }

        var materialId = recipe.Value.Ingredient[0].RowId;
        var needed = recipe.Value.AmountIngredient[0] * step.Quantity;
        var owned = InventoryCount(materialId, 0);
        if (owned >= needed)
        {
            CloseGilShop();
            CompleteStep();
            return;
        }

        currentVendor ??= FindGilVendor(step.Npc.TerritoryId, materialId);
        if (currentVendor is null)
        {
            Stop(Loc.L($"{CustomDeliveryService.ItemName(materialId)}を販売するNPCが見つかりません。",
                $"No vendor was found for {CustomDeliveryService.ItemName(materialId)}."));
            return;
        }

        if (!MoveTo(currentVendor.TerritoryId, currentVendor.Position, now))
            return;
        if (!IsGilShopOpen(currentVendor.ShopId))
        {
            Interact(currentVendor.ResidentId);
            nextActionUtc = now.AddSeconds(2);
            return;
        }

        if (!BuyGilShopItem(currentVendor.ShopId, materialId, needed - owned))
        {
            Stop(Loc.L("素材をショップから購入できませんでした。",
                "Unable to purchase crafting materials from the vendor."));
            return;
        }
        Status = Loc.L($"素材を購入中：{CustomDeliveryService.ItemName(materialId)} ×{needed - owned}",
            $"Buying materials: {CustomDeliveryService.ItemName(materialId)} ×{needed - owned}");
        nextActionUtc = now.AddSeconds(2);
    }

    private void UpdateCrafting(CustomDeliveryPlanStep step, DateTime now)
    {
        var owned = InventoryCount(step.Request.ItemId, 1);
        if (owned >= step.Quantity)
        {
            CompleteStep();
            return;
        }

        if (!EnsureGearset(plugin.Configuration.CustomDeliverySettings.CrafterJobId))
        {
            nextActionUtc = now.AddSeconds(2);
            return;
        }

        var busy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus").InvokeFunc();
        if (busy || Plugin.Condition[ConditionFlag.Crafting] ||
            Plugin.Condition[ConditionFlag.PreparingToCraft])
        {
            Status = Loc.L($"Artisanで製作中：{step.Request.ItemName} {owned}/{step.Quantity}",
                $"Crafting with Artisan: {step.Request.ItemName} {owned}/{step.Quantity}");
            return;
        }

        if (stepPhase > 0 && now - stepStartedUtc > TimeSpan.FromSeconds(8) && owned <= startingInventory)
        {
            Stop(Loc.L("Artisanが製作を開始できませんでした。素材と装備を確認してください。",
                "Artisan could not start crafting. Check materials and equipment."));
            return;
        }
        if (stepPhase > 0)
            return;

        var recipe = FindRecipe(step.Request.ItemId, plugin.Configuration.CustomDeliverySettings.CrafterJobId);
        if (recipe is null || recipe.Value.RowId > ushort.MaxValue)
        {
            Stop(Loc.L("Artisanに渡せる製作レシピが見つかりません。",
                "No Artisan-compatible crafting recipe was found."));
            return;
        }

        startingInventory = owned;
        Plugin.PluginInterface.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem")
            .InvokeAction((ushort)recipe.Value.RowId, step.Quantity - owned);
        stepPhase = 1;
        nextActionUtc = now.AddSeconds(2);
    }

    private void UpdateGathering(CustomDeliveryPlanStep step, DateTime now)
    {
        var owned = InventoryCount(step.Request.ItemId, (short)Math.Min(short.MaxValue, step.Request.Collectability));
        if (owned >= step.Quantity)
        {
            CompleteStep();
            return;
        }

        var running = Plugin.PluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning").InvokeFunc();
        if (running)
        {
            Status = Loc.L($"Questionableで採集中：{step.Request.ItemName} {owned}/{step.Quantity}",
                $"Gathering with Questionable: {step.Request.ItemName} {owned}/{step.Quantity}");
            return;
        }
        if (stepPhase > 0)
        {
            Stop(Loc.L("Questionableの採集が必要数に達する前に終了しました。",
                "Questionable stopped before gathering enough collectables."));
            return;
        }

        var accepted = Plugin.PluginInterface.GetIpcSubscriber<uint, uint, byte, int, ushort, bool>(
            "Questionable.StartGatheringComplex").InvokeFunc(step.Npc.ResidentId,
            step.Request.ItemId, (byte)plugin.Configuration.CustomDeliverySettings.GathererJobId,
            step.Quantity - owned, step.Request.Collectability);
        if (!accepted)
        {
            Stop(Loc.L("Questionableが採集依頼を受け付けませんでした。",
                "Questionable rejected the gathering request."));
            return;
        }

        stepPhase = 1;
        nextActionUtc = now.AddSeconds(2);
    }

    private void UpdateDeliveries(CustomDeliveryPlanStep step, DateTime now)
    {
        var manager = SatisfactionSupplyManager.Instance();
        if (manager == null)
            return;
        var remaining = Math.Max(0, manager->GetRemainingAllowances());
        if (startingAllowances - remaining >= step.Quantity || remaining <= 0 ||
            InventoryCount(step.Request.ItemId, 1) <= 0)
        {
            CompleteStep();
            return;
        }

        if (plugin.Configuration.CustomDeliverySettings.AutoExchangeEnabled &&
            activePlan?.PreferredCurrencyId is > 0 &&
            CustomDeliveryService.CurrencyCount(activePlan.PreferredCurrencyId) >=
                plugin.Configuration.CustomDeliverySettings.ExchangeThreshold)
        {
            Stop(Loc.L("スクリップ上限に近いため納品を停止しました。交換後に再計画してください。",
                "Delivery stopped near the scrip cap. Exchange scrips and rebuild the plan."));
            return;
        }

        if (TryConfirmNpcTrade(step.Request.ItemId, step.Request.Slot))
        {
            nextActionUtc = now.AddSeconds(2);
            return;
        }

        var supply = AgentSatisfactionSupply.Instance();
        if (supply == null || !supply->IsAgentActive() || supply->NpcInfo.Id != step.Npc.RowId)
        {
            if (TryProgressSelection())
            {
                nextActionUtc = now.AddSeconds(1);
                return;
            }
            Interact(step.Npc.ResidentId);
            nextActionUtc = now.AddSeconds(2);
            return;
        }

        AtkValue result = default;
        var values = stackalloc AtkValue[2];
        values[0].SetInt(1);
        values[1].SetInt(step.Request.Slot);
        supply->ReceiveEvent(&result, values, 2, 0);
        Status = Loc.L($"{step.Npc.Name}へ納品中：{startingAllowances - remaining}/{step.Quantity}",
            $"Delivering to {step.Npc.Name}: {startingAllowances - remaining}/{step.Quantity}");
        nextActionUtc = now.AddSeconds(1);
    }

    private void UpdateExchange(CustomDeliveryPlanStep step, DateTime now)
    {
        var item = step.ExchangeItem;
        if (item is null)
        {
            Stop(Loc.L("スクリップ交換アイテムが設定されていません。",
                "No scrip exchange item has been configured."));
            return;
        }

        var balance = CustomDeliveryService.CurrencyCount(item.CurrencyItemId);
        if (stepPhase > 0 && balance < startingCurrency)
        {
            CompleteStep();
            return;
        }
        if (balance < item.Cost)
        {
            CompleteStep();
            return;
        }

        if (TryConfirmExchangeDialog())
        {
            nextActionUtc = now.AddSeconds(2);
            return;
        }

        var shopAddress = Plugin.GameGui.GetAddonByName("InclusionShop").Address;
        if (shopAddress != nint.Zero)
        {
            if (TryPurchaseExchangeItem((AddonInclusionShop*)shopAddress, item,
                    Math.Max(1, (int)balance / item.Cost)))
            {
                stepPhase = 1;
                nextActionUtc = now.AddSeconds(1);
                return;
            }
            if (SelectExchangeCategory(item, now))
                return;
            Stop(Loc.L($"交換所で「{item.Name}」が見つかりません。交換カテゴリの開放状況を確認してください。",
                $"{item.Name} was not found in the exchange. Check unlocked shop categories."));
            return;
        }

        currentVendor ??= FindExchangeVendor(item.SpecialShopId, step.Npc.TerritoryId);
        if (currentVendor is null)
        {
            Stop(Loc.L("対応するスクリップ交換窓口が見つかりません。",
                "No compatible scrip exchange vendor was found."));
            return;
        }
        if (!MoveTo(currentVendor.TerritoryId, currentVendor.Position, now))
            return;
        if (TryProgressSelection())
        {
            nextActionUtc = now.AddSeconds(1);
            return;
        }
        Interact(currentVendor.ResidentId);
        nextActionUtc = now.AddSeconds(2);
    }

    private bool MoveTo(uint territoryId, Vector3 position, DateTime now)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local is null)
            return false;
        if (Plugin.ClientState.TerritoryType != territoryId)
        {
            if (IsLifestreamBusy() || now - lastTravelRequestUtc < TimeSpan.FromSeconds(8))
                return false;
            var destination = Plugin.DataManager.GetExcelSheet<Aetheryte>()
                .FirstOrDefault(aetheryte => aetheryte.IsAetheryte &&
                    aetheryte.Territory.RowId == territoryId).RowId;
            if (destination == 0)
            {
                Stop(Loc.L("目的地エリアへのエーテライトが見つかりません。",
                    "No aetheryte was found for the destination territory."));
                return false;
            }

            lastTravelRequestUtc = now;
            var accepted = Plugin.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport")
                .InvokeFunc(destination, 0);
            if (!accepted)
                Status = Loc.L("Lifestreamの移動受付を待機中", "Waiting for Lifestream to accept travel");
            else
                Status = Loc.L("Lifestreamで目的地へ移動中", "Travelling with Lifestream");
            return false;
        }

        var distance = Vector3.Distance(local.Position, position);
        if (distance <= 3.2f)
        {
            try
            {
                Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction();
            }
            catch
            {
                // The path may already have completed.
            }
            return true;
        }

        if (now - lastTravelRequestUtc < TimeSpan.FromSeconds(3))
            return false;
        if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc())
            return false;
        lastTravelRequestUtc = now;
        var started = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>(
            "vnavmesh.SimpleMove.PathfindAndMoveCloseTo").InvokeFunc(position, false, 2.8f);
        if (started)
            Status = Loc.L($"目的地へ移動中（残り{distance:0}m）",
                $"Walking to destination ({distance:0}m remaining)");
        return false;
    }

    private void BeginStep()
    {
        if (activePlan is null || currentStep >= activePlan.Steps.Count)
        {
            IsRunning = false;
            Status = Loc.L("お得意様取引が完了しました。", "Custom deliveries completed.");
            Plugin.ChatGui.Print($"AltMate：{Status}");
            deliveries.Refresh();
            return;
        }

        var step = activePlan.Steps[currentStep];
        stepStartedUtc = DateTime.UtcNow;
        nextActionUtc = stepStartedUtc;
        lastTravelRequestUtc = DateTime.MinValue;
        stepPhase = 0;
        currentVendor = null;
        startingInventory = InventoryCount(step.Request.ItemId, 1);
        var manager = SatisfactionSupplyManager.Instance();
        startingAllowances = manager == null ? 0 : manager->GetRemainingAllowances();
        startingCurrency = step.ExchangeItem is null ? 0 :
            CustomDeliveryService.CurrencyCount(step.ExchangeItem.CurrencyItemId);
        Status = Describe(step);
    }

    private void CompleteStep()
    {
        currentStep++;
        BeginStep();
    }

    private static string Describe(CustomDeliveryPlanStep step) => step.Kind switch
    {
        CustomDeliveryStepKind.BuyMaterials => Loc.L($"{step.Npc.Name}：素材購入", $"{step.Npc.Name}: buy materials"),
        CustomDeliveryStepKind.CraftItems => Loc.L($"{step.Npc.Name}：製作", $"{step.Npc.Name}: craft"),
        CustomDeliveryStepKind.GatherMaterials => Loc.L($"{step.Npc.Name}：採集", $"{step.Npc.Name}: gather"),
        CustomDeliveryStepKind.TravelToClient => Loc.L($"{step.Npc.Name}へ移動", $"Travel to {step.Npc.Name}"),
        CustomDeliveryStepKind.DeliverItems => Loc.L($"{step.Npc.Name}へ納品", $"Deliver to {step.Npc.Name}"),
        CustomDeliveryStepKind.ExchangeScrip => Loc.L("スクリップを交換", "Exchange scrips"),
        _ => Loc.L("待機中", "Idle"),
    };

    private static int InventoryCount(uint itemId, short minimumCollectability)
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetInventoryItemCount(itemId, false, false, false,
            minimumCollectability);
    }

    private static Recipe? FindRecipe(uint itemId, uint jobId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<RecipeLookup>();
        if (!sheet.TryGetRow(itemId, out var lookup))
            return null;
        return jobId switch
        {
            8 => lookup.CRP.Value,
            9 => lookup.BSM.Value,
            10 => lookup.ARM.Value,
            11 => lookup.GSM.Value,
            12 => lookup.LTW.Value,
            13 => lookup.WVR.Value,
            14 => lookup.ALC.Value,
            15 => lookup.CUL.Value,
            _ => null,
        };
    }

    private static bool EnsureGearset(uint jobId)
    {
        if (Plugin.PlayerState.ClassJob.RowId == jobId)
            return true;
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return false;
        for (var index = 0; index < 100; index++)
        {
            if (!module->IsValidGearset(index))
                continue;
            var gearset = module->GetGearset(index);
            if (gearset != null && gearset->ClassJob == jobId)
            {
                module->EquipGearset(index);
                return false;
            }
        }
        return false;
    }

    private static bool IsLifestreamBusy()
    {
        try
        {
            return Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    private static bool Interact(uint residentId)
    {
        var target = Plugin.ObjectTable.FirstOrDefault(obj => obj.DataId == residentId);
        if (target is null || target.Address == nint.Zero)
            return false;
        var system = TargetSystem.Instance();
        if (system == null)
            return false;
        system->InteractWithObject((GameObject*)target.Address, false);
        return true;
    }

    private static bool TryProgressSelection()
    {
        foreach (var name in new[] { "SelectIconString", "SelectString" })
        {
            var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
            if (addon == null || !addon->IsVisible || !addon->IsReady)
                continue;
            AtkValue choice = default;
            choice.SetInt(0);
            addon->FireCallback(1, &choice, true);
            return true;
        }
        return false;
    }

    private static bool TryConfirmNpcTrade(uint itemId, int slot)
    {
        var agent = AgentNpcTrade.Instance();
        var ui = UIState.Instance();
        if (agent == null || ui == null || !agent->IsAgentActive() ||
            ui->NpcTrade.Requests.Count != 1 || ui->NpcTrade.Requests.Items[0].ItemId != itemId)
            return false;

        AtkValue result = default;
        var arguments = stackalloc AtkValue[4];
        arguments[0].SetInt(2);
        arguments[1].SetInt(slot);
        arguments[2].SetInt(0);
        arguments[3].SetInt(0);
        agent->ReceiveEvent(&result, arguments, 4, 0);
        if (agent->SelectedTurnInSlot < 0)
            return false;
        arguments[0].SetInt(0);
        arguments[1].SetInt(0);
        agent->ReceiveEvent(&result, arguments, 4, 1);
        agent->ReceiveEvent(&result, arguments, 4, 0);
        return true;
    }

    private static bool IsGilShopOpen(uint shopId)
    {
        var agent = AgentShop.Instance();
        if (agent == null || !agent->IsAgentActive() || agent->EventReceiver == null)
            return false;
        var framework = EventFramework.Instance();
        if (framework == null || !framework->EventHandlerModule.EventHandlerMap.TryGetValuePointer(shopId,
                out var handler) || handler == null || handler->Value == null)
            return false;
        return ((ShopEventHandler.AgentProxy*)agent->EventReceiver)->Handler == handler->Value;
    }

    private static bool BuyGilShopItem(uint shopId, uint itemId, int quantity)
    {
        var framework = EventFramework.Instance();
        if (framework == null || !framework->EventHandlerModule.EventHandlerMap.TryGetValuePointer(shopId,
                out var handler) || handler == null || handler->Value == null)
            return false;
        var shop = (ShopEventHandler*)handler->Value;
        for (var visible = 0; visible < shop->VisibleItemsCount; visible++)
        {
            var itemIndex = shop->VisibleItems[visible];
            if (shop->Items[itemIndex].ItemId != itemId)
                continue;
            shop->BuyItemIndex = itemIndex;
            shop->ExecuteBuy(quantity);
            return true;
        }
        return false;
    }

    private static void CloseGilShop()
    {
        var agent = AgentShop.Instance();
        if (agent == null || !agent->IsAgentActive())
            return;
        AtkValue result = default;
        AtkValue close = default;
        close.SetInt(-1);
        agent->ReceiveEvent(&result, &close, 1, 0);
    }

    private static VendorLocation? FindGilVendor(uint territoryId, uint materialId)
    {
        var npcSheet = Plugin.DataManager.GetExcelSheet<ENpcBase>();
        var shops = Plugin.DataManager.GetSubrowExcelSheet<GilShopItem>();
        foreach (var level in Plugin.DataManager.GetExcelSheet<Level>())
        {
            if (level.Territory.RowId != territoryId || level.Object.RowId == 0 ||
                !npcSheet.TryGetRow(level.Object.RowId, out var npc))
                continue;
            foreach (var data in npc.ENpcData)
            {
                var shopId = data.RowId;
                if (shopId == 0 || shopId >> 16 != (uint)EventHandlerContent.Shop ||
                    !shops.TryGetRow(shopId, out var merchandise))
                    continue;
                for (var index = 0; index < merchandise.Count; index++)
                {
                    if (merchandise[index].Item.RowId == materialId)
                        return new VendorLocation(level.Object.RowId, territoryId,
                            new Vector3(level.X, level.Y, level.Z), shopId);
                }
            }
        }
        return null;
    }

    private static VendorLocation? FindExchangeVendor(uint specialShopId, uint preferredTerritory)
    {
        var inclusionSheet = Plugin.DataManager.GetExcelSheet<InclusionShop>();
        var seriesSheet = Plugin.DataManager.GetSubrowExcelSheet<InclusionShopSeries>();
        var candidateShopIds = inclusionSheet.Where(inclusion => inclusion.Category.Any(category =>
        {
            if (category.RowId == 0 || !seriesSheet.TryGetRow(category.Value.InclusionShopSeries.RowId,
                    out var series))
                return false;
            for (var index = 0; index < series.Count; index++)
                if (series[index].SpecialShop.RowId == specialShopId)
                    return true;
            return false;
        })).Select(shop => shop.RowId).ToHashSet();

        if (candidateShopIds.Count == 0)
            return null;
        var npcSheet = Plugin.DataManager.GetExcelSheet<ENpcBase>();
        VendorLocation? fallback = null;
        foreach (var level in Plugin.DataManager.GetExcelSheet<Level>())
        {
            if (level.Object.RowId == 0 || !npcSheet.TryGetRow(level.Object.RowId, out var npc))
                continue;
            if (!npc.ENpcData.Any(data => candidateShopIds.Contains(data.RowId)))
                continue;
            var found = new VendorLocation(level.Object.RowId, level.Territory.RowId,
                new Vector3(level.X, level.Y, level.Z), 0);
            if (level.Territory.RowId == preferredTerritory)
                return found;
            fallback ??= found;
        }
        return fallback;
    }

    private bool SelectExchangeCategory(ScripExchangeItem item, DateTime now)
    {
        var agent = AgentInclusionShop.Instance();
        if (agent == null || agent->Data == null)
            return false;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("InclusionShop").Address;
        if (addon == null)
            return false;
        var shopSheet = Plugin.DataManager.GetExcelSheet<InclusionShop>();
        var seriesSheet = Plugin.DataManager.GetSubrowExcelSheet<InclusionShopSeries>();
        if (!shopSheet.TryGetRow(agent->Data->InclusionShopId, out var shop))
            return false;

        for (var page = 0; page < shop.Category.Count; page++)
        {
            var category = shop.Category[page];
            if (category.RowId == 0 || !seriesSheet.TryGetRow(category.Value.InclusionShopSeries.RowId,
                    out var series))
                continue;
            for (var subpage = 0; subpage < series.Count; subpage++)
            {
                if (series[subpage].SpecialShop.RowId != item.SpecialShopId)
                    continue;
                if (agent->Data->SelectedCategoryIndex != page)
                {
                    var values = stackalloc AtkValue[2];
                    values[0].SetInt(12);
                    values[1].SetUInt((uint)page);
                    addon->FireCallback(2, values, true);
                }
                else
                {
                    var values = stackalloc AtkValue[2];
                    values[0].SetInt(13);
                    values[1].SetUInt((uint)subpage + 1);
                    addon->FireCallback(2, values, true);
                }
                nextActionUtc = now.AddSeconds(2);
                return true;
            }
        }
        return false;
    }

    private static bool TryPurchaseExchangeItem(AddonInclusionShop* addon, ScripExchangeItem item, int quantity)
    {
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->TypedAtkValues == null)
            return false;
        var values = addon->TypedAtkValues;
        var count = Math.Min(60, (int)values->ItemCount.UInt);
        for (var index = 0; index < count; index++)
        {
            if (values->Items[index].ItemId.UInt != item.ItemId)
                continue;
            var arguments = stackalloc AtkValue[3];
            arguments[0].SetInt(14);
            arguments[1].SetUInt((uint)index);
            arguments[2].SetUInt((uint)Math.Min(quantity, 99));
            addon->AtkUnitBase.FireCallback(3, arguments, true);
            return true;
        }
        return false;
    }

    private static bool TryConfirmExchangeDialog()
    {
        foreach (var name in new[] { "ShopExchangeItemDialog", "SelectYesno" })
        {
            var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
            if (addon == null || !addon->IsVisible || !addon->IsReady)
                continue;
            AtkValue confirm = default;
            confirm.SetInt(0);
            addon->FireCallback(1, &confirm, true);
            return true;
        }
        return false;
    }

    private sealed record VendorLocation(uint ResidentId, uint TerritoryId, Vector3 Position, uint ShopId);
}
