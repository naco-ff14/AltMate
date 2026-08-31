using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal sealed record CrafterQuestItem(uint QuestId, uint JobId, int Level, string QuestName, uint ItemId,
    string ItemName, int RequiredCount, bool RequiresHq, string Condition);

internal static class CrafterQuestCatalog
{
    private const string Data = """
1	CRP	メープル材	1	-
5	CRP	エキュ	3	-
10	CRP	アッシュ材	12	-
15	CRP	フェザーハープーン	1	-
15	CRP	アッシュショートボウ	1	-
20	CRP	ランス	1	マテリア装着
25	CRP	ウォルナット材	1	HQ
30	CRP	ウォルナットケーン	1	HQ
35	CRP	オークロングボウ	1	HQ
40	CRP	オークコンポジットボウ	1	HQ
45	CRP	ユーロングボウ	1	HQ
45	CRP	コバルトハルバード	1	HQ
45	CRP	ジェイドクルーク	1	HQ
50	CRP	ローズウッド材	1	HQ
50	CRP	クラブボウ	1	HQ・武略のマテリダ装着
53	CRP	ホーリーシーダー・コンポジットボウ	1	HQ
55	CRP	ダークチェスナットロッド	1	HQ
58	CRP	バーチ材	3	HQ
60	CRP	アダマントライデント	1	HQ・秘伝書第3巻
1	BSM	ブロンズインゴット	1	-
5	BSM	クロスペインハンマー	1	-
10	BSM	ブロンズリベット	12	-
15	BSM	スパタ	1	-
15	BSM	スパイクドラブリュス	1	-
20	BSM	アイアン・クロスペインハンマー	1	マテリア装着
25	BSM	スチールインゴット	1	HQ
30	BSM	チョコボハチェット	1	HQ
35	BSM	スチールブージ	1	HQ
40	BSM	ラップド・スチールビークハンマー	1	HQ
45	BSM	コバルトナックル	1	HQ
45	BSM	バッカニアバルディッシュ	1	HQ
45	BSM	シャムシール	1	HQ
50	BSM	ウィングレット	1	HQ・雄略のマテリダ装着
53	BSM	ミスライトリベット	3	HQ
55	BSM	チタンバスタードソード	1	HQ
58	BSM	チタンランプハンマー	1	HQ
60	BSM	アダマンウィングレット	1	HQ・秘伝書第3巻
1	ARM	ブロンズインゴット	1	-
5	ARM	ホプロン	3	-
10	ARM	ブロンズプレート	12	-
15	ARM	バルビュートDX	1	-
15	ARM	バックラー	1	-
20	ARM	アイアンホプロン	1	マテリア装着
25	ARM	スチールインゴット	1	HQ
30	ARM	スチールチェーンメイル	1	HQ
35	ARM	スチールフライパン	1	HQ
40	ARM	ミスリルキュイラス	1	HQ
45	ARM	ミスリルアーマードカリガ	1	HQ
45	ARM	ミスリルエルモDX	1	HQ
45	ARM	ミスリルソルレット	1	HQ
50	ARM	コバルトホーバージョン	1	HQ・天眼のマテリダ装着
53	ARM	チタンストライカーマスク	1	-
55	ARM	チタンスレイヤーキュイラス	1	HQ
58	ARM	チタンホプロン	1	HQ
60	ARM	アダマンディフェンダーロリカ	1	HQ・秘伝書第3巻
1	GSM	カッパーインゴット	1	-
5	GSM	カッパーゴルゲット	3	-
10	GSM	カッパーリングズ	12	-
15	GSM	ファングイヤリング	1	-
15	GSM	ブラスゴルゲット	1	-
20	GSM	スタッグホーンスタッフ	1	マテリア装着
25	GSM	シルバーインゴット	1	HQ
30	GSM	マラカイトイヤリング	1	HQ
35	GSM	ファイアブランド	1	HQ
40	GSM	ホーンスタッフ	1	HQ
45	GSM	エレクトラムサークレット（アンバー）	1	HQ
45	GSM	エレクトラムサークレット（ジルコン）	1	HQ
45	GSM	エレクトラムサークレット（スピネル）	1	HQ
50	GSM	ブラックパールリング	1	HQ・信力のマテリダ装着
53	GSM	ハードシルバー・キャスターバングル	1	-
55	GSM	ハードシルバーインゴット	1	-
58	GSM	オーラムレギスシリンダー	1	-
60	GSM	スターサファイアオルゴール	1	秘伝書第3巻
60	GSM	スタールビーオルゴール	1	秘伝書第3巻
1	LTW	レザー	1	-
5	LTW	レザーチョーカー	3	-
10	LTW	ハードレザー	12	-
15	LTW	カリガ	1	-
15	LTW	ハードレザーチョーカー	1	-
20	LTW	アルドゴートレギンス	1	マテリア装着
25	LTW	ギガントードレザー	1	HQ
30	LTW	トードジャケット	1	HQ
35	LTW	ボアリングバンド	1	HQ
40	LTW	ボアスミスグローブ	1	HQ
45	LTW	ラプトルフィンガレスグローブ	1	HQ
45	LTW	ラプトルタージェ	1	HQ
45	LTW	ラプトルチョーカー	1	HQ
50	LTW	ラプトルジャーキン	1	HQ・器識のマテリダ装着
53	LTW	ワイバーンワークブーツ	1	HQ
55	LTW	ダルメルスカウトレギンス	1	HQ
58	LTW	ドラゴンレザーチョーカー	1	HQ
60	LTW	シバルリー・レンジャーバトルドレス	1	秘伝書第3巻
1	WVR	草糸	1	-
5	WVR	ブリーチ	3	-
10	WVR	草布	12	-
15	WVR	コットンスカーフ	1	-
15	WVR	コットンシェパードスロップ	1	-
20	WVR	コットンアクトン	1	マテリア装着
25	WVR	別珍	1	HQ
30	WVR	ベルベティーンゲイター	1	HQ
35	WVR	リネンシャツ	1	HQ
40	WVR	ウールタイツ	1	HQ
45	WVR	ウールガウン	1	HQ
45	WVR	ウールガスキン	1	HQ
45	WVR	ウールベレー	1	HQ
50	WVR	パトリシアンコーティー	1	HQ
50	WVR	パトリシアンボトム	1	HQ
50	WVR	パトリシアンウェッジキャップ	1	HQ
53	WVR	ホーリーレインボー・ドレスグローブ	1	HQ
55	WVR	ホーリーレインボー・ヒーラーハット	1	HQ
58	WVR	クロウラーの絹糸	3	HQ
60	WVR	シバルリー・ヒーラーダブレット	1	秘伝書第3巻
1	ALC	蒸留水	1	-
5	ALC	毒消し	3	-
10	ALC	蜜蝋	12	-
15	ALC	知力の薬	1	-
15	ALC	眼力の薬	1	-
20	ALC	ハードレザーグリモアDX	1	マテリア装着
25	ALC	重曹	1	HQ
30	ALC	暗闇の毒薬	3	HQ
35	ALC	ハイエーテル	1	HQ
40	ALC	剛力の妙薬	3	HQ
45	ALC	知力の秘薬	1	HQ
45	ALC	心力の秘薬	1	HQ
45	ALC	活力の秘薬	1	HQ
50	ALC	バッデッドローズワンド	1	HQ・詠唱のマテリダ装着
53	ALC	ミスライトエンチャントインク	3	HQ
55	ALC	知力の錬金溶剤G1	3	HQ
58	ALC	剛力の竜薬	1	HQ・秘伝書第3巻
60	ALC	インデックス・オブ・オーラムレギス	1	HQ・秘伝書第3巻
1	CUL	メープルシロップ	1	-
5	CUL	グリルドトラウト	3	-
10	CUL	ドードーのグリル	2	-
15	CUL	ミコッテ風山の幸串焼	1	-
20	CUL	ドライプルーン	1	-
25	CUL	大山羊ステーキ	1	-
30	CUL	スモークドラプトル	1	HQ
35	CUL	ラタトゥイユ	1	HQ
40	CUL	ブラッドカーラントタルト	1	HQ
40	CUL	ペストリーフィッシュ	1	HQ
40	CUL	カモミールティー	1	HQ
45	CUL	ゼーメル家風グラタン	1	HQ
50	CUL	エフトステーキ	1	HQ
50	CUL	ビーフシチュー	1	HQ
50	CUL	猟師風エフトキッシュ	3	HQ
50	CUL	ガレット・デ・ロワ	3	HQ
53	CUL	ソーム・アル・オ・マロン	1	-
53	CUL	イシュガルドティー	1	HQ
55	CUL	クリムゾンスープ	1	HQ
55	CUL	カイザーゼンメル	1	HQ
55	CUL	グリルドスイートフィッシュ	1	HQ
58	CUL	フリカデレ	1	HQ
60	CUL	オーケアニスシュニッツェル	1	HQ
60	CUL	モリーユサラダ	1	HQ
60	CUL	マロングラッセ	1	HQ
""";

