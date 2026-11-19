using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// This is the native same-name/same-extension raster branch after its file
// dialog. The full live refresh path still requires acceptance in the actual
// Designer. No host/global object is substituted by a component fixture.
internal sealed class NativeV11ImageReplacement(NativeV11Session session, Func<ModuleAddress, ModuleDocument> readBoundDocument)
{
    private const string ReviewCode = "NativeImageReplacementRequiresReview";
    private static readonly ConditionalWeakTable<Assembly, Contract> Contracts = new();

    internal sealed record Contract(Type ImageType, Type ManagerType, Type SpreadType, Type InputType,
        Type DrawingType, Type ViewType, Type FileType, Type FileContextType, Type UtilitiesType,
        Type ReferenceType, Type PreviewType, Type UserControlDesignerType, Type PageImageCacheType,
        ConstructorInfo Executor, MethodInfo CanExecute, MethodInfo MakeWritable, MethodInfo ReplaceReference,
        MethodInfo GetImageFile, MethodInfo ClearDrawing, MethodInfo SpreadInvalidate, MethodInfo InputInvalidate,
        PropertyInfo SpreadProperty, PropertyInfo InputProperty, FieldInfo ViewField, FieldInfo PreviewLazy,
        FieldInfo PreviewPage, FieldInfo UserControlCache, FieldInfo PageImageCache, FieldInfo DrawingCache,
        string SpreadsheetUiSha256, CacheInitializationContract Initialization, ExistingRefreshContract ExistingRefresh);

    internal sealed record CacheInitializationContract(Type DocumentType, Type SpreadWrapperType,
        FieldInfo Singleton, FieldInfo DocumentOpened, FieldInfo ActiveSheetChanged, FieldInfo ThemesInstance,
        FieldInfo ThemeChanged, MethodInfo[] UserControlHandlers, MethodInfo[] PageImageHandlers);

    internal sealed record CacheInitializationWitness(object Themes, object UserControlTarget, object PageImageTarget);
    internal sealed record ExistingRefreshContract(FieldInfo SpreadOverride, FieldInfo ItemsOwner, FieldInfo ContextLayer,
        FieldInfo LayerItems, FieldInfo LayerDefaults, FieldInfo ParentLayer, Type ContextItem, FieldInfo ItemSpread,
        FieldInfo ServicesOwner, FieldInfo Services);

