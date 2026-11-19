using System.Collections;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

internal static class NativeV11TableDefinitionLoading
{
    internal static async Task EnsureAsync(NativeV11Session session)
    {
        session.VerifyContext();
        var route = session.Route(ModuleKind.Table);
        NativeV11DefinitionLoading.RequireNotQuarantined(route.Root);
        NativeV11ExternalDefinitionLoading.RequireNotQuarantined(route.Root);
        if (NativeV11Reflection.Get(route.Root, "Loaded") is true) return;
        var names = NativeV11Reflection.Call(route.Loader, "GetAllTableName") as IEnumerable
            ?? throw new BridgeException("NativeDefinitionLoadRequiresReview", "原生表定义的文件登记不可用。");
        // Each selected path independently proves the complete source/cache
        // set. A child count or an empty filename list alone is never proof.
        if (names.Cast<object>().Any())
            await NativeV11ExternalDefinitionLoading.EnsureExternalTableRootAsync(session);
        else
            await NativeV11DefinitionLoading.EnsureEmptyTableRootAsync(session);
    }
}
