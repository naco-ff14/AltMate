using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace AltMate;

public sealed partial class MainWindow
{
    private string webSyncProjectUrl = "https://pdupjpgszabdtrirftvt.supabase.co";
    private string webSyncInputError = "";

    private void DrawWebSyncSettings()
    {
        ImGui.Separator();
        ImGui.TextUnformatted(Loc.L("Webダッシュボード同期", "Web dashboard sync"));
        ImGui.TextWrapped(Loc.L(
            "キャラクター・住宅・資産などの記録を、自分のWebダッシュボードに送信します。",
            "Upload character, housing and asset observations to your private dashboard."));
        var enabled = plugin.Configuration.WebSyncEnabled;
        ImGui.BeginDisabled(!plugin.WebSync.Status.Paired);
        if (ImGui.Checkbox(Loc.L("Web同期を有効にする（このクライアント）", "Enable web sync (this client)"), ref enabled))
        {
            plugin.Configuration.WebSyncEnabled = enabled;
            plugin.Configuration.Save();
            plugin.WebSync.SetEnabled(enabled);
        }
        ImGui.EndDisabled();
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
            ImGui.TextWrapped(Loc.L("コードをWebでログイン時に登録してください。承認後に同期が自動でONになります。", "Register this code when signing in on the web. Approval enables sync automatically."));
            if (state.PairingCode.Length > 0)
            {
                var code = state.PairingCode.Insert(6, "-");
                ImGui.TextUnformatted(Loc.L("連携コード: ", "Pairing code: ") + code);
                ImGui.TextDisabled(Loc.L("有効期限: ", "Expires: ") + FormatWebSyncTime(state.PairingExpiresAt));
                if (ImGui.Button(Loc.L("コードをコピー", "Copy code"))) ImGui.SetClipboardText(code);
                ImGui.SameLine();
                if (ImGui.Button(Loc.L("Webでログイン・連携", "Sign in and link on web")))
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://altmate-web.pages.dev/#link") { UseShellExecute = true }); }
                    catch { webSyncInputError = Loc.L("ブラウザーで altmate-web.pages.dev を開いてください。", "Open altmate-web.pages.dev in your browser."); }
                }
                ImGui.BeginDisabled(state.Busy);
                if (ImGui.Button(Loc.L("連携をキャンセル", "Cancel pairing"))) plugin.WebSync.Forget();
                ImGui.EndDisabled();
                ImGui.TextWrapped(Loc.L("自分で発行したコードだけを登録してください。他の人に共有しないでください。", "Register only a code you generated yourself. Do not share it."));
            }
            else
            {
                if (ImGui.CollapsingHeader(Loc.L("接続先の詳細設定", "Advanced connection settings")))
                {
                    ImGui.SetNextItemWidth(440 * ImGuiHelpers.GlobalScale);
                    ImGui.InputText(Loc.L("SupabaseのProject URL", "Supabase project URL"), ref webSyncProjectUrl, 256);
                }
                ImGui.BeginDisabled(enabled || state.Busy);
                if (ImGui.Button(Loc.L("Webと連携する（コード発行）", "Link web (generate code)")))
                    webSyncInputError = plugin.WebSync.BeginPluginPairing(webSyncProjectUrl.Trim()) ? "" : Loc.L("同期をOFFにし、接続先を確認してください。", "Turn sync off and check the project URL.");
                ImGui.EndDisabled();
            }
            if (webSyncInputError.Length > 0) ImGui.TextWrapped(webSyncInputError);
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
        WebSyncPhase.Unpaired => Loc.L("未連携です。「Webと連携する」でコードを発行してください。", "Not linked. Generate a code with Link web."),
        WebSyncPhase.WaitingOwner => Loc.L("別のクライアントまたは同期アプリが送信を担当しています。待機中です。", "Another client or the standalone agent owns sync. Waiting."),
        WebSyncPhase.WaitingInterval => Loc.L("次の送信可能時刻まで待機しています。", "Waiting for the next permitted upload time."),
        WebSyncPhase.Pairing => Loc.L("Webでの承認を待っています…", "Waiting for web approval…"),
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
