using System.Collections;
using System.Security.Cryptography;
using System.Text;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed class NativeProjectGuard
{
    private readonly AdapterHostContext context;
    private readonly object? workbook;
    private readonly object? settings;
    private readonly string project;
    private readonly ProjectTarget target;
    internal NativeProjectGuard(AdapterHostContext context)
    {
        this.context = context;
        workbook = NativeReflection.Get(context.NativeDocument, "Workbook");
        settings = NativeReflection.Get(context.NativeDocument,"ForguncySettings");
        target = context.CaptureSnapshot().Target;
        project = Path.GetFullPath(target.ProjectPath);
    }

    internal ProjectSnapshot Capture()
    {
        var snapshot = context.CaptureSnapshot();
        CheckTarget(snapshot.Target);
        // Registered C# APIs and native reference scanners resolve this global
        // document/settings pair. An old adapter must never dispatch against it.
        var state = context.DesignerAssembly.GetType("s",true)!.GetProperty("Instance",NativeReflection.Flags)!.GetValue(null)!;
        if (!ReferenceEquals(NativeReflection.Get(state,"ew"),context.NativeDocument) ||
            !ReferenceEquals(NativeReflection.Get(state,"et"),settings) ||
            !ReferenceEquals(NativeReflection.Get(context.NativeDocument,"ForguncySettings"),settings))
            throw new BridgeException("DocumentChanged","Native API global document/settings differ from the bound adapter document.");
        if (!ReferenceEquals(workbook, NativeReflection.Get(context.NativeDocument, "Workbook")) ||
            !string.Equals(project, Path.GetFullPath((string)NativeReflection.Get(context.NativeDocument, "CurrentFilePath")!), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("DocumentChanged", "Native document or workbook was replaced; construct a new adapter.");
        var dirty = NativeReflection.Get(context.NativeDocument, "ContainsUnsavedChanges") is true;
        var busy = NativeReflection.Get(context.NativeDocument, "Opening") is true || NativeReflection.Get(context.NativeDocument, "IsImporting") is true;
        return snapshot with { HasUnsavedChanges = dirty || snapshot.HasUnsavedChanges, Busy = busy || snapshot.Busy,
            Revision = Wire.Hash(snapshot.Revision + "\n" + SourceRevision(project) + "\n" + dirty + "\n" + busy +
                (snapshot.InCollaborationMode ? "\n"+NativeCollaborationGit.GuardFingerprint(project) : "")) };
    }

    internal void CheckTarget(ProjectTarget requested)
    {
        if (requested.InstanceId != target.InstanceId || requested.SessionId != target.SessionId ||
            requested.DesignerVersion != V12AdapterFactory.Version ||
            !string.Equals(Path.GetFullPath(requested.ProjectPath), project, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ProjectTargetMismatch", "Instance, session, path or precise designer version differs from the bound document.");
    }

    internal ProjectSnapshot RequireWrite(ProjectTarget requested, string revision, UserIdentity? expectedUser, bool allowDirty = false)
    {
        CheckTarget(requested);
        var state = Capture();
        if (state.Revision != revision) throw new BridgeException("ProjectRevisionChanged", "Project baseline changed; read a fresh snapshot.");
        if (state.Busy || (!allowDirty && state.HasUnsavedChanges))
            throw new BridgeException("ProjectNotReady", "Project is busy or contains unsaved edits.");
        // er.CanSave rejects DemoStatus.Load and otherwise allows saving. Read
        // the same fixed native state before the first memory write, without
        // triggering CanSave's warning dialog or changing demo/license state.
        var demo=NativeReflection.Get(context.NativeDocument,"DemoStatusModel") ?? throw new BridgeException("NativeWritePermissionUnavailable","Native document demo/write state is unavailable.");
        if(Convert.ToInt32(NativeReflection.Get(demo,"DemoStatus"))==1)
            throw new BridgeException("NativeDocumentReadOnly","Native read-only demo documents cannot be changed or saved through this adapter.");
        if (expectedUser is not null && state.CurrentUser != expectedUser)
            throw new BridgeException("UserChanged", "Current designer identity differs from the mutation identity.");
        return state;
    }

    // Existing-module edits must not acquire or rewrite another user's lock implicitly.
    internal void RequireOwnedModule(ProjectSnapshot snapshot, string moduleName)
    {
        if (!snapshot.InCollaborationMode) return;
        var managerType = context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager", true)!;
        var manager = managerType.GetField("_instance", NativeReflection.Flags)!.GetValue(null)
            ?? throw new BridgeException("CollaborationUnavailable", "The native collaboration manager is not initialized.");
        if (NativeReflection.Get(manager, "Modules") is not IDictionary modules || modules[moduleName] is not { } module)
            throw new BridgeException("ModuleRegistrationMissing", "Native collaboration module is not registered.");
        var owner = NativeReflection.Get(module, "LockedBy");
        var user = owner is null ? null : new UserIdentity((string)NativeReflection.Get(owner, "UserName")!, (string)NativeReflection.Get(owner, "Email")!);
        if (Convert.ToInt32(NativeReflection.Get(module, "State")) is not (1 or 3 or 4) || user is null || user != snapshot.CurrentUser ||
            NativeReflection.Call(manager, "IsModuleCheckoutByCurrentUser", moduleName) is not true)
            throw new BridgeException("ModuleLockRequired", "The exact native module must be checked out by the current user.");
    }

    internal void RequireAvailableModule(ProjectSnapshot snapshot, string moduleName)
    {
        if (!snapshot.InCollaborationMode) return;
        var managerType=context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager",true)!;
        var manager=managerType.GetProperty("Instance",NativeReflection.Flags)!.GetValue(null)!;
        if(NativeReflection.Get(manager,"Modules") is not IDictionary modules || modules[moduleName] is not { } module)
            throw new BridgeException("ModuleRegistrationMissing","Native collaboration module is not registered.");
        var state=NativeReflection.Get(module,"State")!;
        var owner=NativeReflection.Get(module,"LockedBy");
        var user=owner is null?null:new UserIdentity((string)NativeReflection.Get(owner,"UserName")!,(string)NativeReflection.Get(owner,"Email")!);
        if(state.ToString()=="Remove" || (state.ToString()!="Unlocked" && user!=snapshot.CurrentUser))
            throw new BridgeException("ModuleLockUnavailable","The native owner is deleted or locked by another user.");
    }

    internal static string SourceRevision(string path, bool includeCollaboration = true)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));
        if (File.Exists(path))
        {
            using var input = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(input));
        }
        if (!Directory.Exists(path)) throw new BridgeException("ProjectSourceMissing", "Bound project source no longer exists.");
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".local", ".agents", ".codex", "LocalData", "LastSaveFiles", "output", "bin", "obj" };
        IEnumerable<string> Files(string root)
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("ProjectPathIsLink", "Source baseline cannot traverse a linked directory.");
            foreach (var file in Directory.EnumerateFiles(root).Order(StringComparer.Ordinal)) yield return file;
            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
                if (!ignored.Contains(Path.GetFileName(directory)))
                    foreach (var file in Files(directory)) yield return file;
        }
        foreach (var file in Files(path))
        {
            if (!includeCollaboration && string.Equals(Path.GetRelativePath(path,file), ".collaboration", StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("ProjectPathIsLink", "Source baseline cannot read linked files.");
            Append(Path.GetRelativePath(path, file).Replace('\\', '/')); Append("\0");
            using var input = File.OpenRead(file);
            hash.AppendData(SHA256.HashData(input)); Append("\0");
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
