using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using Application = System.Windows.Application;

namespace ForguncyDesignerMcp.Designer.Native;

internal sealed partial class NativeDesignerBackend
{
    internal StartNativeDevelopmentPreview? BindDevelopmentPreview(IProjectAdapter adapter)
    {
        if (DesignerVersion is not ("11.0.3.0" or "11.0.104.0" or "12.0.102.0"))
            return null;

        var mainType = assembly.GetType("Forguncy.MainWindow", false);
        var ribbonType = assembly.GetType("Forguncy.Views.RibbonView", false);
        var run = ribbonType?.GetMethods(NativeAccess.Flags).SingleOrDefault(m =>
            m.Name == "BrowserHomeExecutedWithProgressbarAsync" && !m.IsStatic &&
            m.ReturnType == typeof(Task<bool>) &&
            m.GetParameters().Select(p => p.ParameterType.FullName)
                .SequenceEqual(new[] { "Forguncy.Common.IBrowser", "WPFBase.Common.TaskInfo", "System.Boolean" }));
        var url = ribbonType?.GetMethod("GetStartUrl", NativeAccess.Flags, null, Type.EmptyTypes, null);
        var isDebugging = ribbonType?.GetMethod("IsDebugging", NativeAccess.Flags, null, Type.EmptyTypes, null);
        var owner = ribbonType?.GetProperty("SaveLoadManager", NativeAccess.Flags);
        var runButton = ribbonType?.GetField("RunButton", NativeAccess.Flags);
        var ribbonProperty = mainType?.GetProperty("RibbonService", NativeAccess.Flags);
        var ribbonField = mainType?.GetField("ribbonView", NativeAccess.Flags);
        if (mainType is null || ribbonType is null || run is null ||
            url is not { IsStatic: false } || url.ReturnType != typeof(string) ||
            isDebugging is not { IsStatic: false } || isDebugging.ReturnType != typeof(bool) ||
            owner?.GetMethod is not { IsStatic: false } || runButton is not { IsStatic: false } ||
            (ribbonProperty?.GetMethod is not { IsStatic: false } && (ribbonField is not { IsStatic: false } || ribbonField.FieldType != ribbonType)))
            return null;

        return (request, dispatched) => new WpfUiScheduler(dispatcher).RunAsync<string?>(async () =>
        {
            PlatformProjectRouter.RequireCleanPreviewSnapshot(await adapter.InspectSnapshotAsync(), request);
            var (document, _) = RequireSession(request.Target.SessionId);
            var main = Application.Current?.MainWindow;
            if (main is not { IsEnabled: true } || main.GetType() != mainType)
                throw new BridgeException("PreviewDesignerUnavailable", "The bound Designer window is not ready.");
            var ribbon = ribbonField?.GetValue(main);
            if (ribbon?.GetType() != ribbonType && ribbonProperty?.GetMethod is { IsStatic: false })
                ribbon = ribbonProperty.GetValue(main);
            if (ribbon is not UIElement { IsEnabled: true } || ribbon.GetType() != ribbonType ||
                !ReferenceEquals(owner.GetValue(ribbon), document) || isDebugging.Invoke(ribbon, null) is not false ||
                runButton.GetValue(ribbon) is not UIElement { IsEnabled: true })
                throw new BridgeException("PreviewDesignerBusy", "The native run command is unavailable or belongs to another document.");

            // Same UI-thread call as the native Run command; no click, HTTP or vendor MCP.
            // Resolve the bound document and check its live dirty flag immediately before dispatch.
            if (!ReferenceEquals(RequireSession(request.Target.SessionId).Document, document) ||
                NativeAccess.Get(document, "ContainsUnsavedChanges") is not false)
                throw new BridgeException("PreviewRequiresCleanDocument", "The document changed or has pending edits.");
            dispatched();
            try
            {
                if (run.Invoke(ribbon, new object?[] { null, null, false }) is not Task<bool> operation)
                    throw new BridgeException("NativePreviewOutcomeUnconfirmed", "The Designer did not return a native preview task.");
                if (!await operation) return null;
                if (!ReferenceEquals(RequireSession(request.Target.SessionId).Document, document) ||
                    !ReferenceEquals(owner.GetValue(ribbon), document))
                    throw new BridgeException("PreviewDocumentChanged", "The project changed as preview started; the URL cannot be associated safely.");
                return url.Invoke(ribbon, null) as string ?? "";
            }
            catch (Exception error) when (error is not BridgeException)
            {
                throw new BridgeException("NativePreviewOutcomeUnconfirmed", "The native preview invocation failed after dispatch; do not replay it.");
            }
        });
    }
}
