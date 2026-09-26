using System;
using System.Numerics;

namespace AltMate;

/// <summary>
/// Calculates a stable follow target behind the leader. Movement input itself is
/// supplied by <see cref="SmoothFollowController"/>.
/// </summary>
internal sealed class FollowController
{
    private const float DeadZoneRadius = 0.40f;
    private const float ResumeRadius = 0.65f;
    private const float CatchUpStartDistance = 3.0f;
    private const float FullSpeedDistance = 10.0f;
    private bool moving;
    private Vector3 previousLeader;
    private Vector3 velocity;
    private long previousTick = -1;

    internal FollowDecision Update(Vector3 followerPosition, Vector3 leaderPosition,
        float leaderRotation, float spacing, long now = -1)
    {
        now = now < 0 ? Environment.TickCount64 : now;
        if (previousTick >= 0 && now > previousTick && now - previousTick <= 500)
        {
            var seconds = (now - previousTick) / 1000f;
            var observed = (leaderPosition - previousLeader) / seconds;
            // Teleports must never become extrapolated movement targets.
            velocity = observed.LengthSquared() <= 400f
                ? Vector3.Lerp(velocity, observed, 1f - MathF.Exp(-seconds / 0.12f))
                : Vector3.Zero;
        }
        else velocity = Vector3.Zero;
        previousTick = now;
        previousLeader = leaderPosition;
        spacing = MathF.Max(0.5f, spacing);

        // Dalamud/FFXIV rotations use X=sin(rotation), Z=cos(rotation) as forward.
        var leaderForward = new Vector3(MathF.Sin(leaderRotation), 0f, MathF.Cos(leaderRotation));
        var target = leaderPosition - leaderForward * spacing + velocity * 0.12f;
        target.Y = followerPosition.Y;

        var offset = target - followerPosition;
        offset.Y = 0f;
        var targetDistance = offset.Length();

        if (moving)
        {
            if (targetDistance <= DeadZoneRadius)
                moving = false;
        }
        else if (targetDistance >= ResumeRadius)
        {
            moving = true;
        }

        if (!moving || targetDistance < 0.001f)
            return new FollowDecision(false, target, Vector3.Zero, 0f, false, targetDistance);

        var catchUp = Math.Clamp(
            (targetDistance - CatchUpStartDistance) /
            (FullSpeedDistance - CatchUpStartDistance), 0f, 1f);
        var strength = 0.35f + catchUp * 0.65f;
        return new FollowDecision(true, target, offset, strength, catchUp > 0f, targetDistance);
    }

    internal void Reset()
    {
        moving = false;
        velocity = Vector3.Zero;
        previousTick = -1;
    }
}

internal readonly record struct FollowDecision(bool ShouldMove, Vector3 Target,
    Vector3 Direction, float Strength, bool IsCatchingUp, float DistanceToTarget);
