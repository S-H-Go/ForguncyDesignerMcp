using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// Definition-only JSON/BPMN operations. This class never invokes engine, deployment,
// workflow-instance, table creation or DeleteProcessData APIs.
public sealed class NativeProcesses
{
    private readonly NativeReports definitions;
    private readonly object colleague;
    private readonly Assembly assembly;
    private readonly object? document;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public NativeProcesses(Assembly assembly, object document)
    {
        this.assembly = assembly; this.document = document;
        var colleagues = document.GetType().GetProperty("ForguncyColleagueManager", Flags)!.GetValue(document)!;
        colleague = colleagues.GetType().GetProperty("ProcessModel", Flags)!.GetValue(colleagues)!;
        var manager = assembly.GetType("Forguncy.Common.BusinessProcessManager", true)!.GetProperty("Instance", Flags)!.GetValue(null);
        definitions = new(assembly, colleague, manager, true);
    }
    public NativeProcesses(Assembly assembly, object colleague, object? manager) { this.assembly = assembly; this.colleague = colleague; definitions = new(assembly, colleague, manager, true); }
    internal NativeReports Definitions => definitions;
    public string LocalRoot => definitions.LocalRoot;
    public string SourcePath => definitions.SourcePath;
    internal string StagingRoot => (string)NativeReflection.Get(NativeReflection.Get(NativeReflection.Get(NativeReflection.Get(colleague, "Mediator")!, "SaveDataColleague")!, "SaveLoadFile")!, "LastPath")!;
    public ModulePage List(ModuleQuery query) => definitions.List(query);
    public ModuleDocument Read(ModuleAddress address) => definitions.Read(address);
    public string GetModuleName(ModuleAddress address) => definitions.GetModuleName(address);
    public ReferenceResult References(ModuleAddress address) => definitions.References(address);
    public ModuleValidationResult Validate(ModuleMutationRequest request) => definitions.Validate(request);
    public IReadOnlyList<string> Apply(ModuleMutationRequest request) => definitions.Apply(request);
    public bool VerifySource(ModuleMutationRequest request) => definitions.VerifySource(request);

    internal static (MethodInfo Copy, MethodInfo Wait, MethodInfo Initialize) FirstInitializationContract(Assembly designer)
    {
        var databaseAssembly = Assembly.Load("ForguncyDataBaseService");
        if (databaseAssembly.GetName().Version?.ToString() != V12AdapterFactory.Version ||
            !string.Equals(Path.GetDirectoryName(databaseAssembly.Location), Path.GetDirectoryName(designer.Location), StringComparison.OrdinalIgnoreCase) ||
            NativeFiles.Hash(File.ReadAllBytes(databaseAssembly.Location)) != "72DAD46001CFCB59AD4E6C200E1286A9C4B0522AC0FD8295976E633C64FE7D04")
            throw new BridgeException("NativeProcessInitializationContractChanged", "The native process database service differs from the reviewed Designer build.");
        var database = databaseAssembly.GetType("ForguncyDataAccess.DataAccess", true)!;
        var copy = database.GetMethod("CopySqliteDBIndirect", Flags, null, [typeof(string), typeof(string)], null);
        var manager = designer.GetType("Forguncy.Common.BusinessProcessManager", true)!;
        var wait = manager.GetMethod("WaitLoadComplete", Flags, null, Type.EmptyTypes, null);
        var folder = designer.GetType("Forguncy.Views.ObjectViews.BusinessProcessFolderViewModel", true)!;
        var initialize = folder.GetMethod("CreatProcessTableAndPageAsync", Flags, null, [typeof(string), typeof(Task)], null);
        if (copy is null || !copy.IsStatic || copy.ReturnType != typeof(void) || wait is null || wait.ReturnType != typeof(Task) ||
            initialize is null || initialize.ReturnType != typeof(Task<bool>))
            throw new BridgeException("NativeProcessInitializationContractChanged", "The pinned native process initialization methods differ from the reviewed build.");
        return (copy, wait, initialize);
    }

    internal void VerifyFirstInitializationContext() => _ = FirstInitializationContext();

