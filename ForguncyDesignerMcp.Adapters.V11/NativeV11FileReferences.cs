using System.Collections;
using System.IO;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

/// <summary>Native file providers and command interfaces; incomplete context never means no references.</summary>
internal static class NativeV11FileReferences
{
    internal static ReferenceResult Find(NativeV11Session session,ModuleAddress address,string relative)
    {
        var references=new List<ModuleReference>();
        try
        {
            session.VerifyContext();NativeV11StaticFiles.VerifyConfigOwner(session);
            var before=session.Context.CaptureSnapshot();
            var assembly=session.Context.DesignerAssembly;
            var expected=new[]{ModuleKind.Page,ModuleKind.MasterPage,ModuleKind.UserControlPage,ModuleKind.Table,ModuleKind.ServerCommand,ModuleKind.SharedCommand,ModuleKind.ScheduledTask,ModuleKind.Notification,ModuleKind.Report,ModuleKind.BusinessProcess};
            if(expected.Any(k=>!session.Routes.Any(r=>r.Kind==k)))throw new InvalidOperationException("Native definition route inventory is incomplete.");
            var manager=NativeV11Reflection.Static(assembly.GetType("Forguncy.Common.CollaborationManager",true)!,"get_Instance")!;
            var modules=new Dictionary<string,object>(StringComparer.Ordinal);
            void Add(object module)
            {
                var name=NativeV11Reflection.Get(module,"ModuleName") as string;
                if(string.IsNullOrWhiteSpace(name) || !modules.TryAdd(name,module))throw new InvalidOperationException("Duplicate or unnamed native module.");
            }
            var roots=new List<object>();
            foreach(var route in session.Routes)
            {
                if(roots.Any(r=>ReferenceEquals(r,route.Root)))continue;
                roots.Add(route.Root);RequireLoaded(route.Root,new HashSet<object>(ReferenceEqualityComparer.Instance));
                if(route.IsPage)
                    foreach(var item in session.Items(route.Kind))
                    {
                        var sheet=session.Sheet(item.Address.Name);
                        if(sheet is null || NativeV11Reflection.Get(sheet,"Loaded") is not true || NativeV11Reflection.Get(sheet,"Tag") is not object tag || !ReferenceEquals(NativeV11Reflection.Get(tag,"PageInfo"),item.Model))
                            throw new InvalidOperationException("Every native page worksheet must be fully loaded and identity-bound.");
                    }
                foreach(var module in (IEnumerable)NativeV11Reflection.Call(manager,"EnumFolderAndFolderItems",route.Root)!)Add(module!);
            }
            var settings=NativeV11Reflection.Get(session.Document,"ForguncySettings") ?? throw new InvalidOperationException("Missing native settings.");
            var image=NativeV11Reflection.Get(settings,"ImageFolder") ?? throw new InvalidOperationException("Missing native image tree.");
            RequireLoaded(image,new HashSet<object>(ReferenceEqualityComparer.Instance));
            foreach(var module in (IEnumerable)NativeV11Reflection.Call(manager,"EnumFolderAndFolderItems",image)!)Add(module!);
            var simple=assembly.GetType("Forguncy.Common.SimpleModule",true)!;
            var create=simple.GetMethods(NativeV11Reflection.Flags).Single(m=>m.Name=="Create" && m.GetParameters().Length==2 && m.GetParameters()[0].ParameterType.FullName=="Forguncy.Collaboration.ModuleType");
            var enumType=create.GetParameters()[0].ParameterType;var moduleType=enumType.Assembly.GetType("Forguncy.Collaboration.Module",true)!;
            var empty=Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),moduleType))!;
            foreach(var name in new[]{"ApplicationSetting","Theme","UserService","CustomLibrary","MapJsonFile","DataContectionSet","Upload","PublishConfiguration","PublishSetting","PermissionGroupConfig"})
            {
                if(!Enum.IsDefined(enumType,name))continue;
                var identity=create.Invoke(null,[Enum.Parse(enumType,name),Enum.ToObject(create.GetParameters()[1].ParameterType,0)])!;
                var module=NativeV11Reflection.Call(identity,"ToModule",empty)!;
                var moduleName=NativeV11Reflection.Get(module,"ModuleName") as string ?? throw new InvalidOperationException("Singleton owner has no name.");
                if(!modules.ContainsKey(moduleName))Add(module);
            }
            var common=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="CommonUtilities");
            var feature=common.GetType("CommonUtilities.PluginFeatureHelper",false)?.GetMethod("IsPluginFeatureEnabled",NativeV11Reflection.Flags,null,Type.EmptyTypes,null);
            if(feature is null)throw new InvalidOperationException("Native plugin feature inventory is unavailable.");
            if(feature.Invoke(null,null) is true)
            {
                if(!Enum.IsDefined(enumType,"Plugin"))throw new InvalidOperationException("Enabled plugin feature has no native module type.");
                var plugin=create.Invoke(null,[Enum.Parse(enumType,"Plugin"),Enum.ToObject(create.GetParameters()[1].ParameterType,0)])!;
                var module=NativeV11Reflection.Call(plugin,"ToModule",empty)!;
                var name=NativeV11Reflection.Get(module,"ModuleName") as string ?? throw new InvalidOperationException("Plugin owner has no name.");
                if(!modules.ContainsKey(name))Add(module);
            }
            if(before.InCollaborationMode)
            {
                if(!ReferenceEquals(manager,NativeV11Collaboration.RequireBoundManager(session)) || NativeV11Reflection.Get(manager,"Modules") is not IDictionary registered)
                    throw new InvalidOperationException("The bound native collaboration module registry is unavailable.");
                foreach(DictionaryEntry entry in registered)
                {
                    if(entry.Key is not string name || entry.Value is not object value || NativeV11Reflection.Get(value,"ModuleName") as string != name)
                        throw new InvalidOperationException("A native collaboration module registration is invalid.");
                    modules[name]=value;
                }
            }
            foreach(var entry in modules)
            {
                var type=NativeV11Reflection.Get(entry.Value,"ModuleType")?.ToString() ?? throw new InvalidOperationException("Missing native module type.");
                if(type is "MapJsonFile" or "Folder" or "Upload")continue;
                var paths=DirectFiles(assembly,entry.Value,session.LocalPath);
                if(paths.Any(path=>Matches(session.LocalPath,path,relative)))
                {
                    var source=Map(type,entry.Key) ?? throw new InvalidOperationException("Unknown referencing native owner: "+type);
                    references.Add(new(source,address,"Native GetDirectReferFiles: "+entry.Key));
                }
            }
            var commandHelper=assembly.GetType(session.Profile.Version=="11.0.104.0"?"rg":"rc",true)!;
            var referenceBase=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="ServerDesignerCommon").GetType("Forguncy.Common.NameReferenceHelperBase",true)!;
            var ignore=referenceBase.GetProperty("IgnoreDisableCommands",NativeV11Reflection.Flags) ?? throw new MissingMemberException("IgnoreDisableCommands");
            var previous=ignore.GetValue(null);
            try
            {
                ignore.SetValue(null,false);
                foreach(var list in (IEnumerable)NativeV11Reflection.Static(commandHelper,"EnumCommandListInternal")!)
                {
                    var commands=Located(list!);var location=NativeV11Reflection.Get(list!,"Location");
                    foreach(var located in (IEnumerable)NativeV11Reflection.Static(commandHelper,"EnumCommandInList",commands,location,true)!)
                    {
                        var command=Located(located!);
                        foreach(var path in CommandFiles(assembly,command))
                        {
                            if(Matches(session.LocalPath,path,relative))
                            {
                                var commandLocation=NativeV11Reflection.Get(located!,"Location") ?? throw new InvalidOperationException("Native command has no location.");
                                var baseType=commandLocation.GetType();
                                while(baseType.FullName!="GrapeCity.Forguncy.Plugin.LocationIndicator")baseType=baseType.BaseType ?? throw new InvalidOperationException("Unknown native command location.");
                                var locations=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(baseType))!;
                                foreach(var segment in (IEnumerable)NativeV11Reflection.Call(commandLocation,"Enum")!)locations.Add(segment);
                                var owners=((IEnumerable)NativeV11Reflection.Static(assembly.GetType("Forguncy.Common.ReferenceFinder",true)!,"EnumEditedModulesByReferences",locations)!).Cast<object>().ToArray();
                                if(owners.Length==0)throw new InvalidOperationException("Native command reference has no mapped owner.");
                                foreach(var owner in owners)
                                {
                                    var ownerType=NativeV11Reflection.Get(owner,"ModuleType")?.ToString() ?? throw new InvalidOperationException("Command owner type missing.");
                                    var ownerName=NativeV11Reflection.Get(owner,"ModuleName") as string ?? throw new InvalidOperationException("Command owner name missing.");
                                    var source=Map(ownerType,ownerName) ?? throw new InvalidOperationException("Unknown command owner type: "+ownerType);
                                    references.Add(new(source,address,"Native command file provider: "+ownerName));
                                }
                            }
                        }
                    }
                }
            }
            finally{ignore.SetValue(null,previous);}
            session.VerifyContext();var after=session.Context.CaptureSnapshot();
            if(before.Target!=after.Target || before.Revision!=after.Revision || after.Busy)throw new InvalidOperationException("Project changed during native reference enumeration.");
            return new(references,true,"Native module file providers and command file interfaces scanned; dynamic runtime-generated paths remain outside static analysis.");
        }
        catch(Exception error) when(error is not OperationCanceledException)
        {return new(references,false,"Native file reference scan incomplete: "+error.GetBaseException().GetType().Name+"; "+error.GetBaseException().Message);}
    }
    internal static IReadOnlyList<string> DirectFiles(Assembly assembly,object module,string localRoot)
    {
        var helper=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
        return ((IEnumerable<string>)NativeV11Reflection.Static(helper,"GetDirectReferFiles",module,localRoot,"../Upload")!).ToArray();
    }
    internal static IReadOnlyList<string> CommandFiles(Assembly assembly,object command)
    {
        var helper=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
        var paths=new List<string>();
        foreach(var contract in command.GetType().GetInterfaces())
        {
            var method=contract.FullName switch {"GrapeCity.Forguncy.Plugin.IReferFiles"=>"GetFilesFromIReferFiles","GrapeCity.Forguncy.Plugin.INeedUploadFileByUser"=>"GetFilesFromNeedUploadFileByUser",_=>null};
            if(method is null)continue;
            var values=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(contract))!;values.Add(command);
            paths.AddRange((IEnumerable<string>)NativeV11Reflection.Static(helper,method,values)!);
        }
        return paths;
    }
    private static object Located(object value)
    {
        for(var type=value.GetType();type is not null;type=type.BaseType)
            if(type.GetProperty("Object",NativeV11Reflection.Flags|BindingFlags.DeclaredOnly) is {} property)return property.GetValue(value) ?? throw new InvalidOperationException("Missing native located value.");
        throw new MissingMemberException("Native located value has no Object property.");
    }
    private static void RequireLoaded(object node,HashSet<object> visited)
    {
        if(!visited.Add(node))throw new InvalidOperationException("Native tree is cyclic.");
        var children=node.GetType().GetProperty("Items",NativeV11Reflection.Flags)?.GetValue(node) as IEnumerable;
        var loaded=node.GetType().GetProperty("Loaded",NativeV11Reflection.Flags);
        if(children is not null && loaded is null || loaded is not null && loaded.GetValue(node) is not true)throw new InvalidOperationException("Native definition tree is not fully loaded.");
        if(children is not null)foreach(var child in children)RequireLoaded(child!,visited);
    }
    private static bool Matches(string root,string nativePath,string relative)
    {
        var path=nativePath.Replace('\\','/');if(path.StartsWith("../Upload/",StringComparison.OrdinalIgnoreCase))return false;
        var basePath=Path.GetFullPath(root).TrimEnd('\\','/');var full=Path.GetFullPath(Path.IsPathRooted(nativePath)?nativePath:Path.Combine(basePath,nativePath));
        if(!full.StartsWith(basePath+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Native dependency escaped the bound resource cache.");
        return string.Equals(Path.GetRelativePath(basePath,full).Replace('\\','/'),relative,StringComparison.OrdinalIgnoreCase);
    }
    private static ModuleAddress? Map(string type,string name)
    {
        ModuleKind? kind=type switch {"PCPage" or "MobilePage" or "TemplatePage"=>ModuleKind.Page,"MasterPCPage" or "MasterMobilePage"=>ModuleKind.MasterPage,"UserControlPage"=>ModuleKind.UserControlPage,"Table" or "TableSqlite"=>ModuleKind.Table,"TableView" or "TableProcessView"=>ModuleKind.View,"ServerCommand" or "ServerCommandPrivate"=>ModuleKind.ServerCommand,"ShareCommand" or "ShareCommandContainer"=>ModuleKind.SharedCommand,"TaskScheduler"=>ModuleKind.ScheduledTask,"Notification"=>ModuleKind.Notification,"PageReport" or "RdlReport"=>ModuleKind.Report,"ProcessItem"=>ModuleKind.BusinessProcess,"Image"=>ModuleKind.Image,"Theme"=>ModuleKind.Theme,"ApplicationSetting"=>ModuleKind.ApplicationSettings,"CustomLibrary"=>ModuleKind.CustomLibrary,"DataContectionSet"=>ModuleKind.DataConnection,"PermissionGroupConfig"=>ModuleKind.PermissionGroup,_=>null};
        return kind is null?null:new(kind.Value,kind is ModuleKind.ApplicationSettings or ModuleKind.Theme?"Current":name);
    }
}
