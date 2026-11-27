using Seein.Core.Tools;
using Seein.Core;
using System.IO;

namespace Seein.Tray;

internal static class ToolShortcutCatalog
{
    public static string DoubleAltLibrary(string? appRoot = null)
    {
        var capture = Path.Combine(ProcessNames.ModuleDirectory("capture", appRoot), "Pane.Native.dll");
        // A capture-only install still needs the pause/resume gesture.
        return File.Exists(capture) ? capture : Path.Combine(
            Path.GetDirectoryName(ProcessNames.Executable("efficiency", appRoot))!, "Pane.Native.dll");
    }

    public static IReadOnlyList<ToolAction> ForModules(IEnumerable<string> moduleIds)
    {
        var enabled = moduleIds.ToHashSet(StringComparer.Ordinal);
        // ToolAction.Module is a localized section title, not the runtime's snow/pane ID.
        return ToolCatalog.Actions.Where(action => enabled.Contains(ToolCatalog.ModuleOf(action.Id))).ToArray();
    }
}
