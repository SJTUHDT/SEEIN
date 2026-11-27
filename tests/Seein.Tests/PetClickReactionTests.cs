using Seein.Pet;

namespace Seein.Tests;

public sealed class PetClickReactionTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void OnlyTheThresholdClickTriggersAndTheNextSequenceStartsFresh(int threshold)
    {
        var clicks = new PetClickReaction(threshold);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            for (var count = 1; count < threshold; count++) Assert.False(clicks.Click());
            Assert.True(clicks.Click());
            Assert.Equal(0, clicks.PendingClicks);
        }
    }

    [Fact]
    public void PlayingAReactionDoesNotRestartItOrQueueTheNextReaction()
    {
        var clicks = new PetClickReaction(3);
        Assert.False(clicks.Click());
        Assert.False(clicks.Click());
        Assert.True(clicks.Click());
        for (var count = 0; count < 30; count++) Assert.False(clicks.Click(reactionPlaying: true));
        Assert.Equal(0, clicks.PendingClicks);
        Assert.False(clicks.Click());
    }

    [Fact]
    public void ADragOrShortcutClearsTheSequenceWithoutAffectingOtherPets()
    {
        var first = new PetClickReaction(3);
        var second = new PetClickReaction(3);
        first.Click(); first.Click();
        second.Click(); second.Click();
        first.Reset();
        Assert.False(first.Click());
        Assert.True(second.Click());
    }
}
