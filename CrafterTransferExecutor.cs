using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal static unsafe class CrafterTransferExecutor
{
    private static uint pendingQuantity;
    private static readonly Queue<CrafterTransferLine> batch = new();
    private static ulong batchRetainerId;
    private static uint waitingItemId;
    private static int waitingCountBefore;
    private static DateTime waitingDeadlineUtc;
    private static DateTime nextActionUtc;
    internal static bool IsRunning { get; private set; }
    internal static string StatusJapanese { get; private set; } = string.Empty;
    internal static string StatusEnglish { get; private set; } = string.Empty;
    internal static bool StatusIsError { get; private set; }
    private static readonly InventoryType[] RetainerContainers =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    ];

    private static readonly InventoryType[] PlayerContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    internal sealed record Result(bool Success, string JapaneseMessage, string EnglishMessage);

    internal static Result BeginBatch(IReadOnlyList<CrafterTransferLine> lines)
    {
        if (IsRunning) return Fail("取得処理はすでに実行中です。", "A withdrawal batch is already running.");
        var manager = RetainerManager.Instance();
        var active = manager == null ? null : manager->GetActiveRetainer();
        if (active == null || active->RetainerId == 0)
            return Fail("対象リテイナーを開いてください。", "Open the target retainer first.");
        var selected = lines.Where(x => x.RetainerId == active->RetainerId && x.Quantity > 0).ToArray();
        if (selected.Length == 0)
            return Fail("現在開いているリテイナーの取得計画がありません。",
                "There are no withdrawals for the currently open retainer.");

        batch.Clear();
        foreach (var line in selected)
            batch.Enqueue(new CrafterTransferLine
            {
                RetainerId = line.RetainerId,
                RetainerName = line.RetainerName,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                Quantity = line.Quantity,
                IsGear = line.IsGear,
            });
        batchRetainerId = active->RetainerId;
        waitingItemId = 0;
        nextActionUtc = DateTime.UtcNow;
        IsRunning = true;
        SetStatus(false, $"{active->NameString}から{batch.Count}種類の取得を開始します。",
            $"Starting {batch.Count} withdrawals from {active->NameString}.");
        return new Result(true, StatusJapanese, StatusEnglish);
    }

    internal static void Update()
    {
        if (!IsRunning) return;
        var now = DateTime.UtcNow;
        var manager = RetainerManager.Instance();
        var active = manager == null ? null : manager->GetActiveRetainer();
        if (active == null || active->RetainerId != batchRetainerId)
        {
            Stop(true, "リテイナー画面が閉じたか、別のリテイナーに切り替わったため停止しました。",
                "Stopped because the retainer window closed or the active retainer changed.");
            return;
        }
        if (waitingItemId != 0)
        {
            var current = CountRetainerItem(waitingItemId);
            if (current < waitingCountBefore)
            {
                var moved = waitingCountBefore - current;
                waitingItemId = 0;
                if (batch.Count > 0)
                {
                    batch.Peek().Quantity -= moved;
                    if (batch.Peek().Quantity <= 0) batch.Dequeue();
                }
                nextActionUtc = now.AddMilliseconds(700);
                SetStatus(false, $"取得を確認しました。残り{batch.Count}種類。",
                    $"Withdrawal confirmed. {batch.Count} items remain.");
            }
            else if (now >= waitingDeadlineUtc)
                Stop(true, "10秒以内に在庫変化を確認できなかったため停止しました。",
                    "Stopped because no inventory change was detected within 10 seconds.");
            return;
        }
        if (batch.Count == 0)
        {
            Stop(false, "現在のリテイナー分をすべて取得しました。",
                "All planned items for the current retainer were withdrawn.");
            return;
        }
        if (now < nextActionUtc || pendingQuantity != 0) return;

        var line = batch.Peek();
        waitingCountBefore = CountRetainerItem(line.ItemId);
        var result = WithdrawOneStack(line);
        if (!result.Success)
        {
            Stop(true, result.JapaneseMessage, result.EnglishMessage);
            return;
        }
        waitingItemId = line.ItemId;
        waitingDeadlineUtc = now.AddSeconds(10);
        SetStatus(false, result.JapaneseMessage, result.EnglishMessage);
    }

    internal static Result WithdrawOneStack(CrafterTransferLine line)
    {
        var manager = InventoryManager.Instance();
        var retainers = RetainerManager.Instance();
        var active = retainers == null ? null : retainers->GetActiveRetainer();
        if (manager == null || active == null || active->RetainerId == 0)
            return Fail("対象リテイナーを開いてください。", "Open the target retainer first.");
        if (active->RetainerId != line.RetainerId)
            return Fail($"{line.RetainerName}を開いてください。", $"Open {line.RetainerName}.");
        if (!HasFreePlayerSlot(manager))
            return Fail("プレイヤー所持品に空き枠がありません。", "No free player inventory slot.");

        foreach (var type in RetainerContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                return Fail("リテイナー所持品の読み込みが完了していません。",
                    "Retainer inventory has not finished loading.");
            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId != line.ItemId || slot->Quantity == 0) continue;
                var agent = AgentRetainer.Instance();
                if (agent == null || !agent->IsAgentActive())
                    return Fail("リテイナー所持品画面を開いてください。", "Open the retainer inventory window.");
                var quantity = (int)slot->Quantity;
                if (quantity > line.Quantity)
                {
                    pendingQuantity = (uint)line.Quantity;
                    agent->HandleCallback((uint)slotIndex, type, 0, 3); // Open Retrieve Quantity.
                    return new Result(true,
                        $"{line.ItemName}の数量入力を{line.Quantity}個で開きました。内容を確認して決定してください。",
                        $"Opened quantity entry for {line.Quantity} {line.ItemName}. Verify and confirm it.");
                }
                agent->HandleCallback((uint)slotIndex, type, 0, 0); // Retrieve the selected full stack.
                return new Result(true,
                    $"{line.ItemName}を{quantity}個取得する操作を送信しました。",
                    $"Requested withdrawal of {quantity} {line.ItemName}.");
            }
        }

        return Fail($"{line.ItemName}が現在のリテイナー所持品に見つかりません。",
            $"{line.ItemName} was not found in the current retainer inventory.");
    }

    internal static void ApplyPendingQuantity(nint addonAddress)
    {
        if (pendingQuantity == 0 || addonAddress == nint.Zero) return;
        var addon = (AtkUnitBase*)addonAddress;
        if (addon->AtkValuesCount < 5 || addon->AtkValues == null) return;
        var minimum = addon->AtkValues + 2;
        var maximum = addon->AtkValues + 3;
        var defaultValue = addon->AtkValues + 4;
        if (minimum->Type != AtkValueType.UInt || maximum->Type != AtkValueType.UInt ||
            defaultValue->Type != AtkValueType.UInt) return;
        defaultValue->UInt = Math.Clamp(pendingQuantity, minimum->UInt, maximum->UInt);
    }

    internal static void ConfirmPendingQuantity(nint addonAddress)
    {
        if (pendingQuantity == 0 || addonAddress == nint.Zero) return;
        var addon = (AtkUnitBase*)addonAddress;
        if (addon->AtkValuesCount < 5 || addon->AtkValues == null) return;
        var value = addon->AtkValues + 4;
        if (value->Type != AtkValueType.UInt || value->UInt != pendingQuantity) return;
        var quantity = pendingQuantity;
        pendingQuantity = 0;
        addon->FireCallbackInt((int)quantity);
    }

    private static bool HasFreePlayerSlot(InventoryManager* manager)
    {
        foreach (var type in PlayerContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot != null && slot->ItemId == 0) return true;
            }
        }
        return false;
    }

    private static int CountRetainerItem(uint itemId)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return 0;
        var count = 0;
        foreach (var type in RetainerContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot != null && slot->ItemId == itemId) count += (int)slot->Quantity;
            }
        }
        return count;
    }

    private static void Stop(bool error, string japanese, string english)
    {
        IsRunning = false;
        pendingQuantity = 0;
        waitingItemId = 0;
        batch.Clear();
        SetStatus(error, japanese, english);
    }

    private static void SetStatus(bool error, string japanese, string english)
    {
        StatusIsError = error;
        StatusJapanese = japanese;
        StatusEnglish = english;
    }

    private static Result Fail(string japanese, string english) => new(false, japanese, english);
}
