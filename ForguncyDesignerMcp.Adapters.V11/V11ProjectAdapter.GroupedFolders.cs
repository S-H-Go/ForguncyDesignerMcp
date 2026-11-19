using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter
{
    private bool IsTableFolderGroupDelete(ModuleMutationRequest request) => request.Address.Kind == ModuleKind.Folder &&
        request.Operation == ModuleOperation.Delete && request.Address.FolderPath?.Split('/')[0] == "Tables" &&
        NativeV11Reflection.Get(folders.Value.NativeModel(request.Address), "Items") is System.Collections.IList children && children.Count != 0;

    private NativeV11Collaboration.Plan BuildTableFolderDeletePlan(ModuleMutationRequest request, NativeV11TableFolderDelete.Plan plan)
    {
        var ownerPlan = NativeV11Collaboration.CreatePlan(session, plan.Owners.Owners,
            request.ExpectedUser ?? throw new BridgeException("CurrentUserMismatch", "协同写入需要当前用户身份。"), mutation: request,
            reviewedFutureGroups: plan.Owners.FutureGroups, reviewedSharedFiles: plan.Journal.OldFiles);
        if (!ownerPlan.Files.Values.SelectMany(paths => paths).Contains(plan.Database, StringComparer.OrdinalIgnoreCase))
            throw new BridgeException("NativeTableDatabaseOwnerMissing", "表目录删除缺少完整共享数据库 owner。 ");
        return ownerPlan;
    }

    private static void RequireSameTableFolderDelete(NativeV11TableFolderDelete.Plan before, NativeV11TableFolderDelete.Plan after)
    {
        string Key(NativeV11CollaborationOwner owner) => NativeV11Reflection.Get(owner.SimpleModule, "ModuleName") + "|" +
            NativeV11Reflection.Get(owner.SimpleModule, "ModuleType") + "|" + owner.State;
        if (before.Database != after.Database || before.Children.Count != after.Children.Count ||
            before.Children.Any(child => !after.Children.Any(value => value.Item.Address == child.Item.Address && ReferenceEquals(value.Item.Model, child.Item.Model) &&
                V11Safety.Equivalent(value.Metadata, child.Metadata))) ||
            !before.Owners.Owners.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(after.Owners.Owners.Select(Key).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            before.OtherTables.Count != after.OtherTables.Count || before.OtherTables.Any(old => !after.OtherTables.Any(current =>
                current.Item.Address == old.Item.Address && ReferenceEquals(current.Item.Model, old.Item.Model) && V11Safety.Equivalent(current.Metadata, old.Metadata))) ||
            before.InternalRelations.Length != after.InternalRelations.Length || before.InternalRelations.Any(relation => !after.InternalRelations.Any(value => ReferenceEquals(value, relation))) ||
            before.OutsideRelations.Length != after.OutsideRelations.Length || before.OutsideRelations.Any(relation => !after.OutsideRelations.Any(value => ReferenceEquals(value, relation))))
            throw new InvalidOperationException("The reviewed table-folder deletion changed during checkout.");
    }
    // Builds an exact native grouped checkout plan without dispatch. The live
    // folder lifecycle remains closed until mutation/save/checkin all consume
    // this same witness and verify the old and new native groups.
    private NativeV11Collaboration.Plan BuildGroupedFolderPlan(ModuleMutationRequest request, NativeV11Folders.GroupedRename grouped)
    {
        var user = request.ExpectedUser;
        if (!folders.Value.IsGroupedNativeRename(request) || user is null)
            throw new BridgeException("NativeFolderPlanUnavailable", "分组目录计划不属于当前支持的协同目录或用户。");
        var owners = grouped.Owners.Owners;
        var plan = NativeV11Collaboration.CreatePlan(session, owners, user, mutation: request,
            reviewedFutureGroups: grouped.Owners.FutureGroups, reviewedSharedFiles: grouped.Journal.OldFiles);
        if (request.Address.FolderPath?.Split('/')[0] == "Tables" && grouped.Inventory.Items.Count != 0 &&
            !plan.Files.Values.SelectMany(paths => paths).Contains(NativeV11CollaborationOwners.TableDatabasePath(), StringComparer.OrdinalIgnoreCase))
            throw new BridgeException("NativeTableDatabaseOwnerMissing", "表目录的完整共享数据库 owner 未包含在计划中。");
        return plan;
    }

    private NativeV11Collaboration.Plan BuildGroupedFolderDeletePlan(ModuleMutationRequest request, NativeV11Folders.GroupedDelete grouped)
    {
        var user = request.ExpectedUser;
        if (!folders.Value.IsGroupedNativeDelete(request) || user is null)
            throw new BridgeException("NativeFolderPlanUnavailable", "分组目录删除计划不属于当前支持的协同目录或用户。");
        return NativeV11Collaboration.CreatePlan(session, grouped.Owners.Owners, user, mutation: request,
            reviewedFutureGroups: grouped.Owners.FutureGroups, reviewedSharedFiles: grouped.Journal.OldFiles);
    }
}
