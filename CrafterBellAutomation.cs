using Dalamud.Game.ClientState.Conditions;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AltMate;

internal static unsafe class CrafterBellAutomation
{
    private enum Step { Idle, OpenBell, SelectRetainer, OpenInventory, Withdraw, CloseInventory, QuitRetainer }
    private sealed record RetainerWork(ulong Id, string Name, IReadOnlyList<CrafterTransferLine> Lines);

    private static readonly Queue<RetainerWork> work = new();
    private static CrafterBellRegistration bell = new();
    private static Step step;
    private static DateTime deadlineUtc;
    private static DateTime nextActionUtc;
    private static bool actionSent;

    internal static bool IsRunning => step != Step.Idle;
    internal static string StatusJapanese { get; private set; } = string.Empty;
    internal static string StatusEnglish { get; private set; } = string.Empty;
    internal static bool StatusIsError { get; private set; }

    internal static CrafterTransferExecutor.Result Begin(CrafterLevelingSettings settings,
        IReadOnlyList<CrafterTransferLine> lines)
    {
        if (IsRunning || CrafterTransferExecutor.IsRunning)
            return Fail("取得処理はすでに実行中です。", "A withdrawal operation is already running.");
        if (!settings.Bell.IsRegistered || settings.Bell.TerritoryId != Plugin.ClientState.TerritoryType ||
            Plugin.ObjectTable.LocalPlayer is not { } local ||
            Vector3.Distance(local.Position, new(settings.Bell.X, settings.Bell.Y, settings.Bell.Z)) > 6f)
            return Fail("登録したリテイナーベルの近くで開始してください。",
                "Start while near the registered summoning bell.");
        if (Plugin.Condition[ConditionFlag.InCombat])
            return Fail("戦闘中は開始できません。", "Cannot start while in combat.");

        work.Clear();
        foreach (var group in lines.Where(x => x.Quantity > 0).GroupBy(x => new { x.RetainerId, x.RetainerName }))
            work.Enqueue(new RetainerWork(group.Key.RetainerId, group.Key.RetainerName, group.ToArray()));
        if (work.Count == 0) return Fail("取得対象がありません。", "There are no planned withdrawals.");

        bell = settings.Bell;
        step = Step.OpenBell;
        actionSent = false;
        SetDeadline(15);
        SetStatus(false, $"ベルから{work.Count}人のリテイナー取得を開始します。",
            $"Starting automatic withdrawal from {work.Count} retainers.");
        return new(true, StatusJapanese, StatusEnglish);
    }

    internal static void Update()
    {
        if (!IsRunning || DateTime.UtcNow < nextActionUtc) return;
        if (Plugin.Condition[ConditionFlag.InCombat]) { Stop(true, "戦闘状態になったため停止しました。", "Stopped because combat began."); return; }
        if (DateTime.UtcNow >= deadlineUtc) { Stop(true, "画面遷移が15秒以内に完了しなかったため停止しました。", "Stopped because the UI transition timed out."); return; }
        try
        {
            switch (step)
            {
                case Step.OpenBell: OpenBell(); break;
                case Step.SelectRetainer: SelectRetainer(); break;
                case Step.OpenInventory: OpenInventory(); break;
                case Step.Withdraw: WaitForWithdrawal(); break;
                case Step.CloseInventory: CloseInventory(); break;
                case Step.QuitRetainer: QuitRetainer(); break;
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "リテイナーベル自動取得に失敗しました。");
            Stop(true, "予期しないエラーで停止しました。", "Stopped because of an unexpected error.");
        }
    }

    private static void OpenBell()
    {
        if (GetAddon("RetainerList") != null) { MoveTo(Step.SelectRetainer); return; }
        if (actionSent) return;
        var position = new Vector3(bell.X, bell.Y, bell.Z);
        var target = Plugin.ObjectTable.FirstOrDefault(x => x.BaseId == bell.ObjectId && x.IsTargetable &&
            Vector3.Distance(x.Position, position) <= 2f);
        if (target is null) { Stop(true, "登録したベルを現在地で確認できません。", "The registered bell was not found nearby."); return; }
        if (Plugin.ObjectTable.LocalPlayer is not { } local || Vector3.Distance(local.Position, target.Position) > 6f)
        { Stop(true, "ベルから離れすぎています。", "You are too far from the bell."); return; }
        TargetSystem.Instance()->InteractWithObject((GameObject*)target.Address, false);
        actionSent = true;
        nextActionUtc = DateTime.UtcNow.AddMilliseconds(500);
        SetStatus(false, "リテイナーベルを操作しました。", "Interacted with the summoning bell.");
    }

    private static void SelectRetainer()
    {
        if (work.Count == 0)
        {
            if (GetAddon("RetainerList") == null) return;
            CloseRetainerList();
            Stop(false, "必要素材をすべて取得しました。", "All required items were withdrawn.");
            return;
        }
        var addon = GetAddon("RetainerList");
        if (addon == null) return;
        var wanted = work.Peek();
        var index = FindRetainerIndex(addon, wanted.Name);
        if (index < 0) { Stop(true, $"一覧に{wanted.Name}が見つかりません。", $"{wanted.Name} was not found in the retainer list."); return; }
        var values = stackalloc AtkValue[4];
        values[0].Type = AtkValueType.Int; values[0].Int = 2;
        values[1].Type = AtkValueType.UInt; values[1].UInt = (uint)index;
        values[2].Type = AtkValueType.Int; values[2].Int = 0;
        values[3].Type = AtkValueType.Int; values[3].Int = 0;
        addon->FireCallback(4, values, true);
        MoveTo(Step.OpenInventory);
        SetStatus(false, $"{wanted.Name}を呼び出しています。", $"Calling {wanted.Name}.");
    }

