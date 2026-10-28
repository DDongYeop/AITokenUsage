using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Providers.Copilot;

public sealed record CopilotUsage(string? Login, string? PlanLabel, IReadOnlyList<UsageMeter> Meters);
