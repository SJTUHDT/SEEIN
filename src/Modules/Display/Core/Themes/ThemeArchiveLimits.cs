namespace Seein.Core.Themes;

internal static class ThemeArchiveLimits
{
    internal const long MaxExpandedBytes = 512L * 1024 * 1024;

    internal static bool TryAddEntryLength(long currentBytes, long entryBytes, out long totalBytes)
    {
        totalBytes = currentBytes;
        if (currentBytes < 0 || entryBytes < 0 || currentBytes > MaxExpandedBytes - entryBytes) return false;
        totalBytes = currentBytes + entryBytes;
        return totalBytes <= MaxExpandedBytes;
    }
}
