using System.Collections;
using System.IO.Compression;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// Read-only effect capture. It does not initialize the process engine,
// invoke CanExecute/checkout, load a database, or mutate a native model.
internal sealed record NativeProcessCreateCandidate(NativeModuleFilePlan Plan)
{
    // Candidate construction follows the detached native-template schema check.
    public bool InitializationVerified => true;
}
internal sealed class NativeProcessLifecycle(Assembly assembly,object settings,NativeProcesses processes,NativeModuleFiles files,Func<bool>? inCollaboration = null)
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    internal NativeModuleFilePlan Capture(ModuleMutationRequest request)
    {
        return CaptureCore(request);
    }
    internal NativeProcessCreateCandidate CaptureCreateCandidate(ModuleMutationRequest request)
    {
        if(request.Operation!=ModuleOperation.Create) throw Denied("A create candidate requires a Create request.");
        return new(CaptureCore(request));
    }
    // Discovery only: first initialization affects process views, folders,
    // pages, application settings and theme as well as the new process.
    // It does not authorize a checkout or call the initialization action.
    internal object[] CaptureFirstInitializationOwners(ModuleMutationRequest request)
    {
        if (request.Address.Kind != ModuleKind.BusinessProcess || request.Operation != ModuleOperation.Create)
            throw Denied("First initialization requires a process Create request.");
        if (!processes.Validate(request).Valid || processes.Definitions.Items().Count != 0)
            throw Denied("First initialization requires a valid new process and an empty process tree.");
        if (files.HasAnyProcessDatabase()) throw Denied("First initialization cannot replace an existing process database.");
        const string database = "ProcessForguncyDB.sqlite3";
        foreach (var suffix in new[] { "-wal", "-journal", "-shm" })
            if (File.Exists(NativeSaveVerification.Under(processes.LocalRoot, database + suffix)) ||
                File.Exists(NativeSaveVerification.Under(processes.StagingRoot, database + suffix)) ||
                NativeSaveVerification.ReadSource(processes.SourcePath, database + suffix) is not null)
                throw Denied("First initialization cannot replace or ignore existing process database sidecars.");
        var connection = NativeReflection.Get(settings, "ProcessDataConnectionSet");
        if (connection is not null && NativeReflection.Get(connection, "IsBuildInDatabase") is not true)
            throw Denied("First initialization only supports the built-in process database.");
        var type = assembly.GetType("Forguncy.Common.CreateProcessItemExecutor", true)!;
        var executor = Activator.CreateInstance(type, [null, request.Address.Name, true, false])!;
        var tuples = Related(executor);
        var tokens = tuples.Select(Token).ToArray();
        if (tokens.Length == 0 || tokens.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != tokens.Length ||
            tokens.Any(x => x.State is not ("New" or "Edit")))
            throw Denied("The native initialization owner set is empty, ambiguous, or has an unsupported state.");
        var model = CreateModel(request.Address.Name);
        var expected = (string)NativeReflection.Get(NativeReflection.Static(assembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", model)!, "ModuleName")!;
        if (tokens.Count(x => x.Name == expected && x.State == "New") != 1)
            throw Denied("The native initialization owner set lacks the exact new process.");
        var mapper = new NativeCollaborationModules(assembly, settings, Array.Empty<object>());
        foreach (var tuple in tuples)
        {
            var native = tuple.GetType().GetField("Item1", Flags)!.GetValue(tuple)!;
            var mapping = mapper.Describe(native);
            var state = Token(tuple).State;
            if ((state == "New" && mapping.ModelPresent) || (state == "Edit" && !mapping.ModelPresent))
                throw Denied("A native initialization owner conflicts with the current loaded project model.");
        }
        // Native initialization writes many built-in files beneath these
        // exact native folder names. Do not overwrite unregistered files left
        // in a cache, staging area, or the saved project.
        foreach (var token in tokens.Where(x => x.Name.StartsWith("Pages/", StringComparison.Ordinal) ||
                     x.Name.StartsWith("MasterPages/", StringComparison.Ordinal) || x.Name.StartsWith("Tables/", StringComparison.Ordinal)))
        {
            if (token.State != "New") throw Denied("Reusing an existing native initialization folder requires a separate collision plan.");
            if (DirectoryEntryExists(processes.LocalRoot, token.Name) || DirectoryEntryExists(processes.StagingRoot, token.Name) ||
                SourceEntryExists(processes.SourcePath, token.Name))
                throw Denied("A native initialization destination already exists in the source, cache, or staging area.");
        }
        return tuples;
    }
    private static bool DirectoryEntryExists(string root, string relative)
    {
        var path = NativeSaveVerification.Under(root, relative);
        return File.Exists(path) || Directory.Exists(path);
    }
    private static bool SourceEntryExists(string source, string relative)
    {
        if (Directory.Exists(source)) return DirectoryEntryExists(source, relative);
        using var input = File.OpenRead(source);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var prefix = relative.TrimEnd('/') + "/";
        return archive.Entries.Any(entry => entry.FullName.Replace('\\','/').Equals(relative,StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.Replace('\\','/').StartsWith(prefix,StringComparison.OrdinalIgnoreCase));
    }
    // Convert the complete actual executor output to the same native source
    // plan used by checkout and save. Discovery alone is never authorization.
    internal NativeModuleFilePlan CaptureFirstInitializationPlan(ModuleMutationRequest request)
    {
        var tuples = CaptureFirstInitializationOwners(request);
        var assets=NativeProcessBuiltInAssets.Capture(assembly,settings,processes.LocalRoot,processes.StagingRoot,processes.SourcePath);
        var appDatabase=files.CaptureProcessAppDatabase();
        var appOwners=files.CaptureProcessAppDatabaseOwners(appDatabase,inCollaboration?.Invoke()==true);
        NativeModuleFilePlan? plan;
        try { plan = files.CaptureNativeModulesCore(tuples, true, true); }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw Denied("The complete native initialization source plan is unavailable: " + error.GetType().Name); }
        var tokens = tuples.Select(Token).ToArray();
        var newTokens = tokens.Where(x => x.State == "New").Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var editTokens = tokens.Where(x => x.State == "Edit").Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (plan is null || !newTokens.SetEquals(plan.ExpectedNewModuleNames) ||
            !editTokens.SetEquals(plan.BeforeGroups.Select(group => group.NativeModuleName)) ||
            plan.BeforeGroups.Any(group => !files.Verify(group)))
            throw Denied("The native initialization owner/source plan is incomplete or differs from its saved baseline.");
        // Recheck absence after all model/source scans, before any checkout.
        if (files.HasAnyProcessDatabase()) throw Denied("A process database appeared during initialization planning.");
        var processName = tokens.Single(token => token.State == "New" && token.Name ==
            (string)NativeReflection.Get(NativeReflection.Static(assembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", CreateModel(request.Address.Name))!, "ModuleName")!).Name;
        var mergedGroups=new Dictionary<string,NativeModuleFileGroup>(StringComparer.Ordinal);
        foreach(var group in plan.BeforeGroups.Concat(appOwners.BeforeGroups))
        {
            if(mergedGroups.TryGetValue(group.NativeModuleName,out var previous))
            {
                if(!ReferenceEquals(previous.NativeIdentity,group.NativeIdentity)) throw Denied("A shared application database owner maps to different native models.");
                mergedGroups[group.NativeModuleName]=V12ProjectAdapter.MergeFileGroups(previous,group);
            }
            else mergedGroups.Add(group.NativeModuleName,group);
        }
        var mergedChanges=new Dictionary<string,object>(StringComparer.Ordinal);
        foreach(var tuple in plan.NativeChanges!.Concat(appOwners.NativeChanges ?? []))
        {
            var token=Token(tuple);
            if(mergedChanges.TryGetValue(token.Name,out var previous))
            {
                var first=previous.GetType().GetField("Item1",Flags)!.GetValue(previous)!; var second=tuple.GetType().GetField("Item1",Flags)!.GetValue(tuple)!;
                if(token.State!="Edit" || Token(previous).State!="Edit" || !first.Equals(second)) throw Denied("Shared application database owner tuples are ambiguous.");
            }
            else mergedChanges.Add(token.Name,tuple);
        }
        if(!appDatabase.BeforeCurrent()) throw Denied("The application database baseline changed during first-initialization planning.");
        return new(mergedGroups.Values.ToArray(), plan.ExpectedNewModules, new Dictionary<string,ModuleAddress> { [processName] = request.Address }, mergedChanges.Values.ToArray(), assets, appDatabase);
    }
    internal void RequireSameFirstInitializationPlan(ModuleMutationRequest request, NativeModuleFilePlan approved)
    {
        var current = CaptureFirstInitializationPlan(request);
        static string Key(object tuple)
        {
            var native = tuple.GetType().GetField("Item1", Flags)!.GetValue(tuple)!;
            return string.Join("\n", NativeReflection.Get(native,"ModuleName"), NativeReflection.Get(native,"ModuleType"),
                NativeReflection.Get(native,"IconType"), NativeReflection.Get(native,"ExclusiveType"),
                tuple.GetType().GetField("Item2", Flags)!.GetValue(tuple));
        }
        if (approved.NativeChanges is null || current.NativeChanges is null ||
            !approved.NativeChanges.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(current.NativeChanges.Select(Key).Order(StringComparer.Ordinal),StringComparer.Ordinal) ||
            approved.ProcessAssets is null || current.ProcessAssets is null || approved.AppDatabase is null || current.AppDatabase is null ||
            !approved.AppDatabase.SameBaseline(current.AppDatabase) || !approved.AppDatabase.BeforeCurrent() ||
            !approved.ProcessAssets.Templates.SequenceEqual(current.ProcessAssets.Templates,StringComparer.Ordinal) ||
            !approved.ProcessAssets.ResourceHashes.SequenceEqual(current.ProcessAssets.ResourceHashes,StringComparer.Ordinal) ||
            approved.BeforeGroups.Count != current.BeforeGroups.Count ||
            approved.BeforeGroups.Any(group => !group.Current() || !current.BeforeGroups.Any(other => ReferenceEquals(group.NativeIdentity,other.NativeIdentity))))
            throw Denied("Native first-initialization owners or their source baselines changed after approval.");
    }
    private NativeModuleFilePlan CaptureCore(ModuleMutationRequest request)
    {
        if(request.Address.Kind!=ModuleKind.BusinessProcess || request.Operation is not (ModuleOperation.Create or ModuleOperation.Delete))
            throw Denied("Only process Create/Delete have a native owner collector.");
        var valid=processes.Validate(request);
        if(!valid.Valid) throw Denied("Native process validation did not approve the definition: "+string.Join("; ",valid.Issues.Select(x=>x.Code)));
        var definitions=processes.Definitions;
        var all=definitions.Items(); // Requires every native definition tree to be loaded.
        var connection=NativeReflection.Get(settings,"ProcessDataConnectionSet")!;
        if(NativeReflection.Get(connection,"IsBuildInDatabase") is not true)
            throw Denied("Only the observed native built-in process data mode has a reviewed owner collector.");
        var hasProcessDatabase = files.HasAnyProcessDatabase();
        if(hasProcessDatabase) RequireInitializedDatabase();
        object[] tuples;
        if(request.Operation==ModuleOperation.Create)
        {
            // Use the native already-initialized branch after schema proof.
            if(!files.HasVerifiedProcessDatabase()) throw Denied("The native process database must already exist and match cache, staging and source before the initialized create branch can be selected.");
            var type=assembly.GetType("Forguncy.Common.CreateProcessItemExecutor",true)!;
            var executor=Activator.CreateInstance(type,[null,request.Address.Name,false,false])!;
            tuples=Related(executor);
        }
        else
        {
            var model=definitions.Find(request.Address).Model;
            var vmType=assembly.GetType("Forguncy.Views.ObjectViews.BusinessProcessItemViewModel",true)!;
            var vm=Activator.CreateInstance(vmType,[null,model])!;
            var baseType=assembly.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel",true)!;
            var selected=Array.CreateInstance(baseType,1); selected.SetValue(vm,0);
            var helper=assembly.GetType("Forguncy.Views.ObjectViews.BusinessProcessViewModelHelper",true)!;
            var executor=NativeReflection.Static(helper,"GetDeleteCollaborationExecutorForBuiltInDatabase",selected)!;
            tuples=Related(executor);
        }
        var primaryName=(string)NativeReflection.Get(NativeReflection.Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"Create",
            request.Operation==ModuleOperation.Delete?definitions.Find(request.Address).Model:CreateModel(request.Address.Name))!,"ModuleName")!;
        var expectedState=request.Operation==ModuleOperation.Create?"New":"Remove";
        if(tuples.Count(tuple=>Token(tuple).Name==primaryName && Token(tuple).State==expectedState)!=1 ||
           tuples.Any(tuple=>Token(tuple).Name!=primaryName && Token(tuple).State!="Edit") ||
           tuples.Select(tuple=>Token(tuple).Name).Distinct(StringComparer.Ordinal).Count()!=tuples.Length)
            throw Denied("Native process collector returned an ambiguous target or unsupported owner state.");
        var plan=files.CaptureNativeModulesCore(tuples,false) ?? throw Denied("A native process owner has no complete file-group mapping.");
        if(plan.BeforeGroups.Any(group=>!files.Verify(group))) throw Denied("A native process owner or shared file group does not match saved source and staging.");
        if(request.Operation==ModuleOperation.Create)
        {
            if(plan.ExpectedNewModules.Count!=1 || plan.ExpectedNewModules[0].Name!=primaryName) throw Denied("The native process New token is incomplete or ambiguous.");
            return new(plan.BeforeGroups,plan.ExpectedNewModules,new Dictionary<string,ModuleAddress>{{primaryName,request.Address}},plan.NativeChanges);
        }
        if(plan.ExpectedNewModules.Count!=0 || plan.BeforeGroups.Count!=all.Count || !plan.BeforeGroups.Any(group=>ReferenceEquals(group.NativeIdentity,definitions.Find(request.Address).Model)))
            throw Denied("Built-in process deletion requires the complete native process-owner set.");
        return plan;
    }
    // Retain the real deleted owner's shared references in its tombstone. The
    // process definition disappears, but its shared runtime database does not.
    // Capture before the mutation so later source verification compares to the
    // exact pre-delete runtime bytes, not merely to another current copy.
    internal Func<NativeModuleFileGroup,NativeModuleFileGroup>? CaptureRetainedDatabase(NativeModuleFileGroup before)
    {
        const string name = "ProcessForguncyDB.sqlite3";
        if (!files.HasAnyProcessDatabase()) return null;
        RequireInitializedDatabase();
        if (!before.ReferencedFiles.Contains(name, StringComparer.Ordinal))
            throw Denied("The native process owner did not identify its shared database reference.");
        var local = NativeSaveVerification.Under(processes.LocalRoot, name);
        var staged = NativeSaveVerification.Under(processes.StagingRoot, name);
        var hash = NativeFiles.Hash(File.ReadAllBytes(local));
        bool StillRetained()
        {
            try
            {
                if (!File.Exists(local) || NativeFiles.Hash(File.ReadAllBytes(local)) != hash) return false;
                foreach (var suffix in new[] { "-wal", "-journal" })
                    if ((File.Exists(local + suffix) && new FileInfo(local + suffix).Length != 0) ||
                        (File.Exists(staged + suffix) && new FileInfo(staged + suffix).Length != 0) ||
                        NativeSaveVerification.ReadSource(processes.SourcePath, name + suffix) is { Length: > 0 }) return false;
                return true;
            }
            catch { return false; }
        }
        return removed =>
        {
            if (!ReferenceEquals(before.NativeIdentity, removed.NativeIdentity) || removed.IsPresent())
                throw Denied("The deleted process tombstone does not identify the original native owner.");
            return new(removed.Address, removed.NativeModuleName, removed.RequiredFiles, removed.AbsentFiles,
                before.ReferencedFiles, removed.ExpectedJson, () => removed.Current() && StillRetained(),
                (file, bytes) => before.ReferencedFiles.Contains(file, StringComparer.Ordinal)
                    ? before.MatchesContent(file, bytes) : removed.MatchesContent(file, bytes),
                removed.NativeIdentity, removed.IsPresent);
        };
    }
    // Inspect detached copies only. No runtime process database, WAL, or engine
    // is opened by the schema probe, and existing runtime data is never changed.
    internal void RequireInitializedDatabase()
    {
        if (!files.HasVerifiedProcessDatabase()) throw Denied("Process database cache, staging and saved source must match before lifecycle capture.");
        const string name = "ProcessForguncyDB.sqlite3";
        var local = NativeSaveVerification.Under(processes.LocalRoot, name);
        var staged = NativeSaveVerification.Under(processes.StagingRoot, name);
        foreach (var suffix in new[] { "-wal", "-journal" })
        {
            if ((File.Exists(local + suffix) && new FileInfo(local + suffix).Length != 0) ||
                (File.Exists(staged + suffix) && new FileInfo(staged + suffix).Length != 0) ||
                NativeSaveVerification.ReadSource(processes.SourcePath, name + suffix) is { Length: > 0 })
                throw Denied("A nonempty process database WAL or journal requires native snapshot reconciliation.");
        }
        var saved = NativeSaveVerification.ReadSource(processes.SourcePath, name) ?? throw Denied("Saved process database is missing.");
        var bin = Path.GetDirectoryName(assembly.Location) ?? throw Denied("Native Designer location is unavailable.");
        var website = Directory.GetParent(bin)?.FullName ?? throw Denied("Native Designer resources are unavailable.");
        var template = Path.Combine(website, "DesignerResources", "BPMN", "ProcessForguncyDB_Initial.sqlite3");
        if (!File.Exists(template)) throw Denied("The exact native process schema template is unavailable.");
        var scratch = Path.Combine(Path.GetTempPath(), "forguncy-process-schema-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        var templateScratch = Path.Combine(Path.GetTempPath(), "forguncy-process-template-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            File.WriteAllBytes(scratch, saved);
            File.Copy(template, templateScratch, false);
            if (NativeFiles.Hash(File.ReadAllBytes(templateScratch)) != "6AF6EB83D53AE4B2326E136FF7D2959C1C6C8C82E98CA9FA529B58E05F9C2BFC")
                throw Denied("The pinned V12 native process schema template fingerprint changed.");
            var required = ReadSchema(templateScratch);
            var actual = ReadSchema(scratch);
            if (required.Count == 0 || required.Any(pair => !actual.TryGetValue(pair.Key, out var sql) || sql != pair.Value))
                throw Denied("The process database does not contain the exact native initialized table schema.");
            if (!files.HasVerifiedProcessDatabase()) throw Denied("The process database changed during schema verification.");
        }
        catch (BridgeException) { throw; }
        catch (Exception error) { throw Denied("The native process database schema could not be verified: " + error.GetType().Name); }
        finally { foreach (var path in new[] { scratch, templateScratch }) { try { File.Delete(path); } catch { } } }
    }
    private static Dictionary<string,string> ReadSchema(string path)
    {
        using var connection = AccountAuthoritySnapshot.OpenSqlite(path, true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string,string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }
    private object CreateModel(string name)
    {
        var type=Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.BusinessProcessModel",true)!;
        var model=Activator.CreateInstance(type)!; NativeReflection.Set(model,"Name",name); return model;
    }
    private static object[] Related(object executor) => (NativeReflection.Call(executor,"GetRelatedModules") as IEnumerable)?.Cast<object>().ToArray()
        ?? throw Denied("The native process effect collector returned no plan.");
    private static (string Name,string State) Token(object tuple)
    {
        var simple=tuple.GetType().GetField("Item1",Flags)?.GetValue(tuple) ?? throw Denied("Native process tuple has no owner.");
        var state=tuple.GetType().GetField("Item2",Flags)?.GetValue(tuple)?.ToString() ?? throw Denied("Native process tuple has no state.");
        return ((string)NativeReflection.Get(simple,"ModuleName")!,state);
    }
    private static BridgeException Denied(string message)=>new("NativeProcessLifecyclePlanUnavailable",message);
}







