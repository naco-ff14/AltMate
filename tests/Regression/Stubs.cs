namespace Dalamud.Configuration
{
    public interface IPluginConfiguration { int Version { get; set; } }
}

namespace AltMate
{
    internal static class Plugin
    {
        internal static readonly TestLog Log = new();
        internal static void SaveConfiguration(Configuration _) { }
    }

    internal sealed class TestLog
    {
        internal int Warnings;
        internal void Warning(Exception exception, string message) => Warnings++;
    }
}
