namespace ForguncyDesignerMcp.Adapters.V12;

// A linked binding is distinct from an externally created physical table.
// The native delete path can prompt to drop a remote table when schema changes
// are enabled, so that flag must already be false before definition dispatch.
internal static class NativeLinkedTablePolicy
{
    internal static bool Allows(string ns, string method, object table)
    {
        var info=NativeReflection.Get(table,"ExternalDatabaseInfo");
        if(info is null || NativeReflection.Get(table,"IsLinkedTableOrLinkedView") is not true ||
            NativeReflection.Get(table,"IsCopyExternalBuiltInTable") is true || NativeReflection.Get(info,"AllowModifySchema") is not false)
            return false;
        return ns=="table" && method is "updateTableComment" or "updateTablePermission" or "deleteTable" ||
            ns=="table.view" && method=="delete";
    }
}
