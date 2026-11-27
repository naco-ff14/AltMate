using System;
using System.Collections.Generic;

namespace AltMate;

public sealed partial class CharacterLinkCoordinator
{
    private readonly TemporaryBooleanSetting bmrForbidActions = new();
    private long nextBmrRestoreAt;
    private Guid bmrAiOperation;
    private Guid lastFollowerBmrAiOperation;
    private long bmrAiExpiresAt;
    private bool bmrAiEnabled;
    private long nextBmrSyncAt;
    private (ulong Leader, string? Preset, bool Disabled)? lastBmrPreset;
    public string BmrSettingStatus { get; private set; } = "BMRの既存設定を尊重";
    public string BmrAiControlStatus { get; private set; } = "";

    private bool CanSyncBmr => plugin.Configuration.SyncBmrControls &&
        plugin.Configuration.LinkEnabled && !runtimeStopped &&
        IsPluginLoaded("BossModReborn") && !IsPluginLoaded("BossMod");

    internal void SetLinkedBmrAi(bool enabled)
    {
        if (!IsLeader || !plugin.Configuration.SyncBmrControls) return;
        if (!plugin.Configuration.LinkEnabled || runtimeStopped)
        {
            BmrAiControlStatus = "先に連携操作を開始・再開してください。";
            return;
        }
        if (!IsPluginLoaded("BossModReborn") || IsPluginLoaded("BossMod"))
        {
            BmrAiControlStatus = "BossMod Rebornを有効にし、通常版BossModを無効にしてください。";
            return;
        }
        var command = enabled ? "/bmrai on" : "/bmrai off";
        if (!TryDispatchBmrAi(command))
        {
            BmrAiControlStatus = $"BMRが {command} を受け付けませんでした。";
            return;
        }
        bmrAiEnabled = enabled;
        bmrAiOperation = Guid.NewGuid();
        bmrAiExpiresAt = Environment.TickCount64 + 3000;
        BroadcastState();
        BmrAiControlStatus = $"リーダーで {command} を実行し、同期操作を予約しました。";
    }

    private void AddBmrControlsToState(LinkedCharacterState state)
    {
        if (!IsLeader || !CanSyncBmr) return;
        if (bmrAiOperation != Guid.Empty && Environment.TickCount64 < bmrAiExpiresAt)
        {
            state.BmrAiEnabled = bmrAiEnabled;
            state.BmrAiOperation = bmrAiOperation;
        }
        try
        {
            state.BmrPreset = Plugin.PluginInterface.GetIpcSubscriber<string?>("BossMod.Presets.GetActive").InvokeFunc();
            state.BmrForceDisabled = Plugin.PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.GetForceDisabled").InvokeFunc();
            state.BmrPresetKnown = true;
        }
        catch (Exception) { /* Older BMR versions may not expose these IPC calls. */ }
    }

    private void SyncLeaderBmrControls(LinkedCharacterState leader)
    {
        if (!CanSyncBmr)
        {
            lastBmrPreset = null;
            return;
        }
        if (Environment.TickCount64 < nextBmrSyncAt) return;
        nextBmrSyncAt = Environment.TickCount64 + 500;
        if (leader.BmrAiEnabled is bool enabled && leader.BmrAiOperation != Guid.Empty &&
            lastFollowerBmrAiOperation != leader.BmrAiOperation)
        {
            lastFollowerBmrAiOperation = leader.BmrAiOperation;
            var command = enabled ? "/bmrai on" : "/bmrai off";
            BmrAiControlStatus = TryDispatchBmrAi(command)
                ? $"リーダーから受信して {command} を実行しました。"
                : $"リーダーから受信しましたが、BMRが {command} を受け付けませんでした。";
        }
        if (!leader.BmrPresetKnown) { lastBmrPreset = null; return; }
        var desired = (leader.ContentId, leader.BmrPreset, leader.BmrForceDisabled);
        if (lastBmrPreset == desired) return;
        lastBmrPreset = desired;
        try
        {
            var ipc = Plugin.PluginInterface;
            var current = ipc.GetIpcSubscriber<string?>("BossMod.Presets.GetActive").InvokeFunc();
            var forceDisabled = ipc.GetIpcSubscriber<bool>("BossMod.Presets.GetForceDisabled").InvokeFunc();
            if (forceDisabled == leader.BmrForceDisabled &&
                (forceDisabled || string.Equals(current ?? "", leader.BmrPreset ?? "", StringComparison.Ordinal)))
            {
                BmrSettingStatus = "BMRプリセットは同期済み";
                return;
            }
            var applied = leader.BmrForceDisabled
                ? ipc.GetIpcSubscriber<bool>("BossMod.Presets.SetForceDisabled").InvokeFunc()
                : string.IsNullOrEmpty(leader.BmrPreset)
                    ? ipc.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive").InvokeFunc()
                    : ipc.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive").InvokeFunc(leader.BmrPreset);
            BmrSettingStatus = applied ? "BMRプリセットを同期しました" : "BMRプリセットを適用できません";
        }
        catch (Exception)
        {
            BmrSettingStatus = "BMRプリセット同期のIPCを利用できません";
        }
    }

    private static bool TryDispatchBmrAi(string command)
    {
        try { return Plugin.CommandManager.ProcessCommand(command); }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "BMR AI command failed.");
            return false;
        }
    }

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
