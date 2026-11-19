using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

// Only the signed, fingerprinted V12 account model may be loaded from the
// designer installation. No process-wide Resolving handler or probing path is installed.
internal static class NativeAccountAssemblyLoader
{
    internal const string ServiceIdentity="Forguncy.UserService2, Version=12.0.102.0, Culture=neutral, PublicKeyToken=967100671921c115";
    internal const string ServiceHash="523B25C52E9D1D5B22131742C6E7A4053310FAC57B1102E6C47CE4BF11045290";
    internal const string ConsoleIdentity="ForguncyUserServiceConsole, Version=12.0.102.0, Culture=neutral, PublicKeyToken=967100671921c115";
    internal const string ConsoleHash="46CCAE0AD77D1E898807B329EBFF193922836163592278281204173F0F341CD6";
    private const string DesignerIdentity="Forguncy, Version=12.0.102.0, Culture=neutral, PublicKeyToken=967100671921c115";

    internal static string OfficialBin(string designerPath)
    {
        var full=Path.GetFullPath(designerPath);
        var designerDirectory=Path.GetDirectoryName(full);
        var website=designerDirectory is null ? null : Directory.GetParent(designerDirectory);
        if(!string.Equals(Path.GetFileName(full),"Forguncy.dll",StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(Path.GetFileName(designerDirectory),"designerBin",StringComparison.OrdinalIgnoreCase) || website is null ||
           !string.Equals(website.Name,"WebSite",StringComparison.OrdinalIgnoreCase))
            throw Denied("The native designer is not in its verified WebSite/designerBin location.");
        return Path.Combine(website.FullName,"bin");
    }

    internal static void ValidatePinnedFile(string path,string identity,string sha256)
    {
        var full=Path.GetFullPath(path);
        RequireNoLinks(full);
        try
        {
            using var stream=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read);
            ValidateLockedFile(full,stream,identity,sha256);
        }
        catch(BridgeException) { throw; }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or BadImageFormatException)
        { throw Denied("A required installed native account component is missing or unreadable."); }
    }

    internal static Assembly LoadService(Assembly designer)
    {
        var designerPath=Path.GetFullPath(designer.Location);
        var bin=OfficialBin(designerPath);
        ValidatePinnedFile(designerPath,DesignerIdentity,V12AdapterFactory.DesignerSha256);
        var service=Path.Combine(bin,"Forguncy.UserService2.dll");
        var console=Path.Combine(bin,"ForguncyUserServiceConsole.dll");
        RequireNoLinks(service); RequireNoLinks(console);
        try
        {
            // Keep both reviewed files locked against replacement until the
            // exact model assembly has been loaded and its identity rechecked.
            using var modelStream=new FileStream(service,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var consoleStream=new FileStream(console,FileMode.Open,FileAccess.Read,FileShare.Read);
            ValidateLockedFile(service,modelStream,ServiceIdentity,ServiceHash);
            ValidateLockedFile(console,consoleStream,ConsoleIdentity,ConsoleHash);
            var context=AssemblyLoadContext.GetLoadContext(typeof(NativeAccountAssemblyLoader).Assembly)
                ?? throw Denied("The adapter assembly load context is unavailable.");
            var loaded=context.Assemblies.SingleOrDefault(item=>item.GetName().Name=="Forguncy.UserService2")
                ?? context.LoadFromAssemblyPath(service);
            if(loaded.FullName!=ServiceIdentity || !string.Equals(Path.GetFullPath(loaded.Location),Path.GetFullPath(service),StringComparison.OrdinalIgnoreCase))
                throw Denied("An existing account model came from a different identity or installation path.");
            return loaded;
        }
        catch(BridgeException) { throw; }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        { throw Denied("The reviewed native account model could not be loaded from its installed location."); }
    }

    private static void ValidateLockedFile(string path,Stream stream,string identity,string sha256)
    {
        if(AssemblyName.GetAssemblyName(path).FullName!=identity || Convert.ToHexString(SHA256.HashData(stream))!=sha256)
            throw Denied("The native account component identity, version or SHA256 fingerprint changed.");
    }
    private static void RequireNoLinks(string path)
    {
        var full=Path.GetFullPath(path);
        for(var current=full; !string.IsNullOrEmpty(current); current=Path.GetDirectoryName(current))
        {
            if(!File.Exists(current) && !Directory.Exists(current)) throw Denied("A required installed native account path is missing.");
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw Denied("Native account paths cannot traverse a reparse point.");
        }
    }
    private static BridgeException Denied(string message)=>new("AccountNativeContractChanged",message);
}
