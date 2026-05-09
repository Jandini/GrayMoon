using System.Globalization;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Common;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace GrayMoon.Agent.Logging;

/// <summary>Forwards Error/Fatal Serilog events to the in-app overlay (stderr styling) when a command scope is active.</summary>
internal sealed class OverlayStreamSerilogSink : ILogEventSink
{
    private static readonly MessageTemplateTextFormatter Formatter = new(
        "[{Level:u3}] {Message:lj}{NewLine}{Exception}",
        CultureInfo.InvariantCulture);

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error)
            return;

        if (!TryResolveForward(logEvent, out var forward))
            return;

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Formatter.Format(logEvent, writer);
        var text = writer.ToString().TrimEnd();

        foreach (var line in SplitLines(text))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                forward(new CommandLineStreamEvent(AgentCommandStreamKind.Stderr, line.TrimEnd()));
            }
            catch
            {
                // never break logging
            }
        }
    }

    private static bool TryResolveForward(LogEvent logEvent, [NotNullWhen(true)] out Action<CommandLineStreamEvent>? forward)
    {
        if (TryGetRequestId(logEvent, out var requestId)
            && AgentOverlayLogBridge.TryGetForward(requestId, out forward))
            return true;

        forward = CommandLineStreamAmbient.Current.Value;
        return forward != null;
    }

    private static bool TryGetRequestId(LogEvent logEvent, out string requestId)
    {
        requestId = "";
        if (!logEvent.Properties.TryGetValue(AgentLogProperties.RequestId, out var value))
            return false;

        if (value is ScalarValue { Value: string s } && !string.IsNullOrEmpty(s))
        {
            requestId = s;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
                continue;

            if (i > start)
                yield return text.Substring(start, i - start);

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;

            start = i + 1;
        }

        if (start < text.Length)
            yield return text[start..];
    }
}
