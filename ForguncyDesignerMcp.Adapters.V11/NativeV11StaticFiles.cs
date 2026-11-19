using System.Reflection;

namespace ForguncyDesignerMcp.Adapters.V11;

// The native static-directory saver owns report layouts, images and user files.
// Its IColleague.Mediator is explicit and its save-file property is obfuscated;
// neither is the normal model-loader public property used by JSON modules.
internal static class NativeV11StaticFiles
{
    internal sealed record Binding(object Owner, object Loader, object SaveFile, List<string> Folders, List<string> Files);

    internal static Binding Resolve(NativeV11Session session)
    {
        session.VerifyContext();
        var manager = NativeV11Reflection.Get(session.Document, "ForguncyColleagueManager")!;
        var owner = NativeV11Reflection.Get(manager, "StaticFolder")
            ?? throw new InvalidOperationException("The native static-file colleague is unavailable.");
        return ResolveOwner(session.Context.DesignerAssembly, owner, session.SaveFile);
    }

    internal static void VerifyConfigOwner(NativeV11Session session)
    {
        session.VerifyContext();
        // Exact getters are read-only path composition. Check before manager
        // path getters, which may EnsureExistFolder in the native cache.
        var configured = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!,
            "get_LocalDataFolderPath") as string;
        if (configured is null || !Path.GetFullPath(configured).TrimEnd('\\', '/').Equals(
            Path.GetFullPath(session.LocalPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The native configured working directory belongs to another document/save session.");
    }

    internal static Binding ResolveOwner(Assembly designer, object owner, object expectedSaveFile)
    {
        var newer = V11BuildProfile.Resolve(designer).Version == "11.0.104.0";
        if (owner.GetType() != designer.GetType(newer ? "hd" : "gy", true))
            throw new InvalidOperationException("The native static-file colleague differs from this exact V11 build.");
        var contract = owner.GetType().GetInterfaces().SingleOrDefault(type => type.FullName == "ServerDesignerCommon.IColleague")
            ?? throw new InvalidOperationException("The native static-file colleague has no verified mediator contract.");
        var mediator = contract.GetProperty("Mediator")?.GetValue(owner)
            ?? throw new InvalidOperationException("The native static-file mediator is unavailable.");
        var loader = NativeV11Reflection.Get(mediator, "SaveDataColleague")
            ?? throw new InvalidOperationException("The native static-file saver is unavailable.");
        if (loader.GetType() != designer.GetType(newer ? "ds" : "dn", true))
            throw new InvalidOperationException("The native static-file saver differs from this exact V11 build.");
        var saveProperty = loader.GetType().GetProperties(NativeV11Reflection.Flags).SingleOrDefault(property =>
            property.PropertyType == designer.GetType("Forguncy.SaveLoad.SaveLoadFileBase", true));
        var saveFile = saveProperty?.GetValue(loader);
        if (saveFile is null || !ReferenceEquals(saveFile, expectedSaveFile))
            throw new InvalidOperationException("The native static-file saver belongs to another document/save session.");
        var registry = Registry(designer, loader);
        return new(owner, loader, saveFile, registry.Folders, registry.Files);
    }

    internal static (List<string> Folders, List<string> Files) Registry(Assembly designer, object loader)
    {
        var newer = V11BuildProfile.Resolve(designer).Version == "11.0.104.0";
        if (loader.GetType() != designer.GetType(newer ? "ds" : "dn", true))
            throw new InvalidOperationException("The native static-file saver differs from this exact V11 build.");
        var folders = NativeV11Reflection.Get(loader, newer ? "ve" : "uq") as IEnumerable<string>;
        var files = NativeV11Reflection.Get(loader, newer ? "vf" : "ur") as IEnumerable<string>;
        if (folders is null || files is null) throw new InvalidOperationException("The native static-file save registry is not initialized.");
        return (folders.ToList(), files.ToList());
    }
}
