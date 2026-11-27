namespace Seein.Pet;

/// <summary>Counts deliberate clicks per pet without a timer or a queued animation backlog.</summary>
public sealed class PetClickReaction(int threshold = 5)
{
    public int Threshold { get; } = threshold is >= 2 and <= 100 ? threshold : 5;
    public int PendingClicks { get; private set; }

    public bool Click(bool reactionPlaying = false)
    {
        if (reactionPlaying) return false;
        if (++PendingClicks < Threshold) return false;
        Reset();
        return true;
    }

    public void Reset() => PendingClicks = 0;
}
