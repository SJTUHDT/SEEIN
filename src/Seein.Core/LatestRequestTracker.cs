namespace Seein.Core;

public sealed class LatestRequestTracker
{
    private string? _currentRequestId;

    public string Begin()
    {
        _currentRequestId = Guid.NewGuid().ToString("N");
        return _currentRequestId;
    }

    public bool IsCurrent(string? requestId) =>
        requestId is not null && string.Equals(requestId, _currentRequestId, StringComparison.Ordinal);
}
