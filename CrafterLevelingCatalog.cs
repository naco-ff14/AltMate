using Dalamud.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AltMate;

internal static class CrafterLevelingCatalog
{
    internal sealed record ApplyResult(int Added, int Skipped, IReadOnlyList<string> Unresolved);

    // User-approved leveling plan. Recipe IDs are resolved from the Japanese game-data sheet
    // so the catalog remains stable regardless of the client's display language.
    private const string StandardPlan = """
1	9	CRP	通常製作	メープル材	30
10	15	CRP	通常製作	アッシュ材	30
16	19	CRP	通常製作	エルム材	40
1	15	BSM	通常製作	ブロンズインゴット	40
16	17	BSM	通常製作	アイアンインゴット	30
18	19	BSM	通常製作	アイアンリベット	30
1	2	ARM	通常製作	ブロンズインゴット	10
3	13	ARM	通常製作	ブロンズプレート	40
14	17	ARM	通常製作	アイアンプレート	30
18	19	ARM	通常製作	イニシエートスキレット	30
1	13	GSM	通常製作	カッパーインゴット	40
14	19	GSM	通常製作	ブラスインゴット	40
1	7	LTW	通常製作	レザー	30
8	16	LTW	通常製作	ハードレザー	40
17	19	LTW	通常製作	アルドゴートレザー	30
1	1	WVR	通常製作	草糸	10
2	11	WVR	通常製作	草布	40
12	12	WVR	通常製作	綿糸	20
13	18	WVR	通常製作	綿布	40
19	19	WVR	通常製作	コットンアクトン	20
1	6	ALC	通常製作	蒸留水	30
7	9	ALC	通常製作	ラバー	20
10	19	ALC	通常製作	蜜蝋	50
1	6	CUL	通常製作	メープルシロップ	30
7	15	CUL	通常製作	バター	40
16	19	CUL	通常製作	サイダービネガー	40
20	39	CRP	復興	第四次復興用の合板	25
20	39	BSM	復興	第四次復興用の合金	25
20	39	ARM	復興	第四次復興用の金属板	25
20	39	GSM	復興	第四次復興用の地金	25
20	39	LTW	復興	第四次復興用のなめし革	25
20	39	WVR	復興	第四次復興用の荒縄	25
20	39	ALC	復興	第四次復興用のインク	25
20	39	CUL	復興	第四次復興用のヘンプミルク	25
40	49	CRP	復興	第四次復興用の木箱	20
40	49	BSM	復興	第四次復興用の鉄釘	20
40	49	ARM	復興	第四次復興用のリベット	20
40	49	GSM	復興	第四次復興用の鉄環	20
40	49	LTW	復興	第四次復興用の革紐	20
40	49	WVR	復興	第四次復興用の生地	20
40	49	ALC	復興	第四次復興用の植物油	20
40	49	CUL	復興	第四次復興用のセサミクッキー	20
50	59	CRP	復興	第四次復興用の木箱	35
50	59	BSM	復興	第四次復興用の鉄釘	35
50	59	ARM	復興	第四次復興用のリベット	35
50	59	GSM	復興	第四次復興用の鉄環	35
50	59	LTW	復興	第四次復興用の革紐	35
50	59	WVR	復興	第四次復興用の生地	35
50	59	ALC	復興	第四次復興用の植物油	35
50	59	CUL	復興	第四次復興用のセサミクッキー	35
60	69	CRP	復興	第四次復興用のスピニングホイール	40
60	69	BSM	復興	第四次復興用のハチェット	40
60	69	ARM	復興	第四次復興用のクッキングポット	40
60	69	GSM	復興	第四次復興用の裁縫道具	40
60	69	LTW	復興	第四次復興用の革袋	40
60	69	WVR	復興	第四次復興用のホウキ	40
60	69	ALC	復興	第四次復興用の聖水	40
60	69	CUL	復興	第四次復興用の紅茶	40
70	80	CRP	復興	第四次復興用の脚立	40
70	80	BSM	復興	第四次復興用の大鋸	40
70	80	ARM	復興	第四次復興用のメセイル	40
70	80	GSM	復興	第四次復興用の石材	40
70	80	LTW	復興	第四次復興用の長靴	40
70	80	WVR	復興	第四次復興用の手袋	40
70	80	ALC	復興	第四次復興用の石鹸	40
70	80	CUL	復興	第四次復興用の薬湯	40
50	55	CRP	収集品	収集用のシーダーロングボウ	30
56	60	CRP	収集品	収集用のダークチェスナットロッド	30
61	66	CRP	収集品	収集用のビーチコンポジットボウ	30
67	71	CRP	収集品	収集用のパーシモンブレスレット	30
72	77	CRP	収集品	収集用のホワイトオークパルチザン	30
78	80	CRP	収集品	収集用のサンドチークフォチャード	30
50	55	BSM	収集品	収集用のミスライトカッツバルゲル	30
56	60	BSM	収集品	収集用のチタン・レザーワーカーナイフ	30
61	66	BSM	収集品	収集用のハイスチールディバイダー	30
67	71	BSM	収集品	収集用のドマスチールパタ	30
72	77	BSM	収集品	収集用のディープゴールドアネラス	30
78	80	BSM	収集品	収集用のチタンブロンズピック	30
50	55	ARM	収集品	収集用のミスライトサレット	30
56	60	ARM	収集品	収集用のチタンフライパン	30
61	66	ARM	収集品	収集用のハイスチール・サーマルアレンビック	30
67	71	ARM	収集品	収集用のドマスチールタバード	30
72	77	ARM	収集品	収集用のディープゴールドキュイラス	30
78	80	ARM	収集品	収集用のチタンブロンズ・タワーシールド	30
50	55	GSM	収集品	収集用のミスライトゴーグル	30
56	60	GSM	収集品	収集用のハードシルバーモノクル	30
61	66	GSM	収集品	収集用のキュプロプラニスフィア	30
67	71	GSM	収集品	収集用のダリウムロッド	30
72	77	GSM	収集品	収集用のストーンゴールドデーゲン	30
78	80	GSM	収集品	収集用のチタンブロンズヘッドギア	30
50	55	LTW	収集品	収集用のアルケオーニスベルト	30
56	60	LTW	収集品	収集用のダルメルコート	30
61	66	LTW	収集品	収集用のガガナシューズ	30
67	71	LTW	収集品	収集用のマーリドコルセット	30
72	77	LTW	収集品	収集用のスミロドントラウザー	30
78	80	LTW	収集品	収集用のゾヌールフィンガレスグローブ	30
50	55	WVR	収集品	収集用のレインボークロスボレロ	30
56	60	WVR	収集品	収集用のラミーターバン	30
61	66	WVR	収集品	収集用のレッドヘンプスカート	30
67	71	WVR	収集品	収集用のサージホーズ	30
72	77	WVR	収集品	収集用のホワイトヘンプヒマティオン	30
78	80	WVR	収集品	収集用のオヴィムチュニック	30
50	55	ALC	収集品	収集用のアルケオーニスグリモア	30
56	60	ALC	収集品	収集用のダルメルコーデックス	30
61	66	ALC	収集品	収集用のインデックス・オブ・キュプロ	30
67	71	ALC	収集品	収集用のグロースフォーミュラ	30
72	77	ALC	収集品	収集用の幻水	30
78	80	ALC	収集品	収集用の水薬	30
50	55	CUL	収集品	収集用のアウフラウフ	30
56	60	CUL	収集品	収集用のエッグロワイヤル	30
61	66	CUL	収集品	収集用のバクラヴァ	30
67	71	CUL	収集品	収集用のパーシモンプディング	30
72	77	CUL	収集品	収集用のレイルのグリル	30
78	80	CUL	収集品	収集用のエスプレッソ・コン・パンナ	30
81	84	CRP	収集品	収集用の栃乃木笠	25
81	84	BSM	収集品	収集用のハイダリウムピストル	25
81	84	ARM	収集品	収集用のハイダリウムナックル	25
81	84	GSM	収集品	収集用のハイダリウムミルプレーヴェ	25
81	84	LTW	収集品	収集用のガジャシューズ	25
81	84	WVR	収集品	収集用の黒麻帽	25
81	84	ALC	収集品	収集用のガジャコーデックス	25
81	84	CUL	収集品	収集用の賢人パン	25
85	90	CRP	収集品	収集用のレッドパイン・スピニングホイール	35
85	90	BSM	収集品	収集用のビスマスモール	35
85	90	ARM	収集品	収集用のビスマス・ファットキャットフライパン	35
85	90	GSM	収集品	収集用のフリギアンイヤリング	35
85	90	LTW	収集品	収集用のサイガグローブ	35
85	90	WVR	収集品	収集用のスノーリネン・クラフターダブレット	35
85	90	ALC	収集品	収集用のムーンゲル	35
85	90	CUL	収集品	収集用のハピネスジュース	35
91	94	CRP	収集品	収集用のウコギイヤリング	25
91	94	BSM	収集品	収集用のオルコクロマイト・フィスト	25
91	94	ARM	収集品	収集用のオルコクロマイト・アレンビック	25
91	94	GSM	収集品	収集用のラァー・ロングボウ	25
91	94	LTW	収集品	収集用のシルバリオ・フィンガレスグローブ	25
91	94	WVR	収集品	収集用のスノーコットン・ベレー	25
91	94	ALC	収集品	収集用のシルバリオ・グリモア	25
91	94	CUL	収集品	収集用のボイルド・アルパカステーキ	25
95	99	CRP	収集品	収集用のダークマホガニー・ネックレス	30
95	99	BSM	収集品	収集用のコバルトタングステン・シミター	30
95	99	ARM	収集品	収集用のコバルトタングステン・チョコボフライパン	30
95	99	GSM	収集品	収集用のコバルトタングステン・タック	30
95	99	LTW	収集品	収集用のブラーシャ・アームレット	30
95	99	WVR	収集品	収集用のサーセネット・ケクス	30
95	99	ALC	収集品	収集用のブラーシャ・コーデックス	30
95	99	CUL	収集品	収集用のトラルパイナップルケーキ	30
""";

