using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AltMate;

public sealed class AnimationService
{
    private const int GPoseLocalPlayerIndex = 201;

    public unsafe bool IsInGroupPose => GameMain.IsInGPose();

    public bool IsPenumbraLoaded => Plugin.PluginInterface.InstalledPlugins.Any(x =>
        x.IsLoaded && x.InternalName.Equals("Penumbra", StringComparison.OrdinalIgnoreCase));

    public string Status { get; private set; } = "PenumbraからModを読み込んでください。";

    public IReadOnlyList<AnimationMod> LoadMods()
    {
        if (!IsPenumbraLoaded)
        {
            Status = "Penumbraが読み込まれていません。";
            return [];
        }

        try
        {
            var mods = Plugin.PluginInterface
                .GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList")
                .InvokeFunc();
            Status = $"{mods.Count}件のModを読み込みました。";
            return mods.Select(x => new AnimationMod(x.Key, x.Value))
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "PenumbraのMod一覧を取得できませんでした。");
            Status = "PenumbraのMod一覧を取得できませんでした。";
            return [];
        }
    }

    public IReadOnlyList<AnimationEmote> LoadActiveEmotes()
    {
        try
        {
            var collection = Plugin.PluginInterface
                .GetIpcSubscriber<int, (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)>(
                    "Penumbra.GetCollectionForObject.V5")
                .InvokeFunc(0);
            if (!collection.ObjectValid)
            {
                Status = "現在のキャラクターのPenumbraコレクションを取得できませんでした。";
                return [];
            }

            var activeChangedItems = Plugin.PluginInterface
                .GetIpcSubscriber<Guid, Dictionary<string, object?>>("Penumbra.GetChangedItemsForCollection")
                .InvokeFunc(collection.EffectiveCollection.Id);
            var sourceResolver = Plugin.PluginInterface
                .GetIpcSubscriber<Func<string, (string ModDirectory, string ModName)[]>>(
                    "Penumbra.CheckCurrentChangedItemFunc")
                .InvokeFunc();
            var sheet = Plugin.DataManager.GetExcelSheet<Emote>();
            var emotesByName = sheet
                .Where(x => x.RowId is > 0 and <= ushort.MaxValue && !string.IsNullOrWhiteSpace(x.Name.ToString()))
                .GroupBy(x => x.Name.ToString(), StringComparer.CurrentCultureIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.CurrentCultureIgnoreCase);
            var result = new List<AnimationEmote>();

            foreach (var item in activeChangedItems.Keys)
            {
                var sources = sourceResolver(item);
                if (sources.Length == 0)
                    continue;
                var sourceNames = string.Join(", ", sources.Select(x => x.ModName).Distinct());
                var sourceDirectories = string.Join(", ", sources.Select(x => x.ModDirectory).Distinct());

                var candidate = ExtractChangedItemName(item);
                if (emotesByName.TryGetValue(candidate, out var exact))
                {
                    result.Add(new AnimationEmote((ushort)exact.RowId, exact.Name.ToString(), sourceDirectories, sourceNames,
                        Plugin.UnlockState.IsEmoteUnlocked(exact)));
                    continue;
                }

                // Penumbraの項目種別は表示言語で変わるため、接頭辞には依存しない。
                // 例: "Emote: 格闘訓練" / "エモート：格闘訓練" / "格闘訓練 (166)"
                var match = emotesByName
                    .Where(x => ContainsCompleteName(item, x.Key))
                    .OrderByDescending(x => x.Key.Length)
                    .Select(x => x.Value)
                    .FirstOrDefault();
                if (match.RowId != 0)
                    result.Add(new AnimationEmote((ushort)match.RowId, match.Name.ToString(), sourceDirectories, sourceNames,
                        Plugin.UnlockState.IsEmoteUnlocked(match)));
            }

            Status = result.Count > 0
                ? $"{collection.EffectiveCollection.Name}：有効なエモートを{result.Count}件検出しました。"
                : $"{collection.EffectiveCollection.Name}：現在有効なエモートはありません。";
            return result.DistinctBy(x => x.Id).OrderBy(x => x.Name).ToArray();
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "Penumbraの有効なChanged Itemsを取得できませんでした。");
            Status = $"Changed Itemsを取得できませんでした（{exception.GetType().Name}）。";
            return [];
        }
    }

    public IReadOnlyList<AnimationEmote> LoadGameEmotes()
    {
        if (!Plugin.PlayerState.IsLoaded)
        {
            Status = "ログイン中のキャラクターがいません。";
            return [];
        }
        var result = Plugin.DataManager.GetExcelSheet<Emote>()
            .Where(x => x.RowId is > 0 and <= ushort.MaxValue &&
                        x.Icon != 0 &&
                        !string.IsNullOrWhiteSpace(x.Name.ToString()) &&
                        x.TextCommand.IsValid &&
                        x.TextCommand.Value.Command.ToString().StartsWith("/", StringComparison.Ordinal))
            .Select(x => new AnimationEmote((ushort)x.RowId, x.Name.ToString(), string.Empty, string.Empty,
                Plugin.UnlockState.IsEmoteUnlocked(x)))
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        Status = $"ゲーム内エモートを{result.Length}件読み込みました。";
        return result;
    }

    private static string ExtractChangedItemName(string item)
    {
        var separator = item.IndexOfAny([':', '：']);
        var value = separator >= 0 ? item[(separator + 1)..] : item;
        return Regex.Replace(value.Trim(), @"\s*[（(]\s*\d+\s*[）)]\s*$", string.Empty).Trim();
    }

    private static bool ContainsCompleteName(string item, string name)
    {
        var index = item.IndexOf(name, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
            return false;
        var beforeOkay = index == 0 || item[index - 1] is ':' or '：' or ' ' or '\t';
        var end = index + name.Length;
        var afterOkay = end == item.Length || item[end] is ' ' or '\t' or '(' or '（';
        return beforeOkay && afterOkay;
    }

    public unsafe bool PlayLocal(ushort emoteId)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
        {
            Status = "ログイン中のキャラクターがいません。";
            return false;
        }

        if (IsInGroupPose)
        {
            var emote = Plugin.DataManager.GetExcelSheet<Emote>().GetRow(emoteId);
            var timelineId = emote.ActionTimeline[0].RowId;
            // GPoseは通常のLocalPlayerとは別に表示用キャラクターをObjectTable 201へ複製する。
            // 通常側へタイムラインを送ってもGPose画面には反映されない。
            var gPoseLocal = Plugin.ObjectTable[GPoseLocalPlayerIndex];
            var character = gPoseLocal is null ? null : (Character*)gPoseLocal.Address;
            if (character == null || timelineId == 0 || timelineId > ushort.MaxValue)
            {
                Status = "グループポーズ用キャラクターを取得できませんでした。入り直して再試行してください。";
                return false;
            }

            var timeline = Plugin.DataManager.GetExcelSheet<ActionTimeline>().GetRow(timelineId);
            if (timeline.Pause)
            {
                character->Mode = CharacterModes.EmoteLoop;
                character->ModeParam = 0;
            }
            else if (character->Mode == CharacterModes.EmoteLoop && character->ModeParam == 0)
            {
                character->Mode = CharacterModes.Normal;
            }
            else if (character->Mode == CharacterModes.AnimLock)
            {
                character->Mode = CharacterModes.Normal;
                character->ModeParam = 0;
                character->Timeline.BaseOverride = 0;
            }

            character->Timeline.TimelineSequencer.PlayTimeline((ushort)timelineId);
            Status = Plugin.UnlockState.IsEmoteUnlocked(emote)
                ? "グループポーズ内でエモートを再生しました。"
                : "グループポーズ内で未習得エモートをローカル再生しました。";
            return true;
        }

        var manager = EmoteManager.Instance();
        if (manager == null || !manager->CanExecuteEmote(emoteId))
        {
            Status = "現在このエモートを再生できません。";
            return false;
        }

        var result = manager->ExecuteEmote(emoteId);
        Status = result ? "エモートを再生しました。" : "エモートの再生に失敗しました。";
        return result;
    }
}

public readonly record struct AnimationMod(string Directory, string Name);
public readonly record struct AnimationEmote(
    ushort Id, string Name, string ModDirectory, string ModName, bool IsUnlocked);
