namespace Seein.Modularity;

public static class HostVersion
{
    public static Version Current { get; } = typeof(HostVersion).Assembly.GetName().Version!;
}
