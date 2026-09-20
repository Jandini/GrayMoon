namespace GrayMoon.Application.Features;

/// <summary>
/// Authoritative execution identity for context-specific operations.
/// Never infer this from browser selection, Desktop state, or ambient UI.
/// </summary>
public readonly record struct WorkspaceFeatureContextId(int Value)
{
    public override string ToString() => Value.ToString();

    public static implicit operator int(WorkspaceFeatureContextId id) => id.Value;

    public static explicit operator WorkspaceFeatureContextId(int value) => new(value);
}
