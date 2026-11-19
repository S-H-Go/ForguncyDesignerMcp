namespace ForguncyDesignerMcp.PlatformRuntime;

/// <summary>Host-owned limits; MCP input cannot raise these budgets.</summary>
public sealed record RuntimeLimits
{
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public long MemoryBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxStatements { get; init; } = 1_000_000;
    public int MaxRecursion { get; init; } = 128;
    public int MaxCodeChars { get; init; } = 128_000;
    public int MaxJsonChars { get; init; } = 2_000_000;
    public int MaxResultChars { get; init; } = 256_000;
    public int MaxLogChars { get; init; } = 32_000;
    public int MaxApiCalls { get; init; } = 200;
    public int MaxPendingCalls { get; init; } = 32;
    public int MaxConcurrentExecutions { get; init; } = 4;

    internal void Validate()
    {
        if (ExecutionTimeout <= TimeSpan.Zero || ExecutionTimeout > TimeSpan.FromMinutes(5) ||
            MemoryBytes < 1_000_000 || MaxStatements < 100 || MaxRecursion < 8 || MaxCodeChars < 1 ||
            MaxJsonChars < 1 || MaxResultChars < 512 || MaxLogChars < 1 || MaxApiCalls < 1 ||
            MaxPendingCalls < 1 || MaxConcurrentExecutions < 1)
            throw new ArgumentOutOfRangeException(nameof(RuntimeLimits), "All runtime budgets must be finite and positive.");
    }
}
