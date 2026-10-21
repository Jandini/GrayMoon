using System.Text.Json;
using GrayMoon.Abstractions.Logging;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class CommandJobCorrelationTests
{
    [Fact]
    public void CreateCommandJob_stores_RequestId_and_OperationId()
    {
        var operationId = GrayMoonOperationContext.NewId();
        var args = JsonSerializer.SerializeToElement(new { });
        var envelope = new CommandJobFactory().CreateCommandJob("req-abc", "GetHostInfo", args, operationId);

        var job = Assert.IsType<CommandJob>(envelope.CommandJob);
        Assert.Equal("req-abc", job.RequestId);
        Assert.Equal(operationId, job.OperationId);
        Assert.Equal(GrayMoonLogProperties.RequestId, "GrayMoonRequestId");
        Assert.Equal(GrayMoonLogProperties.OperationId, "GrayMoonOperationId");
    }

    [Fact]
    public void CreateCommandJob_treats_empty_OperationId_as_null()
    {
        var args = JsonSerializer.SerializeToElement(new { });
        var envelope = new CommandJobFactory().CreateCommandJob("req-abc", "GetHostInfo", args, "");

        var job = Assert.IsType<CommandJob>(envelope.CommandJob);
        Assert.Null(job.OperationId);
    }
}
