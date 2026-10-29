using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace AltMate;

public sealed partial class MainWindow
{
    private string webSyncProjectUrl = "";
    private string webSyncPairingCode = "";
    private string webSyncInputError = "";

    private void DrawWebSyncSettings()
    {
        ImGui.Separator();
        ImGui.TextUnformatted(Loc.L("Webダッシュボード同期", "Web dashboard sync"));
        ImGui.TextWrapped(Loc.L(
            "キャラクター・住宅・資産などの記録を、自分のWebダッシュボードに送信します。",
            "Upload character, housing and asset observations to your private dashboard."));
        var enabled = plugin.Configuration.WebSyncEnabled;
        if (ImGui.Checkbox(Loc.L("Web同期を有効にする（このクライアント）", "Enable web sync (this client)"), ref enabled))
        {
            plugin.Configuration.WebSyncEnabled = enabled;
            plugin.Configuration.Save();
            plugin.WebSync.SetEnabled(enabled);
        }
        var state = plugin.WebSync.Status;
        ImGui.TextWrapped(WebSyncPhaseLabel(state.Phase));
        ImGui.TextDisabled(Loc.L("最終送信: ", "Last upload: ") + FormatWebSyncTime(state.LastSuccess));
        if (state.NextAttempt is { } next)
            ImGui.TextDisabled(Loc.L("次の確認: ", "Next check: ") + FormatWebSyncTime(next));
        if (state.HasPending)
            ImGui.TextWrapped(Loc.L("未送信の記録を暗号化して保存しています。再送時も記録は失われません。", "An encrypted upload is pending and will be retained for retry."));
        ImGui.BeginDisabled(!enabled || !state.Paired || state.Busy);
        if (ImGui.Button(Loc.L("今すぐ同期", "Sync now"))) plugin.WebSync.SyncNow();
        ImGui.EndDisabled();
        ImGui.TextWrapped(Loc.L(
            "送信は5分間隔です。複数起動時は1つだけが送信します。別アプリの同期ウィンドウは閉じてください。",
            "Uploads are spaced five minutes apart. One client sends for this PC. Close the standalone sync window."));

        if (!state.Paired)
        {
            ImGui.BeginDisabled(enabled || state.Busy);
            ImGui.SetNextItemWidth(440 * ImGuiHelpers.GlobalScale);
            ImGui.InputText(Loc.L("SupabaseのProject URL", "Supabase project URL"), ref webSyncProjectUrl, 256);
            ImGui.SetNextItemWidth(440 * ImGuiHelpers.GlobalScale);
            ImGui.InputText(Loc.L("Webで発行した連携コード", "One-time dashboard pairing code"), ref webSyncPairingCode, 128, ImGuiInputTextFlags.Password);
            if (ImGui.Button(Loc.L("このPCを連携", "Pair this PC")))
            {
                if (plugin.WebSync.Pair(webSyncProjectUrl.Trim(), webSyncPairingCode.Trim()))
                {
                    webSyncPairingCode = "";
                    webSyncInputError = "";
                }
                else webSyncInputError = Loc.L("URLと連携コードを確認し、同期をOFFにしてから再試行してください。", "Check the URL/code and turn sync off before retrying.");
            }
            ImGui.EndDisabled();
            if (webSyncInputError.Length > 0) ImGui.TextWrapped(webSyncInputError);
            ImGui.TextWrapped(Loc.L("初回のみWebの「同期端末」でコードを発行してください。アカウントのパスワードやAPIキーは入力しません。", "Generate a code under Sync devices in your dashboard. No account password or API key is needed."));
        }
        else
        {
            ImGui.TextWrapped(Loc.L("連携先: ", "Paired project: ") + state.ProjectUrl);
            ImGui.TextWrapped(Loc.L("同じWindowsユーザーで登録した既存のPC連携情報を利用します。", "Uses the existing device registration for this Windows user."));
            ImGui.BeginDisabled(enabled || state.Busy);
            if (ImGui.Button(Loc.L("PC側の連携情報を削除…", "Forget local registration…"))) ImGui.OpenPopup("web-sync-forget");
            ImGui.EndDisabled();
        }
        if (ImGui.BeginPopup("web-sync-forget"))
        {
            ImGui.TextWrapped(Loc.L("PC上の認証情報と送信待ち記録を削除します。Web側の端末登録と同期済みデータは残ります。再連携には新しいコードが必要です。", "Remove local credentials and queued data. Server registration and uploaded records remain. Re-pairing needs a new code."));
            ImGui.BeginDisabled(enabled || state.Busy);
            if (ImGui.Button(Loc.L("削除する", "Forget"))) { plugin.WebSync.Forget(); ImGui.CloseCurrentPopup(); }
            ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button(Loc.L("キャンセル", "Cancel"))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.Separator(); ImGui.Spacing();
    }
    private static string FormatWebSyncTime(DateTimeOffset? time) => time?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? Loc.L("未記録", "Not recorded");
    private static string WebSyncPhaseLabel(WebSyncPhase phase) => phase switch
    {
        WebSyncPhase.Initializing => Loc.L("連携情報を確認中…", "Checking registration…"),
        WebSyncPhase.Disabled => Loc.L("Web同期はOFFです。", "Web sync is off."),
        WebSyncPhase.Unpaired => Loc.L("このPCは未連携です。同期をOFFにして連携コードを入力してください。", "This PC is not paired. Turn sync off and enter a pairing code."),
        WebSyncPhase.WaitingOwner => Loc.L("別のクライアントまたは同期アプリが送信を担当しています。待機中です。", "Another client or the standalone agent owns sync. Waiting."),
        WebSyncPhase.WaitingInterval => Loc.L("次の送信可能時刻まで待機しています。", "Waiting for the next permitted upload time."),
        WebSyncPhase.Pairing => Loc.L("PCを連携中…", "Pairing this PC…"),
        WebSyncPhase.Paired => Loc.L("PC連携が完了しました。同期をONにすると送信を開始します。", "PC paired. Enable sync to start uploading."),
        WebSyncPhase.Uploading => Loc.L("同期中…", "Uploading…"),
        WebSyncPhase.Synced => Loc.L("同期が完了しました。", "Sync acknowledged."),
        WebSyncPhase.NoChanges => Loc.L("新しい観測記録はありません。", "No new observed data."),
        WebSyncPhase.RateLimited => Loc.L("送信間隔の制限により待機中です。5分後に再試行します。", "Upload interval limit. Retrying in five minutes."),
        WebSyncPhase.Retrying => Loc.L("接続または保存データを確認できません。記録を保持して再試行します。", "Connection/source temporarily unavailable. Retaining data for retry."),
        WebSyncPhase.Rejected => Loc.L("サーバーに拒否されました。連携情報とサーバー設定を確認してください。", "Server rejected the request. Check registration and deployment."),
        WebSyncPhase.Failed => Loc.L("同期を継続できません。PCの連携情報や保存先を確認してください。", "Sync cannot continue. Check local registration and storage."),
        _ => Loc.L("Web同期を停止しました。", "Web sync stopped."),
    };
}
