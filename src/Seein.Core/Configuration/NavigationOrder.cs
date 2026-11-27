namespace Seein.Core.Configuration;
public static class NavigationOrder
{
    public static string[] Arrange(IEnumerable<string> available,IEnumerable<string> saved,IEnumerable<string> defaults)
    {
        var keys=available.Distinct(StringComparer.Ordinal).ToArray();var set=keys.ToHashSet(StringComparer.Ordinal);
        return saved.Concat(defaults).Concat(keys).Where(set.Contains).Distinct(StringComparer.Ordinal).ToArray();
    }
    public static string[] Remember(IEnumerable<string> previous,IEnumerable<string> visible)
    {
        var order=visible.Distinct(StringComparer.Ordinal).ToArray();var set=order.ToHashSet(StringComparer.Ordinal);var queue=new Queue<string>(order);
        var result=new List<string>();
        foreach(var key in previous.Distinct(StringComparer.Ordinal))result.Add(set.Contains(key)&&queue.TryDequeue(out var next)?next:key);
        while(queue.TryDequeue(out var next))result.Add(next);
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }
}
