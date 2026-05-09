using GrayMoon.Abstractions.Agent;

namespace GrayMoon.Common;

/// <summary>Command stream segment reported from <see cref="CommandLineService"/> to an ambient sink (no repository context).</summary>
public readonly record struct CommandLineStreamEvent(AgentCommandStreamKind Kind, string Text);
