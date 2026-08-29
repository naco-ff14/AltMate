using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AltMate;

internal static unsafe class CrafterTransferExecutor
{
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
                if (slot->Quantity > line.Quantity)
                    continue; // Never exceed the reviewed plan in the one-stack test.

                var agent = AgentRetainer.Instance();
                if (agent == null || !agent->IsAgentActive())
                    return Fail("リテイナー所持品画面を開いてください。", "Open the retainer inventory window.");
                var quantity = (int)slot->Quantity;
                agent->HandleCallback((uint)slotIndex, type, 0, 0); // Retrieve the selected full stack.
                return new Result(true,
                    $"{line.ItemName}を{quantity}個取得する操作を送信しました。",
                    $"Requested withdrawal of {quantity} {line.ItemName}.");
            }
        }

        return Fail($"計画数量以下の{line.ItemName}スタックが見つかりません。部分取得は次段階で対応します。",
            $"No {line.ItemName} stack within the planned quantity was found. Partial withdrawal is not enabled yet.");
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

    private static Result Fail(string japanese, string english) => new(false, japanese, english);
}
