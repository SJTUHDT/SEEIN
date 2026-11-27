using Seein.Core.Ipc;
using Seein.Core;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Seein.Tests;

public sealed class BoundedPipeLineReaderTests
{
    [Fact]
    public async Task ReadsMultipleLinesFromSharedBuffer()
    {
        var reader = new BoundedPipeLineReader(new MemoryStream(Encoding.UTF8.GetBytes("first\r\nsecond\n")), 16);
        Assert.Equal("first", await reader.ReadLineAsync());
        Assert.Equal("second", await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task AcceptsMessageAtByteLimit()
    {
        var reader = new BoundedPipeLineReader(new MemoryStream(Encoding.UTF8.GetBytes("1234\n")), 4);
        Assert.Equal("1234", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task RejectsMessageOverByteLimit()
    {
        var reader = new BoundedPipeLineReader(new MemoryStream(Encoding.UTF8.GetBytes("12345\n")), 4);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync());
    }

    [Fact]
    public async Task RejectsInvalidUtf8()
    {
        var reader = new BoundedPipeLineReader(new MemoryStream([0xC3, 0x28, 0x0A]), 4);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => reader.ReadLineAsync());
    }

    [Fact]
    public void PipeSidPolicyRequiresExactConfiguredIdentity()
    {
        Assert.True(PipeMessageLimits.IsAuthorizedSid("S-1-5-21-1", "s-1-5-21-1"));
        Assert.False(PipeMessageLimits.IsAuthorizedSid("S-1-5-21-2", "S-1-5-21-1"));
        Assert.False(PipeMessageLimits.IsAuthorizedSid(null, "S-1-5-21-1"));
        Assert.False(PipeMessageLimits.IsAuthorizedSid("S-1-5-21-1", null));
    }

    [Fact]
    public void PipeAclAllowsOnlyConfiguredUserSystemAndAdministrators()
    {
        var trayUserSid = new SecurityIdentifier("S-1-5-21-111-222-333-1001");
        var security = PanelPipeServer.CreatePipeSecurity(trayUserSid);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();
        var expectedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            trayUserSid.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
        };

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(expectedSids, rules.Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.All(rules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));
        Assert.Contains(rules, rule => ((SecurityIdentifier)rule.IdentityReference).Value == trayUserSid.Value &&
            (rule.PipeAccessRights & (PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance)) ==
            (PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance));
    }

    [Fact]
    public async Task RequestQueueAppliesBackpressureAtConfiguredLimit()
    {
        var channel = PipeMessageLimits.CreateRequestChannel();
        var envelope = PipeEnvelope.Create(PipeMessageType.Hello, new { });
        for (var index = 0; index < PipeMessageLimits.MaxPendingRequestsPerClient; index++)
            Assert.True(channel.Writer.TryWrite(envelope));

        var blockedWrite = channel.Writer.WriteAsync(envelope).AsTask();
        Assert.False(blockedWrite.IsCompleted);
        Assert.True(channel.Reader.TryRead(out _));
        await blockedWrite;
        Assert.Equal(PipeMessageLimits.MaxPendingRequestsPerClient, channel.Reader.Count);
    }
}
