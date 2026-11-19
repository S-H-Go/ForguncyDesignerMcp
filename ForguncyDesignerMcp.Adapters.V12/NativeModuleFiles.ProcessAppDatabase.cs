using System.Collections;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class NativeModuleFiles
{
    internal NativeProcessAppDatabase CaptureProcessAppDatabase()=>NativeProcessAppDatabase.Capture(save,(string)R.Get(save,"LocalPath")!,
        (string)R.Get(save,"LastPath")!,(string)R.Call(save,"GetDocumentPath")!);

    // Derive Edit owners from the actual native table checkout collectors and
    // expand registered direct/tracked file intersections to a fixed point.
    internal NativeModuleFilePlan CaptureProcessAppDatabaseOwners(NativeProcessAppDatabase database,bool collaboration)
    {
        var tableColleague=Colleague(ModuleKind.Table) ?? throw AppDenied("The native table colleague is unavailable.");
        var root=R.Get(tableColleague,"Root") ?? throw AppDenied("The native table tree is unavailable.");
        var visited=new HashSet<object>(ReferenceEqualityComparer.Instance); var models=new List<object>();
        void Visit(object node)
        {
            if(!visited.Add(node)) throw AppDenied("The native table tree is cyclic or ambiguous.");
            if(node.GetType().GetProperty("Items",R.Flags) is not null)
            {
                if(R.Get(node,"Loaded") is not true) throw AppDenied("The complete native table tree must be loaded.");
                foreach(var child in ((IEnumerable)R.Get(node,"Items")!).Cast<object>()) Visit(child);
            }
            else models.Add(node);
        }
        Visit(root);
        var simpleType=assembly.GetType("Forguncy.Common.SimpleModule",true)!;
        var stateType=Assembly.Load("Forguncy.Collaboration").GetType("Forguncy.Collaboration.ModuleState",true)!;
        var tupleType=typeof(ValueTuple<,>).MakeGenericType(simpleType,stateType); var edit=Enum.Parse(stateType,"Edit");
        var tuples=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase); var simpleByName=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
        string Name(object simple)=>(string)R.Get(simple,"ModuleName")!;
        void Add(object tuple)
        {
            var simple=tuple.GetType().GetField("Item1",R.Flags)?.GetValue(tuple) ?? throw AppDenied("Native table owner tuple is invalid.");
            var state=tuple.GetType().GetField("Item2",R.Flags)?.GetValue(tuple)?.ToString();
            var name=Name(simple);
            if(tuple.GetType()!=tupleType || state!="Edit") throw AppDenied("Native shared database owners must be exact Edit tuples.");
            if(simpleByName.TryGetValue(name,out var existing)) {if(!existing.Equals(simple)) throw AppDenied("Native shared database owner identity is ambiguous."); return;}
            simpleByName.Add(name,simple); tuples.Add(name,tuple);
        }
        foreach(var model in models)
        {
            var simple=R.Static(simpleType,"Create",model)!;
            var directFiles=DirectReferences(simple).ToArray();
            var ownsApplicationDatabase=directFiles.Contains(database.Name,StringComparer.OrdinalIgnoreCase);
            if(R.Get(model,"ExternalDatabaseInfo") is null && !ownsApplicationDatabase)
                throw AppDenied("A native built-in table provider omitted the application database owner.");
            if(!ownsApplicationDatabase) continue;
            if(!database.HasExisting) throw AppDenied("An existing native table owner refers to an absent application database.");
            var executorType=assembly.GetType("Forguncy.Common.TableCheckOutExecutor",true)!;
            var constructor=executorType.GetConstructors(R.Flags).Single(ctor=>ctor.GetParameters().Length==3 && ctor.GetParameters()[1].ParameterType.IsInstanceOfType(model) && ctor.GetParameters()[2].ParameterType==stateType);
            var executor=constructor.Invoke([null,model,edit]);
            var related=(R.Call(executor,"GetRelatedModules") as IEnumerable)?.Cast<object>().ToArray() ?? throw AppDenied("The native table checkout collector is unavailable.");
            if(!related.Any(tuple=>Equals(tuple.GetType().GetField("Item1",R.Flags)?.GetValue(tuple),simple))) throw AppDenied("The native table collector omitted its target.");
            foreach(var tuple in related) Add(tuple);
        }
        NativeModuleFilePlan Collect()=>tuples.Count==0 ? new([],[]) : CaptureNativeModulesCore(tuples.Values,false) ?? throw AppDenied("A native shared database owner has no complete source group.");
        var plan=Collect();
        var inventoryFiles=new Dictionary<string,string[]>(StringComparer.Ordinal);
        if(collaboration)
        {
            var manager=R.Static(assembly.GetType("Forguncy.Common.CollaborationManager",true)!,"get_Instance")!;
            if(R.Get(manager,"InCollaborationMode") is not true || R.Get(manager,"Modules") is not IDictionary registered || R.Get(manager,"SaveLoadTrackedFiles") is not IDictionary tracked)
                throw AppDenied("The complete native collaboration owner registry is unavailable.");
            var extensions=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!; var stage=(string)R.Get(save,"LastPath")!;
            string ProviderPath(string value)
            {
                var folder=value.EndsWith('/') || value.EndsWith('\\');
                return NativePath(folder ? value.TrimEnd('/','\\') : value)+(folder?"/":"");
            }
            static bool Intersects(string left,string right)=>left.Equals(right,StringComparison.OrdinalIgnoreCase) ||
                left.EndsWith('/') && right.StartsWith(left,StringComparison.OrdinalIgnoreCase) || right.EndsWith('/') && left.StartsWith(right,StringComparison.OrdinalIgnoreCase);
            var inventory=new List<(string Name,object Simple,string State,string[] Paths)>(); var registryNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var key in registered.Keys.Cast<object>())
            {
                if(key is not string name || !registryNames.Add(name) || registered[key] is not { } module || (string?)R.Get(module,"ModuleName")!=name) throw AppDenied("Native collaboration registration is ambiguous.");
                var simple=Activator.CreateInstance(simpleType,[module])!;
                var direct=R.Static(extensions,"GetDirectReferFiles",module,stage,"../Upload") as IEnumerable ?? throw AppDenied("A native module file provider is unavailable.");
                var paths=direct.Cast<string>().Select(ProviderPath).ToList();
                foreach(var trackedKey in tracked.Keys.Cast<object>())
                    if(trackedKey is string trackedName && trackedName.Equals(name,StringComparison.OrdinalIgnoreCase))
                    {
                        if(trackedName!=name || tracked[trackedKey] is not IEnumerable values) throw AppDenied("Native tracked owner files are ambiguous.");
                        paths.AddRange(values.Cast<string>().Select(ProviderPath));
                    }
                if(paths.GroupBy(path=>path,StringComparer.OrdinalIgnoreCase).Any(group=>group.Distinct(StringComparer.Ordinal).Count()!=1))
                    throw AppDenied("Native provider file paths contain a case alias.");
                var normalizedPaths=paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                inventory.Add((name,simple,R.Get(module,"State")?.ToString() ?? "",normalizedPaths)); inventoryFiles.Add(name,normalizedPaths);
            }
            var known=plan.BeforeGroups.SelectMany(group=>group.Paths).Append(database.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var item in inventory.Where(item=>simpleByName.ContainsKey(item.Name))) foreach(var path in item.Paths) known.Add(path);
            var changed=true;
            while(changed)
            {
                changed=false;
                foreach(var item in inventory)
                {
                    if(simpleByName.ContainsKey(item.Name) || !item.Paths.Any(path=>known.Any(other=>Intersects(path,other)))) continue;
                    if(item.State=="Remove") throw AppDenied("A shared application database owner is pending native deletion.");
                    if(!database.HasExisting && item.Paths.Any(path=>path.Equals(database.Name,StringComparison.OrdinalIgnoreCase)))
                        throw AppDenied("A registered native owner refers to an absent application database.");
                    Add(Activator.CreateInstance(tupleType,new object?[]{item.Simple,edit})!); foreach(var path in item.Paths) known.Add(path); changed=true;
                }
            }
            if(simpleByName.Keys.Any(name=>inventory.Count(item=>item.Name==name)!=1)) throw AppDenied("A native shared database owner is not uniquely registered.");
            plan=Collect();
        }
        var protectedGroups=new List<NativeModuleFileGroup>();
        foreach(var original in plan.BeforeGroups)
        {
            if(original.RequiredFiles.Contains(database.Name,StringComparer.OrdinalIgnoreCase)) throw AppDenied("The shared application database cannot be a fabricated primary owner.");
            var extra=(inventoryFiles.TryGetValue(original.NativeModuleName,out var nativePaths)?nativePaths:[])
                .Where(path=>!path.Equals(database.Name,StringComparison.OrdinalIgnoreCase) && !original.Paths.Contains(path,StringComparer.OrdinalIgnoreCase)).ToArray();
            if(extra.Any(path=>path.EndsWith('/'))) throw AppDenied("A shared owner has an unprojected native directory group.");
            if(extra.Any(path=>Path.GetExtension(path).Equals(".sqlite3",StringComparison.OrdinalIgnoreCase))) throw AppDenied("An additional shared SQLite database requires its own complete owner plan.");
            var extraHashes=extra.ToDictionary(path=>path,path=>LocalHash(path) ?? throw AppDenied("A native shared owner file is missing from the working cache."),StringComparer.Ordinal);
            database.RegisterOwnerFiles(original.NativeModuleName,extraHashes);
            var group=new NativeModuleFileGroup(original.Address,original.NativeModuleName,original.RequiredFiles,original.AbsentFiles,
                original.ReferencedFiles.Where(path=>!path.Equals(database.Name,StringComparison.OrdinalIgnoreCase)).Concat(extra).Distinct(StringComparer.Ordinal).ToArray(),original.ExpectedJson,
                ()=>original.Current() && database.BeforeCurrent() && extraHashes.All(pair=>LocalHash(pair.Key)==pair.Value),
                (path,bytes)=>extraHashes.TryGetValue(path,out var expected)?Hash(bytes)==expected:original.MatchesContent(path,bytes),original.NativeIdentity,original.IsPresent,
                original.RequiredDirectories,original.AbsentDirectories,original.DirectoriesRequireLocal);
            if(!Verify(group)) throw AppDenied("A native shared application database owner or saved source differs from baseline.");
            protectedGroups.Add(group);
        }
        return new(protectedGroups,[],nativeChanges:tuples.Values.ToArray());
    }
    internal bool ProcessAppOwnerUnchanged(NativeProcessAppDatabase database,NativeModuleFileGroup before,NativeModuleFileGroup after)
    {
        if(!database.OwnerFiles.TryGetValue(before.NativeModuleName,out var extra)) return true;
        var oldReferences=before.ReferencedFiles.Where(path=>!path.Equals(database.Name,StringComparison.OrdinalIgnoreCase) && !extra.ContainsKey(path)).ToHashSet(StringComparer.Ordinal);
        var newReferences=after.ReferencedFiles.Where(path=>!path.Equals(database.Name,StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        return ReferenceEquals(before.NativeIdentity,after.NativeIdentity) && before.RequiredFiles.ToHashSet(StringComparer.Ordinal).SetEquals(after.RequiredFiles) &&
            oldReferences.SetEquals(newReferences) && before.ExpectedJson.Count==after.ExpectedJson.Count &&
            before.ExpectedJson.All(pair=>after.ExpectedJson.TryGetValue(pair.Key,out var current) && Equivalent(pair.Value,current));
    }

    internal List<NativeModuleFileGroup>? ProtectProcessAppOwnerFiles(NativeProcessAppDatabase database,List<NativeModuleFileGroup> groups)
    {
        var remaining=database.OwnerFiles.Keys.ToHashSet(StringComparer.Ordinal); var result=new List<NativeModuleFileGroup>();
        foreach(var original in groups)
        {
            if(!database.OwnerFiles.TryGetValue(original.NativeModuleName,out var extras)) {result.Add(original); continue;}
            remaining.Remove(original.NativeModuleName);
            if(extras.Keys.Any(path=>original.AbsentFiles.Contains(path,StringComparer.OrdinalIgnoreCase))) return null;
            result.Add(new(original.Address,original.NativeModuleName,original.RequiredFiles,original.AbsentFiles,
                original.ReferencedFiles.Concat(extras.Keys).Distinct(StringComparer.Ordinal).ToArray(),original.ExpectedJson,
                ()=>original.Current() && extras.All(pair=>LocalHash(pair.Key)==pair.Value),
                (path,bytes)=>extras.TryGetValue(path,out var hash)?Hash(bytes)==hash:original.MatchesContent(path,bytes),
                original.NativeIdentity,original.IsPresent,original.RequiredDirectories,original.AbsentDirectories,original.DirectoriesRequireLocal));
        }
        return remaining.Count==0?result:null;
    }

    private static BridgeException AppDenied(string message)=>new("NativeProcessLifecyclePlanUnavailable",message);
}
