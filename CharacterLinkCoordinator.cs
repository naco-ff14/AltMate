using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Hooking;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AltMate;

public sealed class CharacterLinkCoordinator : IDisposable
{
    private const int Port = 47777;
    private static readonly IPAddress Group = IPAddress.Parse("239.255.77.77");
    private readonly Plugin plugin;
    private readonly ConcurrentDictionary<ulong, LinkedCharacterState> peers = new();
    private readonly ConcurrentQueue<LinkedCharacterState> receivedStates = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly object senderLock = new();
    private UdpClient? receiver;
    private UdpClient? sender;
    private Task? receiveTask;
    private DateTime lastBroadcastUtc;
    private DateTime lastFollowUtc;
    private DateTime lastRideUtc;
    private bool runtimeStopped;
    private bool stopFollowRequested;
    private bool followCommandActive;
    private bool localCharacterUnavailable;
    private DateTime localCharacterReadySinceUtc;
    private DateTime followCommandStartedUtc;
    private string? pendingGameCommand;
    private string? pendingTargetName;
    private int pendingCommandTicks;
    private bool pendingTargetApplied;
    private int pendingTargetConfirmAttempts;
    private bool combatAutomationActive;
    private DateTime? leaderCombatEndedUtc;
    private uint lastLeaderOccultAetheryteId;
    private uint pendingOccultDestinationId;
    private DateTime pendingOccultExpiresUtc;
    private DateTime lastOccultAttemptUtc;
    private bool leaderWasCastingReturn;
    private DateTime localReturnIntentExpiresUtc;
    private volatile bool linkedReturnRequested;
    private DateTime returnConfirmationExpiresUtc;
    private DateTime lastTravelPromptCheckUtc;
    private DateTime lastDutyCommenceUtc;
    private DateTime lastTeleportAcceptUtc;
    private DateTime lastReturnAttemptUtc;
    private DateTime lastTreasureInteractUtc;
    private DateTime lastGeneralTravelAttemptUtc;
    private DateTime pendingGeneralTravelExpiresUtc;
    private uint pendingGeneralAetheryteId;
    private byte pendingGeneralAetheryteSubIndex;
    private uint lastLeaderActiveAetheryteId;
    private uint lastLeaderActiveCustomAetheryteId;
    private uint lastLeaderResidentialAetheryteId;
    private uint lastLeaderTravelTerritoryType;
    private uint leaderCityTransitionSourceId;
    private DateTime leaderCityTransitionExpiresUtc;
    private float lastLeaderTravelX;
    private float lastLeaderTravelZ;
    private bool leaderTravelBaselineReady;
    private uint queuedLeaderCityAetheryteId;
    private uint queuedLeaderResidentialAetheryteId;
    private DateTime queuedLeaderAethernetExpiresUtc;
    private int outboundTeleportReady;
    private uint outboundTeleportAetheryteId;
    private byte outboundTeleportSubIndex;
    private int outboundHousingReady;
    private ushort outboundHousingWorldId;
    private ushort outboundHousingTerritoryId;
    private byte outboundHousingWard;
    private byte outboundHousingPlot;
    private ulong outboundHousingHouseId;
    private DateTime pendingHousingTravelExpiresUtc;
    private ushort pendingHousingWorldId;
    private ushort pendingHousingTerritoryId;
    private byte pendingHousingWard;
    private byte pendingHousingPlot;
    private bool housingMovementActive;
    private bool housingMovementObservedBusy;
    private bool lifestreamBusyThisFrame;
    private DateTime housingMovementStartedUtc;
    private DateTime housingMovementResumeUtc;
    private Hook<UseActionDelegate>? useActionHook;
    private Hook<TeleportDelegate>? teleportHook;
    private IAddonEventHandle? demiReturnYesHandle;

    private unsafe delegate bool UseActionDelegate(ActionManager* manager, ActionType actionType,
        uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode,
        uint comboRouteId, bool* outOptAreaTargeted);
    private unsafe delegate bool TeleportDelegate(Telepo* telepo, uint aetheryteId, byte subIndex);

    private static readonly OccultAetheryte[] OccultAetherytes =
    [
        new(1252, 4944, 830.7f, -696.0f),
        new(1252, 4928, -173.0f, -611.1f),
        new(1252, 4929, -358.1f, -121.0f),
        new(1252, 4930, 306.9f, 305.7f),
        new(1252, 4947, -384.1f, 281.4f),
        new(1346, 5571, 880.0f, 880.1f),
        new(1346, 5576, 451.7f, 528.8f),
        new(1346, 5572, 357.7f, -554.3f),
        new(1346, 5573, -547.2f, 594.4f),
        new(1346, 5574, -388.6f, -440.5f),
        new(1346, 5575, -13.7f, -40.5f),
    ];

