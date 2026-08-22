using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using NoireLib.Animations.Helpers;
using NoireLib.Animations.PapFormat;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AltMate;

public delegate bool DirectEmoteFallback(Emote emote, out string message);

/// <summary>
/// 未習得エモートの立ちモーションを習得済みエモートへ一時的に載せ替え、
/// ゲーム本体には習得済みエモートだけを実行させる。
/// </summary>
public sealed class EmoteSwapService : IDisposable
{
    private const string ModDirectoryName = "_AltMateEmoteSwap";
    private const int ModPriority = 9999;
    private static readonly TimeSpan ApplyDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RetryLimit = TimeSpan.FromMilliseconds(750);

    private readonly Action<string> setStatus;
    private readonly DirectEmoteFallback directFallback;
    private readonly GetModDirectory getModDirectory;
    private readonly GetCollectionForObject getCollectionForObject;
    private readonly ResolvePlayerPath resolvePlayerPath;
    private readonly AddMod addMod;
    private readonly ReloadMod reloadMod;
    private readonly TrySetMod trySetMod;
    private readonly TrySetModPriority trySetModPriority;
    private PendingPlay? pending;

    private sealed class PendingPlay
    {
        public required Emote Source { get; init; }
        public required ushort TargetId { get; init; }
        public required DateTime StartedAtUtc { get; init; }
        public DateTime NextAttemptAtUtc { get; set; }
    }

    public EmoteSwapService(Action<string> setStatus, DirectEmoteFallback directFallback)
    {
        this.setStatus = setStatus;
        this.directFallback = directFallback;
        getModDirectory = new GetModDirectory(Plugin.PluginInterface);
        getCollectionForObject = new GetCollectionForObject(Plugin.PluginInterface);
        resolvePlayerPath = new ResolvePlayerPath(Plugin.PluginInterface);
        addMod = new AddMod(Plugin.PluginInterface);
        reloadMod = new ReloadMod(Plugin.PluginInterface);
        trySetMod = new TrySetMod(Plugin.PluginInterface);
        trySetModPriority = new TrySetModPriority(Plugin.PluginInterface);
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public bool TryQueue(Emote source, out string status)
    {
        try
        {
            if (!TryBuildAndEnable(source, out var targetId, out status))
                return false;

            var now = DateTime.UtcNow;
            pending = new PendingPlay
            {
                Source = source,
                TargetId = targetId,
                StartedAtUtc = now,
                NextAttemptAtUtc = now + ApplyDelay,
            };
            status = "未習得エモートをPenumbraへ一時設定しています。";
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "未習得エモートのPenumbra Swapを構築できませんでした。");
            status = $"Penumbra Swapを構築できませんでした（{exception.GetType().Name}）。";
            return false;
        }
    }

    private bool TryBuildAndEnable(Emote source, out ushort targetId, out string status)
    {
        targetId = 0;
        var sourceTimelineId = FirstTimelineId(source);
        if (sourceTimelineId == 0 || RelativePapPath(sourceTimelineId) is not { } sourceRelativePath)
        {
            status = "このエモートには差し替え可能な立ちモーションがありません。";
            return false;
        }

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local is null || local.Customize.Length <= (int)CustomizeIndex.Tribe)
        {
            status = "現在のキャラクター情報を取得できませんでした。";
            return false;
        }

        var skeleton = RaceGenderData.SkeletonFromCustomize(
            local.Customize[(int)CustomizeIndex.Race],
            local.Customize[(int)CustomizeIndex.Tribe],
            local.Customize[(int)CustomizeIndex.Gender]);
        var fallbackOrder = EmotePathHelper.GetFallbackOrder(skeleton);
        var sourcePath = FindSourcePath(sourceRelativePath, fallbackOrder);
        if (sourcePath is null || ReadSourceBytes(sourcePath.Value) is not { } sourceBytes)
        {
            status = "差し替え元のPAPアニメーションを取得できませんでした。";
            return false;
        }

