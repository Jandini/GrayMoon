using GrayMoon.App.Services.Orchestration;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Tests;

public sealed class OperationErrorSinkTests
{
    [Fact]
    public void Repository_logs_and_invokes_callback()
    {
        var logger = new RecordingLogger<OperationErrorSinkTests>();
        var repoErrors = new Dictionary<int, string>();
        var levelErrors = new Dictionary<int, string>();
        var sink = new OperationErrorSink(9, logger, (id, msg) => repoErrors[id] = msg, (level, msg) => levelErrors[level] = msg);

        sink.Repository(42, "push rejected");

        Assert.Equal("push rejected", repoErrors[42]);
        Assert.Empty(levelErrors);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error
            && e.Message.Contains("42", StringComparison.Ordinal)
            && e.Message.Contains("push rejected", StringComparison.Ordinal));
    }

    [Fact]
    public void Level_logs_and_invokes_callback()
    {
        var logger = new RecordingLogger<OperationErrorSinkTests>();
        var repoErrors = new Dictionary<int, string>();
        var levelErrors = new Dictionary<int, string>();
        var sink = new OperationErrorSink(9, logger, (id, msg) => repoErrors[id] = msg, (level, msg) => levelErrors[level] = msg);

        sink.Level(2, "Timed out waiting for package dependencies");

        Assert.Empty(repoErrors);
        Assert.Equal("Timed out waiting for package dependencies", levelErrors[2]);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error
            && e.Message.Contains("level 2", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("Timed out waiting for package dependencies", StringComparison.Ordinal));
    }

    [Fact]
    public void Level_exception_overload_logs_exception_and_message()
    {
        var logger = new RecordingLogger<OperationErrorSinkTests>();
        var levelErrors = new Dictionary<int, string>();
        var sink = new OperationErrorSink(3, logger, (_, _) => { }, (level, msg) => levelErrors[level] = msg);
        var ex = new InvalidOperationException("registry unreachable");

        sink.Level(0, ex);

        Assert.Equal("registry unreachable", levelErrors[0]);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error
            && e.Exception == ex
            && e.Message.Contains("registry unreachable", StringComparison.Ordinal));
    }
}