    private static void OpenInventory()
    {
        var wanted = work.Peek();
        var manager = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
        var active = manager == null ? null : manager->GetActiveRetainer();
        if (active == null || active->RetainerId != wanted.Id) return;
        var talk = GetAddon("Talk");
        if (talk != null)
        {
            var stage = AtkStage.Instance();
            if (stage == null) return;
            var click = new AtkEvent
            {
                Listener = &talk->AtkEventListener,
                Target = &stage->AtkEventTarget,
            };
            AtkEventData data = default;
            talk->ReceiveEvent(AtkEventType.MouseClick, 0, &click, &data);
            nextActionUtc = DateTime.UtcNow.AddMilliseconds(500);
            deadlineUtc = DateTime.UtcNow.AddSeconds(15);
            SetStatus(false, $"{wanted.Name}の会話を進めています。", $"Advancing {wanted.Name}'s dialogue.");
            return;
        }
        var addon = GetAddon("SelectString");
        if (addon == null) return;
        var inventoryText = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2378).Text.ToString();
        var index = FindSelectStringIndex(addon, inventoryText);
        if (index < 0) { Stop(true, "リテイナー所持品メニューを確認できません。", "The retainer inventory menu was not found."); return; }
        addon->FireCallbackInt(index);
        MoveTo(Step.Withdraw);
    }

    private static void WaitForWithdrawal()
    {
        if (CrafterTransferExecutor.IsRunning) { deadlineUtc = DateTime.UtcNow.AddSeconds(15); return; }
        if (!actionSent)
        {
            var result = CrafterTransferExecutor.BeginBatch(work.Peek().Lines);
            if (!result.Success) return;
            actionSent = true;
            deadlineUtc = DateTime.UtcNow.AddSeconds(15);
            return;
        }
        if (!CrafterTransferExecutor.LastRunSucceeded)
        { Stop(true, CrafterTransferExecutor.StatusJapanese, CrafterTransferExecutor.StatusEnglish); return; }
        MoveTo(Step.CloseInventory);
    }

    private static void CloseInventory()
    {
        var agent = AgentRetainer.Instance();
        if (agent != null && agent->IsAgentActive())
        {
            agent->AgentInterface.Hide();
            nextActionUtc = DateTime.UtcNow.AddMilliseconds(500);
            return;
        }
        MoveTo(Step.QuitRetainer);
    }

    private static void QuitRetainer()
    {
        var addon = GetAddon("SelectString");
        if (addon == null) return;
        var quitText = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2383).Text.ToString();
        var index = FindSelectStringIndex(addon, quitText);
        if (index < 0) { Stop(true, "リテイナー終了メニューを確認できません。", "The quit-retainer menu was not found."); return; }
        addon->FireCallbackInt(index);
        work.Dequeue();
        MoveTo(Step.SelectRetainer);
    }

    private static int FindRetainerIndex(AtkUnitBase* addon, string name)
    {
        if (addon->AtkValues == null || addon->AtkValuesCount < 13) return -1;
        for (var index = 0; index < 10; index++)
        {
            var offset = 3 + index * 10;
            if (offset + 8 >= addon->AtkValuesCount) break;
            var entryName = ReadString(addon->AtkValues + offset);
            var active = (addon->AtkValues + offset + 8)->Type == AtkValueType.Bool &&
                         (addon->AtkValues + offset + 8)->Byte != 0;
            if (active && string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private static int FindSelectStringIndex(AtkUnitBase* addon, string expected)
    {
        if (addon->AtkValues == null || addon->AtkValuesCount < 7 || addon->AtkValues[3].Type != AtkValueType.Int) return -1;
        var count = Math.Clamp(addon->AtkValues[3].Int, 0, 20);
        for (var index = 0; index < count && 7 + index < addon->AtkValuesCount; index++)
            if (string.Equals(ReadString(addon->AtkValues + 7 + index).Trim(), expected.Trim(),
                    StringComparison.CurrentCultureIgnoreCase)) return index;
        return -1;
    }

    private static string ReadString(AtkValue* value) =>
        value->Type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString
            ? MemoryHelper.ReadSeStringNullTerminated((nint)value->String.Value).TextValue : string.Empty;

    private static AtkUnitBase* GetAddon(string name)
    {
        var address = Plugin.GameGui.GetAddonByName(name).Address;
        if (address == nint.Zero) return null;
        var addon = (AtkUnitBase*)address;
        return addon->IsVisible && addon->IsReady ? addon : null;
    }

    private static void CloseRetainerList()
    {
        var addon = GetAddon("RetainerList");
        if (addon == null) return;
        var value = stackalloc AtkValue[1]; value->Type = AtkValueType.Int; value->Int = -1;
        addon->FireCallback(1, value, true);
    }

    private static void MoveTo(Step next)
    {
        step = next; actionSent = false; nextActionUtc = DateTime.UtcNow.AddMilliseconds(500); SetDeadline(15);
    }
    private static void SetDeadline(int seconds) => deadlineUtc = DateTime.UtcNow.AddSeconds(seconds);
    private static void Stop(bool error, string japanese, string english)
    { step = Step.Idle; work.Clear(); SetStatus(error, japanese, english); }
    private static void SetStatus(bool error, string japanese, string english)
    { StatusIsError = error; StatusJapanese = japanese; StatusEnglish = english; }
    private static CrafterTransferExecutor.Result Fail(string japanese, string english) => new(false, japanese, english);
}
