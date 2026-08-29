using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace AltMate;

internal sealed class SharedConfigurationStore : IDisposable
{
    private const string MutexName = "Local\\AltMate.SharedConfiguration.v1";
    private readonly Mutex mutex = new(false, MutexName);
    private readonly string path;
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private long knownRevision;
    private DateTime lastPollUtc;
    private Configuration settingsBaseline = new();

    internal SharedConfigurationStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltMate");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "shared-config.json");
    }

    internal long LoadInto(Configuration target)
    {
        if (!TryEnter())
            return knownRevision;
        try
        {
            var shared = ReadUnsafe();
            if (shared is null)
            {
                var initial = new SharedDocument { Revision = 1, Configuration = Clone(target) };
                WriteUnsafe(initial);
                knownRevision = initial.Revision;
            }
            else
            {
                MergeInto(target, shared.Configuration, preferIncomingSettings: true);
                knownRevision = shared.Revision;
            }
            settingsBaseline = Clone(target);
            return knownRevision;
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal long SaveMerged(Configuration current, bool includeSharedSettings)
    {
        if (!TryEnter())
            return knownRevision;
        try
        {
            var document = ReadUnsafe() ?? new SharedDocument();
            var merged = document.Configuration ?? new Configuration();
            MergeInto(merged, current, preferIncomingSettings: false);
            if (includeSharedSettings)
                ApplyChangedSettings(merged, current, settingsBaseline);
            document.Configuration = merged;
            document.Revision = Math.Max(document.Revision, knownRevision) + 1;
            WriteUnsafe(document);
            // データのみの保存では、このクライアントで編集中の共通設定を
            // 共有ファイルの旧値で巻き戻さない。
            MergeInto(current, merged, preferIncomingSettings: includeSharedSettings);
            if (includeSharedSettings)
                settingsBaseline = Clone(current);
            knownRevision = document.Revision;
            return knownRevision;
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal bool Poll(Configuration target, out long revision)
    {
        revision = knownRevision;
        var now = DateTime.UtcNow;
        if (now - lastPollUtc < TimeSpan.FromSeconds(3))
            return false;
        lastPollUtc = now;
        return ReloadIfNewer(target, knownRevision + 1, out revision);
    }

    internal bool ReloadIfNewer(Configuration target, long minimumRevision, out long revision)
    {
        revision = knownRevision;
        if (!TryEnter())
            return false;
        try
        {
            var document = ReadUnsafe();
            if (document is null || document.Revision < minimumRevision || document.Revision <= knownRevision)
                return false;
            MergeInto(target, document.Configuration, preferIncomingSettings: true);
            settingsBaseline = Clone(target);
            knownRevision = document.Revision;
            revision = knownRevision;
            return true;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private bool TryEnter()
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { return true; }
    }

    private SharedDocument? ReadUnsafe()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SharedDocument>(File.ReadAllText(path), jsonOptions)
                : null;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "AltMate共有設定を読み込めませんでした。直前の設定を維持します。");
            return null;
        }
    }

    private void WriteUnsafe(SharedDocument document)
    {
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, jsonOptions));
        if (File.Exists(path))
        {
            var backup = path + ".bak";
            try { File.Replace(temporary, path, backup, true); }
            catch (PlatformNotSupportedException) { File.Move(temporary, path, true); }
        }
        else
            File.Move(temporary, path);
    }

    private static Configuration Clone(Configuration source) =>
        JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(source)) ?? new Configuration();

    private static void MergeInto(Configuration target, Configuration? incoming, bool preferIncomingSettings)
    {
        if (incoming is null) return;
        var incomingCycleIsNewer = incoming.OpenPlotsCycleStartsAtUtc is { } incomingCycle &&
            (target.OpenPlotsCycleStartsAtUtc is null || incomingCycle > target.OpenPlotsCycleStartsAtUtc.Value);
        if (incomingCycleIsNewer)
            target.OpenPlots.Clear();
        foreach (var pair in incoming.Characters)
        {
            if (!target.Characters.TryGetValue(pair.Key, out var existing) ||
                pair.Value.LastCheckedAt >= existing.LastCheckedAt)
                target.Characters[pair.Key] = pair.Value;
        }
        foreach (var incomingWard in incoming.OpenPlots.GroupBy(x =>
                     (x.WorldId, x.TerritoryTypeId, x.WardNumber)))
        {
            var newestIncoming = incomingWard.Max(x => x.CheckedAt);
            var newestCurrent = target.OpenPlots
                .Where(x => x.WorldId == incomingWard.Key.WorldId &&
                            x.TerritoryTypeId == incomingWard.Key.TerritoryTypeId &&
                            x.WardNumber == incomingWard.Key.WardNumber)
                .Select(x => x.CheckedAt).DefaultIfEmpty(DateTime.MinValue).Max();
            if (newestIncoming < newestCurrent) continue;
            target.OpenPlots.RemoveAll(x => x.WorldId == incomingWard.Key.WorldId &&
                                            x.TerritoryTypeId == incomingWard.Key.TerritoryTypeId &&
                                            x.WardNumber == incomingWard.Key.WardNumber);
            target.OpenPlots.AddRange(incomingWard);
        }
        foreach (var pair in incoming.CharacterGil)
        {
            if (!target.CharacterGil.TryGetValue(pair.Key, out var current))
            {
                target.CharacterGil[pair.Key] = pair.Value;
                continue;
            }
            if (pair.Value.UpdatedAt >= current.UpdatedAt)
            {
                current.CharacterName = pair.Value.CharacterName;
                current.WorldName = pair.Value.WorldName;
                current.Gil = pair.Value.Gil;
                current.UpdatedAt = pair.Value.UpdatedAt;
            }
            foreach (var retainer in pair.Value.Retainers)
                if (!current.Retainers.TryGetValue(retainer.Key, out var existingRetainer) ||
                    retainer.Value.UpdatedAt >= existingRetainer.UpdatedAt)
                    current.Retainers[retainer.Key] = retainer.Value;
        }
        foreach (var pair in incoming.CustomDeliveryCharacters)
        {
            if (!target.CustomDeliveryCharacters.TryGetValue(pair.Key, out var existing) ||
                pair.Value.UpdatedAt >= existing.UpdatedAt)
                target.CustomDeliveryCharacters[pair.Key] = pair.Value;
        }
        foreach (var pair in incoming.CrafterLevelingCharacters)
        {
            if (!target.CrafterLevelingCharacters.TryGetValue(pair.Key, out var existing) ||
                pair.Value.Progress.UpdatedAt >= existing.Progress.UpdatedAt)
                target.CrafterLevelingCharacters[pair.Key] = pair.Value;
        }
        foreach (var pair in incoming.HousingDemolition)
        {
            if (!target.HousingDemolition.TryGetValue(pair.Key, out var existing) ||
                pair.Value.UpdatedAt >= existing.UpdatedAt)
                target.HousingDemolition[pair.Key] = pair.Value;
        }
        foreach (var pair in incoming.FreeCompanyGil)
        {
            if (!target.FreeCompanyGil.TryGetValue(pair.Key, out var current))
            {
                target.FreeCompanyGil[pair.Key] = pair.Value;
                continue;
            }
            if (pair.Value.UpdatedAt >= current.UpdatedAt &&
                (pair.Value.Gil != 0 || current.Gil == 0 || pair.Value.GilConfirmed))
            {
                current.FreeCompanyId = pair.Value.FreeCompanyId;
                current.Name = pair.Value.Name;
                current.WorldName = pair.Value.WorldName;
                current.Gil = pair.Value.Gil;
                current.GilConfirmed = pair.Value.GilConfirmed;
                current.UpdatedAt = pair.Value.UpdatedAt;
                current.LastCheckedByContentId = pair.Value.LastCheckedByContentId;
                current.LastCheckedByName = pair.Value.LastCheckedByName;
            }
            if (pair.Value.SubmarinesUpdatedAt >= current.SubmarinesUpdatedAt)
            {
                current.SubmarinesUpdatedAt = pair.Value.SubmarinesUpdatedAt;
                current.Submarines = pair.Value.Submarines;
            }
            if (pair.Value.TreasureVoyagesUpdatedAt >= current.TreasureVoyagesUpdatedAt)
            {
                current.TreasureVoyagesUpdatedAt = pair.Value.TreasureVoyagesUpdatedAt;
                current.TreasureVoyages = pair.Value.TreasureVoyages;
            }
        }
        target.Version = Math.Max(target.Version, incoming.Version);
        if (incomingCycleIsNewer || target.OpenPlotsCycleStartsAtUtc is null)
        {
            target.OpenPlotsCycleStartsAtUtc = incoming.OpenPlotsCycleStartsAtUtc;
            target.CycleAnchorUtc = incoming.CycleAnchorUtc;
        }
        if (!preferIncomingSettings) return;
        target.LinkEnabled = incoming.LinkEnabled;
        target.LinkLeaderContentId = incoming.LinkLeaderContentId;
        target.AutoFollowEnabled = incoming.AutoFollowEnabled;
        target.AutoRidePillionEnabled = incoming.AutoRidePillionEnabled;
        target.MountRouletteFallbackEnabled = incoming.MountRouletteFallbackEnabled;
        target.AutoAcceptPartyInviteEnabled = incoming.AutoAcceptPartyInviteEnabled;
        target.PauseLinkInCombat = incoming.PauseLinkInCombat;
        target.FollowStartDistance = incoming.FollowStartDistance;
        target.VnavmeshStuckRecoveryEnabled = incoming.VnavmeshStuckRecoveryEnabled;
        target.SyncLeaderInteractionEnabled = incoming.SyncLeaderInteractionEnabled;
        target.CombatLinkEnabled = incoming.CombatLinkEnabled;
        target.UseBossModReborn = incoming.UseBossModReborn;
        target.UseRotationSolverReborn = incoming.UseRotationSolverReborn;
        target.CombatStopDelaySeconds = incoming.CombatStopDelaySeconds;
        target.OccultAethernetSyncEnabled = incoming.OccultAethernetSyncEnabled;
        target.SyncReturnEnabled = incoming.SyncReturnEnabled;
        target.SyncDutyCommenceEnabled = incoming.SyncDutyCommenceEnabled;
        target.SyncTeleportInvitationEnabled = incoming.SyncTeleportInvitationEnabled;
        target.SyncRegularTeleportEnabled = incoming.SyncRegularTeleportEnabled;
        target.SyncCityAethernetEnabled = incoming.SyncCityAethernetEnabled;
        target.SyncResidentialAethernetEnabled = incoming.SyncResidentialAethernetEnabled;
        target.SyncZoneBoundaryEnabled = incoming.SyncZoneBoundaryEnabled;
        target.SyncFreeCompanyEstateEnabled = incoming.SyncFreeCompanyEstateEnabled;
        target.AutoOpenNearbyTreasureEnabled = incoming.AutoOpenNearbyTreasureEnabled;
        target.RoleBasedFpsEnabled = incoming.RoleBasedFpsEnabled;
        target.LeaderFpsLimit = incoming.LeaderFpsLimit;
        target.FollowerFpsLimit = incoming.FollowerFpsLimit;
        target.CustomDeliverySettings = incoming.CustomDeliverySettings ?? new CustomDeliverySettings();
        target.Language = incoming.Language;
        target.ShowChatMessages = incoming.ShowChatMessages;
        target.WindowBackgroundOpacity = incoming.WindowBackgroundOpacity;
        target.CompactWindowBackgroundOpacity = incoming.CompactWindowBackgroundOpacity;
        target.CompactMainMenu = incoming.CompactMainMenu;
        target.HiddenCompactMenuSections = incoming.HiddenCompactMenuSections is null
            ? new HashSet<int>()
            : new HashSet<int>(incoming.HiddenCompactMenuSections);
        if (!string.IsNullOrWhiteSpace(incoming.LocalLinkKey)) target.LocalLinkKey = incoming.LocalLinkKey;
    }

    private static void ApplyChangedSettings(Configuration target, Configuration current, Configuration baseline)
    {
        if (current.LinkEnabled != baseline.LinkEnabled) target.LinkEnabled = current.LinkEnabled;
        if (current.LinkLeaderContentId != baseline.LinkLeaderContentId) target.LinkLeaderContentId = current.LinkLeaderContentId;
        if (current.AutoFollowEnabled != baseline.AutoFollowEnabled) target.AutoFollowEnabled = current.AutoFollowEnabled;
        if (current.AutoRidePillionEnabled != baseline.AutoRidePillionEnabled) target.AutoRidePillionEnabled = current.AutoRidePillionEnabled;
        if (current.MountRouletteFallbackEnabled != baseline.MountRouletteFallbackEnabled) target.MountRouletteFallbackEnabled = current.MountRouletteFallbackEnabled;
        if (current.AutoAcceptPartyInviteEnabled != baseline.AutoAcceptPartyInviteEnabled) target.AutoAcceptPartyInviteEnabled = current.AutoAcceptPartyInviteEnabled;
        if (current.PauseLinkInCombat != baseline.PauseLinkInCombat) target.PauseLinkInCombat = current.PauseLinkInCombat;
        if (Math.Abs(current.FollowStartDistance - baseline.FollowStartDistance) > 0.001f) target.FollowStartDistance = current.FollowStartDistance;
        if (current.VnavmeshStuckRecoveryEnabled != baseline.VnavmeshStuckRecoveryEnabled) target.VnavmeshStuckRecoveryEnabled = current.VnavmeshStuckRecoveryEnabled;
        if (current.SyncLeaderInteractionEnabled != baseline.SyncLeaderInteractionEnabled) target.SyncLeaderInteractionEnabled = current.SyncLeaderInteractionEnabled;
        if (current.CombatLinkEnabled != baseline.CombatLinkEnabled) target.CombatLinkEnabled = current.CombatLinkEnabled;
        if (current.UseBossModReborn != baseline.UseBossModReborn) target.UseBossModReborn = current.UseBossModReborn;
        if (current.UseRotationSolverReborn != baseline.UseRotationSolverReborn) target.UseRotationSolverReborn = current.UseRotationSolverReborn;
        if (Math.Abs(current.CombatStopDelaySeconds - baseline.CombatStopDelaySeconds) > 0.001f) target.CombatStopDelaySeconds = current.CombatStopDelaySeconds;
        if (current.OccultAethernetSyncEnabled != baseline.OccultAethernetSyncEnabled) target.OccultAethernetSyncEnabled = current.OccultAethernetSyncEnabled;
        if (current.SyncReturnEnabled != baseline.SyncReturnEnabled) target.SyncReturnEnabled = current.SyncReturnEnabled;
        if (current.SyncDutyCommenceEnabled != baseline.SyncDutyCommenceEnabled) target.SyncDutyCommenceEnabled = current.SyncDutyCommenceEnabled;
        if (current.SyncTeleportInvitationEnabled != baseline.SyncTeleportInvitationEnabled) target.SyncTeleportInvitationEnabled = current.SyncTeleportInvitationEnabled;
        if (current.SyncRegularTeleportEnabled != baseline.SyncRegularTeleportEnabled) target.SyncRegularTeleportEnabled = current.SyncRegularTeleportEnabled;
        if (current.SyncCityAethernetEnabled != baseline.SyncCityAethernetEnabled) target.SyncCityAethernetEnabled = current.SyncCityAethernetEnabled;
        if (current.SyncResidentialAethernetEnabled != baseline.SyncResidentialAethernetEnabled) target.SyncResidentialAethernetEnabled = current.SyncResidentialAethernetEnabled;
        if (current.SyncZoneBoundaryEnabled != baseline.SyncZoneBoundaryEnabled) target.SyncZoneBoundaryEnabled = current.SyncZoneBoundaryEnabled;
        if (current.SyncFreeCompanyEstateEnabled != baseline.SyncFreeCompanyEstateEnabled) target.SyncFreeCompanyEstateEnabled = current.SyncFreeCompanyEstateEnabled;
        if (current.AutoOpenNearbyTreasureEnabled != baseline.AutoOpenNearbyTreasureEnabled) target.AutoOpenNearbyTreasureEnabled = current.AutoOpenNearbyTreasureEnabled;
        if (current.RoleBasedFpsEnabled != baseline.RoleBasedFpsEnabled) target.RoleBasedFpsEnabled = current.RoleBasedFpsEnabled;
        if (current.LeaderFpsLimit != baseline.LeaderFpsLimit) target.LeaderFpsLimit = current.LeaderFpsLimit;
        if (current.FollowerFpsLimit != baseline.FollowerFpsLimit) target.FollowerFpsLimit = current.FollowerFpsLimit;
        if (JsonSerializer.Serialize(current.CustomDeliverySettings) !=
            JsonSerializer.Serialize(baseline.CustomDeliverySettings))
            target.CustomDeliverySettings = current.CustomDeliverySettings;
        if (current.Language != baseline.Language) target.Language = current.Language;
        if (current.ShowChatMessages != baseline.ShowChatMessages) target.ShowChatMessages = current.ShowChatMessages;
        if (Math.Abs(current.WindowBackgroundOpacity - baseline.WindowBackgroundOpacity) > 0.001f)
            target.WindowBackgroundOpacity = current.WindowBackgroundOpacity;
        if (Math.Abs(current.CompactWindowBackgroundOpacity - baseline.CompactWindowBackgroundOpacity) > 0.001f)
            target.CompactWindowBackgroundOpacity = current.CompactWindowBackgroundOpacity;
        if (current.CompactMainMenu != baseline.CompactMainMenu)
            target.CompactMainMenu = current.CompactMainMenu;
        if (!(current.HiddenCompactMenuSections ?? new HashSet<int>())
            .SetEquals(baseline.HiddenCompactMenuSections ?? new HashSet<int>()))
            target.HiddenCompactMenuSections = new HashSet<int>(current.HiddenCompactMenuSections ?? new HashSet<int>());
        if (current.LocalLinkKey != baseline.LocalLinkKey) target.LocalLinkKey = current.LocalLinkKey;
    }

    public void Dispose() => mutex.Dispose();

    private sealed class SharedDocument
    {
        public long Revision { get; set; }
        public Configuration Configuration { get; set; } = new();
    }
}
