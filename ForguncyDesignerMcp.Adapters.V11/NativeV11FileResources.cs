using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

internal sealed partial class NativeV11Resources
{
    internal IReadOnlyDictionary<string, byte[]> CaptureFileMutationSaveFiles(ModuleMutationRequest request)
    {
        if (request.Operation != ModuleOperation.Delete) return CaptureSaveFiles(request.Address);
        session.VerifyContext();
        var (root, relative, _) = FileAddress(request.Address);
        if (File.Exists(V11Safety.Under(root, relative)))
            throw new BridgeException("MemoryRevisionChanged", "The deleted native resource reappeared before save.");
        return new Dictionary<string, byte[]>();
    }

    private void ValidateFileMutation(ModuleMutationRequest request)
    {
        session.VerifyContext();V11Safety.Address(request.Address);
        NativeV11StaticFiles.VerifyConfigOwner(session);
        if(request.Scope!=EffectScope.ProjectDefinition || request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete))throw new NotSupportedException("Only project file create/update/delete are supported.");
        var (root,relative,source)=FileAddress(request.Address);
        var category=source.Split('/')[0];
        if(category is not ("UserFile" or "UserMapFile" or "UserTemplateFile"))throw new NotSupportedException("This native file owner is not supported by the ordinary resource writer.");
        var registry=NativeV11StaticFiles.Resolve(session);
        if(!registry.Folders.Any(f=>f.Replace('\\','/').Trim('/').Equals(category,StringComparison.Ordinal)))throw new BridgeException("ResourceOwnerUnavailable","The native static-file saver does not own this exact resource root.");
        var path=V11Safety.Under(root,relative);
        var cursor=Path.GetFullPath(root);
        foreach(var part in relative.Split('/'))
        {
            if(Directory.Exists(cursor))
            {
                var aliases=Directory.EnumerateFileSystemEntries(cursor).Select(Path.GetFileName).Where(n=>string.Equals(n,part,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(aliases.Length>1 || aliases.Length==1 && aliases[0]!=part)throw new BridgeException("ResourcePathCaseAlias","Use the exact native resource path spelling.");
            }
            cursor=Path.Combine(cursor,part);
        }
        var parent=Path.GetDirectoryName(path)!;
        if(!Directory.Exists(parent))throw new BridgeException("ResourceFolderMissing","Create the native parent folder before creating a file.");
        if(Directory.Exists(path))throw new BridgeException("ResourceNameConflict","A directory occupies the resource file name.");
        if(request.Operation==ModuleOperation.Create)
            NativeV11CreationGuard.VerifySourceGroupAbsent(session.SourcePath,session.LocalPath,session.LastPath,[source]);
        else if(!File.Exists(path))throw new BridgeException("ResourceNotFound","The native resource file does not exist.");
        if(request.Operation==ModuleOperation.Delete)
        {
            if(request.Document is not null)throw new BridgeException("InvalidResourceMutation","Deletion cannot carry replacement bytes.");
            var references=FindReferences(request.Address);
            if(!references.Complete || references.References.Count!=0)throw new BridgeException("ResourceReferencesIncomplete","Native resource references are incomplete or nonempty; deletion is refused.");
            return;
        }
        var document=request.Document;
        if(document is null || document.Address!=request.Address || document.Companions.Count!=1 || document.Metadata.ValueKind!=JsonValueKind.Object)throw new BridgeException("InvalidResourceMutation","Resource metadata and one native companion are required.");
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in document.Metadata.EnumerateObject())
            if(!keys.Add(property.Name) || property.Value.ValueKind!=JsonValueKind.String || (property.Name=="name"?property.Value.GetString()!=request.Address.Name:property.Name!="resourceRoot" || property.Value.GetString()!=category))
                throw new BridgeException("InvalidResourceMutation","Resource name and native root must retain their readback identity.");
        if(keys.Count!=2)throw new BridgeException("InvalidResourceMutation","Resource name and root metadata are required.");
        V11Safety.Companion(document.Companions[0],source);
        var bytes=Convert.FromBase64String(document.Companions[0].ContentBase64);
        if(bytes.Length>32*1024*1024)throw new BridgeException("ResourceTooLarge","Resource exceeds the 32 MiB limit.");
        if(category=="UserMapFile")
        {
            if(!request.Address.Name.EndsWith(".json",StringComparison.OrdinalIgnoreCase))throw new BridgeException("InvalidMapResource","Map resources require JSON.");
            using var parsed=JsonDocument.Parse(bytes);if(parsed.RootElement.ValueKind!=JsonValueKind.Object)throw new BridgeException("InvalidMapResource","Map resource must contain a JSON object.");
        }
    }

