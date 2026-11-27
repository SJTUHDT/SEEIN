using System.Diagnostics;

namespace Seein.Core.Tools;

/// <summary>External documents and browsers are launched by SEEIN, outside tool jobs.</summary>
public static class ToolExternalLaunch
{
    public static ProcessStartInfo Create(string target)
    {
        if (target.Length > 16000 || !Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "file" or "mailto" or "ms-settings" or "ms-windows-store"))
            throw new ArgumentException("不支持的外部链接。", nameof(target));
        return new ProcessStartInfo(uri.IsFile ? uri.LocalPath : uri.AbsoluteUri) { UseShellExecute = true };
    }
}
