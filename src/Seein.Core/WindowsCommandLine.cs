using System.Text;
namespace Seein.Core;

public static class WindowsCommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));
    public static string Quote(string argument)
    {
        if (argument.Contains('\0')) throw new ArgumentException("命令参数不能包含空字符。", nameof(argument));
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
