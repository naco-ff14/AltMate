using Dalamud.Game.Config;
using Dalamud.Plugin.Services;
using System;

namespace AltMate;

/// <summary>
/// Applies the frame-rate cap to this running game process according to its
/// AltMate role. The role is based on ContentId, not window focus.
/// </summary>
public sealed class RoleBasedFpsController : IDisposable
{
    private readonly Plugin plugin;
    private DateTime nextCheckUtc;
    private uint? originalFps;
    private uint? originalInactiveFps;
    private int lastRequestedLimit = -1;
    private bool disposed;

    public string Status { get; private set; } = "待機中";

    public RoleBasedFpsController(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void ApplyNow()
    {
        nextCheckUtc = default;
        OnFrameworkUpdate(Plugin.Framework);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed || DateTime.UtcNow < nextCheckUtc)
            return;
        nextCheckUtc = DateTime.UtcNow.AddSeconds(2);

        if (!plugin.Configuration.RoleBasedFpsEnabled)
        {
            RestoreOriginal();
            Status = "無効";
            return;
        }

        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
        {
            Status = "ログイン待ち";
            return;
        }

        try
        {
            CaptureOriginal();
            var isLeader = Plugin.PlayerState.ContentId == plugin.Configuration.LinkLeaderContentId;
            var limit = isLeader
                ? plugin.Configuration.LeaderFpsLimit
                : plugin.Configuration.FollowerFpsLimit;
            limit = NormalizeLimit(limit);

            // Fps is an enum in FFXIV.cfg: 0=unlimited, 1=60fps, 2=30fps.
            var gameValue = LimitToGameValue(limit);
            if (!Plugin.GameConfig.TryGet(SystemConfigOption.Fps, out uint current) || current != gameValue)
                Plugin.GameConfig.Set(SystemConfigOption.Fps, gameValue);

            // Do not lower the leader merely because the browser has focus.
            if (!Plugin.GameConfig.TryGet(SystemConfigOption.FPSInActive, out uint inactive) || inactive != 0)
                Plugin.GameConfig.Set(SystemConfigOption.FPSInActive, 0u);

            lastRequestedLimit = limit;
            Status = $"{(isLeader ? "リーダー" : "フォロワー")}：{(limit == 0 ? "無制限" : $"{limit} FPS")}";
        }
        catch (Exception ex)
        {
            Status = "適用失敗";
            Plugin.Log.Warning(ex, "Failed to apply role-based FPS limit.");
        }
    }

    private void CaptureOriginal()
    {
        if (originalFps is null && Plugin.GameConfig.TryGet(SystemConfigOption.Fps, out uint fps))
            originalFps = fps;
        if (originalInactiveFps is null && Plugin.GameConfig.TryGet(SystemConfigOption.FPSInActive, out uint inactive))
            originalInactiveFps = inactive;
    }

    private void RestoreOriginal()
    {
        if (lastRequestedLimit < 0)
            return;
        try
        {
            if (originalFps is uint fps)
                Plugin.GameConfig.Set(SystemConfigOption.Fps, fps);
            if (originalInactiveFps is uint inactive)
                Plugin.GameConfig.Set(SystemConfigOption.FPSInActive, inactive);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to restore FPS settings.");
        }
        lastRequestedLimit = -1;
        originalFps = null;
        originalInactiveFps = null;
    }

    private static int NormalizeLimit(int value) => value switch
    {
        <= 0 => 0,
        <= 30 => 30,
        _ => 60,
    };

    private static uint LimitToGameValue(int limit) => limit switch
    {
        30 => 2u,
        60 => 1u,
        _ => 0u,
    };

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        RestoreOriginal();
    }
}
