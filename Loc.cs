using System.Collections.Generic;

namespace AltMate;

internal static class Loc
{
    private static string Language => Plugin.CurrentConfiguration?.Language == "en" ? "en" : "ja";

    private static readonly Dictionary<string, (string Ja, string En)> Text = new()
    {
        ["Home"] = ("ホーム", "Home"),
        ["Link"] = ("連携操作", "Linked Controls"),
        ["Animation"] = ("アニメーション", "Animations"),
        ["Housing"] = ("ハウジング", "Housing"),
        ["Gil"] = ("ギル管理", "Gil Overview"),
        ["Settings"] = ("設定", "Settings"),
        ["DashboardDescription"] = ("複数キャラクターの状況をまとめたダッシュボードです。", "A dashboard summarizing all linked characters."),
        ["HousingSummary"] = ("ハウジング応募状況", "Housing Lottery"),
        ["LinkStatus"] = ("連携状態", "Link Status"),
        ["Leader"] = ("リーダー", "Leader"),
        ["Connected"] = ("接続中", "Connected"),
        ["EntryComplete"] = ("応募済み", "Entered"),
        ["NotEntered"] = ("未参加", "Not Entered"),
        ["Checked"] = ("結果確認済み", "Checked"),
        ["Unchecked"] = ("未確認", "Unchecked"),
        ["People"] = ("人", ""),
        ["TotalAssets"] = ("総資産", "Total Assets"),
        ["Available"] = ("使用可能", "Available"),
        ["LotteryDeposit"] = ("抽選預かり中", "Lottery Deposit"),
        ["Language"] = ("表示言語", "Language"),
        ["Japanese"] = ("日本語", "Japanese"),
        ["English"] = ("English", "English"),
        ["SettingsDescription"] = ("AltMate全体の設定です。", "General AltMate settings."),
        ["HousingDescription"] = ("抽選状況と、確認した空き土地を管理します。", "Manage lottery status and inspected open plots."),
        ["LinkDescription"] = ("同じPCで起動しているAltMate同士を検出し、リーダーへ追従します。", "Detect AltMate clients running on this PC and follow the leader."),
        ["AnimationDescription"] = ("Penumbraで差し替えたエモートを各キャラクターで再生します。", "Play Penumbra-replaced emotes on linked characters."),
        ["GilDescription"] = ("全キャラクター・リテイナー・FCチェストの最新確認額をまとめて表示します。", "Show the latest gil totals for characters, retainers, and company chests."),
        ["Command"] = ("コマンド", "Command"),
        ["LegacyCommand"] = ("/altmate（旧コマンド /hlt も使用可能）", "/altmate (legacy command /hlt is also available)"),
        ["DataStorage"] = ("データ保存", "Data Storage"),
        ["DataStorageDescription"] = ("設定とキャラクター情報は、このPCのAltMate共有設定へ保存されます。", "Settings and character data are stored in AltMate's shared data file on this PC."),
        ["Privacy"] = ("プライバシー", "Privacy"),
        ["PrivacyDescription"] = ("キャラクター名、Content ID、リテイナー名、FC ID、ギル情報をローカルに保存します。外部サーバーへ送信しません。", "Character names, Content IDs, retainer names, FC IDs, and gil data are stored locally and are never sent to an external server."),
        ["RestartNotRequired"] = ("変更はすぐに反映されます。", "Changes take effect immediately."),
        ["Minimize"] = ("最小化", "Minimize"),
        ["Maximize"] = ("最大化", "Maximize"),
        ["EmergencyStop"] = ("緊急停止", "Emergency Stop"),
        ["Status"] = ("状態", "Status"),
        ["LoadedVersion"] = ("読込バージョン", "Loaded version"),
        ["ResumeLink"] = ("連携操作を再開", "Resume linked controls"),
        ["EmergencyStopped"] = ("緊急停止中", "Emergency stopped"),
        ["StopAll"] = ("すべて緊急停止", "Emergency stop all"),
        ["EnableLink"] = ("連携操作を有効にする", "Enable linked controls"),
        ["SelectCharacter"] = ("選択してください", "Select a character"),
        ["ThisIsLeader"] = ("このキャラクターはリーダーです。", "This character is the leader."),
        ["ThisIsFollower"] = ("このキャラクターはフォロワーとして動作します。", "This character acts as a follower."),
        ["MoveToLeader"] = ("リーダーの元へ移動", "Move to leader"),
        ["DifferentWorldHelp"] = ("別ワールドでは自動連携を停止します。合流するときだけこのボタンを押してください。", "Automatic linking pauses in another World. Use this button only when you want to join the leader."),
        ["AutoFollow"] = ("フォロワーがリーダーを自動追従", "Follower automatically follows leader"),
        ["AutoRide"] = ("リーダーのマウントへ自動で相乗り", "Automatically ride leader's multi-seat mount"),
        ["PauseCombat"] = ("どちらかが戦闘中なら移動操作を一時停止", "Pause movement controls while either character is in combat"),
        ["FollowDistance"] = ("追従を開始する距離", "Distance to start following"),
        ["CombatLink"] = ("戦闘連携", "Combat Link"),
        ["CombatLinkHelp"] = ("リーダーの戦闘開始に合わせて、フォロワー側の戦闘支援を開始します。", "Starts combat assistance on the follower when the leader enters combat."),
        ["LinkCombatStart"] = ("リーダーの戦闘開始にフォロワーを連動", "Link follower to leader's combat start"),
        ["StopAfterCombat"] = ("リーダーの戦闘終了後に停止", "Stop after leader leaves combat"),
        ["OccultLink"] = ("クレセントアイル連携", "Occult Crescent Link"),
        ["OccultHelp"] = ("エーテライト移動・デミデジョン・宝箱操作をまとめて設定します。", "Configure aetheryte travel, Demi-Return, and treasure interaction."),
        ["AreaContentSync"] = ("エリア移動・コンテンツ同期", "Travel & Duty Sync"),
        ["AreaContentHelp"] = ("通常テレポ・都市内・住宅街の移動をLifestream経由で同期します。", "Sync teleport, city, and residential travel through Lifestream."),
        ["ConnectedClients"] = ("接続中の別クライアント", "Other connected clients"),
        ["WaitingOtherClient"] = ("別のFF14クライアントでAltMateが起動するのを待っています。", "Waiting for AltMate on another FFXIV client."),
        ["Follower"] = ("フォロワー", "Follower"),
        ["Character"] = ("キャラクター", "Character"),
        ["Role"] = ("役割", "Role"),
        ["Job"] = ("ジョブ", "Job"),
        ["RefreshList"] = ("一覧を更新", "Refresh list"),
        ["PlayCharacter"] = ("再生するキャラクター", "Character to play"),
        ["FilterEmote"] = ("エモート名またはMod名で絞り込み", "Filter by emote or mod name"),
        ["Emote"] = ("エモート", "Emote"),
        ["SourceMod"] = ("適用元Mod", "Applied mod"),
        ["Play"] = ("再生", "Play"),
        ["PenumbraMissing"] = ("Penumbraが読み込まれていません。", "Penumbra is not loaded."),
        ["AnimationEmpty"] = ("現在有効な差し替えエモートはありません。上の「一覧を更新」で再取得できます。", "No active replaced emotes were found. Use Refresh list above to scan again."),
    };