        var sourceTimeline = Plugin.DataManager.GetExcelSheet<ActionTimeline>().GetRow(sourceTimelineId);
        foreach (var candidate in UnlockedCandidates(source, sourceTimeline.Pause))
        {
            var candidateTimelineId = FirstTimelineId(candidate);
            if (RelativePapPath(candidateTimelineId) is not { } targetRelativePath)
                continue;

            var targetPath = FindVanillaPath(targetRelativePath, fallbackOrder);
            if (targetPath is null || IsPathModded(targetPath)
                || Plugin.DataManager.GetFile(targetPath)?.Data is not { } targetBytes)
                continue;

            var requiredNames = PapAnimationNames.Read(targetBytes);
            if (requiredNames.Count == 0)
                continue;

            byte[] retargeted;
            try
            {
                retargeted = PapRetargeter.Retarget(sourceBytes, requiredNames, removeAnimationLock: true, out _);
            }
            catch (Exception exception)
            {
                Plugin.Log.Debug(exception, $"エモート{candidate.RowId}へのPAPリターゲットに失敗しました。");
                continue;
            }

            if (!WriteAndEnableMod(targetPath, retargeted, out status))
                return false;

            targetId = (ushort)candidate.RowId;
            return true;
        }

        status = "差し替え先に使える習得済みエモートが見つかりませんでした。";
        return false;
    }

    private static IEnumerable<Emote> UnlockedCandidates(Emote source, bool pause)
    {
        var timelines = Plugin.DataManager.GetExcelSheet<ActionTimeline>();
        return Plugin.DataManager.GetExcelSheet<Emote>()
            .Where(candidate => candidate.RowId is > 0 and <= ushort.MaxValue
                && candidate.RowId != source.RowId
                && candidate.Icon != 0
                && candidate.TextCommand.IsValid
                && Plugin.UnlockState.IsEmoteUnlocked(candidate)
                && FirstTimelineId(candidate) is var timelineId and not 0
                && timelines.GetRow(timelineId).Pause == pause)
            .OrderBy(candidate => candidate.ActionTimeline.Count(timeline => timeline.RowId != 0))
            .ThenBy(candidate => candidate.RowId);
    }

    private (string Requested, string Resolved)? FindSourcePath(
        string relativePath, IReadOnlyList<string> fallbackOrder)
    {
        foreach (var skeleton in fallbackOrder)
        {
            var requested = EmotePathHelper.GetSkeletonPath(skeleton, relativePath);
            var resolved = resolvePlayerPath.Invoke(requested) ?? requested;
            if (!string.Equals(resolved, requested, StringComparison.OrdinalIgnoreCase)
                || Plugin.DataManager.GetFile(requested) != null)
                return (requested, resolved);
        }
        return null;
    }

    private static string? FindVanillaPath(string relativePath, IReadOnlyList<string> fallbackOrder)
    {
        foreach (var skeleton in fallbackOrder)
        {
            var requested = EmotePathHelper.GetSkeletonPath(skeleton, relativePath);
            if (Plugin.DataManager.GetFile(requested) != null)
                return requested;
        }
        return null;
    }

    private bool IsPathModded(string requested)
    {
        var resolved = resolvePlayerPath.Invoke(requested) ?? requested;
        return !string.Equals(resolved, requested, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? ReadSourceBytes((string Requested, string Resolved) source)
    {
        if (!string.Equals(source.Requested, source.Resolved, StringComparison.OrdinalIgnoreCase)
            && File.Exists(source.Resolved))
            return File.ReadAllBytes(source.Resolved);
        return Plugin.DataManager.GetFile(source.Requested)?.Data;
    }

    private bool WriteAndEnableMod(string targetPath, byte[] papBytes, out string status)
    {
        var root = getModDirectory.Invoke();
        var collection = getCollectionForObject.Invoke(0);
        if (string.IsNullOrWhiteSpace(root) || !collection.ObjectValid)
        {
            status = "PenumbraのModフォルダーまたはコレクションを取得できませんでした。";
            return false;
        }

        var modDirectory = Path.Combine(root, ModDirectoryName);
        var filesDirectory = Path.Combine(modDirectory, "files");
        Directory.CreateDirectory(filesDirectory);
        File.WriteAllBytes(Path.Combine(filesDirectory, "active.pap"), papBytes);

        WriteJson(Path.Combine(modDirectory, "meta.json"), new
        {
            FileVersion = 3,
            Name = "AltMate Emote Swap",
            Author = "AltMate",
            Description = "AltMateが未習得エモート再生用に自動生成します。",
            Version = "1.0.0",
            Website = "https://github.com/naco-ff14/AltMate",
            ModTags = Array.Empty<string>(),
        });
        WriteJson(Path.Combine(modDirectory, "default_mod.json"), new
        {
            Name = string.Empty,
            Priority = 0,
            Files = new Dictionary<string, string> { [targetPath] = "files/active.pap" },
            FileSwaps = new Dictionary<string, string>(),
            Manipulations = Array.Empty<object>(),
        });

        var reloadResult = reloadMod.Invoke(ModDirectoryName, string.Empty);
        if (reloadResult == PenumbraApiEc.ModMissing && addMod.Invoke(ModDirectoryName) != PenumbraApiEc.Success)
        {
            status = "Penumbraへ一時Modを登録できませんでした。";
            return false;
        }
        if (reloadResult is not (PenumbraApiEc.Success or PenumbraApiEc.ModMissing))
        {
            status = $"Penumbraが一時Modを読み込めませんでした（{reloadResult}）。";
            return false;
        }

        var collectionId = collection.EffectiveCollection.Id;
        var enabled = trySetMod.Invoke(collectionId, ModDirectoryName, true);
        if (enabled is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
        {
            status = $"Penumbraで一時Modを有効化できませんでした（{enabled}）。";
            return false;
        }
        trySetModPriority.Invoke(collectionId, ModDirectoryName, ModPriority);
        status = "Penumbra Swapを準備しました。";
        return true;
    }

    private static void WriteJson(string path, object value)
        => File.WriteAllText(path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private unsafe void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        if (pending is not { } play || DateTime.UtcNow < play.NextAttemptAtUtc)
            return;

        var manager = EmoteManager.Instance();
        if (manager != null && manager->CanExecuteEmote(play.TargetId) && manager->ExecuteEmote(play.TargetId))
        {
            pending = null;
            setStatus("Penumbra Swapで未習得エモートを再生しました。");
            return;
        }

        var now = DateTime.UtcNow;
        if (now - play.StartedAtUtc < RetryLimit)
        {
            play.NextAttemptAtUtc = now + RetryInterval;
            return;
        }

        pending = null;
        if (!directFallback(play.Source, out var message))
            setStatus("ゲーム本体が代替エモートを実行できず、直接再生にも失敗しました。");
        else
            setStatus(message);
    }

    private static ushort FirstTimelineId(Emote emote)
    {
        var rowId = emote.ActionTimeline[0].RowId;
        return rowId is > 0 and <= ushort.MaxValue ? (ushort)rowId : (ushort)0;
    }

    private static string? RelativePapPath(ushort timelineId)
    {
        if (timelineId == 0)
            return null;
        var timeline = Plugin.DataManager.GetExcelSheet<ActionTimeline>().GetRow(timelineId);
        var key = timeline.Key.ToString();
        return string.IsNullOrWhiteSpace(key) ? null : $"bt_common/{key}.pap";
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        pending = null;
        try
        {
            var collection = getCollectionForObject.Invoke(0);
            if (collection.ObjectValid)
                trySetMod.Invoke(collection.EffectiveCollection.Id, ModDirectoryName, false);
        }
        catch
        {
            // Penumbraが先に終了している場合は何もしない。
        }
    }
}