    private Action ApplyFileMutation(ModuleMutationRequest request)
    {
        ValidateFileMutation(request);
        var (root,relative,_)=FileAddress(request.Address);var path=V11Safety.Under(root,relative);
        var dirty=NativeV11Reflection.Get(session.Document,"ContainsUnsavedChanges") is true;
        var incoming=request.Operation==ModuleOperation.Delete?null:Convert.FromBase64String(request.Document!.Companions[0].ContentBase64);
        return ApplyOrdinaryFileCore(session.Context.DesignerAssembly,path,request.Operation,incoming,session.VerifyContext,
            state=>NativeV11Reflection.Set(session.Document,"ContainsUnsavedChanges",state),dirty);
    }

    internal static Action ApplyOrdinaryFileCore(System.Reflection.Assembly assembly,string path,ModuleOperation operation,byte[]? incoming,Action verify,Action<bool> setDirty,bool wasDirty)
    {
        var native=NativeV11LocalLibraries.ResolveNativeFile(assembly);
        var common=AssemblyLoadContext.GetLoadContext(assembly)!.Assemblies.Single(a=>a.GetName().Name=="CommonUtilities");
        var utilities=common.GetType("Forguncy.Common.FileUtilities",true)!;
        var before=File.Exists(path)?File.ReadAllBytes(path):null;
        if(operation==ModuleOperation.Create?before is not null:before is null)throw new InvalidOperationException("Native file state changed before dispatch.");
        if(operation!=ModuleOperation.Delete && incoming is null)throw new ArgumentException("Resource bytes required.");
        var attributes=before is null?(FileAttributes?)null:File.GetAttributes(path);
        var creation=before is null?(DateTime?)null:File.GetCreationTimeUtc(path);
        var modified=before is null?(DateTime?)null:File.GetLastWriteTimeUtc(path);
        bool undone=false;
        void Copy(byte[] bytes,bool overwrite)
        {
            var dir=Path.Combine(Path.GetTempPath(),"ForguncyDesignerMcp-resource-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            var staged=Path.Combine(dir,Path.GetFileName(path));
            try{File.WriteAllBytes(staged,bytes);var prior=File.Exists(path)?File.GetLastWriteTimeUtc(path):DateTime.MinValue;var anchor=prior>DateTime.UtcNow?prior:DateTime.UtcNow;if(anchor>DateTime.MaxValue.AddSeconds(-4))throw new InvalidOperationException("Resource timestamp cannot be advanced safely.");File.SetLastWriteTimeUtc(staged,anchor.AddSeconds(4));NativeV11Reflection.Call(native,"Copy",staged,path,overwrite);}
            finally{if(File.Exists(staged))File.Delete(staged);if(Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir);}
        }
        void Undo()
        {
            if(undone)return;verify();
            if(before is not null && !File.Exists(path) && operation!=ModuleOperation.Delete)
                throw new BridgeException("ResourceRollbackConflict","Native cache file disappeared independently; rollback refused to recreate it.");
            if(File.Exists(path))
            {
                var current=File.ReadAllBytes(path);
                if(!(before is not null && current.AsSpan().SequenceEqual(before)) && !(incoming is not null && current.AsSpan().SequenceEqual(incoming)))
                    throw new BridgeException("ResourceRollbackConflict","Native cache changed independently; rollback refused to overwrite it.");
            }
            if(before is null){if(File.Exists(path))NativeV11Reflection.Call(native,"Delete",path);}
            else {if(File.Exists(path))NativeV11Reflection.Static(utilities,"MakeFileNotReadOnly",path);Copy(before,File.Exists(path));NativeV11Reflection.Call(native,"SetCreationTimeUtc",path,creation!.Value);NativeV11Reflection.Call(native,"SetLastWriteTimeUtc",path,modified!.Value);NativeV11Reflection.Call(native,"SetAttributes",path,attributes!.Value);}
            setDirty(wasDirty);undone=true;
        }
        try
        {
            verify();
            if(operation==ModuleOperation.Delete){NativeV11Reflection.Static(utilities,"MakeFileNotReadOnly",path);NativeV11Reflection.Call(native,"Delete",path);}
            else {if(before is not null)NativeV11Reflection.Static(utilities,"MakeFileNotReadOnly",path);Copy(incoming!,before is not null);}
            verify();
            if(incoming is null?File.Exists(path):!File.Exists(path)||!File.ReadAllBytes(path).AsSpan().SequenceEqual(incoming))throw new InvalidOperationException("Native resource readback differs from the requested bytes.");
            setDirty(true);return Undo;
        }
        catch(Exception error){try{Undo();}catch(Exception rollback){throw new AggregateException("Native resource mutation and rollback failed.",error,rollback);}throw;}
    }
}
