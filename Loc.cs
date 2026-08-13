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
        return value;
    }

    internal static bool IsEnglish => Language == "en";
}
