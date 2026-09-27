using System;
using System.Collections.Generic;

namespace AltMate;

public sealed partial class CharacterLinkCoordinator
{
    private readonly TemporaryBooleanSetting bmrForbidActions = new();
    private long nextBmrRestoreAt;
    public string BmrSettingStatus { get; private set; } = "BMRの既存設定を尊重";

    private bool? ReadBmrForbidActions()
    {
        // BMR and upstream BossMod share this channel name. Reject ambiguous providers.
        if (!IsPluginLoaded("BossModReborn") || IsPluginLoaded("BossMod")) return null;
        try
        {
            var lines = Plugin.PluginInterface
                .GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration")
                .InvokeFunc(new List<string> { "AIConfig", "ForbidActions" }, false);
            return lines.Count == 1 && bool.TryParse(lines[0], out var value) ? value : null;
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "BMRのForbidActionsを読み取れませんでした。");
            return null;
        }
    }

    private bool WriteBmrForbidActions(bool value)
    {
        if (!IsPluginLoaded("BossModReborn") || IsPluginLoaded("BossMod")) return false;
        try
        {
            var lines = Plugin.PluginInterface
                .GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration")
                .InvokeFunc(new List<string> { "AIConfig", "ForbidActions", value.ToString() }, true);
            return lines.Count == 0;
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "BMRのForbidActionsを更新できませんでした。");
            return false;
        }
    }

    private bool PrepareBmrActions(bool? desired)
    {
        if (bmrForbidActions.Pending && Environment.TickCount64 < nextBmrRestoreAt) return false;
        var success = bmrForbidActions.Acquire(desired, ReadBmrForbidActions, WriteBmrForbidActions);
        BmrSettingStatus = success
            ? desired is null ? "BMRの既存設定を維持" : $"ForbidActionsを一時的に{(desired.Value ? "ON" : "OFF")}"
            : "BMR設定の取得・適用・復元待ち（BMR連携開始を保留）";
        return success;
    }

    private void RestoreBmrActions()
    {
        if (!bmrForbidActions.Pending) return;
        if (!IsPluginLoaded("BossModReborn"))
        {
            bmrForbidActions.Forget();
            BmrSettingStatus = "BMR未読込：以前のセッションの設定は再適用しません";
            return;
        }
        if (Environment.TickCount64 < nextBmrRestoreAt) return;
        var restored = bmrForbidActions.Release(ReadBmrForbidActions, WriteBmrForbidActions);
        nextBmrRestoreAt = restored ? 0 : Environment.TickCount64 + 2000;
        BmrSettingStatus = restored
            ? "BMR設定を復元済み（手動変更がある場合は維持）"
            : "BMR設定を復元できません。接続を確認してください";
    }
}