    // Metadata only: does not read a singleton, initialize a preview service,
    // execute CanExecute, or touch an image/working file.
    internal static Contract VerifySignatures(Assembly designer) => Contracts.GetValue(designer, assembly =>
    {
        var profile = V11BuildProfile.Resolve(assembly);
        var newer = profile.Version == "11.0.104.0";
        var context = AssemblyLoadContext.GetLoadContext(assembly)!;
        var ui = context.Assemblies.SingleOrDefault(value => value.GetName().Name == "GrapeCity.WPF.SpreadSheet.UI") ??
            context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "GrapeCity.WPF.SpreadSheet.UI.dll"));
        var expectedUi = newer ? "AC67875E0D17BF3518E5594DB53B19F2B5E6D2D70663AA53586E194285AAF506" :
            "5921CEDA609A7E6EBAD52E4970C116392C29A63A0FE65BAE5D4C845BCBDDE229";
        if (ui.GetName().Version?.ToString() != "7.40.20142.1" || string.IsNullOrEmpty(ui.Location) ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ui.Location))) != expectedUi)
            throw new NotSupportedException("The native image replacement UI component is not the pinned V11 build.");
        var common = context.Assemblies.Single(value => value.GetName().Name == "CommonUtilities");
        Type D(string name) => assembly.GetType(name, true)!;
        var image = D("Forguncy.HelpForm.ImageItem");
        var manager = D("Forguncy.Common.ImageManager");
        var spread = D("Forguncy.Model.ISpread");
        var input = D("Forguncy.CellTypes.Views.IInputFieldControlService");
        var gcSpread = ui.GetType("GrapeCity.Windows.SpreadSheet.UI.GcSpreadSheet", true)!;
        var view = ui.GetType("GrapeCity.Windows.SpreadSheet.UI.SpreadView", true)!;
        var drawing = ui.GetType("GrapeCity.Windows.SpreadSheet.UI.DrawingObjectManager", true)!;
        var reference = D("Forguncy.Views.ObjectViews.ImageReferenceHelper");
        var utilities = D("Forguncy.Common.Utilities");
        var preview = D("Forguncy.Common.CellTypePreview.CellTypePreviewService");
        var userControl = D("Forguncy.UserControlPageCellTypeDesigner");
        var pageImages = D("bh");
        var file = common.GetType("Forguncy.Common.IFile", true)!;
        var fileContext = common.GetType("Forguncy.Common.FileContext", true)!;
        var fileUtilities = common.GetType("Forguncy.Common.FileUtilities", true)!;
        var executorType = D("Forguncy.Common.ImageReplaceExecutor");
        var executor = executorType.GetConstructors(NativeV11Reflection.Flags).Single(value =>
            value.GetParameters() is var p && p.Length == 2 && p[0].ParameterType.FullName ==
            "GrapeCity.Windows.SpreadSheet.UI.UndoRedo.ActionBase" && p[1].ParameterType == image);
        var singleton = D("o");
        PropertyInfo Property(Type type, string name, Type result)
        {
            for (var current = type; current is not null; current = current.BaseType)
                if (current.GetProperty(name, NativeV11Reflection.Flags | BindingFlags.DeclaredOnly) is { } property && property.PropertyType == result)
                    return property;
            throw new MissingMemberException(type.FullName, name);
        }
        FieldInfo Field(Type type, string name) => type.GetField(name, NativeV11Reflection.Flags) ?? throw new MissingFieldException(type.FullName, name);
        MethodInfo Method(Type type, string name, Type result, bool isStatic, params Type[] parameters)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                var method = current.GetMethod(name, NativeV11Reflection.Flags | BindingFlags.DeclaredOnly, null, parameters, null);
                if (method is not null && method.ReturnType == result && method.IsStatic == isStatic) return method;
            }
            throw new MissingMethodException(type.FullName, name);
        }
        _ = Method(file, "Copy", typeof(void), false, typeof(string), typeof(string), typeof(bool));
        _ = Method(file, "Delete", typeof(void), false, typeof(string));
        _ = Method(file, "SetAttributes", typeof(void), false, typeof(string), typeof(FileAttributes));
        _ = Method(file, "ReadAllBytes", typeof(byte[]), false, typeof(string));
        _ = Method(view, "HasSelectedFloatingObject", typeof(bool), false);
        foreach (var name in IdleProperties) _ = Property(view, name, typeof(bool));
        foreach (var name in new[] { "_movingResizingFloatingObject", "_movingResizingFloatingObjects", "_isMouseDownFloatingObject", "_shapeDrawingContainer" })
            _ = Field(view.BaseType!, name);
        _ = Property(spread, "Workbook", assembly.GetType(profile.DocumentType, true)!.GetProperty("Workbook", NativeV11Reflection.Flags)!.PropertyType);
        _ = Property(spread, "Dispatcher", typeof(Dispatcher));
        _ = Property(spread, "IsCellEditing", typeof(bool));
        var documentType = D(profile.DocumentType);
        var spreadWrapper = D(newer ? "lw" : "lq");
        var themes = D("Forguncy.ThemesManager");
        var opened = Field(documentType, newer ? "ws" : "we");
        var activeChanged = Field(spreadWrapper, newer ? "bnu" : "bls");
        var themeChanged = Field(themes, "ThemeChanged");
        foreach (var field in new[] { opened, activeChanged, themeChanged })
            if (field.IsStatic || !typeof(Delegate).IsAssignableFrom(field.FieldType))
                throw new MissingMemberException("A native cache initialization event has no verified instance delegate field.");
        MethodInfo[] Handlers(Type cache, string[] names)
        {
            var callbacks = cache.GetNestedType("<>c", BindingFlags.NonPublic) ?? throw new MissingMemberException(cache.FullName, "<>c");
            return names.Select((name, index) =>
            {
                var signature = new[] { opened, themeChanged, activeChanged }[index].FieldType.GetMethod("Invoke")!.GetParameters().Select(value => value.ParameterType).ToArray();
                return Method(callbacks, name, typeof(void), false, signature);
            }).ToArray();
        }
        // Metadata is safe to resolve on cold cache classes. Do not read any
        // static cache/delegate value to learn whether its .cctor already ran.
        var initialization = new CacheInitializationContract(documentType, spreadWrapper, Field(singleton, "bv"),
            opened, activeChanged, Field(themes, "_instance"), themeChanged,
            Handlers(userControl, ["<Init>b__7_0", "<Init>b__7_1", "<Init>b__7_2"]),
            Handlers(pageImages, newer ? ["ok", "ol", "om"] : ["og", "oh", "oi"]));
        var itemsOwner = D("o+a"); var layer = D("o+a+a"); var contextItem = D("j"); var services = D("o+b");
        var existingRefresh = new ExistingRefreshContract(Field(singleton, "bi"), Field(singleton, "bw"),
            Field(itemsOwner, newer ? "dx" : "dt"), Field(layer, newer ? "ec" : "dy"),
            Field(layer, newer ? "eb" : "dx"), Field(layer, newer ? "ed" : "dz"), contextItem, Field(contextItem, "bj"),
            Field(singleton, "bx"), Field(services, "bx"));
        if (singleton.TypeInitializer is not null || manager.TypeInitializer is not null || fileContext.TypeInitializer is not null)
            throw new NotSupportedException("A native existing-owner read would initialize an unverified static owner.");
        return new(image, manager, spread, input, drawing, view, file, fileContext, utilities, reference, preview,
            userControl, pageImages, executor, Method(executorType, "CanExecute", typeof(bool), false),
            Method(fileUtilities, "MakeFileNotReadOnly", typeof(void), true, typeof(string)),
            Method(reference, "ReplaceReference", typeof(void), true, image),
            Method(utilities, "GetImageFile", typeof(string), true, typeof(string), typeof(bool)),
            Method(drawing, "ClearCachedDrawingObject", typeof(void), true),
            Method(spread, "Invalidate", typeof(void), false), Method(input, "Invalidate", typeof(void), false, typeof(bool)),
            Property(singleton, newer ? "dg" : "dc", spread), Property(singleton, newer ? "dk" : "dg", input),
            Field(gcSpread, "_view"), Field(preview, "InstanceLazy"), Field(preview, "_page"),
            Field(userControl, "<ImageCache>k__BackingField"), Field(pageImages, newer ? "oh" : "od"),
            Field(drawing, "_cacheDrawingObjects"), expectedUi, initialization, existingRefresh);
    });

    private static readonly string[] IdleProperties =
    [
        "IsEditing", "IsCellEditing", "IsMovingFloatingOjects", "IsResizingFloatingObjects",
        "IsTouchingMovingFloatingObjects", "IsTouchingResizingFloatingObjects", "IsMouseLeftButtonPressed",
        "IsMouseRightButtonPressed", "IsMouseCaptureWithin", "AreAnyTouchesCapturedWithin"
    ];

    internal static byte[] ValidatePayload(ModuleDocument before, ModuleMutationRequest request)
    {
        V11Safety.Address(request.Address);
        if (request.Operation != ModuleOperation.Update || request.Address.Kind != ModuleKind.Image || request.Scope != EffectScope.ProjectDefinition ||
            request.Document is not { } replacement || replacement.Address != request.Address || before.Address != request.Address ||
            request.ExpectedModuleRevision != before.Revision || replacement.Companions.Count != 1 || before.Companions.Count != 1)
            throw new InvalidDataException("Image replacement requires the exact current image revision and one fixed-path binary companion.");
        if (!V11Safety.Equivalent(before.Metadata, replacement.Metadata))
            throw new InvalidDataException("Image replacement cannot change native image metadata, identity, flags or path.");
        if (before.Metadata.GetProperty("BuiltIn").GetBoolean() || before.Metadata.GetProperty("IsImportImage").GetBoolean())
            throw new NotSupportedException("Built-in images and native import aliases cannot be replaced.");
        V11Safety.Companion(replacement.Companions[0], before.Companions[0].RelativePath);
        var bytes = Convert.FromBase64String(replacement.Companions[0].ContentBase64);
        NativeV11Resources.ValidateRaster(request.Address.Name, bytes);
        return bytes;
    }

    internal void Validate(ModuleMutationRequest request) => _ = Prepare(request);

    internal Action Apply(ModuleMutationRequest request)
    {
        var plan = Prepare(request); // Every dependency and native guard precedes CanExecute (which itself marks generation dirty).
        var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var began = false;
        var referencesAttempted = false;
        var restored = false;
        var tempInvalidated = false;
        var ownedFileRemoval = false;
        string? installedSource = null;
        CacheSlot? installedTemp = null;
        CacheSnapshot[]? installedCaches = null;
        void Undo()
        {
            if (restored) return;
            try
            {
                Verify(plan, false);
                var current = File.Exists(plan.File) ? Read(plan.File) : null;
                if (current is null && !ownedFileRemoval || current is not null && !current.SequenceEqual(plan.BeforeBytes) && !current.SequenceEqual(plan.AfterBytes))
                    throw Review("The image changed concurrently; rollback will not overwrite different bytes.");
                var expectedTemp = installedTemp ?? (tempInvalidated ? new(false, null) : plan.TempSlot);
                RequireSlot(plan.TempCache, plan.File, expectedTemp);
                RequireSlot(plan.NameCache, request.Address.Name, plan.NameSlot);
                if (installedCaches is not null) foreach (var cache in installedCaches) cache.RequireUnchanged();
                if (current is null || current.SequenceEqual(plan.AfterBytes))
                    CopyNative(plan, plan.BeforeBytes, plan.AfterBytes, current is null);
                if (referencesAttempted) Refresh(plan); // Uses the original file again, never a model/preview substitute.
                if (plan.TempSlot.Exists && (!File.Exists(plan.TempSlot.Value) || !Read(plan.TempSlot.Value!).SequenceEqual(plan.BeforeBytes)))
                    throw Review("The original native preview file changed; its cache mapping cannot be restored safely.");
                SetSlotChecked(plan.TempCache, plan.File, Slot(plan.TempCache, plan.File), plan.TempSlot);
                NativeV11Reflection.Set(plan.Model, "ImageSource", plan.ImageSource);
                if (referencesAttempted && installedCaches is null)
                    throw Review("The native refresh stopped without a complete cache checkpoint; original bytes were restored, but derived-cache recovery requires review.");
                foreach (var cache in plan.BeforeCaches) cache.RestoreChecked(CacheSnapshot.Capture(cache.Store));
                NativeV11Reflection.Call(plan.NativeFile, "SetAttributes", plan.File, plan.Attributes);
                if (!Read(plan.File).SequenceEqual(plan.BeforeBytes) || NativeV11Reflection.Get(plan.Model, "ImageSource") as string != plan.ImageSource)
                    throw Review("The original native image bytes or image source were not restored.");
                NativeV11Generation.Mark(session, "Image");
                NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty);
                restored = true;
            }
            catch (Exception error) { throw Review("Native image rollback could not verify all bytes, owner/cache identities and refresh hooks.", error); }
        }
        try
        {
            Verify(plan, true);
            began = true;
            var executor = plan.Contract.Executor.Invoke([null, plan.Model]);
            if (plan.Contract.CanExecute.Invoke(executor, null) is not true)
                throw new InvalidOperationException("The native image replacement executor refused the operation.");
            Verify(plan, false);
            plan.Contract.MakeWritable.Invoke(null, [plan.File]);
            SetSlotChecked(plan.TempCache, plan.File, plan.TempSlot, new(false, null));
            tempInvalidated = true;
            CopyNative(plan, plan.AfterBytes, plan.BeforeBytes, false, () => ownedFileRemoval = true);
            SetSlotChecked(plan.NameCache, request.Address.Name, plan.NameSlot, plan.NameSlot);
            referencesAttempted = true;
            Refresh(plan, value => installedTemp = value);
            Verify(plan, false);
            installedSource = NativeV11Reflection.Get(plan.Model, "ImageSource") as string;
            installedTemp = Slot(plan.TempCache, plan.File);
            installedCaches = plan.BeforeCaches.Select(value => CacheSnapshot.Capture(value.Store)).ToArray();
            if (!installedTemp.Exists || installedSource != installedTemp.Value || !Read(installedSource!).SequenceEqual(plan.AfterBytes) ||
                !Read(plan.File).SequenceEqual(plan.AfterBytes))
                throw Review("The native replacement source and preview cache do not contain the requested image bytes.");
            if (!V11Safety.Equivalent(NativeV11Resources.CaptureImageCore(request.Address, plan.Model, session.LocalPath).Metadata, plan.Before.Metadata))
                throw Review("The native image metadata changed during same-name replacement.");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            return () =>
            {
                if (NativeV11Reflection.Get(plan.Model, "ImageSource") as string != installedSource)
                    throw Review("The native image preview changed after replacement; rollback requires review.");
                Undo();
            };
        }
        catch (Exception primary)
        {
            if (!began) throw;
            try { Undo(); }
            catch (Exception rollback) { throw new AggregateException("Native image replacement failed and rollback requires review.", primary, rollback); }
            throw;
        }
    }

    private sealed record CacheSlot(bool Exists, string? Value);
    private sealed record Plan(Contract Contract, ModuleMutationRequest Request, ModuleDocument Before, object Model, object Manager,
        object Spread, object Input, object View, object ActiveSheet, object Preview, object NativeFile, string File,
        ConcurrentDictionary<string, string> NameCache, ConcurrentDictionary<string, string> TempCache,
        CacheSlot NameSlot, CacheSlot TempSlot, string? ImageSource, FileAttributes Attributes, byte[] BeforeBytes, byte[] AfterBytes,
        string EditRevision, bool CollaborationMode, object UserControlCache, object PageImageCache, object DrawingCache, CacheSnapshot[] BeforeCaches,
        CacheInitializationWitness Initialization);

    private Plan Prepare(ModuleMutationRequest request)
    {
        session.VerifyContext(); NativeV11StaticFiles.VerifyConfigOwner(session);
        RequireImageSaveRegistration(NativeV11StaticFiles.Resolve(session).Folders);
        if (request.Operation != ModuleOperation.Update || request.Address.Kind != ModuleKind.Image || request.Scope != EffectScope.ProjectDefinition)
            throw new NotSupportedException("This native branch accepts only image project-definition updates.");
        var contract = VerifySignatures(session.Context.DesignerAssembly);
        var snapshot = session.Context.CaptureSnapshot();
        V11Safety.SameTarget(request.Target, snapshot.Target);
        var singleton = ExistingSingleton(contract);
        var owners = RequireExistingRefreshOwners(contract, singleton);
        var spread = owners.Spread;
        // Both .cctor chains end by subscribing ActiveSheetChanged. Observe
        // already existing instance delegates before any static cache read.
        var initialization = RequireInitializationWitness(contract, session.Document, spread);
        var manager = contract.ManagerType.GetField("_instance", NativeV11Reflection.Flags)!.GetValue(null) ??
            throw Review("The actual native image manager has not been initialized; validation will not create it.");
        var resources = new NativeV11Resources(session);
        var native = resources.Read(request.Address);
        var before = readBoundDocument(request.Address);
        RequireBoundBaseline(native, before);
        var bytes = ValidatePayload(before, request);
        var references = resources.FindReferences(request.Address);
        RequireNoReferences(references);
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var root = NativeV11Reflection.Get(settings, "ImageFolder")!;
        object node = root;
        foreach (var segment in string.IsNullOrEmpty(request.Address.FolderPath) ? [] : request.Address.FolderPath.Split('/'))
            node = Children(node).Cast<object>().Single(value => NativeV11Reflection.Get(value, "Name") as string == segment &&
                NativeV11Reflection.Get(value, "Items") is IList);
        var model = Children(node).Cast<object>().Single(value => value.GetType() == contract.ImageType &&
            NativeV11Reflection.Get(value, "Name") as string == request.Address.Name);
        var input = owners.Input;
        if (!contract.SpreadType.IsInstanceOfType(spread) || !contract.InputType.IsInstanceOfType(input) ||
            !ReferenceEquals(NativeV11Reflection.Get(spread, "Workbook"), session.Workbook))
            throw Review("Native refresh services are not bound to this document.");
        var gcSpread = NativeV11Reflection.Get(spread, "View") ?? throw Review("The native spread view is unavailable.");
        if (gcSpread.GetType() != contract.ViewField.DeclaringType)
            throw Review("The actual spread view type is not the pinned native GcSpreadSheet.");
        var view = contract.ViewField.GetValue(gcSpread) ?? throw Review("The native drawing view is unavailable.");
        var active = NativeV11Reflection.Get(spread, "ActiveSheet") ?? throw Review("An actual native active worksheet is required.");
        // Reading Lazy.IsValueCreated never creates the preview service/timer/browser.
        var lazy = contract.PreviewLazy.GetValue(null) ?? throw Review("The native preview lazy owner is unavailable.");
        if (NativeV11Reflection.Get(lazy, "IsValueCreated") is not true)
            throw Review("The native preview service is not initialized; replacement will not start it.");
        var preview = NativeV11Reflection.Get(lazy, "Value")!;
        RequirePreviewIdle(contract, preview);
        var file = V11Safety.Under(session.LocalPath, before.Companions.Single().RelativePath);
        var names = NativeV11Reflection.Get(manager, "ImageNameToFullNameCache") as ConcurrentDictionary<string, string> ?? throw Review("The native image-name cache is unavailable.");
        var imports = NativeV11Reflection.Get(manager, "ImportImageNameToFullNameCache") as IDictionary ?? throw Review("The native imported-image registry is unavailable.");
        if (imports.Keys.Cast<string>().Any(value => value.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
            imports.Values.Cast<string>().Any(value => SamePath(value, file))) throw Review("A native import alias owns this image.");
        var share = AssemblyLoadContext.GetLoadContext(session.Context.DesignerAssembly)!.Assemblies
            .Single(value => value.GetName().Name == "ServerDesignerCommon").GetType("Forguncy.Common.ShareUtilities", true)!;
        var temp = NativeV11Reflection.Static(share, "get_TempImagePathCache") as ConcurrentDictionary<string, string> ?? throw Review("The native temporary-image cache is unavailable.");
        var nameSlot = Slot(names, request.Address.Name);
        if (!nameSlot.Exists || !SamePath(nameSlot.Value, file) || names.Any(value => value.Key != request.Address.Name && SamePath(value.Value, file)))
            throw Review("The image does not have a unique native name-cache registration.");
        var tempSlot = Slot(temp, file);
        var old = Read(file);
        var source = NativeV11Reflection.Get(model, "ImageSource") as string;
        if (tempSlot.Exists && (!File.Exists(tempSlot.Value) || !Read(tempSlot.Value!).SequenceEqual(old)) ||
            !string.IsNullOrEmpty(source) && (!tempSlot.Exists || source != tempSlot.Value))
            throw Review("The original native temporary image mapping/source cannot be restored exactly.");
        var nativeFile = contract.FileContextType.GetField("_file", NativeV11Reflection.Flags)!.GetValue(null) ??
            throw Review("The native file owner is not initialized; validation will not create it.");
        if (nativeFile.GetType().FullName != "Forguncy.Common.FileContext+FileImp" || nativeFile.GetType().Assembly != contract.FileType.Assembly)
            throw Review("A substituted native file provider is not accepted for image replacement.");
        var userControlCache = contract.UserControlCache.GetValue(null)!;
        var pageImageCache = contract.PageImageCache.GetValue(null)!;
        var drawingCache = contract.DrawingCache.GetValue(null)!;
        var plan = new Plan(contract, request, before, model, manager, spread, input, view, active, preview, nativeFile, file,
            names, temp, nameSlot, tempSlot, source, File.GetAttributes(file), old, bytes, snapshot.Revision, snapshot.InCollaborationMode,
            userControlCache, pageImageCache, drawingCache,
            new[] { userControlCache, pageImageCache, drawingCache }.Select(CacheSnapshot.Capture).ToArray(), initialization);
        Verify(plan, true);
        return plan;
    }

    private void Verify(Plan plan, bool beforeDispatch)
    {
        session.VerifyContext(); NativeV11StaticFiles.VerifyConfigOwner(session);
        RequireImageSaveRegistration(NativeV11StaticFiles.Resolve(session).Folders);
        var snapshot = session.Context.CaptureSnapshot();
        V11Safety.SameTarget(plan.Request.Target, snapshot.Target);
        if (snapshot.InCollaborationMode != plan.CollaborationMode || beforeDispatch && snapshot.Revision != plan.EditRevision)
            throw Review("The native image edit epoch or collaboration context changed.");
        var singleton = ExistingSingleton(plan.Contract);
        var owners = RequireExistingRefreshOwners(plan.Contract, singleton);
        var initialization = RequireInitializationWitness(plan.Contract, session.Document, plan.Spread);
        if (!ReferenceEquals(initialization.Themes, plan.Initialization.Themes) ||
            !ReferenceEquals(initialization.UserControlTarget, plan.Initialization.UserControlTarget) ||
            !ReferenceEquals(initialization.PageImageTarget, plan.Initialization.PageImageTarget))
            throw Review("A native cache initialization witness changed.");
        var currentGcSpread = NativeV11Reflection.Get(plan.Spread, "View");
        if (!ReferenceEquals(owners.Spread, plan.Spread) || !ReferenceEquals(owners.Input, plan.Input) ||
            currentGcSpread?.GetType() != plan.Contract.ViewField.DeclaringType ||
            !ReferenceEquals(plan.Contract.ViewField.GetValue(currentGcSpread), plan.View) ||
            !ReferenceEquals(plan.Contract.ManagerType.GetField("_instance", NativeV11Reflection.Flags)!.GetValue(null), plan.Manager) ||
            !ReferenceEquals(NativeV11Reflection.Get(plan.Manager, "ImageNameToFullNameCache"), plan.NameCache) ||
            !ReferenceEquals(plan.Contract.FileContextType.GetField("_file", NativeV11Reflection.Flags)!.GetValue(null), plan.NativeFile) ||
            !ReferenceEquals(FindModel(plan.Request.Address), plan.Model))
            throw Review("A native image tree, manager, file owner or live refresh-service identity changed.");
        if (NativeV11Reflection.Get(plan.Spread, "Dispatcher") is not Dispatcher dispatcher || !dispatcher.CheckAccess() ||
            !ReferenceEquals(NativeV11Reflection.Get(plan.Spread, "Workbook"), session.Workbook) ||
            !ReferenceEquals(NativeV11Reflection.Get(plan.Spread, "ActiveSheet"), plan.ActiveSheet) ||
            NativeV11Reflection.Get(plan.Spread, "IsCellEditing") is not false ||
            NativeV11Reflection.Get(NativeV11Reflection.Get(plan.ActiveSheet, "Tag")!, "PageInfo")?.GetType().FullName != "Forguncy.Model.Pages.NormalPage")
            throw Review("Replacement requires the current native UI thread and an idle ordinary page, with no user-control context.");
        RequireIdleView(plan.Contract, plan.View);
        RequirePreviewIdle(plan.Contract, plan.Preview);
        if (!ReferenceEquals(plan.Contract.UserControlCache.GetValue(null), plan.UserControlCache) ||
            !ReferenceEquals(plan.Contract.PageImageCache.GetValue(null), plan.PageImageCache) ||
            !ReferenceEquals(plan.Contract.DrawingCache.GetValue(null), plan.DrawingCache))
            throw Review("A native preview/drawing cache owner changed.");
        if (!SamePath(NativeV11Reflection.Get(plan.Model, "FullName") as string, plan.File) ||
            NativeV11Reflection.Get(plan.Model, "Name") as string != plan.Request.Address.Name ||
            NativeV11Reflection.Get(plan.Model, "BuiltIn") is not false || NativeV11Reflection.Get(plan.Model, "IsImportImage") is true)
            throw Review("The native image identity or owner changed.");
        if (beforeDispatch)
        {
            var bound = readBoundDocument(plan.Request.Address);
            RequireBoundBaseline(NativeV11Resources.CaptureImageCore(plan.Request.Address, plan.Model, session.LocalPath), bound);
            if (bound.Revision != plan.Before.Revision) throw Review("The authoritative image/source revision changed before dispatch.");
            if (!Read(plan.File).SequenceEqual(plan.BeforeBytes)) throw Review("The native image bytes changed before dispatch.");
            RequireSlot(plan.NameCache, plan.Request.Address.Name, plan.NameSlot); RequireSlot(plan.TempCache, plan.File, plan.TempSlot);
            foreach (var cache in plan.BeforeCaches) cache.RequireUnchanged();
        }
        RequireNoReferences(NativeV11References.Find(session, plan.Request.Address, plan.Model));
    }

    private object FindModel(ModuleAddress address)
    {
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        object folder = NativeV11Reflection.Get(settings, "ImageFolder")!;
        foreach (var segment in string.IsNullOrEmpty(address.FolderPath) ? [] : address.FolderPath.Split('/'))
            folder = Children(folder).Cast<object>().Single(value => NativeV11Reflection.Get(value, "Name") as string == segment &&
                NativeV11Reflection.Get(value, "Items") is IList);
        return Children(folder).Cast<object>().Single(value => value.GetType() == VerifySignatures(session.Context.DesignerAssembly).ImageType &&
            NativeV11Reflection.Get(value, "Name") as string == address.Name);
    }

    // Save must persist the exact binary companion via the bound native static-file owner.
    internal static void RequireImageSaveRegistration(IEnumerable<string> folders)
    {
        if (folders.Count(folder => folder.Replace('\\', '/').Trim('/').Equals("image", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("The native static-file saver must register the image directory exactly once.");
    }

    internal static void RequireNoReferences(ReferenceResult result)
    {
        if (!result.Complete || result.References.Count != 0)
            throw new NotSupportedException("Noninteractive image replacement currently requires a complete native reference scan with no references; plugin/referrer hooks are not rollback-proven.");
    }

    private static object ExistingSingleton(Contract contract) => contract.Initialization.Singleton.GetValue(null) ??
        throw Review("The native application owner is absent; validation will not create a singleton.");

    // The ordinary o getters can create an Items context or execute a published
    // service factory. Read only their already populated native fields instead.
    internal static (object Spread, object Input) RequireExistingRefreshOwners(Contract contract, object singleton)
    {
        var fields = contract.ExistingRefresh;
        if (singleton.GetType() != fields.SpreadOverride.DeclaringType)
            throw Review("The existing native application owner is unknown.");
        var spread = fields.SpreadOverride.GetValue(singleton);
        if (spread is null)
        {
            var items = fields.ItemsOwner.GetValue(singleton);
            if (items?.GetType() != fields.ContextLayer.DeclaringType)
                throw Review("The existing native Items owner is unavailable; validation will not create it.");
            var first = fields.ContextLayer.GetValue(items);
            var layer = first;
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            object? selected = null;
            while (layer is not null)
            {
                if (layer.GetType() != fields.LayerItems.DeclaringType || !visited.Add(layer))
                    throw Review("The native Items context chain is unknown or cyclic.");
                if (fields.LayerItems.GetValue(layer) is IDictionary entries && entries.Contains(fields.ContextItem))
                {
                    selected = entries[fields.ContextItem];
                    break;
                }
                layer = fields.ParentLayer.GetValue(layer);
            }
            // Native GetValue searches explicit Items through the context chain,
            // then the first context's default cache, then would create j. Stop
            // before that final factory branch when no existing j is present.
            if (selected is null && first is not null && fields.LayerDefaults.GetValue(first) is IDictionary defaults && defaults.Contains(fields.ContextItem))
                selected = defaults[fields.ContextItem];
            if (selected?.GetType() != fields.ContextItem)
                throw Review("The native spread context has not been populated; validation will not create it.");
            spread = fields.ItemSpread.GetValue(selected);
        }
        var serviceOwner = fields.ServicesOwner.GetValue(singleton);
        if (!contract.SpreadType.IsInstanceOfType(spread) || serviceOwner?.GetType() != fields.Services.DeclaringType ||
            fields.Services.GetValue(serviceOwner) is not IDictionary services || !services.Contains(contract.InputType) ||
            services[contract.InputType] is not object input || !contract.InputType.IsInstanceOfType(input) || input is Delegate)
            throw Review("A real spread/input service is not already published; validation will not execute a native factory.");
        return (spread, input);
    }

    // Pure instance-field inspection. In particular, missing witnesses return
    // before ThemesManager._instance or either static cache value is read.
    internal static CacheInitializationWitness RequireInitializationWitness(Contract contract, object document, object spread)
    {
        var metadata = contract.Initialization;
        if (document.GetType() != metadata.DocumentType || spread.GetType() != metadata.SpreadWrapperType)
            throw Review("The actual document/spread event owners are unknown; cold cache validation is unavailable.");
        object Target(FieldInfo field, object owner, MethodInfo method)
        {
            var handler = RequireExistingHandler(field.GetValue(owner) as Delegate, method);
            return handler.Target!;
        }
        var ucTarget = Target(metadata.DocumentOpened, document, metadata.UserControlHandlers[0]);
        var pageTarget = Target(metadata.DocumentOpened, document, metadata.PageImageHandlers[0]);
        if (!ReferenceEquals(ucTarget, Target(metadata.ActiveSheetChanged, spread, metadata.UserControlHandlers[2])) ||
            !ReferenceEquals(pageTarget, Target(metadata.ActiveSheetChanged, spread, metadata.PageImageHandlers[2])))
            throw Review("The native document/spread cache initialization targets do not match.");
        // The successful native chains reached their final spread subscription,
        // so ThemesManager.get_Instance and its .cctor already completed. Only
        // now read its existing instance, never its creating getter.
        var themes = metadata.ThemesInstance.GetValue(null) ?? throw Review("The witnessed native theme owner is no longer present.");
        if (themes.GetType() != metadata.ThemeChanged.DeclaringType ||
            !ReferenceEquals(ucTarget, Target(metadata.ThemeChanged, themes, metadata.UserControlHandlers[1])) ||
            !ReferenceEquals(pageTarget, Target(metadata.ThemeChanged, themes, metadata.PageImageHandlers[1])))
            throw Review("The current native theme owner lacks the same complete cache initialization witnesses.");
        return new(themes, ucTarget, pageTarget);
    }

    internal static Delegate RequireExistingHandler(Delegate? listeners, MethodInfo expected)
    {
        if (listeners is null) throw Review("A native cache initialization event has no existing handlers; validation will not initialize it.");
        var matches = listeners.GetInvocationList().Where(value => IsExpectedHandlerMethod(value.Method, expected) &&
            value.Target?.GetType() == expected.DeclaringType).ToArray();
        return matches.Length == 1 ? matches[0] : throw Review("A native cache initialization handler is missing, duplicated or unknown.");
    }

    internal static bool IsExpectedHandlerMethod(MethodInfo observed, MethodInfo expected) =>
        observed.Module == expected.Module && observed.MetadataToken == expected.MetadataToken &&
        observed.DeclaringType == expected.DeclaringType && observed.Name == expected.Name && !observed.IsStatic;

    internal static void RequireBoundBaseline(ModuleDocument native, ModuleDocument bound)
    {
        if (native.Address != bound.Address || string.IsNullOrEmpty(bound.Revision) ||
            !V11Safety.Equivalent(native.Metadata, bound.Metadata) || native.Companions.Count != bound.Companions.Count ||
            native.Companions.Zip(bound.Companions).Any(pair => pair.First.RelativePath != pair.Second.RelativePath ||
                pair.First.Sha256 != pair.Second.Sha256 || !Convert.FromBase64String(pair.First.ContentBase64).SequenceEqual(Convert.FromBase64String(pair.Second.ContentBase64))))
            throw Review("The authoritative image baseline does not describe the same actual native metadata and complete binary group.");
    }

    private static void RequireIdleView(Contract contract, object view)
    {
        if (view.GetType() != contract.ViewType) throw Review("The drawing view type is not the pinned native SpreadView.");
        foreach (var name in IdleProperties)
            if (NativeV11Reflection.Get(view, name) is not false) throw Review("A native cell, pointer or drawing interaction is active or unknown: " + name);
        if (NativeV11Reflection.Call(view, "HasSelectedFloatingObject") is not false) throw Review("A native floating/drawing object is selected.");
        var type = contract.ViewType.BaseType!;
        if (type.GetField("_movingResizingFloatingObject", NativeV11Reflection.Flags)!.GetValue(view) is not null ||
            type.GetField("_movingResizingFloatingObjects", NativeV11Reflection.Flags)!.GetValue(view) is Array { Length: > 0 } ||
            type.GetField("_isMouseDownFloatingObject", NativeV11Reflection.Flags)!.GetValue(view) is not false ||
            type.GetField("_shapeDrawingContainer", NativeV11Reflection.Flags)!.GetValue(view) is Canvas { Children.Count: > 0 })
            throw Review("A native drawing interaction or overlay is active.");
    }

    private static void RequirePreviewIdle(Contract contract, object preview)
    {
        if (preview.GetType() != contract.PreviewType || contract.PreviewPage.GetValue(preview) is not null ||
            NativeV11Reflection.Get(preview, "PreviewTasks") is not IEnumerable tasks || tasks.Cast<Task>().Any(value => !value.IsCompleted))
            throw Review("An active/unknown native browser preview prevents confirmed synchronous replacement and rollback.");
    }

    private static void Refresh(Plan plan, Action<CacheSlot>? observeTemp = null)
    {
        plan.Contract.ReplaceReference.Invoke(null, [plan.Model]);
        var source = plan.Contract.GetImageFile.Invoke(null, [plan.Request.Address.Name, false]) as string;
        if (string.IsNullOrEmpty(source)) throw Review("The native image cache did not return an image source.");
        var temp = Slot(plan.TempCache, plan.File);
        if (!temp.Exists || temp.Value != source) throw Review("The native image source does not match its cache owner.");
        observeTemp?.Invoke(temp);
        NativeV11Reflection.Set(plan.Model, "ImageSource", source);
        plan.Contract.ClearDrawing.Invoke(null, null);
        plan.Contract.SpreadInvalidate.Invoke(plan.Spread, null);
        plan.Contract.InputInvalidate.Invoke(plan.Input, [false]);
    }

    private static void CopyNative(Plan plan, byte[] bytes, byte[] expected, bool allowOwnedMissing = false, Action? removed = null)
    {
        if (File.Exists(plan.File) ? !Read(plan.File).SequenceEqual(expected) : !allowOwnedMissing)
            throw Review("The native image file changed; replacement will not overwrite it.");
        var directory = Directory.CreateTempSubdirectory("v11-image-replace-").FullName;
        try
        {
            var stage = Path.Combine(directory, plan.Request.Address.Name);
            File.WriteAllBytes(stage, bytes); // Private input only; project working bytes use the real native file owner.
            if (File.Exists(plan.File))
            {
                plan.Contract.MakeWritable.Invoke(null, [plan.File]);
                NativeV11Reflection.Call(plan.NativeFile, "Delete", plan.File);
                removed?.Invoke();
            }
            NativeV11Reflection.Call(plan.NativeFile, "Copy", stage, plan.File, false);
            if (!Read(plan.File).SequenceEqual(bytes)) throw Review("The native file copy did not produce the requested image bytes.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static IList Children(object folder)
    {
        if (NativeV11Reflection.Get(folder, "Loaded") is not true || NativeV11Reflection.Get(folder, "Items") is not IList children)
            throw Review("The native image folder is not completely loaded.");
        return children;
    }

    // Cache values are the original native objects. Restore only entries whose
    // post-operation value still matches; never clear or overwrite a new value.
    internal sealed record CacheSnapshot(IDictionary Store, Dictionary<object, object?> Entries)
    {
        internal static CacheSnapshot Capture(object store)
        {
            if (store is not IDictionary dictionary || dictionary.IsReadOnly || dictionary.IsFixedSize)
                throw Review("A native derived cache does not expose a checked restorable dictionary.");
            return new(dictionary, dictionary.Keys.Cast<object>().ToDictionary(key => key, key => dictionary[key]));
        }
        internal void RequireUnchanged()
        {
            var current = Capture(Store);
            if (current.Entries.Count != Entries.Count || Entries.Any(value =>
                !current.Entries.TryGetValue(value.Key, out var actual) || !SameValue(value.Value, actual)))
                throw Review("A native preview/drawing cache changed concurrently; rollback requires review.");
        }
        internal void RestoreChecked(CacheSnapshot expected)
        {
            if (!ReferenceEquals(Store, expected.Store)) throw Review("A native cache owner changed.");
            expected.RequireUnchanged();
            var concurrent = Store.GetType().GetMethod("TryUpdate", NativeV11Reflection.Flags);
            foreach (var entry in expected.Entries)
            {
                if (Entries.TryGetValue(entry.Key, out var original) && SameValue(entry.Value, original)) continue;
                if (concurrent is null)
                {
                    if (!Store.Contains(entry.Key) || !SameValue(Store[entry.Key], entry.Value)) throw Review("A native cache entry changed.");
                    if (Entries.ContainsKey(entry.Key)) Store[entry.Key] = original; else Store.Remove(entry.Key);
                }
                else if (Entries.ContainsKey(entry.Key))
                {
                    if (concurrent.Invoke(Store, [entry.Key, original, entry.Value]) is not true) throw Review("A native cache entry changed.");
                }
                else
                {
                    var arguments = Store.GetType().GetGenericArguments();
                    var pair = typeof(KeyValuePair<,>).MakeGenericType(arguments);
                    var collection = typeof(ICollection<>).MakeGenericType(pair);
                    if (collection.GetMethod("Remove")!.Invoke(Store, [Activator.CreateInstance(pair, entry.Key, entry.Value)]) is not true)
                        throw Review("A native cache entry changed.");
                }
            }
            foreach (var entry in Entries.Where(value => !expected.Entries.ContainsKey(value.Key)))
            {
                if (concurrent is null)
                {
                    if (Store.Contains(entry.Key)) throw Review("A native cache entry appeared concurrently.");
                    Store.Add(entry.Key, entry.Value);
                }
                else if (NativeV11Reflection.Call(Store, "TryAdd", entry.Key, entry.Value) is not true)
                    throw Review("A native cache entry appeared concurrently.");
            }
            RequireUnchanged();
        }
        private static bool SameValue(object? left, object? right) => ReferenceEquals(left, right) || left is not null &&
            (left.GetType().IsValueType || left is string) && left.Equals(right);
    }

    private static CacheSlot Slot(ConcurrentDictionary<string, string> cache, string key) => cache.TryGetValue(key, out var value) ? new(true, value) : new(false, null);
    private static void RequireSlot(ConcurrentDictionary<string, string> cache, string key, CacheSlot expected)
    {
        if (Slot(cache, key) != expected) throw Review("A native image cache registration changed concurrently.");
    }
    private static void SetSlotChecked(ConcurrentDictionary<string, string> cache, string key, CacheSlot expected, CacheSlot desired)
    {
        RequireSlot(cache, key, expected);
        var accepted = desired.Exists ? expected.Exists ? cache.TryUpdate(key, desired.Value!, expected.Value!) : cache.TryAdd(key, desired.Value!) :
            !expected.Exists || ((ICollection<KeyValuePair<string, string>>)cache).Remove(new(key, expected.Value!));
        if (!accepted) throw Review("The native image cache changed during its checked update.");
    }
    private static byte[] Read(string file)
    {
        if (!File.Exists(file) || new FileInfo(file).Length > 32 * 1024 * 1024) throw Review("The native image/cache file is missing or exceeds its verified size limit.");
        return File.ReadAllBytes(file);
    }
    private static bool SamePath(string? left, string right) => !string.IsNullOrEmpty(left) && Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static BridgeException Review(string message, Exception? inner = null) => new(ReviewCode, inner is null ? message : message + " " + inner.GetBaseException().Message);
}
