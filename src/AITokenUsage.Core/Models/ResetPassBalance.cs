namespace AITokenUsage.Core.Models;

public sealed record ResetPassBalance(int AvailableCount, IReadOnlyList<ResetPass> Passes);

/// <summary>NoExpiry is true only when the provider explicitly reports that the pass never expires.</summary>
public sealed record ResetPass(string? Label, DateTimeOffset? ExpiresAt, bool NoExpiry = false);
