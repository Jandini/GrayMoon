using GrayMoon.Abstractions.Worker;

namespace GrayMoon.Common;

/// <summary>Command stream segment reported from <see cref="CommandLineService"/> to an ambient sink (no repository context).</summary>
public readonly record struct CommandLineStreamEvent(WorkerCommandStreamKind Kind, string Text);