    private ((MethodInfo Copy, MethodInfo Wait, MethodInfo Initialize) Contract, object Folder, string Template, string Destination) FirstInitializationContext()
    {
        if (document is null) throw new BridgeException("NativeProcessInitializationUnavailable", "An actual bound native document is required.");
        var contract = FirstInitializationContract(assembly);
        var settings = NativeReflection.Get(document, "ForguncySettings")!;
        var state = assembly.GetType("s", true)!.GetProperty("Instance", Flags)!.GetValue(null)!;
        if (!ReferenceEquals(NativeReflection.Get(state, "ew"), document) || !ReferenceEquals(NativeReflection.Get(state, "et"), settings))
            throw new BridgeException("DocumentChanged", "The native initialization document changed.");
        var explorer = NativeReflection.Get(state, "fh")!;
        var tree = NativeReflection.Get(explorer, "Processes")!;
        var folder = NativeReflection.Get(tree, "Root")!;
        if (folder.GetType() != assembly.GetType("Forguncy.Views.ObjectViews.BusinessProcessFolderViewModel", true) ||
            !ReferenceEquals(NativeReflection.Get(folder, "Model"), NativeReflection.Get(settings, "BusinessProcessList")))
            throw new BridgeException("NativeProcessInitializationUnavailable", "The actual native process root view model is unavailable.");
        var config = assembly.GetType("Forguncy.Common.Config", true)!;
        var destination = (string)config.GetProperty("LocalProcessDBSqliteFilePath", Flags)!.GetValue(null)!;
        var expected = NativeSaveVerification.Under(LocalRoot, "ProcessForguncyDB.sqlite3");
        if (!string.Equals(Path.GetFullPath(destination), expected, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("NativeProcessInitializationUnavailable", "Native process database path differs from the bound project cache.");
        var website = Directory.GetParent(Path.GetDirectoryName(assembly.Location)!)!.FullName;
        var template = Path.Combine(website, "DesignerResources", "BPMN", "ProcessForguncyDB_Initial.sqlite3");
        if (!File.Exists(template) || NativeFiles.Hash(File.ReadAllBytes(template)) != "6AF6EB83D53AE4B2326E136FF7D2959C1C6C8C82E98CA9FA529B58E05F9C2BFC")
            throw new BridgeException("NativeProcessInitializationUnavailable", "The installed native initialization template differs from the verified build.");
        return (contract, folder, template, destination);
    }

    // Called only after the complete native owner plan has been checked out and
    // the absence of existing process data has been revalidated by the caller.
    // Once the native copy begins, callers must retain the original operation
    // ID and await the native task even if cancellation was requested.
    internal async Task<IReadOnlyList<string>> InitializeFirstProcessAsync(Action revalidate, Func<IReadOnlyList<string>> createDefinition)
    {
        var (contract,folder,template,destination)=FirstInitializationContext();
        CopyInitialDatabase(contract.Copy, template, destination, revalidate);
        var manager = assembly.GetType("Forguncy.Common.BusinessProcessManager", true)!.GetProperty("Instance", Flags)!.GetValue(null)!;
        var load = contract.Wait.Invoke(manager, null) as Task ?? throw new BridgeException("NativeProcessInitializationUnavailable", "The native process load task is unavailable.");
        var task = contract.Initialize.Invoke(folder, [null, load]) as Task<bool> ?? throw new BridgeException("NativeProcessInitializationUnavailable", "The native process initialization task is unavailable.");
        return await CompleteFirstInitializationAsync(task,createDefinition);
    }

    internal static async Task<IReadOnlyList<string>> CompleteFirstInitializationAsync(Task<bool> initialization, Func<IReadOnlyList<string>> createDefinition)
    {
        // Native UI inserts the new process while the initialization task is
        // running, then awaits the same task. Preserve that ordering without
        // launching its interactive BPM designer.
        if (initialization.IsCompleted && !await initialization)
            throw new BridgeException("NativeProcessInitializationFailed", "Native process initialization did not complete successfully.");
        IReadOnlyList<string>? result=null;
        Exception? createError=null;
        try { result=createDefinition(); }
        catch (Exception error) { createError=error; }
        bool accepted;
        try { accepted=await initialization; }
        catch when (createError is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(createError).Throw(); throw; }
        if (createError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(createError).Throw();
        if (!accepted) throw new BridgeException("NativeProcessInitializationFailed", "Native process initialization did not complete successfully.");
        return result!;
    }

    internal static void CopyInitialDatabase(MethodInfo nativeCopy, string template, string destination, Action revalidate)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var temporary = Path.Combine(directory, ".forguncy-process-init-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        revalidate();
        if (File.Exists(destination) || Directory.Exists(destination) || File.Exists(temporary) || Directory.Exists(temporary))
            throw new BridgeException("NativeProcessInitializationUnavailable", "The native process database destination is no longer fresh.");
        try
        {
            // The vendor SQLite copy produces the database; an atomic,
            // no-overwrite move prevents replacing a file created meanwhile.
            nativeCopy.Invoke(null, [template, temporary]);
            revalidate();
            if (!File.Exists(temporary) || new[] { "-wal", "-journal", "-shm" }.Any(suffix => File.Exists(temporary + suffix)))
                throw new BridgeException("NativeProcessInitializationUnavailable", "The native database copy has incomplete sidecars.");
            File.Move(temporary, destination, false);
        }
        finally
        {
            // Only the unique native copy is cleaned, never the destination or
            // a sidecar whose contents have not been reconciled.
            if (File.Exists(temporary) && !new[] { "-wal", "-journal", "-shm" }.Any(suffix => File.Exists(temporary + suffix)))
                File.Delete(temporary);
        }
    }
}

