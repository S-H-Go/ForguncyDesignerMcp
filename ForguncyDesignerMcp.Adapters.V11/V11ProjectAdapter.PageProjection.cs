using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter : IProjectPageProjectionAdapter,IProjectPageBaselineGuardAdapter
{
    public Task RequireFreshPageBaselineAsync(ProjectTarget target,ModuleAddress address,string revision,CancellationToken token=default) => ReadOnUi(target,false,()=>
    {
        CheckTarget(target);session.VerifyContext();
        if(address.Kind is not (ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage) || string.IsNullOrWhiteSpace(revision) ||
           !baselines.TryGetValue(revision,out var issued) || DateTimeOffset.UtcNow-issued>TimeSpan.FromMinutes(30))
            throw new BridgeException("ModuleBaselineExpired","Page baseline was not read or is older than 30 minutes.");
        return true;
    },token);
    public Task<ModuleDocument> ProjectPageDocumentAsync(ProjectTarget target,ModuleDocument document,CancellationToken token=default) => ReadOnUi(target,false,()=>
    {
        CheckTarget(target);session.VerifyContext();
        if(string.IsNullOrWhiteSpace(document.Revision) || !baselines.TryGetValue(document.Revision,out var issued) || DateTimeOffset.UtcNow-issued>TimeSpan.FromMinutes(30))
            throw new BridgeException("ModuleBaselineExpired","Page baseline was not read or is older than 30 minutes.");
        var before=Snapshot();var current=ReadDocument(document.Address, issueBaseline: false);
        if(current.Revision!=document.Revision)throw new BridgeException("ModuleBaselineChanged","Page changed before detached projection.");
        var projected=NativeV11PageWriter.ProjectDetached(session,document);
        var after=Snapshot();session.VerifyContext();CheckTarget(target);
        if(before.Revision!=after.Revision || before.Target!=after.Target || after.Busy)throw new BridgeException("BaselineChanged","Project changed during detached projection.");
        return projected;
    },token);
}
