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
    };

    internal static string T(string key) => Text.TryGetValue(key, out var value)
        ? Language == "en" ? value.En : value.Ja
        : key;
}
