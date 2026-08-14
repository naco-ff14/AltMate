using Dalamud.Hooking;
using System;
using System.Numerics;

namespace AltMate;

/// <summary>
/// Supplies a movement direction only while the game (or another movement plugin)
/// has not supplied input. This keeps manual controls and BMR movement authoritative.
/// </summary>
internal sealed unsafe class SmoothFollowController : IDisposable
{
    private Vector3? desiredDirection;
    private readonly Hook<ReadWalkInputDelegate>? walkHook;

    private delegate void ReadWalkInputDelegate(nint self, float* sumLeft, float* sumForward,
        float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* unknown, byte additiveInput);

    internal SmoothFollowController()
    {
        try
        {
            var address = Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D");
            walkHook = Plugin.InteropProvider.HookFromAddress<ReadWalkInputDelegate>(address, ReadWalkInputDetour);
            walkHook.Enable();
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "滑らか追従の移動入力フックを開始できませんでした。");
        }
    }

    internal void Follow(Vector3 worldDirection)
    {
        desiredDirection = worldDirection.LengthSquared() > 0.001f ? worldDirection : null;
    }

    internal void Stop() => desiredDirection = null;

    private void ReadWalkInputDetour(nint self, float* sumLeft, float* sumForward,
        float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* unknown, byte additiveInput)
    {
        walkHook!.Original(self, sumLeft, sumForward, sumTurnLeft,
            haveBackwardOrStrafe, unknown, additiveInput);

        // additiveInput == 0 is the final movement-input pass. Existing input always wins.
        if (additiveInput != 0 || *sumLeft != 0 || *sumForward != 0 ||
            desiredDirection is not { } desired)
            return;

        var horizontal = new Vector2(desired.X, desired.Z);
        if (horizontal.LengthSquared() < 0.001f)
            return;

        var worldAngle = MathF.Atan2(horizontal.X, horizontal.Y);
        var forwardAngle = GetForwardAngle();
        var relativeAngle = worldAngle - forwardAngle;
        *sumLeft = MathF.Sin(relativeAngle);
        *sumForward = MathF.Cos(relativeAngle);
    }

    private static float GetForwardAngle()
    {
        // Character-relative movement is sufficient for direct input injection and avoids
        // depending on BMR's internal Camera wrapper.
        return Plugin.ObjectTable.LocalPlayer?.Rotation ?? 0f;
    }

    public void Dispose()
    {
        desiredDirection = null;
        walkHook?.Disable();
        walkHook?.Dispose();
    }
}
