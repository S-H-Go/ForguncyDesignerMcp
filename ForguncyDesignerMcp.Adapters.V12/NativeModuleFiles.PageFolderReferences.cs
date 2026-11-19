using System.Collections;
using ForguncyDesignerMcp.Core;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class NativeModuleFiles
{
    internal IReadOnlyList<object> PageDependentTemplates(IReadOnlyList<object> pages)
    {
        var templates=R.Get(R.Get(settings,"Pages")!,"TemplatePages")!;
        var helper=assembly.GetType("Forguncy.RepeaterCellTypeDesigner",true)!;
        return pages.SelectMany(page=>((IEnumerable)R.Static(helper,"GetSubTemplatePages",(string)R.Get(page,"Name")!,templates)!).Cast<object>())
            .Distinct(ReferenceEqualityComparer.Instance).ToArray();
    }
    // The pinned native PageBase branch of GetReferences contains the page
    // itself, page-name references and cross-sheet formulas. It does not add
    // the exclusive-owner set used for several other native module types.
    internal void RequirePageFolderReferencesContained(IReadOnlyList<object> pages)
    {
        RequirePureReferenceContext();
        var state=assembly.GetType("s",true)!.GetProperty("Instance",R.Flags)!.GetValue(null)!;
        if(!ReferenceEquals(R.Get(state,"et"),settings) || workbook is null || !ReferenceEquals(R.Get(state,"ev"),workbook) ||
            boundDocument is not null && !ReferenceEquals(R.Get(state,"ew"),boundDocument))
            throw new BridgeException("NativeWorkbookMismatch","The native reference finder is bound to another workbook.");
        var simpleType=assembly.GetType("Forguncy.Common.SimpleModule",true)!;
        var finderType=assembly.GetType("Forguncy.Common.ReferenceFinder",true)!;
        var owners=pages.Select(page=>R.Static(simpleType,"Create",page) ?? throw new BridgeException("NativePageReferenceUnavailable","The native page owner is unavailable.")).ToArray();
        if(owners.Distinct().Count()!=owners.Length) throw new BridgeException("NativePageReferenceUnavailable","The page deletion owner set is ambiguous.");
        foreach(var page in pages)
        {
            var finder=Activator.CreateInstance(finderType,[page]) ?? throw new BridgeException("NativePageReferenceUnavailable","The native page reference finder is unavailable.");
            var references=R.Call(finder,"GetReferences") as IEnumerable ?? throw new BridgeException("NativePageReferenceUnavailable","Native page references are unavailable.");
            foreach(var reference in references.Cast<object>())
                if(!owners.Any(owner=>owner.Equals(reference)))
                    throw new BridgeException("NativePageExternalReference","A page outside the folder still references a page selected for deletion.");
        }
    }

    internal void RequirePageFolderManagerContext(ForguncyDesignerMcp.Core.Platform.ModuleKind kind=ForguncyDesignerMcp.Core.Platform.ModuleKind.Page)
    {
        var state=assembly.GetType("s",true)!.GetProperty("Instance",R.Flags)!.GetValue(null)!;
        if(!ReferenceEquals(R.Get(state,"et"),settings) || !ReferenceEquals(R.Get(state,"fm"),colleagues) || boundDocument is not null && !ReferenceEquals(R.Get(state,"ew"),boundDocument) ||
            workbook is null || !ReferenceEquals(R.Get(state,"ev"),workbook))
            throw new BridgeException("NativePageManagerContextMismatch","The native page manager is bound to another document or workbook.");
        var registered=colleagues.GetType().GetField("avy",R.Flags)?.GetValue(colleagues) as IList;
        var pageColleague=Colleague(kind);
        if(registered is null || pageColleague is null || !registered.Cast<object>().Any(item=>ReferenceEquals(item,pageColleague)))
            throw new BridgeException("NativePageManagerContextMismatch","The bound native page colleague is absent from the page manager's save registry.");
    }
}
