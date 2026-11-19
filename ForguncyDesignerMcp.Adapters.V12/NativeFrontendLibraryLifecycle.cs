using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>Exact V12 native package lifecycle. Inspection is read-only; deployment is explicit and never asserts a project save.</summary>
public sealed class NativeFrontendLibraryLifecycle
{
    public sealed record Library(string Id,string Name,string Version,string FileName,string PackageFileName,
        string? GlobalName,string? Description,string? TypeDefinitionSha256);
    public sealed record FileEvidence(string Path,string? Sha256);
    public sealed record PackageIdentity(string Id,string Name,string Version,string GlobalName,string Sha256,string BundleSha256,string TypeDefinitionSha256);
    public sealed record Plan(string Method,string Id,string? Name,string? Version,string? GlobalName,string? PackageSha256,string? BundleSha256,string? TypeDefinitionSha256,bool Overwrite,
        string Owner,IReadOnlyList<string> OwnerFiles,IReadOnlyList<Library> Registry,
        IReadOnlyList<FileEvidence> LocalFiles,IReadOnlyList<FileEvidence> RuntimeFiles,
        IReadOnlyList<FileEvidence> SourceFiles,string ProjectSourceRevision);
    public sealed record NativeResult(bool Accepted,bool RegistryVerified,bool RuntimeFileSynchronized,
        bool RequiresRegenerate,JsonElement Response,Plan Before,Plan After);
    private readonly AdapterHostContext context;
    private readonly NativeProjectGuard guard;
    private readonly object settings, nativeLibraries;
    private readonly Type service, config;
    private readonly NativeModuleFiles fileGroups;
    private const int MaximumPackage=64*1024*1024;

