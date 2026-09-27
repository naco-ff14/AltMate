using System;

namespace AltMate;

// Track only a value this session actually changed. A manual edit takes precedence on release.
internal sealed class TemporaryBooleanSetting
{
    private bool? original;
    private bool written;
    internal bool Pending => original.HasValue;

    internal bool Acquire(bool? desired, Func<bool?> read, Func<bool, bool> write)
    {
        if (Pending && !Release(read, write)) return false;
        if (desired is null) return true;
        var before = read();
        if (before is null) return false;
        if (before == desired) return true;
        original = before;
        written = desired.Value;
        return write(written) && read() == written;
    }

    internal bool Release(Func<bool?> read, Func<bool, bool> write)
    {
        if (original is not { } before) return true;
        var current = read();
        if (current is null) return false;
        if (current != written || current == before)
        {
            original = null;
            return true;
        }
        if (!write(before) || read() != before) return false;
        original = null;
        return true;
    }

    internal void Forget() => original = null;
}
