namespace AITokenUsage.Core.Rotation;

public sealed class RotationChangedEventArgs(string? currentId, int direction) : EventArgs
{
    public string? CurrentId { get; } = currentId;

    /// <summary>+1 next, -1 previous, 0 replaced because the provider list changed.</summary>
    public int Direction { get; } = direction;
}