    internal static IReadOnlyList<CrafterQuestItem> BuildToLevel60()
    {
        var items = Plugin.DataManager.GetExcelSheet<Item>()
            .GroupBy(x => x.Name.ToString()).ToDictionary(x => x.Key, x => x.First().RowId);
        var quests = Plugin.DataManager.GetExcelSheet<Quest>().ToArray();
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>()
            .GroupBy(x => x.Abbreviation.ToString()).ToDictionary(x => x.Key, x => x.First().RowId);
        var result = new List<CrafterQuestItem>();
        foreach (var line in Data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cells = line.Split('\t');
            if (cells.Length != 5 || !jobs.TryGetValue(cells[1], out var jobId) ||
                !int.TryParse(cells[0], out var level) || !int.TryParse(cells[3], out var count)) continue;
            items.TryGetValue(cells[2], out var itemId);
            var quest = quests.FirstOrDefault(q => q.ClassJobRequired.RowId == jobId && q.QuestClassJobSupply.IsValid &&
                q.QuestClassJobSupply.Value.Any(s => s.Item.RowId == itemId && s.AmountRequired == count));
            result.Add(new CrafterQuestItem(quest.RowId, jobId, level,
                quest.RowId == 0 ? $"Lv{level} {cells[1]}" : quest.Name.ToString(),
                itemId, cells[2], count, cells[4].Contains("HQ", StringComparison.Ordinal), cells[4]));
        }
        return result;
    }
}