    internal CharacterLinkCoordinator(Plugin plugin)
    {
        this.plugin = plugin;
        TryHookActions();
        TryHookTeleport();
        TryStartNetwork();
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public bool RuntimeStopped => runtimeStopped;
    public bool IsLeader => Plugin.PlayerState.ContentId != 0 &&
                            Plugin.PlayerState.ContentId == plugin.Configuration.LinkLeaderContentId;
    public string LastAction { get; private set; } = "待機中";
    public string DiagnosticMessage { get; private set; } = "未実行";
    public string CombatStatus { get; private set; } = "待機中";
    public string OccultTravelStatus { get; private set; } = "リーダーのエーテライト移動待ち";
    public string AreaSyncStatus { get; private set; } = "待機中";
    public string GeneralTravelStatus { get; private set; } = "リーダーの移動待ち";
    public string HousingTravelStatus { get; private set; } = "リーダーのFCハウステレポ待ち";
    public string TreasureStatus { get; private set; } = "近くの宝箱を監視中";
    public string WorldLinkStatus { get; private set; } = "同じワールドで待機";
    public bool IsLifestreamLoaded => IsPluginLoaded("Lifestream");
    public LinkedCharacterState[] Peers => peers.Values
        .Where(x => DateTime.UtcNow - x.ReceivedAtUtc < TimeSpan.FromSeconds(5))
        .OrderBy(x => x.CharacterName).ToArray();

    public void EmergencyStop()
    {
        runtimeStopped = true;
        stopFollowRequested = true;
        LastAction = "緊急停止中";
        StopCombatAutomation();
        BroadcastControl("stop");
    }

    public void Resume()
    {
        runtimeStopped = false;
        LastAction = "待機中";
        CombatStatus = "待機中";
        BroadcastControl("resume");
    }

    public void SettingsChanged()
    {
        plugin.SaveSharedSettings();
        BroadcastSettings();
    }

    public void NotifySharedConfigurationChanged(long revision)
    {
        if (revision <= 0 || sender is null)
            return;
        try
        {
            var message = new LinkedCharacterState
            {
                Protocol = 1,
                LinkKey = plugin.Configuration.LocalLinkKey,
                Kind = "configrevision",
                ContentId = Plugin.PlayerState.ContentId,
                SharedConfigurationRevision = revision,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            lock (senderLock)
            {
                for (var i = 0; i < 3; i++)
                    sender?.Send(bytes, bytes.Length, new IPEndPoint(Group, Port));
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "共有設定の更新通知を送信できませんでした。");
        }
    }


    public void TestFollow()
    {
        if (!TryGetLeaderObject(out var leader, out var leaderObject))
            return;
        QueueTargetCommand(leaderObject, "/follow <t>");
        LastAction = "追従テスト実行";
        DiagnosticMessage = $"対象：{leader.CharacterName} → 送信待ち：/follow <t>";
    }

    public void TestRidePillion()
    {
        if (!TryGetLeaderObject(out var leader, out var leaderObject))
            return;
        if (TryRidePillion(leaderObject))
        {
            LastAction = "相乗りテスト実行";
            DiagnosticMessage = $"対象：{leader.CharacterName} → 相乗り要求済み";
        }
    }

    public void PlayEmote(ushort emoteId, ulong targetContentId)
    {
        if (emoteId == 0 || targetContentId == 0)
            return;
        if (targetContentId == Plugin.PlayerState.ContentId)
        {
            plugin.Animations.PlayLocal(emoteId);
            return;
        }
        if (peers.ContainsKey(targetContentId))
            BroadcastEmote(emoteId, targetContentId);
    }

    public void MoveToLeader()
    {
        if (!TryGetLeader(out var leader, out var error))
        {
            WorldLinkStatus = error;
            return;
        }
        if (!IsLifestreamLoaded)
        {
            WorldLinkStatus = "Lifestreamが読み込まれていません";
            return;
        }
        if (!IsLocalCharacterReady() || IsBlocked())
        {
            WorldLinkStatus = "現在は移動を開始できません";
            return;
        }

        var currentWorld = Plugin.PlayerState.CurrentWorld.Value.Name.ToString();
        var destinationAetheryteId = GetTerritoryAetheryteId(leader.TerritoryType);
        var destinationName = destinationAetheryteId == 0 ? string.Empty : GetAetheryteName(destinationAetheryteId);
        stopFollowRequested = true;
        if (!currentWorld.Equals(leader.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            var command = string.IsNullOrWhiteSpace(destinationName)
                ? $"/li {leader.WorldName}"
                : $"/li {leader.WorldName}, tp {destinationName}";
            Plugin.CommandManager.ProcessCommand(command);
            WorldLinkStatus = string.IsNullOrWhiteSpace(destinationName)
                ? $"{leader.WorldName}へ移動開始（現在地へ直接移動できるエーテライトなし）"
                : $"{leader.WorldName} → {destinationName}へ移動開始";
            LastAction = "リーダーのワールドへ手動合流中";
            return;
        }
        if (leader.TerritoryType == Plugin.ClientState.TerritoryType)
        {
            WorldLinkStatus = "すでにリーダーと同じワールド・エリアです";
            return;
        }
        if (destinationAetheryteId == 0)
        {
            WorldLinkStatus = "リーダーのエリアに直接移動できるエーテライトがありません";
            return;
        }
        try
        {
            var accepted = Plugin.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport")
                .InvokeFunc(destinationAetheryteId, 0);
            WorldLinkStatus = accepted
                ? $"{destinationName}へ移動開始"
                : "Lifestreamが別の処理中です。少し待って再実行してください";
            if (accepted)
                LastAction = "リーダーのエリアへ手動合流中";
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "リーダーへの手動合流をLifestreamへ依頼できませんでした。");
            WorldLinkStatus = "Lifestream IPCへ接続できません";
        }
    }

    private bool TryGetLeader(out LinkedCharacterState leader, out string error)
    {
        if (IsLeader)
        {
            leader = null!;
            error = "このキャラクターはリーダーです";
            LastAction = error;
            return false;
        }
        if (!peers.TryGetValue(plugin.Configuration.LinkLeaderContentId, out leader!) ||
            DateTime.UtcNow - leader.ReceivedAtUtc > TimeSpan.FromSeconds(3))
        {
            error = "リーダーへ接続できていません";
            LastAction = error;
            return false;
        }
        error = string.Empty;
        return true;
    }

    private bool TryGetLeaderObject(out LinkedCharacterState leader,
        out Dalamud.Game.ClientState.Objects.Types.IGameObject leaderObject)
    {
        leaderObject = null!;
        if (!TryGetLeader(out leader, out _))
            return false;
        var leaderName = leader.CharacterName;
        var found = Plugin.ObjectTable.PlayerObjects.FirstOrDefault(x =>
            x.Name.TextValue.Equals(leaderName, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            LastAction = "リーダーが表示範囲外です";
            return false;
        }
        leaderObject = found;
        return true;
    }

    private void TryStartNetwork()
    {
        try
        {
            receiver = new UdpClient(AddressFamily.InterNetwork);
            receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            receiver.ExclusiveAddressUse = false;
            receiver.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            receiver.JoinMulticastGroup(Group, IPAddress.Loopback);

            sender = new UdpClient(AddressFamily.InterNetwork);
            sender.MulticastLoopback = true;
            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);
            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                IPAddress.Loopback.GetAddressBytes());
            receiveTask = ReceiveLoopAsync(cancellation.Token);
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "AltMateのローカル連携通信を開始できませんでした。");
            LastAction = "ローカル通信を開始できませんでした";
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (receiver is null)
            return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await receiver.ReceiveAsync(token);
                var state = JsonSerializer.Deserialize<LinkedCharacterState>(result.Buffer);
                if (state is null || state.Protocol != 1 ||
                    !IsValidLinkKey(state.LinkKey))
                    continue;
                // Dalamudのゲーム状態と設定には受信スレッドから触れない。
                // Frameworkスレッド側で処理するため、ここではデータを積むだけにする。
                receivedStates.Enqueue(state);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Plugin.Log.Verbose(exception, "AltMate連携パケットを読み取れませんでした。");
            }
        }
    }

    private bool IsValidLinkKey(string? receivedKey)
    {
        var expected = plugin.Configuration.LocalLinkKey;
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(receivedKey))
            return false;
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var receivedBytes = System.Text.Encoding.UTF8.GetBytes(receivedKey);
        return expectedBytes.Length == receivedBytes.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, receivedBytes);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // ログアウト・キャラクター選択・エリア切替中は、ゲームオブジェクトや
        // ネイティブUIへ触れる処理を最優先で止める。予約済み操作も持ち越さない。
        if (!IsLocalCharacterReady())
        {
            ResetForLogout();
            return;
        }

        var now = DateTime.UtcNow;
        if (localCharacterUnavailable)
        {
            localCharacterUnavailable = false;
            localCharacterReadySinceUtc = now;
            LastAction = IsLeader ? "リーダーとして待機中" : "待機中";
        }

        // PlayerState.IsLoaded直後もExcel参照やObjectTableが安定しない場合があるため、
        // 1秒間は状態送信・ターゲット・ネイティブUI操作を再開しない。
        if (localCharacterReadySinceUtc != default &&
            now - localCharacterReadySinceUtc < TimeSpan.FromSeconds(1))
            return;

        DrainReceivedStates();
        FlushOutboundTeleport();
        FlushOutboundHousingTravel();

        // 移動停止は予約済み追従コマンドより先に処理する。
        if (stopFollowRequested)
        {
            pendingGameCommand = null;
            pendingTargetName = null;
            pendingTargetApplied = false;
            if (followCommandActive)
                ExecuteGameCommand("/automove off");
            followCommandActive = false;
            stopFollowRequested = false;
        }

        if (pendingGameCommand is not null)
        {
            if (--pendingCommandTicks <= 0)
            {
                if (pendingTargetName is not null)
                {
                    var targetName = pendingTargetName;
                    var target = Plugin.ObjectTable.PlayerObjects.FirstOrDefault(x =>
                        x.IsValid() && x.IsTargetable && x.Name.TextValue.Equals(targetName,
                            StringComparison.OrdinalIgnoreCase));
                    if (target is null)
                    {
                        pendingGameCommand = null;
                        pendingTargetName = null;
                        pendingTargetApplied = false;
                        DiagnosticMessage = $"送信中止：{targetName}が表示範囲外です";
                        LastAction = "リーダーが表示範囲外です";
                        return;
                    }

                    if (!pendingTargetApplied)
                    {
                        Plugin.TargetManager.Target = target;
                        pendingTargetApplied = true;
                        pendingTargetConfirmAttempts = 0;
                        pendingCommandTicks = 2;
                        return;
                    }

                    var actualTarget = Plugin.TargetManager.Target;
                    if (actualTarget is null || !actualTarget.IsValid() ||
                        !actualTarget.Name.TextValue.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (++pendingTargetConfirmAttempts < 10)
                        {
                            Plugin.TargetManager.Target = target;
                            pendingCommandTicks = 2;
                            return;
                        }
                        pendingGameCommand = null;
                        pendingTargetName = null;
                        pendingTargetApplied = false;
                        DiagnosticMessage = $"送信中止：{targetName}をターゲット確認できません";
                        LastAction = "リーダーをターゲットできません";
                        return;
                    }
                }
                // /follow は確認済みの現在ターゲットに対して引数なしで実行する。
                // キャラクター名を引数へ付けると、名前に空白がある場合にゲーム側で
                // 「1番目のターゲット名の指定が正しくありません」が発生する。
                var command = pendingGameCommand;
                pendingGameCommand = null;
                pendingTargetName = null;
                pendingTargetApplied = false;
                var sent = ExecuteGameCommand(command);
                if (sent && command.StartsWith("/follow", StringComparison.OrdinalIgnoreCase))
                {
                    followCommandActive = true;
                    followCommandStartedUtc = DateTime.UtcNow;
                }
                DiagnosticMessage = sent ? $"送信済み：{command}" : $"送信失敗：{command}";
            }
            return;
        }
        plugin.CheckSharedConfiguration();
        UpdateFollowState(now);
        UpdateTravelInterlock(now);
        if (now - lastBroadcastUtc >= TimeSpan.FromSeconds(1))
        {
            BroadcastState();
            lastBroadcastUtc = now;
            foreach (var stale in peers.Where(x => now - x.Value.ReceivedAtUtc > TimeSpan.FromSeconds(10)).Select(x => x.Key))
                peers.TryRemove(stale, out _);
        }

        UpdateNearbyTreasure(now);

        if (!plugin.Configuration.LinkEnabled || runtimeStopped || IsLeader)
            return;
        if (!TryGetLeader(out var leader, out _))
        {
            return;
        }

        var currentWorld = Plugin.PlayerState.CurrentWorld.Value.Name.ToString();
        if (!leader.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
        {
            ClearCrossWorldAutomation();
            WorldLinkStatus = $"別ワールドのため連携停止：{currentWorld} / {leader.WorldName}";
            LastAction = "リーダーと別ワールドのため連携停止";
            return;
        }
        WorldLinkStatus = "同じワールド・連携可能";

        UpdateCombatAutomation(leader, now);
        UpdateAreaTransitionSync(leader, now);
        UpdateGeneralTravelSync(leader, now);
        UpdateHousingTravelSync(now);
        UpdateOccultAethernetSync(leader, now);
        RunFollowerAutomation(leader, now);
    }

    private void DrainReceivedStates()
    {
        // 異常なパケット集中でも1フレームを占有しないよう上限を設ける。
        for (var count = 0; count < 128 && receivedStates.TryDequeue(out var state); count++)
        {
            switch (state.Kind)
            {
                case "stop":
                    runtimeStopped = true;
                    stopFollowRequested = true;
                    LastAction = "別クライアントから緊急停止";
                    continue;
                case "resume":
                    runtimeStopped = false;
                    LastAction = "待機中";
                    continue;
                case "settings":
                    ApplySettings(state);
                    continue;
                case "configrevision":
                    plugin.CheckSharedConfiguration(state.SharedConfigurationRevision);
                    continue;
                case "emote":
                    if (state.TargetContentId == Plugin.PlayerState.ContentId && state.EmoteId != 0)
                        plugin.Animations.PlayLocal(state.EmoteId);
                    continue;
                case "return":
                    if (state.ContentId == plugin.Configuration.LinkLeaderContentId)
                        linkedReturnRequested = true;
                    continue;
                case "teleport":
                    if (state.ContentId == plugin.Configuration.LinkLeaderContentId &&
                        state.DestinationAetheryteId != 0)
                    {
                        pendingGeneralAetheryteId = state.DestinationAetheryteId;
                        pendingGeneralAetheryteSubIndex = state.DestinationSubIndex;
                        pendingGeneralTravelExpiresUtc = DateTime.UtcNow.AddSeconds(45);
                        GeneralTravelStatus = $"通常テレポを受信：{GetAetheryteName(state.DestinationAetheryteId)}";
                    }
                    continue;
                case "housing":
                    if (state.ContentId == plugin.Configuration.LinkLeaderContentId &&
                        state.HousingWorldId != 0 && state.HousingWard != 0 && state.HousingPlot != 0)
                    {
                        pendingHousingWorldId = state.HousingWorldId;
                        pendingHousingTerritoryId = state.HousingTerritoryId;
                        pendingHousingWard = state.HousingWard;
                        pendingHousingPlot = state.HousingPlot;
                        pendingHousingTravelExpiresUtc = DateTime.UtcNow.AddSeconds(90);
                        pendingGeneralAetheryteId = 0;
                        HousingTravelStatus = $"FCハウス移動を受信：{FormatHousingAddress(state.HousingWorldId, state.HousingTerritoryId, state.HousingWard, state.HousingPlot)}";
                    }
                    continue;
            }

            if (state.ContentId == 0 || state.ContentId == Plugin.PlayerState.ContentId)
                continue;
            state.ReceivedAtUtc = DateTime.UtcNow;
            peers[state.ContentId] = state;
        }
    }

    private unsafe void UpdateNearbyTreasure(DateTime now)
    {
        if (!plugin.Configuration.AutoOpenNearbyTreasureEnabled ||
            !IsOccultTerritory(Plugin.ClientState.TerritoryType))
        {
            TreasureStatus = plugin.Configuration.AutoOpenNearbyTreasureEnabled
                ? "クレセントアイル内で待機"
                : "無効";
            return;
        }
        if (Plugin.Condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
                ConditionFlag.InCombat, ConditionFlag.Unconscious, ConditionFlag.Occupied,
                ConditionFlag.OccupiedInEvent, ConditionFlag.Casting, ConditionFlag.Casting87) ||
            now - lastTreasureInteractUtc < TimeSpan.FromMilliseconds(750))
            return;

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local is null)
            return;

        var treasure = Plugin.ObjectTable
            .Where(o => o.IsValid() && !o.IsDead &&
                        Vector2.Distance(new Vector2(local.Position.X, local.Position.Z),
                            new Vector2(o.Position.X, o.Position.Z)) <= 2f)
            .Where(o =>
            {
                var native = (GameObject*)(void*)o.Address;
                return native != null && native->ObjectKind == ObjectKind.Treasure &&
                       native->GetIsTargetable();
            })
            .OrderBy(o => Vector2.Distance(new Vector2(local.Position.X, local.Position.Z),
                new Vector2(o.Position.X, o.Position.Z)))
            .FirstOrDefault();
        if (treasure is null)
        {
            TreasureStatus = "近くの宝箱を監視中";
            return;
        }

        var treasureNative = (GameObject*)(void*)treasure.Address;
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return;
        lastTreasureInteractUtc = now;
        targetSystem->InteractWithObject(treasureNative, false);
        TreasureStatus = $"宝箱を開けました（{treasure.Name.TextValue}）";
    }

    private void UpdateAreaTransitionSync(LinkedCharacterState leader, DateTime now)
    {
        // GeneralAction 8 (Return) points to Action row 6 while casting.
        var leaderCastingReturn = leader.CastActionId == 6;
        if (plugin.Configuration.SyncReturnEnabled && IsOccultTerritory(leader.TerritoryType) &&
            leader.TerritoryType == Plugin.ClientState.TerritoryType &&
            (linkedReturnRequested || (leaderCastingReturn && !leaderWasCastingReturn)) &&
            !Plugin.Condition[ConditionFlag.Casting] && !IsBlocked() &&
            now - lastReturnAttemptUtc >= TimeSpan.FromSeconds(1))
        {
            lastReturnAttemptUtc = now;
            pendingGameCommand = null;
            pendingTargetName = null;
            stopFollowRequested = true;
            if (Plugin.Condition[ConditionFlag.Mounted])
            {
                ExecuteGeneralAction(23);
                AreaSyncStatus = "デミデジョンのためマウント解除";
                return;
            }
            if (ExecuteReturn())
            {
                linkedReturnRequested = false;
                returnConfirmationExpiresUtc = now.AddSeconds(12);
                AreaSyncStatus = "リーダーに合わせてデミデジョンを開始";
            }
            else
            {
                AreaSyncStatus = "デジョンの実行に失敗";
            }
        }
        leaderWasCastingReturn = leaderCastingReturn;

        if (now - lastTravelPromptCheckUtc < TimeSpan.FromMilliseconds(250))
            return;
        lastTravelPromptCheckUtc = now;

        if (plugin.Configuration.SyncReturnEnabled && now <= returnConfirmationExpiresUtc)
            TryAcceptReturnConfirmation();
        if (plugin.Configuration.SyncDutyCommenceEnabled &&
            now - lastDutyCommenceUtc >= TimeSpan.FromSeconds(2))
            TryCommenceDuty(now);
        if (plugin.Configuration.SyncTeleportInvitationEnabled &&
            now - lastTeleportAcceptUtc >= TimeSpan.FromSeconds(2))
            TryAcceptTeleportInvitation(now);
    }

    public unsafe void OnSelectYesnoOpened(AddonArgs args)
    {
        if (!IsLeader || !plugin.Configuration.SyncReturnEnabled ||
            !IsOccultTerritory(Plugin.ClientState.TerritoryType) ||
            DateTime.UtcNow > localReturnIntentExpiresUtc)
            return;

        var addon = (AddonSelectYesno*)args.Addon.Address;
        if (addon == null || addon->PromptText == null || addon->YesButton == null ||
            addon->YesButton->OwnerNode == null ||
            !ContainsReturnPrompt(addon->PromptText->NodeText.ToString()))
            return;

        demiReturnYesHandle = Plugin.AddonEventManager.AddEvent(
            (nint)addon, (nint)addon->YesButton->OwnerNode,
            AddonEventType.ButtonClick, OnLeaderDemiReturnConfirmed);
        AreaSyncStatus = "リーダーのデミデジョン確認待ち";
    }

    public void OnSelectYesnoClosed()
    {
        if (demiReturnYesHandle is null)
            return;
        Plugin.AddonEventManager.RemoveEvent(demiReturnYesHandle);
        demiReturnYesHandle = null;
    }

    private void OnLeaderDemiReturnConfirmed(AddonEventType _, AddonEventData __)
    {
        BroadcastControl("return", Plugin.PlayerState.ContentId);
        localReturnIntentExpiresUtc = DateTime.MinValue;
        AreaSyncStatus = "リーダーが確認・フォロワーへデミデジョン指示を送信";
    }

    private static unsafe bool ExecuteReturn()
    {
        try
        {
            var actionManager = ActionManager.Instance();
            return actionManager != null && actionManager->UseAction(ActionType.GeneralAction, 8);
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "デジョンを実行できませんでした。");
            return false;
        }
    }

    private static unsafe bool ExecuteGeneralAction(uint id)
    {
        var actionManager = ActionManager.Instance();
        return actionManager != null && actionManager->UseAction(ActionType.GeneralAction, id);
    }

    private unsafe void TryHookActions()
    {
        try
        {
            useActionHook = Plugin.InteropProvider.HookFromAddress<UseActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseAction, UseActionDetour);
            useActionHook.Enable();
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "デミデジョン操作の監視を開始できませんでした。");
        }
    }

    private unsafe void TryHookTeleport()
    {
        try
        {
            teleportHook = Plugin.InteropProvider.HookFromAddress<TeleportDelegate>(
                (nint)Telepo.MemberFunctionPointers.Teleport, TeleportDetour);
            teleportHook.Enable();
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "通常テレポの監視を開始できませんでした。");
        }
    }

    private unsafe bool TeleportDetour(Telepo* telepo, uint aetheryteId, byte subIndex)
    {
        TeleportInfo? housingDestination = null;
        try
        {
            if (telepo != null && Plugin.ObjectTable.LocalPlayer is not null)
            {
                // Telepo.Teleport のフック内で一覧を更新すると、内部処理へ再入する恐れがある。
                // 選択された時点の一覧からFCハウス情報を特定する。
                for (var i = 0; i < telepo->TeleportList.Count; i++)
                {
                    var info = telepo->TeleportList[i];
                    if (info.AetheryteId == aetheryteId && info.SubIndex == subIndex &&
                        info.EstateType == EstateType.FreeCompanyEstate &&
                        info.HouseId.Id != 0)
                    {
                        housingDestination = info;
                        break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "FCハウステレポ情報を取得できませんでした。");
        }

        var result = teleportHook!.Original(telepo, aetheryteId, subIndex);
        try
        {
            if (result && IsLeader && plugin.Configuration.SyncFreeCompanyEstateEnabled &&
                housingDestination is { } housing)
            {
                outboundHousingWorldId = housing.HouseId.WorldId;
                outboundHousingTerritoryId = housing.HouseId.TerritoryTypeId != 0
                    ? housing.HouseId.TerritoryTypeId
                    : housing.TerritoryId;
                outboundHousingWard = GetHousingWard(housing);
                outboundHousingPlot = GetHousingPlot(housing);
                outboundHousingHouseId = housing.HouseId.Id;
                Interlocked.Exchange(ref outboundHousingReady, 1);
            }
            else if (result && IsLeader && plugin.Configuration.SyncRegularTeleportEnabled && aetheryteId != 0)
            {
                outboundTeleportAetheryteId = aetheryteId;
                outboundTeleportSubIndex = subIndex;
                Interlocked.Exchange(ref outboundTeleportReady, 1);
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "通常テレポの通知を予約できませんでした。");
        }
        return result;
    }

    private void FlushOutboundTeleport()
    {
        if (Interlocked.Exchange(ref outboundTeleportReady, 0) == 0)
            return;
        BroadcastTravel("teleport", outboundTeleportAetheryteId, outboundTeleportSubIndex);
        GeneralTravelStatus = $"通常テレポを共有：{GetAetheryteName(outboundTeleportAetheryteId)}";
    }

    private void FlushOutboundHousingTravel()
    {
        if (Interlocked.Exchange(ref outboundHousingReady, 0) == 0)
            return;
        BroadcastHousingTravel(outboundHousingWorldId, outboundHousingTerritoryId,
            outboundHousingWard, outboundHousingPlot, outboundHousingHouseId);
        HousingTravelStatus = $"FCハウス移動を共有：{FormatHousingAddress(outboundHousingWorldId, outboundHousingTerritoryId, outboundHousingWard, outboundHousingPlot)}";
    }

    private unsafe bool UseActionDetour(ActionManager* manager, ActionType actionType,
        uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode,
        uint comboRouteId, bool* outOptAreaTargeted)
    {
        try
        {
            // クレセントアイルの「デミデジョン」も入力段階ではGeneralAction 8。
            if (IsLeader && plugin.Configuration.SyncReturnEnabled &&
                IsOccultTerritory(Plugin.ClientState.TerritoryType) &&
                actionType == ActionType.GeneralAction && actionId == 8)
            {
                localReturnIntentExpiresUtc = DateTime.UtcNow.AddSeconds(15);
                AreaSyncStatus = "リーダーのデミデジョン確認待ち";
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "デミデジョン操作を通知できませんでした。");
        }
        return useActionHook!.Original(manager, actionType, actionId, targetId, extraParam,
            mode, comboRouteId, outOptAreaTargeted);
    }

    private unsafe void TryAcceptReturnConfirmation()
    {
        var addon = (AddonSelectYesno*)Plugin.GameGui.GetAddonByName("SelectYesno").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->PromptText == null)
            return;
        var prompt = addon->PromptText->NodeText.ToString();
        if (!ContainsReturnPrompt(prompt))
            return;
        addon->AtkUnitBase.FireCallbackInt(0);
        returnConfirmationExpiresUtc = DateTime.MinValue;
        AreaSyncStatus = "デジョン確認を承認";
    }

    private unsafe void TryCommenceDuty(DateTime now)
    {
        var addon = (AddonContentsFinderConfirm*)Plugin.GameGui
            .GetAddonByName("ContentsFinderConfirm").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->CommenceButton == null ||
            !addon->CommenceButton->IsEnabled)
            return;

        addon->AtkUnitBase.Focus();
        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 8;
        addon->AtkUnitBase.FireCallback(1, values);
        lastDutyCommenceUtc = now;
        AreaSyncStatus = "コンテンツ突入を承認";
    }

    private unsafe void TryAcceptTeleportInvitation(DateTime now)
    {
        var addon = (AddonSelectYesno*)Plugin.GameGui.GetAddonByName("SelectYesno").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->PromptText == null ||
            addon->YesButton == null || !addon->YesButton->IsEnabled)
            return;
        var prompt = addon->PromptText->NodeText.ToString();
        if (!ContainsTeleportInvitation(prompt))
            return;

        addon->AtkUnitBase.FireCallbackInt(0);
        lastTeleportAcceptUtc = now;
        AreaSyncStatus = "テレポ勧誘を承認";
    }

    private static bool ContainsReturnPrompt(string prompt)
    {
        // SelectYesnoは共通画面なので、誤承認を防ぐため表示言語ごとの語句を確認する。
        // 内部状態や処理分岐には翻訳済みのUI表示文字列を使用しない。
        var normalized = prompt.Trim();
        return ContainsAny(normalized, "デジョン", "デミデジョン", "帰還") ||
               (ContainsAny(normalized, "開始地点") && ContainsAny(normalized, "戻ります", "戻る")) ||
               ContainsAny(normalized, "Return", "Demi-Return", "starting point");
    }

    private static bool ContainsTeleportInvitation(string prompt)
    {
        var hasTeleport = ContainsAny(prompt, "テレポ", "teleport");
        var looksLikeInvitation = ContainsAny(prompt, "一緒", "勧誘", "invitation", "party member", "join");
        return hasTeleport && looksLikeInvitation;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private void UpdateGeneralTravelSync(LinkedCharacterState leader, DateTime now)
    {
        if (!plugin.Configuration.SyncRegularTeleportEnabled &&
            !plugin.Configuration.SyncCityAethernetEnabled &&
            !plugin.Configuration.SyncResidentialAethernetEnabled)
        {
            GeneralTravelStatus = "無効";
            leaderTravelBaselineReady = false;
            return;
        }

        if (!leaderTravelBaselineReady)
        {
            lastLeaderActiveAetheryteId = leader.ActiveAetheryteId;
            lastLeaderActiveCustomAetheryteId = leader.ActiveCustomAetheryteId;
            lastLeaderResidentialAetheryteId = leader.ActiveResidentialAetheryteId;
            lastLeaderTravelTerritoryType = leader.TerritoryType;
            lastLeaderTravelX = leader.X;
            lastLeaderTravelZ = leader.Z;
            leaderTravelBaselineReady = true;
        }
        else
        {
            var leaderTerritoryChanged = leader.TerritoryType != lastLeaderTravelTerritoryType;
            if (leaderTerritoryChanged)
            {
                leaderCityTransitionSourceId = lastLeaderActiveCustomAetheryteId != 0
                    ? lastLeaderActiveCustomAetheryteId
                    : lastLeaderActiveAetheryteId;
                leaderCityTransitionExpiresUtc = now.AddSeconds(15);
            }

            var leaderTravelDistance = Vector2.Distance(
                new Vector2(lastLeaderTravelX, lastLeaderTravelZ),
                new Vector2(leader.X, leader.Z));
            var leaderResidentialPositionJumped = leaderTravelDistance >= 40f;
            var leaderCityPositionJumped = leaderTravelDistance >= 12f;
            if (leader.ActiveResidentialAetheryteId != 0 &&
                leader.ActiveResidentialAetheryteId != lastLeaderResidentialAetheryteId)
            {
                queuedLeaderResidentialAetheryteId = leader.ActiveResidentialAetheryteId;
                queuedLeaderAethernetExpiresUtc = now.AddSeconds(12);
            }
            if (leader.ActiveCustomAetheryteId != 0 &&
                leader.ActiveCustomAetheryteId != lastLeaderActiveCustomAetheryteId)
            {
                queuedLeaderCityAetheryteId = leader.ActiveCustomAetheryteId;
                queuedLeaderAethernetExpiresUtc = now.AddSeconds(12);
            }
            else if (leader.ActiveAetheryteId != 0 &&
                     leader.ActiveAetheryteId != lastLeaderActiveAetheryteId)
            {
                queuedLeaderCityAetheryteId = leader.ActiveAetheryteId;
                queuedLeaderAethernetExpiresUtc = now.AddSeconds(12);
            }
            var cityTransitionDetected = now <= leaderCityTransitionExpiresUtc &&
                queuedLeaderCityAetheryteId != 0 && leaderCityTransitionSourceId != 0 &&
                IsSameAethernetGroup(leaderCityTransitionSourceId, queuedLeaderCityAetheryteId);

            if (!IsBlocked() && plugin.Configuration.SyncResidentialAethernetEnabled &&
                leader.TerritoryType == Plugin.ClientState.TerritoryType &&
                leaderResidentialPositionJumped &&
                queuedLeaderResidentialAetheryteId != 0 &&
                now <= queuedLeaderAethernetExpiresUtc)
            {
                if (TryRequestAethernet("Lifestream.HousingAethernetTeleportById",
                        queuedLeaderResidentialAetheryteId, "住宅街", now))
                    queuedLeaderResidentialAetheryteId = 0;
            }
            else if (!IsBlocked() && plugin.Configuration.SyncCityAethernetEnabled &&
                     (leaderCityPositionJumped || cityTransitionDetected) &&
                     queuedLeaderCityAetheryteId != 0 &&
                     now <= queuedLeaderAethernetExpiresUtc)
            {
                if (TryRequestAethernet("Lifestream.AethernetTeleportById",
                        queuedLeaderCityAetheryteId, "都市内", now))
                {
                    queuedLeaderCityAetheryteId = 0;
                    leaderCityTransitionExpiresUtc = DateTime.MinValue;
                }
            }
            if (now > queuedLeaderAethernetExpiresUtc)
            {
                queuedLeaderCityAetheryteId = 0;
                queuedLeaderResidentialAetheryteId = 0;
            }
        }

        lastLeaderActiveAetheryteId = leader.ActiveAetheryteId;
        lastLeaderActiveCustomAetheryteId = leader.ActiveCustomAetheryteId;
        lastLeaderResidentialAetheryteId = leader.ActiveResidentialAetheryteId;
        lastLeaderTravelTerritoryType = leader.TerritoryType;
        lastLeaderTravelX = leader.X;
        lastLeaderTravelZ = leader.Z;

        if (!plugin.Configuration.SyncRegularTeleportEnabled || pendingGeneralAetheryteId == 0)
            return;
        if (now > pendingGeneralTravelExpiresUtc)
        {
            pendingGeneralAetheryteId = 0;
            GeneralTravelStatus = "通常テレポの再試行期限切れ";
            return;
        }
        if (IsBlocked() || now - lastGeneralTravelAttemptUtc < TimeSpan.FromSeconds(2))
            return;

        lastGeneralTravelAttemptUtc = now;
        try
        {
            var accepted = Plugin.PluginInterface
                .GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport")
                .InvokeFunc(pendingGeneralAetheryteId, pendingGeneralAetheryteSubIndex);
            if (accepted)
            {
                GeneralTravelStatus = $"フォロワーもテレポ開始：{GetAetheryteName(pendingGeneralAetheryteId)}";
                pendingGeneralAetheryteId = 0;
                stopFollowRequested = true;
            }
            else
                GeneralTravelStatus = "Lifestreamの受付待ち（自動再試行）";
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "通常テレポをLifestreamへ依頼できませんでした。");
            GeneralTravelStatus = "Lifestream IPCへ接続できません";
        }
    }

    private bool TryRequestAethernet(string ipcName, uint destinationId, string kind, DateTime now)
    {
        if (now - lastGeneralTravelAttemptUtc < TimeSpan.FromSeconds(2))
            return false;
        lastGeneralTravelAttemptUtc = now;
        try
        {
            var accepted = Plugin.PluginInterface.GetIpcSubscriber<uint, bool>(ipcName)
                .InvokeFunc(destinationId);
            GeneralTravelStatus = accepted
                ? $"{kind}移動を同期：{GetAetheryteName(destinationId)}"
                : $"{kind}移動の受付待ち（自動再試行）";
            if (accepted)
            {
                stopFollowRequested = true;
                LastAction = $"{kind}エーテライト移動中";
            }
            return accepted;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "{Kind}エーテライト移動をLifestreamへ依頼できませんでした。", kind);
            GeneralTravelStatus = "Lifestream IPCへ接続できません";
            return false;
        }
    }

    private static bool IsSameAethernetGroup(uint sourceId, uint destinationId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            var source = sheet.GetRow(sourceId);
            var destination = sheet.GetRow(destinationId);
            return source.AethernetGroup != 0 && source.AethernetGroup == destination.AethernetGroup;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateHousingTravelSync(DateTime now)
    {
        if (!plugin.Configuration.SyncFreeCompanyEstateEnabled)
        {
            HousingTravelStatus = "無効";
            ClearPendingHousingTravel();
            return;
        }
        if (pendingHousingWorldId == 0)
            return;
        if (now > pendingHousingTravelExpiresUtc)
        {
            HousingTravelStatus = "FCハウス移動の再試行期限切れ";
            ClearPendingHousingTravel();
            return;
        }
        if (IsBlocked() || now - lastGeneralTravelAttemptUtc < TimeSpan.FromSeconds(3))
            return;

        lastGeneralTravelAttemptUtc = now;
        try
        {
            var world = GetWorldName(pendingHousingWorldId);
            var district = GetResidentialCommandName(pendingHousingTerritoryId);
            if (string.IsNullOrEmpty(world) || string.IsNullOrEmpty(district))
            {
                HousingTravelStatus = "住宅住所をLifestream形式へ変換できません";
                ClearPendingHousingTravel();
                return;
            }

            stopFollowRequested = true;
            Plugin.CommandManager.ProcessCommand(
                $"/li {world} {district} {pendingHousingWard} {pendingHousingPlot}");
            HousingTravelStatus = "LifestreamでFC住宅の住所へ移動開始";
            StartHousingMovement(now);
            ClearPendingHousingTravel();
            stopFollowRequested = true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "FCハウスへの連動移動を実行できませんでした。");
            HousingTravelStatus = "移動処理の受付待ち（自動再試行）";
        }
    }

    private void ClearPendingHousingTravel()
    {
        pendingHousingWorldId = 0;
        pendingHousingTerritoryId = 0;
        pendingHousingWard = 0;
        pendingHousingPlot = 0;
    }

    private void StartHousingMovement(DateTime now)
    {
        housingMovementActive = true;
        housingMovementObservedBusy = false;
        housingMovementStartedUtc = now;
        housingMovementResumeUtc = DateTime.MinValue;
        pendingGameCommand = null;
        pendingTargetName = null;
        pendingTargetApplied = false;
    }

    private void UpdateTravelInterlock(DateTime now)
    {
        lifestreamBusyThisFrame = IsLifestreamBusy();
        if (!housingMovementActive)
            return;

        if (lifestreamBusyThisFrame)
        {
            housingMovementObservedBusy = true;
            housingMovementResumeUtc = DateTime.MinValue;
            LastAction = "Lifestreamの住宅移動完了待ち";
            return;
        }

        if (housingMovementObservedBusy)
        {
            if (housingMovementResumeUtc == DateTime.MinValue)
                housingMovementResumeUtc = now.AddSeconds(3);
            if (now >= housingMovementResumeUtc)
            {
                housingMovementActive = false;
                HousingTravelStatus = "FCハウスの区画前へ移動完了";
            }
            return;
        }

        if (now - housingMovementStartedUtc >= TimeSpan.FromSeconds(120))
        {
            housingMovementActive = false;
            HousingTravelStatus = "住宅移動の監視を終了（時間切れ）";
        }
    }

    private static bool IsLifestreamBusy()
    {
        try
        {
            return IsPluginLoaded("Lifestream") && Plugin.PluginInterface
                .GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    private static byte GetHousingWard(TeleportInfo info) =>
        info.Ward > 0 ? info.Ward : (byte)(info.HouseId.WardIndex + 1);

    private static byte GetHousingPlot(TeleportInfo info) =>
        info.Plot > 0 ? info.Plot : (byte)(info.HouseId.PlotIndex + 1);

    private static string GetWorldName(ushort worldId)
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()
                .GetRow(worldId).Name.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetResidentialCommandName(ushort territoryId)
    {
        try
        {
            var regionId = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                .GetRow(territoryId).PlaceNameRegion.RowId;
            return regionId switch
            {
                22 => "mist",
                23 => "lavender",
                24 => "goblet",
                25 => "empyreum",
                2402 => "shirogane",
                _ => string.Empty,
            };
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatHousingAddress(ushort worldId, ushort territoryId, byte ward, byte plot)
    {
        var world = GetWorldName(worldId);
        var district = GetResidentialCommandName(territoryId);
        return $"{(string.IsNullOrEmpty(world) ? $"World {worldId}" : world)} / {(string.IsNullOrEmpty(district) ? $"住宅街 {territoryId}" : district)} / {ward}区 {plot}番地";
    }

    private static string GetAetheryteName(uint id)
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>()
                .GetRow(id).PlaceName.Value.Name.ToString();
        }
        catch
        {
            return $"エーテライトID {id}";
        }
    }

    private static uint GetTerritoryAetheryteId(uint territoryType)
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>()
                .Where(x => x.IsAetheryte && x.Territory.RowId == territoryType)
                .Select(x => x.RowId).FirstOrDefault();
        }
        catch
        {
            return 0;
        }
    }

    private void UpdateOccultAethernetSync(LinkedCharacterState leader, DateTime now)
    {
        if (!plugin.Configuration.OccultAethernetSyncEnabled)
        {
            ResetOccultTravel("無効");
            return;
        }

        if (!IsOccultTerritory(leader.TerritoryType) || leader.TerritoryType != Plugin.ClientState.TerritoryType)
        {
            ResetOccultTravel("クレセントアイル内で待機");
            return;
        }

        var leaderNode = FindOccultAetheryte(leader.TerritoryType, leader.X, leader.Z);
        if (leaderNode is not null)
        {
            if (lastLeaderOccultAetheryteId == 0)
            {
                lastLeaderOccultAetheryteId = leaderNode.Value.PlaceNameId;
            }
            else if (lastLeaderOccultAetheryteId != leaderNode.Value.PlaceNameId)
            {
                lastLeaderOccultAetheryteId = leaderNode.Value.PlaceNameId;
                pendingOccultDestinationId = leaderNode.Value.PlaceNameId;
                pendingOccultExpiresUtc = now.AddSeconds(20);
                OccultTravelStatus = $"リーダーの移動を検出：{GetPlaceName(pendingOccultDestinationId)}";
            }
        }

        if (pendingOccultDestinationId == 0)
            return;
        if (now > pendingOccultExpiresUtc)
        {
            pendingOccultDestinationId = 0;
            OccultTravelStatus = "移動受付を終了（フォロワーがエーテライト付近にいません）";
            return;
        }
        if (IsBlocked() || now - lastOccultAttemptUtc < TimeSpan.FromSeconds(1.5))
            return;

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local is null)
            return;
        var localNode = FindOccultAetheryte(Plugin.ClientState.TerritoryType, local.Position.X, local.Position.Z);
        if (localNode is null)
        {
            OccultTravelStatus = "リーダーの移動を検出・フォロワーの接近待ち";
            return;
        }
        if (localNode.Value.PlaceNameId == pendingOccultDestinationId)
        {
            pendingOccultDestinationId = 0;
            OccultTravelStatus = $"到着済み：{GetPlaceName(localNode.Value.PlaceNameId)}";
            return;
        }
        if (!IsLifestreamLoaded)
        {
            OccultTravelStatus = "Lifestreamが読み込まれていません";
            return;
        }

        lastOccultAttemptUtc = now;
        try
        {
            var accepted = Plugin.PluginInterface
                .GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportByPlaceNameId")
                .InvokeFunc(pendingOccultDestinationId);
            if (accepted)
            {
                OccultTravelStatus = $"フォロワーも移動開始：{GetPlaceName(pendingOccultDestinationId)}";
                pendingOccultDestinationId = 0;
            }
            else
            {
                OccultTravelStatus = "Lifestreamの受付待ち（自動再試行）";
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "クレセントアイルの連動移動をLifestreamへ依頼できませんでした。");
            OccultTravelStatus = "Lifestream IPCへ接続できません";
        }
    }

    private void ResetOccultTravel(string status)
    {
        lastLeaderOccultAetheryteId = 0;
        pendingOccultDestinationId = 0;
        OccultTravelStatus = status;
    }

    private static bool IsOccultTerritory(uint territoryType) => territoryType is 1252 or 1346;

    private static OccultAetheryte? FindOccultAetheryte(uint territoryType, float x, float z)
    {
        const float detectionRadius = 30f;
        var position = new Vector2(x, z);
        return OccultAetherytes
            .Where(a => a.TerritoryType == territoryType)
            .Select(a => (Node: a, Distance: Vector2.Distance(position, new Vector2(a.X, a.Z))))
            .Where(x => x.Distance <= detectionRadius)
            .OrderBy(x => x.Distance)
            .Select(x => (OccultAetheryte?)x.Node)
            .FirstOrDefault();
    }

    private static string GetPlaceName(uint placeNameId)
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>()
                .GetRow(placeNameId).Name.ToString();
        }
        catch
        {
            return $"目的地ID {placeNameId}";
        }
    }

    private void UpdateCombatAutomation(LinkedCharacterState leader, DateTime now)
    {
        if (!plugin.Configuration.CombatLinkEnabled)
        {
            if (combatAutomationActive)
                StopCombatAutomation();
            CombatStatus = "無効";
            return;
        }

        if (leader.InCombat)
        {
            leaderCombatEndedUtc = null;
            if (!combatAutomationActive)
                StartCombatAutomation(leader);
            return;
        }

        if (!combatAutomationActive)
        {
            CombatStatus = "リーダーの戦闘開始待ち";
            return;
        }

        leaderCombatEndedUtc ??= now;
        var remaining = plugin.Configuration.CombatStopDelaySeconds -
                        (float)(now - leaderCombatEndedUtc.Value).TotalSeconds;
        if (remaining <= 0)
            StopCombatAutomation();
        else
            CombatStatus = $"戦闘終了待機中（あと{remaining:0.0}秒）";
    }

    private void StartCombatAutomation(LinkedCharacterState leader)
    {
        var messages = new System.Collections.Generic.List<string>();
        if (plugin.Configuration.UseBossModReborn && IsPluginLoaded("BossModReborn"))
        {
            Plugin.CommandManager.ProcessCommand($"/bmrai follow {leader.CharacterName}");
            Plugin.CommandManager.ProcessCommand("/bmrai forbidactions on");
            Plugin.CommandManager.ProcessCommand("/bmrai followcombat on");
            Plugin.CommandManager.ProcessCommand("/bmrai followtarget on");
            Plugin.CommandManager.ProcessCommand("/bmrai on");
            messages.Add("BMR");
        }
        if (plugin.Configuration.UseRotationSolverReborn &&
            (IsPluginLoaded("RotationSolver") || IsPluginLoaded("RotationSolverReborn")))
        {
            Plugin.CommandManager.ProcessCommand("/rsr Auto");
            messages.Add("RSR");
        }

        combatAutomationActive = messages.Count > 0;
        CombatStatus = combatAutomationActive
            ? $"戦闘連携中：{string.Join(" + ", messages)}"
            : "BMR／RSRが読み込まれていません";
    }

    private void StopCombatAutomation()
    {
        if (plugin.Configuration.UseRotationSolverReborn &&
            (IsPluginLoaded("RotationSolver") || IsPluginLoaded("RotationSolverReborn")))
            Plugin.CommandManager.ProcessCommand("/rsr Off");
        if (plugin.Configuration.UseBossModReborn && IsPluginLoaded("BossModReborn"))
            Plugin.CommandManager.ProcessCommand("/bmrai off");
        combatAutomationActive = false;
        leaderCombatEndedUtc = null;
        CombatStatus = "停止済み";
    }

    private static bool IsPluginLoaded(string internalName) =>
        Plugin.PluginInterface.InstalledPlugins.Any(x => x.IsLoaded &&
            x.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));

    private void BroadcastState()
    {
        if (sender is null || !IsLocalCharacterReady())
            return;
        try
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local is null || !IsLocalCharacterReady())
                return;
            var state = new LinkedCharacterState
            {
                Protocol = 1,
                LinkKey = plugin.Configuration.LocalLinkKey,
                Kind = "state",
                ContentId = Plugin.PlayerState.ContentId,
                CharacterName = Plugin.PlayerState.CharacterName,
                WorldName = Plugin.PlayerState.CurrentWorld.Value.Name.ToString(),
                TerritoryType = Plugin.ClientState.TerritoryType,
                X = local.Position.X,
                Y = local.Position.Y,
                Z = local.Position.Z,
                JobName = Plugin.PlayerState.ClassJob.Value.Abbreviation.ToString(),
                CurrentHp = local.CurrentHp,
                MaxHp = local.MaxHp,
                InCombat = Plugin.Condition[ConditionFlag.InCombat],
                Mounted = Plugin.Condition[ConditionFlag.Mounted],
                RidingPillion = Plugin.Condition[ConditionFlag.RidingPillion],
                CastActionType = local.CastActionType,
                CastActionId = local.CastActionId,
                ActiveAetheryteId = GetLifestreamId("Lifestream.GetActiveAetheryte"),
                ActiveCustomAetheryteId = GetLifestreamId("Lifestream.GetActiveCustomAetheryte"),
                ActiveResidentialAetheryteId = GetLifestreamId("Lifestream.GetActiveResidentialAetheryte"),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            lock (senderLock)
                sender?.Send(bytes, bytes.Length, new IPEndPoint(Group, Port));
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "AltMateの状態を送信できませんでした。");
        }
    }

    private void BroadcastControl(string kind, ulong contentId = 0)
    {
        if (sender is null)
            return;
        try
        {
            var message = new LinkedCharacterState
            {
                Protocol = 1, LinkKey = plugin.Configuration.LocalLinkKey, Kind = kind, ContentId = contentId,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            lock (senderLock)
            {
                for (var i = 0; i < 3; i++)
                    sender?.Send(bytes, bytes.Length, new IPEndPoint(Group, Port));
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "AltMateの停止指示を送信できませんでした。");
        }
    }

    private void BroadcastTravel(string kind, uint destinationId, byte subIndex)
    {
        if (sender is null)
            return;
        try
        {
            var message = new LinkedCharacterState
            {
                Protocol = 1,
                LinkKey = plugin.Configuration.LocalLinkKey,
                Kind = kind,
                ContentId = Plugin.PlayerState.ContentId,
                DestinationAetheryteId = destinationId,
                DestinationSubIndex = subIndex,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            lock (senderLock)
            {
                for (var i = 0; i < 3; i++)
                    sender?.Send(bytes, bytes.Length, new IPEndPoint(Group, Port));
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "AltMateの移動指示を送信できませんでした。");
        }
    }

    private void BroadcastEmote(ushort emoteId, ulong targetContentId)
    {
        if (sender is null)
            return;
        try
        {
            var message = new LinkedCharacterState
            {
                Protocol = 1,
                LinkKey = plugin.Configuration.LocalLinkKey,
                Kind = "emote",
                ContentId = Plugin.PlayerState.ContentId,
                TargetContentId = targetContentId,
                EmoteId = emoteId,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            lock (senderLock)
            {
                for (var i = 0; i < 3; i++)
                    sender?.Send(bytes, bytes.Length, new IPEndPoint(Group, Port));
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "エモート再生指示を送信できませんでした。");
        }
    }

    private void BroadcastHousingTravel(ushort worldId, ushort territoryId, byte ward, byte plot,
        ulong houseId)
    {
        if (sender is null)
            return;
        try
        {
            var message = new LinkedCharacterState
            {
                Protocol = 1,
                LinkKey = plugin.Configuration.LocalLinkKey,
                Kind = "housing",
                ContentId = Plugin.PlayerState.ContentId,
                HousingWorldId = worldId,
                HousingTerritoryId = territoryId,
                HousingWard = ward,
                HousingPlot = plot,
                HousingHouseId = houseId,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            lock (senderLock)
            {
                for (var i = 0; i < 3; i++)
                    sender?.Send(bytes, bytes.Length, new IPEndPoint(Group, Port));
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "AltMateのFCハウス移動指示を送信できませんでした。");
        }
    }

    private void BroadcastSettings()
    {
        if (sender is null)
            return;
        try
        {
            var message = new LinkedCharacterState
            {
                Protocol = 1,
                LinkKey = plugin.Configuration.LocalLinkKey,
                Kind = "settings",
                LinkEnabled = plugin.Configuration.LinkEnabled,
                LeaderContentId = plugin.Configuration.LinkLeaderContentId,
                AutoFollow = plugin.Configuration.AutoFollowEnabled,
                AutoRidePillion = plugin.Configuration.AutoRidePillionEnabled,
                PauseInCombat = plugin.Configuration.PauseLinkInCombat,
                FollowDistance = plugin.Configuration.FollowStartDistance,
                CombatLinkEnabled = plugin.Configuration.CombatLinkEnabled,
                UseBossModReborn = plugin.Configuration.UseBossModReborn,
                UseRotationSolverReborn = plugin.Configuration.UseRotationSolverReborn,
                CombatStopDelaySeconds = plugin.Configuration.CombatStopDelaySeconds,
                OccultAethernetSyncEnabled = plugin.Configuration.OccultAethernetSyncEnabled,
                SyncReturnEnabled = plugin.Configuration.SyncReturnEnabled,
                SyncDutyCommenceEnabled = plugin.Configuration.SyncDutyCommenceEnabled,
                SyncTeleportInvitationEnabled = plugin.Configuration.SyncTeleportInvitationEnabled,
                SyncRegularTeleportEnabled = plugin.Configuration.SyncRegularTeleportEnabled,
                SyncCityAethernetEnabled = plugin.Configuration.SyncCityAethernetEnabled,
                SyncResidentialAethernetEnabled = plugin.Configuration.SyncResidentialAethernetEnabled,
                SyncFreeCompanyEstateEnabled = plugin.Configuration.SyncFreeCompanyEstateEnabled,
                AutoOpenNearbyTreasureEnabled = plugin.Configuration.AutoOpenNearbyTreasureEnabled,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            lock (senderLock)
            {
                for (var i = 0; i < 3; i++)
                    sender?.Send(bytes, bytes.Length, new IPEndPoint(Group, Port));
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Verbose(exception, "AltMateの連携設定を送信できませんでした。");
        }
    }

    private void ApplySettings(LinkedCharacterState state)
    {
        plugin.Configuration.LinkEnabled = state.LinkEnabled;
        plugin.Configuration.LinkLeaderContentId = state.LeaderContentId;
        plugin.Configuration.AutoFollowEnabled = state.AutoFollow;
        plugin.Configuration.AutoRidePillionEnabled = state.AutoRidePillion;
        plugin.Configuration.PauseLinkInCombat = state.PauseInCombat;
        plugin.Configuration.FollowStartDistance = Math.Clamp(state.FollowDistance, 3f, 15f);
        plugin.Configuration.CombatLinkEnabled = state.CombatLinkEnabled;
        plugin.Configuration.UseBossModReborn = state.UseBossModReborn;
        plugin.Configuration.UseRotationSolverReborn = state.UseRotationSolverReborn;
        plugin.Configuration.CombatStopDelaySeconds = Math.Clamp(state.CombatStopDelaySeconds, 0f, 15f);
        plugin.Configuration.OccultAethernetSyncEnabled = state.OccultAethernetSyncEnabled;
        plugin.Configuration.SyncReturnEnabled = state.SyncReturnEnabled;
        plugin.Configuration.SyncDutyCommenceEnabled = state.SyncDutyCommenceEnabled;
        plugin.Configuration.SyncTeleportInvitationEnabled = state.SyncTeleportInvitationEnabled;
        plugin.Configuration.SyncRegularTeleportEnabled = state.SyncRegularTeleportEnabled;
        plugin.Configuration.SyncCityAethernetEnabled = state.SyncCityAethernetEnabled;
        plugin.Configuration.SyncResidentialAethernetEnabled = state.SyncResidentialAethernetEnabled;
        plugin.Configuration.SyncFreeCompanyEstateEnabled = state.SyncFreeCompanyEstateEnabled;
        plugin.Configuration.AutoOpenNearbyTreasureEnabled = state.AutoOpenNearbyTreasureEnabled;
    }

    private void RunFollowerAutomation(LinkedCharacterState leader, DateTime now)
    {
        if (!IsLocalCharacterReady())
        {
            ResetForLogout();
            return;
        }
        if (housingMovementActive || lifestreamBusyThisFrame)
        {
            LastAction = "Lifestream移動中のため追従を停止";
            return;
        }
        if (leader.WorldName != Plugin.PlayerState.CurrentWorld.Value.Name.ToString() ||
            leader.TerritoryType != Plugin.ClientState.TerritoryType)
        {
            LastAction = "リーダーと別エリアです";
            return;
        }
        if (IsBlocked() || (plugin.Configuration.PauseLinkInCombat &&
                            (leader.InCombat || Plugin.Condition[ConditionFlag.InCombat])))
        {
            LastAction = "安全条件により一時停止";
            return;
        }

        var local = Plugin.ObjectTable.LocalPlayer;
        var leaderObject = Plugin.ObjectTable.PlayerObjects.FirstOrDefault(x =>
            x.Name.TextValue.Equals(leader.CharacterName, StringComparison.OrdinalIgnoreCase));
        if (local is null || leaderObject is null)
        {
            LastAction = "リーダーが表示範囲外です";
            return;
        }

        var distance = Vector3.Distance(local.Position, leaderObject.Position);
        if (plugin.Configuration.AutoRidePillionEnabled && leader.Mounted &&
            !Plugin.Condition[ConditionFlag.Mounted] && !Plugin.Condition[ConditionFlag.RidingPillion])
        {
            if (distance <= 5f && now - lastRideUtc > TimeSpan.FromSeconds(2))
            {
                if (TryRidePillion(leaderObject))
                {
                    lastRideUtc = now;
                    LastAction = $"相乗りを実行しました（{distance:0.0}m）";
                }
                else
                {
                    LastAction = $"相乗り可能条件を待機中（{distance:0.0}m）";
                }
                return;
            }

            // 相乗り待ちでは通常の追従開始距離に関係なく、確実に相乗り可能距離まで近づく。
            if (distance > 4f && !followCommandActive &&
                now - lastFollowUtc > TimeSpan.FromSeconds(2))
            {
                QueueTargetCommand(leaderObject, "/follow <t>");
                lastFollowUtc = now;
                LastAction = $"相乗りのため接近中（{distance:0.0}m）";
                return;
            }
        }

        if (plugin.Configuration.AutoFollowEnabled && distance >= plugin.Configuration.FollowStartDistance &&
            !followCommandActive && now - lastFollowUtc > TimeSpan.FromSeconds(2))
        {
            QueueTargetCommand(leaderObject, "/follow <t>");
            lastFollowUtc = now;
            LastAction = $"リーダーを追従中（{distance:0.0}m）";
        }
        else if (distance < plugin.Configuration.FollowStartDistance)
        {
            LastAction = leader.Mounted
                ? $"相乗りを再試行待ち（{distance:0.0}m）"
                : $"リーダーの近くで待機中（{distance:0.0}m）";
        }
    }

    private static bool IsLocalCharacterReady() =>
        Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded &&
        Plugin.PlayerState.ContentId != 0 && Plugin.ObjectTable.LocalPlayer is not null &&
        Plugin.ClientState.TerritoryType != 0;

    private void ResetForLogout()
    {
        while (receivedStates.TryDequeue(out _))
        {
        }
        peers.Clear();
        pendingGameCommand = null;
        pendingTargetName = null;
        pendingTargetApplied = false;
        pendingTargetConfirmAttempts = 0;
        pendingCommandTicks = 0;
        stopFollowRequested = false;
        followCommandActive = false;
        combatAutomationActive = false;
        leaderCombatEndedUtc = null;
        linkedReturnRequested = false;
        returnConfirmationExpiresUtc = default;
        pendingGeneralAetheryteId = 0;
        pendingGeneralTravelExpiresUtc = default;
        pendingHousingWorldId = 0;
        pendingHousingTerritoryId = 0;
        pendingHousingWard = 0;
        pendingHousingPlot = 0;
        pendingHousingTravelExpiresUtc = default;
        pendingOccultDestinationId = 0;
        pendingOccultExpiresUtc = default;
        housingMovementActive = false;
        housingMovementObservedBusy = false;
        lifestreamBusyThisFrame = false;
        Interlocked.Exchange(ref outboundTeleportReady, 0);
        Interlocked.Exchange(ref outboundHousingReady, 0);
        leaderTravelBaselineReady = false;
        queuedLeaderCityAetheryteId = 0;
        queuedLeaderResidentialAetheryteId = 0;
        LastAction = "ログアウト中";
        localCharacterUnavailable = true;
        localCharacterReadySinceUtc = default;
    }

    private void ClearCrossWorldAutomation()
    {
        pendingGameCommand = null;
        pendingTargetName = null;
        followCommandActive = false;
        combatAutomationActive = false;
        leaderCombatEndedUtc = null;
        linkedReturnRequested = false;
        pendingGeneralAetheryteId = 0;
        pendingHousingWorldId = 0;
        pendingOccultDestinationId = 0;
        queuedLeaderCityAetheryteId = 0;
        queuedLeaderResidentialAetheryteId = 0;
        leaderTravelBaselineReady = false;
    }

    private static unsafe bool IsGameAutoRunning() => InputManager.IsAutoRunning();

    private static unsafe bool TryRidePillion(
        Dalamud.Game.ClientState.Objects.Types.IGameObject target)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (target.Address == nint.Zero || local is null || local.Address == nint.Zero)
            return false;
        if (Plugin.Condition[ConditionFlag.Mounted] ||
            Plugin.Condition[ConditionFlag.RidingPillion] ||
            Vector3.Distance(local.Position, target.Position) > 5f)
            return false;
        try
        {
            // RidePillionは乗せてもらう対象BattleCharaに対して座席を指定する。
            Plugin.TargetManager.Target = target;
            ((BattleChara*)target.Address)->RidePillion(0);
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "相乗り要求を実行できませんでした。");
            return false;
        }
    }

    private void UpdateFollowState(DateTime now)
    {
        if (!followCommandActive ||
            now - followCommandStartedUtc < TimeSpan.FromSeconds(1))
            return;
        if (IsGameAutoRunning())
            return;

        followCommandActive = false;
        LastAction = "追従キャンセルを検出・再開待ち";
    }

    private void QueueTargetCommand(
        Dalamud.Game.ClientState.Objects.Types.IGameObject target, string command)
    {
        Plugin.TargetManager.Target = target;
        var targetName = target.Name.TextValue.Replace("\"", string.Empty);
        // ProcessChatBoxEntryでは<t>が展開されないため、先に対象を現在ターゲットへ
        // 設定してから、/follow は引数なしで実行する。
        pendingGameCommand = command.StartsWith("/follow ", StringComparison.OrdinalIgnoreCase)
            ? "/follow"
            : command.Replace("<t>", $"\"{targetName}\"", StringComparison.Ordinal);
        pendingTargetName = targetName;
        pendingTargetApplied = false;
        pendingTargetConfirmAttempts = 0;
        pendingCommandTicks = 2;
    }

    private static unsafe bool ExecuteGameCommand(string command)
    {
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule is null)
                return false;
            using var message = new Utf8String(command);
            uiModule->ProcessChatBoxEntry(&message);
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "ゲームコマンドを実行できませんでした: {Command}", command);
            return false;
        }
    }

    private static bool IsBlocked() => Plugin.Condition.Any(
        ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
        ConditionFlag.Occupied, ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent, ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78,
        ConditionFlag.Crafting, ConditionFlag.Gathering, ConditionFlag.TradeOpen,
        ConditionFlag.Unconscious, ConditionFlag.Casting, ConditionFlag.Casting87);

    private static uint GetLifestreamId(string ipcName)
    {
        try
        {
            return IsPluginLoaded("Lifestream")
                ? Plugin.PluginInterface.GetIpcSubscriber<uint>(ipcName).InvokeFunc()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (combatAutomationActive)
            StopCombatAutomation();
        useActionHook?.Disable();
        useActionHook?.Dispose();
        teleportHook?.Disable();
        teleportHook?.Dispose();
        OnSelectYesnoClosed();
        Plugin.Framework.Update -= OnFrameworkUpdate;
        cancellation.Cancel();
        receiver?.Dispose();
        lock (senderLock)
        {
            sender?.Dispose();
            sender = null;
        }
        // Dispose中に待機するとゲームスレッドを止めるため、受信タスクは
        // receiverのDisposeによる終了だけを監視し、継続処理は行わない。
        _ = receiveTask;
        cancellation.Dispose();
    }
}

internal readonly record struct OccultAetheryte(uint TerritoryType, uint PlaceNameId, float X, float Z);

public sealed class LinkedCharacterState
{
    public int Protocol { get; set; }
    public string LinkKey { get; set; } = string.Empty;
    public string Kind { get; set; } = "state";
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = "不明";
    public string WorldName { get; set; } = "不明";
    public uint TerritoryType { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public string JobName { get; set; } = "—";
    public uint CurrentHp { get; set; }
    public uint MaxHp { get; set; }
    public bool InCombat { get; set; }
    public bool Mounted { get; set; }
    public bool RidingPillion { get; set; }
    public byte CastActionType { get; set; }
    public uint CastActionId { get; set; }
    public uint ActiveAetheryteId { get; set; }
    public uint ActiveCustomAetheryteId { get; set; }
    public uint ActiveResidentialAetheryteId { get; set; }
    public uint DestinationAetheryteId { get; set; }
    public byte DestinationSubIndex { get; set; }
    public ushort HousingWorldId { get; set; }
    public ushort HousingTerritoryId { get; set; }
    public byte HousingWard { get; set; }
    public byte HousingPlot { get; set; }
    public ulong HousingHouseId { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public bool LinkEnabled { get; set; }
    public ulong LeaderContentId { get; set; }
    public bool AutoFollow { get; set; }
    public bool AutoRidePillion { get; set; }
    public bool PauseInCombat { get; set; }
    public float FollowDistance { get; set; }
    public bool CombatLinkEnabled { get; set; }
    public bool UseBossModReborn { get; set; }
    public bool UseRotationSolverReborn { get; set; }
    public float CombatStopDelaySeconds { get; set; }
    public bool OccultAethernetSyncEnabled { get; set; }
    public bool SyncReturnEnabled { get; set; }
    public bool SyncDutyCommenceEnabled { get; set; }
    public bool SyncTeleportInvitationEnabled { get; set; }
    public bool SyncRegularTeleportEnabled { get; set; }
    public bool SyncCityAethernetEnabled { get; set; }
    public bool SyncResidentialAethernetEnabled { get; set; }
    public bool SyncFreeCompanyEstateEnabled { get; set; }
    public bool AutoOpenNearbyTreasureEnabled { get; set; }
    public long SharedConfigurationRevision { get; set; }
    public ulong TargetContentId { get; set; }
    public ushort EmoteId { get; set; }
}
