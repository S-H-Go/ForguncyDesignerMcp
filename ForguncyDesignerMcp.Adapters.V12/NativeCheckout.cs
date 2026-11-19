using System.Reflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public static class NativeCheckout
{
    /// <summary>Create the V12 native executor for an explicit set of module and state pairs.</summary>
    public static object CreateExecutor(Assembly designerAssembly, Array changes) =>
        Activator.CreateInstance(designerAssembly.GetType("Forguncy.Common.MultiExecutor", throwOnError: true)!,
            [null, changes])!;
}
