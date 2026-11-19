using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

/// <summary>Construct on the document's owning dispatcher. No process or file editing API is exposed.</summary>
public sealed class V11AdapterFactory : IProjectAdapterFactory
{
    public bool Supports(AdapterIdentity identity) => V11BuildProfile.Supports(identity);

    public IProjectAdapter Create(AdapterHostContext context) => new V11ProjectAdapter(context);
}
