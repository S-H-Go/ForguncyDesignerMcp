namespace ForguncyDesignerMcp.Core.Platform;

/// <summary>Deterministic validation only; never dispatches native writes.</summary>
public interface IProjectApiPreflightAdapter
{
    Task ValidateProjectApiAsync(PlatformApiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Signals the first possible native side effect, including collaboration checkout.</summary>
public interface IProjectApiDispatchAdapter : IProjectApiPreflightAdapter
{
    Task<System.Text.Json.JsonElement> InvokeProjectApiAsync(PlatformApiRequest request, Action startingNativeWrite,
        CancellationToken cancellationToken = default);
}
