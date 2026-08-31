using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal static class CrafterGearCatalog
{
    internal static readonly int[] TierLevels = [21, 41, 53, 63, 71, 81, 91];
    internal sealed record Result(int TierCount, IReadOnlyList<string> Missing);

    internal static Result BuildStandard(CrafterLevelingSettings settings)
    {
        var items = Plugin.DataManager.GetExcelSheet<Item>()
            .Where(x => !string.IsNullOrWhiteSpace(x.Name.ToString()))
            .GroupBy(x => x.Name.ToString()).ToDictionary(x => x.Key, x => x.First().RowId);
        var missing = new List<string>();
        foreach (var tier in TierLevels.Where(x => x <= settings.TargetLevel))
        {
            var preset = new CrafterGearPreset { TierLevel = tier };
            var names = GearNames[tier];
            for (var index = 0; index < names.Length; index++)
            {
                if (!items.TryGetValue(names[index], out var itemId))
                {
                    missing.Add($"Lv{tier} {names[index]}");
                    continue;
                }
                if (index < 10) preset.SharedItemIds.Add(itemId);
                else
                {
                    var jobId = (uint)(8 + (index - 10) / 2);
                    if (!settings.EnabledJobIds.Contains(jobId)) continue;
                    if (!preset.JobItemIds.TryGetValue(jobId, out var jobItems))
                        preset.JobItemIds[jobId] = jobItems = new List<uint>();
                    jobItems.Add(itemId);
                }
            }
            settings.GearPresets.RemoveAll(x => x.TierLevel == tier);
            settings.GearPresets.Add(preset);
        }
        settings.GearPresets.Sort((a, b) => a.TierLevel.CompareTo(b.TierLevel));
        return new Result(settings.GearPresets.Count, missing);
    }

    // Each tier contains 10 shared slots (including two rings), followed by main/off-hand pairs
    // for CRP, BSM, ARM, GSM, LTW, WVR, ALC and CUL in that order.
    private static readonly Dictionary<int, string[]> GearNames = new()
    {
        [21] = Lines("""
イニシエートヘッドギア
コットン・クラフターダブレットベスト
イニシエートグローブ
コットンクラフターブリーチ
イニシエートサイブーツ
ファングイヤリング
ブラスチョーカー
ブラスクラフターリストレット
ブラスクラフターリング
ブラスクラフターリング
イニシエートソー
アイアンクローハンマー
イニシエート・クロスペインハンマー
アイアンファイル
イニシエートドーミングハンマー
アイアンプライヤー
イニシエートチェーサーハンマー
グラインディングホイール
イニシエートヘッドナイフ
アイアンアウル
ブラスニードル
イニシエートスピニングホイール
イニシエートアレンビック
アイアンモーター
イニシエートスキレット
アイアンクリナリーナイフ
"""),
        [41] = Lines("""
ヴィンテージシェフズハット
スモック
ボアスミスグローブ
リネンスロップ
ヴィンテージサイブーツ
ウルフファングイヤリング
ミスリルチョーカー
ミスリルクラフターリストレット
ミスリルクラフターリング
ミスリルクラフターリング
ミスリルソー
アプレンティスクローハンマー
ラップド・スチールビークハンマー
アプレンティスファイル
スチールレイジングハンマー
アプレンティスプライヤー
ミスリルオーナメンタルハンマー
アプレンティスグラインディングホイール
ミスリルヘッドナイフ
アプレンティスアウル
ウルフファングニードル
マホガニースピニングホイール
ミスリルアレンビック
アプレンティスモーター
スチールフライパン
アプレンティスクリナリーナイフ
"""),
        [53] = Lines("""
ホーリーレインボー・ウェッジキャップ
ホーリーレインボーコーティー
ホーリーレインボー・ドレスグローブ
ホーリーレインボーボトム
ホーリーレインボー・ドレスシューズ
イエティファングイヤリング
ローズゴールドチョーカー
ホーリーシーダーアルミラ
ホーリーシーダーリング
ホーリーシーダーリング
ミスライトラウンドソー
ミスライトクローハンマー
ミスライトランプハンマー
ミスライトファイル
ミスライトレイジングハンマー
ミスライトプライヤー
ミスライトラピダリーハンマー
アストラルグラインディングホイール
ミスライトラウンドナイフ
ミスライトアウル
ミスライトニードル
ホーリーシーダー・スピニングホイール
ミスライトアレンビック
ミスライトモーター
サボテンダーフライパン
ミスライトクリナリーナイフ
"""),
        [63] = Lines("""
ルビーコットンキャップ
ルビーコットンコーティー
ギュウキクラフターグローブ
ルビーコットンボトム
ギュウキシューズ
ラーチイヤリング
ラーチネックレス
ラーチブレスレット
ラーチリング
ラーチリング
ハイスチールソー
ハイスチール・クローハンマー
ハイスチール・クロスペインハンマー
ハイスチールファイル
ハイスチール・ドーミングハンマー
ハイスチールプライヤー
キュプロオーナメンタルハンマー
スタイパーストーン・グラインディングホイール
ハイスチール・ヘッドナイフ
ハイスチールアウル
ボムフィッシュニードル
ラーチスピニングホイール
ハイスチール・サーマルアレンビック
ハイスチールモーター
ハイスチール・ボムフライパン
ハイスチール・クリナリーナイフ
"""),
        [71] = Lines("""
ホワイトヘンプ・クラフターターバン
ホワイトヘンプ・クラフターダブレット
スミロドンクラフターグローブ
ホワイトヘンプ・クラフターボトム
スミロドンクラフターシューズ
ホワイトオークイヤリング
ホワイトオークネックレス
ホワイトオークブレスレット
ホワイトオークリング
ホワイトオークリング
ディープゴールドソー
ディープゴールド・クローハンマー
ディープゴールド・クロスペインハンマー
ディープゴールドファイル
ディープゴールド・レイジングハンマー
ディープゴールドプライヤー
ディープゴールド・ラピダリーハンマー
ホワイトオーク・グラインディングホイール
ディープゴールド・ヘッドナイフ
ディープゴールドアウル
ストーンゴールドニードル
ホワイトオーク・スピニングホイール
ディープゴールド・アレンビック
ディープゴールドモーター
ディープゴールド・レイルフライパン
ディープゴールド・クリナリーナイフ
"""),
        [81] = Lines("""
黒麻帽
黒麻胴着
象皮半手甲
黒麻股引
象皮足袋
アメトリン・クラフターイヤーカフ
アメトリン・クラフターネックレス
アメトリン・クラフターアルミラ
アメトリン・クラフターリング
アメトリン・クラフターリング
ハイダリウム・ソー
ハイダリウム・クローハンマー
ハイダリウム・クロスペインハンマー
ハイダリウム・ファイル
ハイダリウム・レイジングハンマー
ハイダリウム・プライヤー
ハイダリウム・ラピダリーハンマー
ホースチェスナット・グラインディングホイール
ハイダリウム・レザーワーカーナイフ
ハイダリウム・アウル
ハイダリウム・ニードル
ホースチェスナット・スピニングホイール
ハイダリウム・アレンビック
ハイダリウム・モーター
ハイダリウム・ナマズオフライパン
ハイダリウム・クリナリーナイフ
"""),
        [91] = Lines("""
スノーコットン・ベレー
スノーコットン・ジャケット
シルバリオ・フィンガレスグローブ
スノーコットン・トラウザー
シルバリオ・シューズ
ラァー・イヤーカフ
ラァー・チョーカー
ラァー・ブレスレット
ラァー・リング
ラァー・リング
オルコクロマイト・ソー
オルコクロマイト・クローハンマー
オルコクロマイト・クロスペインハンマー
オルコクロマイト・ファイル
オルコクロマイト・レイジングハンマー
オルコクロマイト・プライヤー
オルコクロマイト・ラピダリーハンマー
ウコギ・グラインディングホイール
オルコクロマイト・レザーワーカーナイフ
オルコクロマイト・アウル
ラァー・ニードル
ウコギ・スピニングホイール
オルコクロマイト・アレンビック
オルコクロマイト・モーター
オルコクロマイト・フライパン
オルコクロマイト・クリナリーナイフ
"""),
    };

    private static string[] Lines(string value) => value.Split('\n')
        .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
}