    public NativeFrontendLibraryLifecycle(AdapterHostContext context)
    {
        this.context=context;
        var assembly=context.DesignerAssembly;
        if(assembly.GetName().Version?.ToString()!=V12AdapterFactory.Version ||
           Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))!=V12AdapterFactory.DesignerSha256)
            throw new BridgeException("UnsupportedDesignerBuild","Frontend packages require the exact verified native V12 build.");
        guard=new(context);
        settings=R.Get(context.NativeDocument,"ForguncySettings")!;
        nativeLibraries=R.Get(settings,"CustomLibraries")!;
        service=assembly.GetType("sb",true)!; config=assembly.GetType("Forguncy.Common.Config",true)!;
        fileGroups=new(assembly,context.NativeDocument);
    }

    public Plan Inspect(string method,string? libraryId,byte[]? package,bool overwrite)
    {
        _=guard.Capture();
        if(method is not ("uploadFrontendLibrary" or "deleteFrontendLibrary")) throw new BridgeException("UnknownRuntimeApi","Unknown native frontend library lifecycle.");
        string id;string? name=null;string? version=null;string? globalName=null;string? packageHash=null;string? bundleHash=null;string? typesHash=null;
        if(method=="uploadFrontendLibrary")
        {
            if(package is null || package.Length==0 || package.Length>MaximumPackage) throw new BridgeException("FrontendPackageInvalid","A nonempty frontend package of at most 64 MiB is required.");
            var manifest=InspectPackage(context.DesignerAssembly,package);
            packageHash=manifest.Sha256;bundleHash=manifest.BundleSha256;typesHash=manifest.TypeDefinitionSha256;id=manifest.Id;name=manifest.Name;version=manifest.Version;globalName=manifest.GlobalName;
            if(string.IsNullOrWhiteSpace(version)) throw new BridgeException("FrontendPackageInvalid","Native package manifest version is missing.");
        }
        else { if(package is not null || overwrite)throw new BridgeException("FrontendPackageInvalid","Delete takes only a native library ID."); id=libraryId?.Trim() ?? ""; }
        ValidateId(id);
        var registry=Registry();var found=registry.Where(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(found.Length>1 || method=="deleteFrontendLibrary" && found.Length!=1 || method=="uploadFrontendLibrary" && found.Length==1 && !overwrite)
            throw new BridgeException("FrontendLibraryConflict","The native library ID is absent, duplicated, or already installed without overwrite.");
        if(method=="deleteFrontendLibrary" && (int)R.Static(service,"GetReferenceCount",id)! != 0)
            throw new BridgeException("FrontendLibraryReferenced","The native frontend library is still referenced.");
        var owner=fileGroups.Resolve(new(ModuleKind.CustomLibrary,"Current")) ?? throw new BridgeException("FrontendOwnerUnavailable","The native CustomLibrary owner is unavailable.");
        var localRoot=Root("LocalDataFolderPath");var userRoot=Root("LocalUserFilePath");var packageRoot=Root("LocalFrontendLibraryPath");var runtimeRoot=Root("ServerUserFilePath");
        if(!Same(userRoot,Path.Combine(localRoot,"UserFile")) || !Within(localRoot,packageRoot)) throw new BridgeException("FrontendOwnerUnavailable","Native frontend paths do not belong to the bound local cache.");
        var relativeBundle="FrontendLibraries/"+id+"/bundle.js";var relativePackage=id+"/library.zip";
        if(found.Length==1 && (found[0].FileName.Replace('\\','/')!=relativeBundle || found[0].PackageFileName.Replace('\\','/')!=relativePackage))
            throw new BridgeException("FrontendRegistryAmbiguous","The native library registration does not match its canonical package and bundle paths.");
        var localPaths=new[]{NativeSaveVerification.Under(userRoot,relativeBundle),NativeSaveVerification.Under(packageRoot,relativePackage)};
        var runtimePath=NativeSaveVerification.Under(runtimeRoot,relativeBundle);
        var packageSource=Path.GetRelativePath(localRoot,localPaths[1]).Replace('\\','/');
        var local=SnapshotTrees(userRoot,"FrontendLibraries/"+id,packageRoot,id);
        var project=context.CaptureSnapshot().Target.ProjectPath;
        var packagePrefix=Path.GetRelativePath(localRoot,packageRoot).Replace('\\','/')+"/"+id;
        var sourcePaths=local.Select(x=>Path.GetRelativePath(localRoot,x.Path.Replace('/',Path.DirectorySeparatorChar)).Replace('\\','/'))
            .Concat(EnumerateSourceFiles(project,"UserFile/FrontendLibraries/"+id,packagePrefix))
            .Concat(new[]{"CustomLibraries.json","UserFile/"+relativeBundle,packageSource}).Distinct(StringComparer.Ordinal).ToArray();
        var runtime=SnapshotTrees(runtimeRoot,"FrontendLibraries/"+id);
        var source=sourcePaths.Select(path=>new FileEvidence(path,HashOrNull(NativeSaveVerification.ReadSource(context.CaptureSnapshot().Target.ProjectPath,path)))).ToArray();
        if(method=="uploadFrontendLibrary" && found.Length==0 && (local.Count!=0 || runtime.Count!=0 || source.Any(x=>x.Path!="CustomLibraries.json" && x.Sha256 is not null)))
            throw new BridgeException("FrontendFileCollision","An unregistered frontend library file already occupies the native target directory.");
        return new(method,id,name,version,globalName,packageHash,bundleHash,typesHash,overwrite,owner.NativeModuleName,owner.Paths,registry,local,runtime,source,
            NativeProjectGuard.SourceRevision(context.CaptureSnapshot().Target.ProjectPath,includeCollaboration:false));
    }

    public static PackageIdentity InspectPackage(System.Reflection.Assembly assembly,byte[] package)
    {
        if(assembly.GetName().Version?.ToString()!=V12AdapterFactory.Version ||
           Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))!=V12AdapterFactory.DesignerSha256)
            throw new BridgeException("UnsupportedDesignerBuild","Frontend package inspection requires the exact verified V12 build.");
        if(package is null || package.Length==0 || package.Length>MaximumPackage)throw new BridgeException("FrontendPackageInvalid","A nonempty frontend package of at most 64 MiB is required.");
        return WithPackage(package,path=>
        {
            var service=assembly.GetType("sb",true)!;
            var manifest=R.Static(service,"Inspect",path)!;
            var parsed=R.Static(service,"ehe",path)!;
            if(R.Get(parsed,"BundleBytes") is not byte[] bundle || bundle.Length==0)throw new BridgeException("FrontendPackageInvalid","Native package parser returned no bundle bytes.");
            if(R.Get(parsed,"TypeDefinition") is not string types || string.IsNullOrWhiteSpace(types))throw new BridgeException("FrontendPackageInvalid","Native package parser returned no type definition.");
            return new PackageIdentity((string)R.Get(manifest,"Id")!,(string)R.Get(manifest,"Name")!,
                (string)R.Get(manifest,"Version")!,(string)R.Get(manifest,"GlobalName")!,Hash(package),Hash(bundle),Hash(System.Text.Encoding.UTF8.GetBytes(types)));
        });
    }

    public async Task<NativeResult> DispatchAsync(Plan before,byte[]? package,Func<string,JsonElement,Task<JsonElement>> invokeNative)
    {
        ArgumentNullException.ThrowIfNull(invokeNative);
        var current=Inspect(before.Method,before.Id,package,before.Overwrite);
        if(!Equivalent(before,current))throw new BridgeException("FrontendBaselineChanged","Frontend package, file groups, registry or source changed after inspection.");
        JsonElement native;
        if(before.Method=="uploadFrontendLibrary") native=await WithPackageAsync(package!,path=>invokeNative(before.Method,PlatformJson.Element(new{packageFilePath=path,overwrite=before.Overwrite})));
        else native=await invokeNative(before.Method,PlatformJson.Element(new{libraryId=before.Id}));
        var after=CaptureAfter(before);
        var target=after.Registry.Where(x=>x.Id.Equals(before.Id,StringComparison.OrdinalIgnoreCase)).ToArray();
        var savedPackage=after.LocalFiles.SingleOrDefault(x=>x.Path.EndsWith("/"+before.Id+"/library.zip",StringComparison.OrdinalIgnoreCase));
        var savedBundle=after.LocalFiles.SingleOrDefault(x=>x.Path.EndsWith("/FrontendLibraries/"+before.Id+"/bundle.js",StringComparison.OrdinalIgnoreCase));
        var registryOk=before.Method=="deleteFrontendLibrary"?target.Length==0 && after.LocalFiles.Count==0:
            target.Length==1 && target[0].Version==before.Version && target[0].Id==before.Id && target[0].Name==before.Name && target[0].GlobalName==before.GlobalName && target[0].TypeDefinitionSha256==before.TypeDefinitionSha256 &&
            target[0].FileName.Replace('\\','/')=="FrontendLibraries/"+before.Id+"/bundle.js" &&
            target[0].PackageFileName.Replace('\\','/')==before.Id+"/library.zip" && savedPackage?.Sha256==before.PackageSha256 && savedBundle?.Sha256==before.BundleSha256;
        var unrelatedBefore=before.Registry.Where(x=>!x.Id.Equals(before.Id,StringComparison.OrdinalIgnoreCase)).ToArray();
        var unrelatedAfter=after.Registry.Where(x=>!x.Id.Equals(before.Id,StringComparison.OrdinalIgnoreCase)).ToArray();
        registryOk &= unrelatedBefore.SequenceEqual(unrelatedAfter);
        bool synchronized;
        if(before.Method=="uploadFrontendLibrary") synchronized=Boolean(native,"runtimeFileSynchronized") && SameBundle(after);
        else synchronized=after.RuntimeFiles.Count==0;
        if(before.Method=="deleteFrontendLibrary" && !Boolean(native,"deleted"))registryOk=false;
        return new(true,registryOk,synchronized,Boolean(native,"requiresRegenerate"),native,before,after);
    }
    internal Plan CaptureAfter(Plan before)
    {
        _=guard.Capture();var owner=fileGroups.Resolve(new(ModuleKind.CustomLibrary,"Current")) ?? throw new BridgeException("FrontendOwnerUnavailable","Native owner disappeared.");
        var localRoot=Root("LocalDataFolderPath");var user=Root("LocalUserFilePath");var packages=Root("LocalFrontendLibraryPath");var runtime=Root("ServerUserFilePath");
        var local=SnapshotTrees(user,"FrontendLibraries/"+before.Id,packages,before.Id);var server=SnapshotTrees(runtime,"FrontendLibraries/"+before.Id);
        var source=before.SourceFiles.Select(x=>new FileEvidence(x.Path,HashOrNull(NativeSaveVerification.ReadSource(context.CaptureSnapshot().Target.ProjectPath,x.Path)))).ToArray();
        return before with{Registry=Registry(),Owner=owner.NativeModuleName,OwnerFiles=owner.Paths,LocalFiles=local,RuntimeFiles=server,SourceFiles=source,ProjectSourceRevision=NativeProjectGuard.SourceRevision(context.CaptureSnapshot().Target.ProjectPath,false)};
    }
    private IReadOnlyList<Library> Registry()
    {
        if(!ReferenceEquals(R.Get(R.Static(context.DesignerAssembly.GetType("s",true)!,"get_Instance")!,"et"),settings) || !ReferenceEquals(R.Get(settings,"CustomLibraries"),nativeLibraries))throw new BridgeException("FrontendContextChanged","Native frontend settings changed.");
        if(R.Get(nativeLibraries,"FrontendLibraryList") is not IEnumerable list)throw new BridgeException("FrontendRegistryUnavailable","Native frontend library registry is unavailable.");
        var rows=list.Cast<object>().Select(item=>new Library((string)R.Get(item,"Id")!,(string)R.Get(item,"Name")!,(string)R.Get(item,"Version")!,
            (string)R.Get(item,"FileName")!,(string)R.Get(item,"PackageFileName")!,R.Get(item,"GlobalName") as string,R.Get(item,"Description") as string,
            R.Get(item,"TypeDefinition") is string types?Hash(System.Text.Encoding.UTF8.GetBytes(types)):null)).OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray();
        if(rows.Any(x=>string.IsNullOrWhiteSpace(x.Id)) || rows.Select(x=>x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=rows.Length)throw new BridgeException("FrontendRegistryAmbiguous","Native frontend library IDs are invalid or duplicated.");
        return rows;
    }
    private void ValidateId(string id)
    {
        if(string.IsNullOrWhiteSpace(id) || id.Length>128 || id is "." or ".." || id.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.')) || id.EndsWith('.'))throw new BridgeException("FrontendIdentityInvalid","Frontend library ID is not a safe native path segment.");
        R.Static(service,"ehh",id);
    }
    private string Root(string name)=>Path.GetFullPath((string)R.Static(config,"get_"+name)!);
    private static bool Same(string a,string b)=>Path.GetFullPath(a).TrimEnd('\\','/').Equals(Path.GetFullPath(b).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);
    private static bool Within(string root,string child)=>Path.GetFullPath(child).StartsWith(Path.GetFullPath(root).TrimEnd('\\','/')+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value));
    private static string? HashOrNull(byte[]? value)=>value is null?null:Hash(value);
    private static bool Boolean(JsonElement value,string name)
    {
        if(value.ValueKind!=JsonValueKind.Object)throw new BridgeException("FrontendNativeResponseInvalid","Native frontend lifecycle returned a non-object response.");
        var matches=value.EnumerateObject().Where(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length!=1 || matches[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new BridgeException("FrontendNativeResponseInvalid","Native frontend response is missing Boolean "+name+".");
        return matches[0].Value.GetBoolean();
    }
    private static bool SameBundle(Plan state)
    {
        var local=state.LocalFiles.SingleOrDefault(x=>x.Path.EndsWith("/bundle.js",StringComparison.OrdinalIgnoreCase));
        var runtime=state.RuntimeFiles.SingleOrDefault(x=>x.Path.EndsWith("/bundle.js",StringComparison.OrdinalIgnoreCase));
        return local?.Sha256 is not null && local.Sha256==runtime?.Sha256;
    }
    private static IReadOnlyList<FileEvidence> SnapshotTrees(params string[] pairs)
    {
        var result=new List<FileEvidence>();
        for(var i=0;i<pairs.Length;i+=2)
        {
            var root=pairs[i];var relative=pairs[i+1];var dir=NativeSaveVerification.Under(root,relative);
            if(!Directory.Exists(dir))continue;
            void Walk(string current)
            {
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new BridgeException("FrontendPathIsLink","Frontend library tree contains a link.");
                foreach(var file in Directory.EnumerateFiles(current))
                {
                    if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0 || new FileInfo(file).Length>MaximumPackage)throw new BridgeException("FrontendFileInvalid","Frontend file is linked or too large.");
                    result.Add(new FileEvidence(Path.GetFullPath(file).Replace('\\','/'),Hash(File.ReadAllBytes(file))));
                }
                foreach(var sub in Directory.EnumerateDirectories(current))Walk(sub);
            }
            Walk(dir);
        }
        if(result.Select(x=>x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=result.Count)
            throw new BridgeException("FrontendFileCollision","Frontend paths contain a case-insensitive alias.");
        return result.OrderBy(x=>x.Path,StringComparer.Ordinal).ToArray();
    }
    private static IReadOnlyList<string> EnumerateSourceFiles(string project,params string[] folders)
    {
        if(Directory.Exists(project))
            return SnapshotTrees(folders.SelectMany(folder=>new[]{project,folder}).ToArray())
                .Select(x=>Path.GetRelativePath(project,x.Path.Replace('/',Path.DirectorySeparatorChar)).Replace('\\','/')).ToArray();
        if(!File.Exists(project) || !Path.GetExtension(project).Equals(".fgcc",StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("FrontendSourceUnavailable","The frontend source is not an existing project directory or FGCC archive.");
        using var input=File.OpenRead(project);using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        var paths=zip.Entries.Select(entry=>entry.FullName.Replace('\\','/')).Where(path=>!path.EndsWith('/') &&
            folders.Any(folder=>path.StartsWith(folder+"/",StringComparison.OrdinalIgnoreCase))).ToArray();
        if(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Length || paths.Any(path=>!folders.Any(folder=>path.StartsWith(folder+"/",StringComparison.Ordinal))))
            throw new BridgeException("FrontendSourceAmbiguous","The frontend source contains duplicate or case-aliased paths.");
        return paths;
    }
    private static bool Equivalent(Plan a,Plan b)=>a.Method==b.Method && a.Id==b.Id && a.Name==b.Name && a.Version==b.Version && a.GlobalName==b.GlobalName && a.PackageSha256==b.PackageSha256 && a.BundleSha256==b.BundleSha256 && a.TypeDefinitionSha256==b.TypeDefinitionSha256 && a.Overwrite==b.Overwrite && a.Owner==b.Owner && a.OwnerFiles.SequenceEqual(b.OwnerFiles) && a.Registry.SequenceEqual(b.Registry) && a.LocalFiles.SequenceEqual(b.LocalFiles) && a.RuntimeFiles.SequenceEqual(b.RuntimeFiles) && a.SourceFiles.SequenceEqual(b.SourceFiles) && a.ProjectSourceRevision==b.ProjectSourceRevision;
    private static T WithPackage<T>(byte[] bytes,Func<string,T> action)
    {
        var directory=Path.Combine(Path.GetTempPath(),"ForguncyDesignerMcp","frontend-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"library.zip");
        try { File.WriteAllBytes(file,bytes);return action(file); }
        finally { if(File.Exists(file))File.Delete(file);if(Directory.Exists(directory))Directory.Delete(directory); }
    }
    private static async Task<T> WithPackageAsync<T>(byte[] bytes,Func<string,Task<T>> action)
    {
        var directory=Path.Combine(Path.GetTempPath(),"ForguncyDesignerMcp","frontend-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"library.zip");
        try { await File.WriteAllBytesAsync(file,bytes);return await action(file); }
        finally { if(File.Exists(file))File.Delete(file);if(Directory.Exists(directory))Directory.Delete(directory); }
    }
}