    internal static string T(string key) => Text.TryGetValue(key, out var value)
        ? Language == "en" ? value.En : value.Ja
        : key;

    internal static string Status(string value)
    {
        if (Language != "en")
            return value;
        var exact = value switch
        {
            "待機中" => "Idle",
            "リーダーとして待機中" => "Leader: idle",
            "ログアウト中" => "Character data unavailable",
            "緊急停止中" => "Emergency stopped",
            "別クライアントから緊急停止" => "Stopped by another client",
            "リーダーが表示範囲外です" => "Leader is out of range",
            "リーダーをターゲットできません" => "Unable to target leader",
            "リーダーと別エリアです" => "Leader is in another area",
            "リーダーと別ワールドのため連携停止" => "Paused: leader is in another World",
            "安全条件により一時停止" => "Paused by safety conditions",
            "Lifestream移動中のため追従を停止" => "Paused during Lifestream travel",
            "追従キャンセルを検出・再開待ち" => "Follow cancelled; waiting to retry",
            "無効" => "Disabled",
            "停止済み" => "Stopped",
            "リーダーの移動待ち" => "Waiting for leader travel",
            "リーダーのFCハウステレポ待ち" => "Waiting for leader's FC estate teleport",
            "リーダーのエーテライト移動待ち" => "Waiting for leader's aetheryte travel",
            "近くの宝箱を監視中" => "Watching for nearby treasure",
            "同じワールドで待機" => "Idle in the same World",
            "同じワールド・連携可能" => "Same World; linking available",
            "Lifestreamが読み込まれていません" => "Lifestream is not loaded",
            "Lifestream IPCへ接続できません" => "Unable to connect to Lifestream IPC",
            "Lifestreamの受付待ち（自動再試行）" => "Waiting for Lifestream; retrying automatically",
            "移動処理の受付待ち（自動再試行）" => "Waiting for travel request; retrying automatically",
            "通常テレポの再試行期限切れ" => "Regular teleport retry timed out",
            "FCハウス移動の再試行期限切れ" => "FC estate travel retry timed out",
            "住宅住所をLifestream形式へ変換できません" => "Unable to convert the estate address for Lifestream",
            "LifestreamでFC住宅の住所へ移動開始" => "Travelling to the FC estate address with Lifestream",
            "FCハウスの区画前へ移動完了" => "Arrived at the FC estate plot",
            "住宅移動の監視を終了（時間切れ）" => "Estate travel monitoring timed out",
            "リーダーの戦闘開始待ち" => "Waiting for leader to enter combat",
            "デミデジョンのためマウント解除" => "Dismounting for Demi-Return",
            "リーダーに合わせてデミデジョンを開始" => "Starting Demi-Return with the leader",
            "デジョンの実行に失敗" => "Failed to execute Return",
            "リーダーのデミデジョン確認待ち" => "Waiting for leader's Demi-Return confirmation",
            "リーダーが確認・フォロワーへデミデジョン指示を送信" => "Leader confirmed; sent Demi-Return to follower",
            "デジョン確認を承認" => "Accepted Return confirmation",
            "コンテンツ突入を承認" => "Accepted duty commencement",
            "テレポ勧誘を承認" => "Accepted teleport invitation",
            "リーダーの移動を検出・フォロワーの接近待ち" => "Leader travel detected; waiting for follower to approach",
            "移動受付を終了（フォロワーがエーテライト付近にいません）" => "Travel cancelled: follower is not near the aetheryte",
            "現在は移動を開始できません" => "Travel cannot start right now",
            "すでにリーダーと同じワールド・エリアです" => "Already in the leader's World and area",
            "リーダーのエリアに直接移動できるエーテライトがありません" => "No aetheryte travels directly to the leader's area",
            "Lifestreamの住宅移動完了待ち" => "Waiting for Lifestream estate travel",
            "リーダーのエリアへ手動合流中" => "Manually joining the leader's area",
            "リーダーのワールドへ手動合流中" => "Manually joining the leader's World",
            "ローカル通信を開始できませんでした" => "Unable to start local communication",
            "追従テスト実行" => "Follow test requested",
            "相乗りテスト実行" => "Pillion test requested",
            _ => string.Empty,
        };
        if (exact.Length > 0)
            return exact;
        if (value.StartsWith("リーダーを追従中")) return value.Replace("リーダーを追従中", "Following leader");
        if (value.StartsWith("相乗りのため接近中")) return value.Replace("相乗りのため接近中", "Approaching for pillion ride");
        if (value.StartsWith("相乗り可能条件を待機中")) return value.Replace("相乗り可能条件を待機中", "Waiting for pillion conditions");
        if (value.StartsWith("相乗りを再試行待ち")) return value.Replace("相乗りを再試行待ち", "Waiting to retry pillion");
        if (value.StartsWith("リーダーの近くで待機中")) return value.Replace("リーダーの近くで待機中", "Idle near leader");
        if (value.StartsWith("戦闘終了待機中（あと")) return value.Replace("戦闘終了待機中（あと", "Stopping in ").Replace("秒）", "s");
        if (value.StartsWith("通常テレポを共有：")) return value.Replace("通常テレポを共有：", "Shared regular teleport: ");
        if (value.StartsWith("通常テレポを受信：")) return value.Replace("通常テレポを受信：", "Received regular teleport: ");
        if (value.StartsWith("フォロワーもテレポ開始：")) return value.Replace("フォロワーもテレポ開始：", "Follower teleport started: ");
        if (value.StartsWith("FCハウス移動を共有：")) return value.Replace("FCハウス移動を共有：", "Shared FC estate travel: ");
        if (value.StartsWith("FCハウス移動を受信：")) return value.Replace("FCハウス移動を受信：", "Received FC estate travel: ");
        if (value.StartsWith("リーダーの移動を検出：")) return value.Replace("リーダーの移動を検出：", "Leader travel detected: ");
        if (value.StartsWith("フォロワーも移動開始：")) return value.Replace("フォロワーも移動開始：", "Follower travel started: ");
        if (value.StartsWith("到着済み：")) return value.Replace("到着済み：", "Arrived: ");
        if (value.StartsWith("別ワールドのため連携停止：")) return value.Replace("別ワールドのため連携停止：", "Paused in another World: ");
        if (value.StartsWith("宝箱を開けました（")) return value.Replace("宝箱を開けました（", "Opened treasure (").Replace("）", ")");
        if (value.StartsWith("相乗りを実行しました（")) return value.Replace("相乗りを実行しました（", "Pillion requested (").Replace("）", ")");
        return value;
    }

    internal static bool IsEnglish => Language == "en";
    internal static string L(string ja, string en) => IsEnglish ? en : ja;
}
