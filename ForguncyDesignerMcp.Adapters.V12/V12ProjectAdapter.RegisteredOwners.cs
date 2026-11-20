using System.Collections;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter : IProjectSaveDispatchAdapter
{
    private readonly HashSet<object> pendingRegisteredOwners = new(ReferenceEqualityComparer.Instance);

    private async Task<NativeCollaboration.EditRegistration?> PrepareApiOwnerAsync(NativeApiFileCapture? capture, ProjectSnapshot reviewed, CancellationToken token)
    {
        if (capture is null) return null;
        bool variable = NativeModuleFiles.IsGlobalVariableEdit(capture.Namespace, capture.Method);
        bool library = capture is { Namespace: "app.customLibrary", Method: "updateContent" };
        bool clear = capture is { Namespace: "page", Method: "clearCells" };
        if (!variable && !library && !clear) return null;
        if (variable) ValidateGlobalVariable(capture.Address, capture.Method switch
        {
            "createGlobalVariable" => ModuleOperation.Create,
            "deleteGlobalVariable" => ModuleOperation.Delete,
            _ => ModuleOperation.Update
        });
        if (!reviewed.InCollaborationMode) return null;
        var expectedOwner = capture.Namespace switch
        {
            "page" => capture.Address,
            "app" => NativeModuleFiles.ApplicationRoot,
            _ => new ModuleAddress(ModuleKind.CustomLibrary, "Current")
        };
        if (capture.Plan?.BeforeGroups is not [var group] || group.Address != expectedOwner)
            throw new BridgeException("NativeOwnerUnavailable", "The mutation requires its existing native owner and file group.");
        return await PrepareRootTrackingAsync(group, reviewed, token);
    }

    internal static ModuleAddress? RegisteredApplicationOwner(ModuleKind kind) =>
        kind is ModuleKind.GlobalVariable or ModuleKind.LanguageResource ? NativeModuleFiles.ApplicationRoot : null;

    private Task<NativeCollaboration.EditRegistration> PrepareApplicationModuleOwnerAsync(ModuleKind kind, ProjectSnapshot reviewed, CancellationToken token)
    {
        var address = RegisteredApplicationOwner(kind) ??
            throw new BridgeException("NativeOwnerUnavailable", "No registered application owner exists for this logical module kind.");
        var group = targetFiles.Value.Resolve(address) ??
            throw new BridgeException("NativeOwnerUnavailable", "The registered native application owner and file group are required.");
        return PrepareRootTrackingAsync(group, reviewed, token);
    }

    private async Task<NativeCollaboration.EditRegistration> PrepareRootTrackingAsync(NativeModuleFileGroup group, ProjectSnapshot reviewed, CancellationToken token)
    {
        var mapping = collaborationModules.Value;
        if (mapping.MapNativeModule(mapping.ResolveNativeModule(group.Address)) != group.Address)
            throw new BridgeException("NativeOwnerUnavailable", "Native ownership does not match the reviewed root.");
        guard.Value.RequireOwnedModule(reviewed, group.NativeModuleName);
        ProjectSaveDiagnostic? failure = null;
        var matchesBaseline = apiTargetGroups.TryGetValue(group.NativeIdentity, out var previous)
            ? previous.Current() : targetFiles.Value.Verify(group, evidence => failure = evidence, "beforeMutation");
        if (!matchesBaseline)
            throw new BridgeException(group.Address.Kind == ModuleKind.CustomLibrary ? "LibraryBaselineChanged" : "BaselineChanged", "Native root metadata must match the staged and saved group before editing." +
                (failure is null ? "" : $" Stage={failure.Stage}; fileRole={failure.FileRole}; reason={failure.Code}."));
        var register = await collaboration.Value.PrepareRegisteredEditFilesAsync(group, token);
        if (guard.Value.Capture().Revision != reviewed.Revision || !group.Current())
            throw new BridgeException("BaselineChanged", "The root or project changed during native ownership verification.");
        return register;
    }

    private void ValidateGlobalVariable(ModuleAddress address, ModuleOperation operation, bool exactName = false)
    {
        var actions = context.DesignerAssembly.GetType("Forguncy.AI.Actions.AppSandboxAIActions", true)!;
        string name;
        try { name = (string)R.Static(actions, "NormalizeGlobalVariableName", address.Name)!; }
        catch (ArgumentException e) { throw new BridgeException("InvalidGlobalVariableName", e.Message); }
        if (exactName && name != address.Name)
            throw new BridgeException("InvalidGlobalVariableName", "The typed module address must use the exact normalized global variable name.");
        bool creating = operation == ModuleOperation.Create;
        try { R.Static(actions, creating ? "EnsureGlobalVariableNotExists" : "EnsureGlobalVariableExists", name); }
        catch (ArgumentException e) { throw new BridgeException(creating ? "ModuleAlreadyExists" : "ModuleNotFound", e.Message); }
        if (operation != ModuleOperation.Delete) return;
        // The native alias and the typed single-name formula scanner cover complementary references.
        var references = modules.Value.References(new(ModuleKind.GlobalVariable, name));
        if (!references.Complete)
            throw new BridgeException("GlobalVariableReferencesUnavailable", "Native variable reference enumeration was incomplete.");
        if (references.References.Count != 0 || R.Static(actions, "FindGlobalVariableReferencesCore", name) is not IList { Count: 0 })
            throw new BridgeException("ModuleHasReferences", "Deletion requires a complete native scan and a variable with no remaining references.");
    }
}
