using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter
{
    // Build the complete read-only collaboration plan before checkout. Hidden
    // template pages are ancillary effects, not synthetic native owners.
    private NativeV11Collaboration.Plan BuildFirstProcessPlan(ModuleMutationRequest request,
        NativeV11ProcessInitialization.Candidate candidate)
    {
        if (request.ExpectedUser is null) throw new InvalidOperationException("The native collaboration user is unavailable.");
        NativeV11ProcessInitialization.RequireSameCandidate(session, request, candidate);
        var created = NativeV11ProcessInitialization.ProjectNewOwners(candidate.Owners, candidate.NewDirectGroups, request.Address);
        var editOwners = candidate.Owners.Where(owner => owner.State == "Edit").ToArray();
        ModuleAddress EditAddress(NativeV11ProcessInitialization.Owner owner)
        {
            if (owner.Type == "ApplicationSetting") return new(ModuleKind.ApplicationSettings, "ApplicationSettings");
            if (owner.Type == "Theme") return new(ModuleKind.Theme, "Theme");
            var kind = owner.Type switch { "Table" or "TableSqlite" => ModuleKind.Table,
                "TableView" or "TableProcessView" => ModuleKind.View,
                _ => throw new InvalidOperationException("An unreviewed existing first-process owner requires its native shared-file projection.") };
            var factory = session.Context.DesignerAssembly.GetType("Forguncy.Common.SimpleModule", true)!;
            var matches = session.Items(kind).Where(item =>
            {
                var simple = NativeV11Reflection.Static(factory, "Create", item.Model)!;
                return NativeV11Reflection.Get(simple, "ModuleName") as string == owner.Name &&
                    NativeV11Reflection.Get(simple, "ModuleType")?.ToString() == owner.Type;
            }).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("A native existing process table owner is missing or ambiguous.");
            return matches[0].Address;
        }
        var editAddresses = editOwners.Select(EditAddress).ToArray();
        var existing = NativeV11CollaborationOwners.Resolve(session, editAddresses);
        foreach (var expected in editOwners)
        {
            var matches = existing.Where(owner => NativeV11Reflection.Get(owner.SimpleModule, "ModuleName") as string == expected.Name &&
                NativeV11Reflection.Get(owner.SimpleModule, "ModuleType")?.ToString() == expected.Type &&
                NativeV11Reflection.Get(owner.SimpleModule, "IconType")?.ToString() == expected.Icon &&
                NativeV11Reflection.Get(owner.SimpleModule, "ExclusiveType")?.ToString() == expected.Exclusive).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("A native existing first-process owner differs from its collector identity.");
        }
        var proposed = candidate.NewDirectGroups.Values.SelectMany(paths => paths).Concat(candidate.TemplatePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var expanded = NativeV11CollaborationOwners.ExpandExistingOwners(session, created.Concat(existing).ToArray(), proposed, allowSharedTableDatabase: true);
        return NativeV11Collaboration.CreatePlan(session, expanded, request.ExpectedUser, mutation: request,
            reviewedFutureGroups: candidate.NewDirectGroups, reviewedAncillaryFiles: candidate.TemplatePaths,
            reviewedSharedFiles: [NativeV11CollaborationOwners.TableDatabasePath()]);
    }

    // The caller must hold the write/UI gate and must supply a complete,
    // separately verified native owner + source plan. A native initialization
    // may alter shared DB, views, pages, application settings and themes.
    private async Task<Action> ApplyFirstProcessInitializationAsync(ModuleMutationRequest request,
        NativeV11ProcessInitialization.Candidate candidate, Action verifyCompleteOwnerAndSourcePlan)
    {
        NativeV11ProcessInitialization.RequireSameCandidate(session, request, candidate);
        verifyCompleteOwnerAndSourcePlan();
        var dispatched = false;
        try
        {
            void Revalidate()
            {
                NativeV11ProcessInitialization.RequireSameCandidate(session, request, candidate);
                verifyCompleteOwnerAndSourcePlan();
            }
            // Native copy/initialization and definition creation are one
            // potentially irreversible sequence. Await the original task.
            dispatched = true;
            var undo = await NativeV11ProcessInitialization.InitializeAsync(session, Revalidate,
                () => NativeV11Processes.ApplyFirstCandidate(session, request));
            _ = NativeV11FirstProcessTemplates.VerifyActual(session, candidate);
            return undo;
        }
        catch
        {
            if (dispatched) requiresReview = true;
            throw;
        }
    }
}
