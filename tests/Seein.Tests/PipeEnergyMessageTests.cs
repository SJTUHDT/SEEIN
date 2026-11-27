using Seein.Core;
using System.Text.Json;

namespace Seein.Tests;

public sealed class PipeEnergyMessageTests
{
    [Fact]
    public void EnergyDayRequestAndResponseRoundTripWithRequestId()
    {
        const string requestId = "req-20261116-02";
        var requestEnvelope = PipeEnvelope.Create(PipeMessageType.EnergyDayRequest,
            new EnergyDayRequest("2026-11-16", requestId));
        var requestJson = JsonSerializer.Serialize(requestEnvelope, JsonDefaults.Options);
        var decodedRequest = JsonSerializer.Deserialize<PipeEnvelope>(requestJson, JsonDefaults.Options)!
            .Read<EnergyDayRequest>()!;
        Assert.Equal(requestId, decodedRequest.RequestId);
        Assert.Equal("2026-11-16", decodedRequest.LocalDate);

        var response = new EnergyDayResult("2026-11-16", requestId, 42, 10, 20, 8, 4, 0,
            60, 55, [new PowerHistoryPoint(1_794_715_200, 60, 80, 120, 170, 54, 0)], "1 分钟", false);
        var responseJson = JsonSerializer.Serialize(
            PipeEnvelope.Create(PipeMessageType.EnergyDayResult, response), JsonDefaults.Options);
        var decodedResponse = JsonSerializer.Deserialize<PipeEnvelope>(responseJson, JsonDefaults.Options)!
            .Read<EnergyDayResult>()!;
        Assert.Equal(requestId, decodedResponse.RequestId);
        Assert.Single(decodedResponse.Points);
        Assert.Equal(120, decodedResponse.Points[0].AverageWatts);
    }

    [Fact]
    public void LatestRequestTrackerRejectsOutOfOrderResponses()
    {
        var tracker = new LatestRequestTracker();
        var first = tracker.Begin();
        var second = tracker.Begin();
        Assert.False(tracker.IsCurrent(first));
        Assert.True(tracker.IsCurrent(second));
        Assert.False(tracker.IsCurrent(null));
    }

    [Fact]
    public void ThemeDeleteMessagesRoundTripWithRequestId()
    {
        var request = new DeleteThemeRequest("my-theme", "delete-1");
        var envelope = PipeEnvelope.Create(PipeMessageType.DeleteThemeRequest, request);
        var encoded = JsonSerializer.Serialize(envelope, JsonDefaults.Options);
        var decodedEnvelope = JsonSerializer.Deserialize<PipeEnvelope>(encoded, JsonDefaults.Options)!;
        Assert.Equal(PipeMessageType.DeleteThemeRequest, decodedEnvelope.Type);
        Assert.Equal(request, decodedEnvelope.Read<DeleteThemeRequest>());

        var response = new DeleteThemeResult(true, "my-theme", RequestId: "delete-1");
        var responseEnvelope = PipeEnvelope.Create(PipeMessageType.DeleteThemeResult, response);
        var responseJson = JsonSerializer.Serialize(responseEnvelope, JsonDefaults.Options);
        var decodedResponse = JsonSerializer.Deserialize<PipeEnvelope>(responseJson, JsonDefaults.Options)!
            .Read<DeleteThemeResult>()!;
        Assert.True(decodedResponse.Success);
        Assert.Equal("delete-1", decodedResponse.RequestId);
    }
}