    internal static ApplyResult ApplyStandard(CrafterLevelingSettings settings)
    {
        var itemIdsByJapaneseName = Plugin.DataManager.GetExcelSheet<Item>(ClientLanguage.Japanese)
            .Where(x => !x.Name.IsEmpty)
            .GroupBy(x => x.Name.ToString(), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(item => item.RowId).ToHashSet(), StringComparer.Ordinal);
        var recipes = Plugin.DataManager.GetExcelSheet<Recipe>().Where(x => x.ItemResult.RowId != 0).ToArray();
        var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>()
            .GroupBy(x => x.Abbreviation.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().RowId, StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();
        var added = 0;

        // Recalculation replaces every generated row, including catalogs from older versions.
        settings.RecipePresets.RemoveAll(x => x.IsCatalogGenerated);

        foreach (var line in StandardPlan.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cells = line.Split('\t');
            if (cells.Length != 6 || !int.TryParse(cells[0], out var minLevel) ||
                !int.TryParse(cells[1], out var maxLevel) || !int.TryParse(cells[5], out var craftCount) ||
                !jobs.TryGetValue(cells[2], out var jobId) || !settings.EnabledJobIds.Contains(jobId))
                continue;

            var route = cells[3] switch
            {
                "復興" => CrafterLevelingRoute.Restoration,
                "収集品" => CrafterLevelingRoute.Collectable,
                _ => CrafterLevelingRoute.Normal,
            };
            if (!itemIdsByJapaneseName.TryGetValue(cells[4], out var itemIds))
            {
                unresolved.Add(cells[4]);
                continue;
            }
            var recipe = recipes.FirstOrDefault(x => itemIds.Contains(x.ItemResult.RowId) &&
                x.CraftType.RowId + 8 == jobId);
            if (recipe.RowId == 0)
            {
                unresolved.Add(cells[4]);
                continue;
            }

            settings.RecipePresets.Add(new CrafterRecipePreset
            {
                JobId = jobId,
                MinLevel = minLevel,
                MaxLevel = maxLevel,
                RecipeId = recipe.RowId,
                MaxCraftCount = craftCount,
                Route = route,
                RequiredUnlock = route switch
                {
                    CrafterLevelingRoute.Restoration => "Towards the Firmament",
                    CrafterLevelingRoute.Collectable => "Inscrutable Tastes",
                    _ => string.Empty,
                },
                IsCatalogGenerated = true,
            });
            added++;
        }

        settings.RecipePresets.Sort((left, right) =>
        {
            var job = left.JobId.CompareTo(right.JobId);
            return job != 0 ? job : left.MinLevel.CompareTo(right.MinLevel);
        });
        return new ApplyResult(added, 0, unresolved.Distinct().ToArray());
    }

    internal static bool IsActiveForLeveling(CrafterLevelingSettings settings, CrafterRecipePreset preset) =>
        preset.MinLevel is < 50 or > 80 || preset.Route == settings.Level50To80Route;
}
