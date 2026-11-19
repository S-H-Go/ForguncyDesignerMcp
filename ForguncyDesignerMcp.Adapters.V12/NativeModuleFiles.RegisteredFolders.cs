namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class NativeModuleFiles
{
    internal NativeRegisteredFolders RegisteredPageFolders()=>new(assembly,Colleague(ForguncyDesignerMcp.Core.Platform.ModuleKind.Page)
        ?? throw new ForguncyDesignerMcp.Core.BridgeException("NativeFolderUnavailable","The native page colleague is unavailable."));
    internal NativeRegisteredFolders RegisteredTableFolders()=>new(assembly,Colleague(ForguncyDesignerMcp.Core.Platform.ModuleKind.Table)
        ?? throw new ForguncyDesignerMcp.Core.BridgeException("NativeFolderUnavailable","The native table colleague is unavailable."),"Table");
    internal NativeRegisteredFolders RegisteredFolders(ForguncyDesignerMcp.Core.Platform.ModuleKind kind)
    {
        if(kind==ForguncyDesignerMcp.Core.Platform.ModuleKind.Image)
        {
            var owner=NativeReflection.Get(colleagues,"StaticFolder") ?? throw new ForguncyDesignerMcp.Core.BridgeException("NativeImageSaveUnavailable","The native static folder owner is unavailable.");
            var loader=NativeReflection.Get(RootMediator(owner),"SaveDataColleague") ?? throw new ForguncyDesignerMcp.Core.BridgeException("NativeImageSaveUnavailable","The native static folder loader is unavailable.");
            var saveProperty=loader.GetType().GetProperties(NativeReflection.Flags).SingleOrDefault(property=>property.PropertyType==assembly.GetType("Forguncy.SaveLoad.SaveLoadFileBase",true));
            if(loader.GetType()!=assembly.GetType("ea",true) || saveProperty is null || !ReferenceEquals(saveProperty.GetValue(loader),save) ||
                NativeReflection.Get(loader,"ya") is not IEnumerable<string> paths || !paths.Contains("image",StringComparer.Ordinal))
                throw new ForguncyDesignerMcp.Core.BridgeException("NativeImageSaveUnavailable","The bound native static saver does not own the image directory.");
        }
        return new(assembly,Colleague(kind) ?? throw new ForguncyDesignerMcp.Core.BridgeException("NativeFolderUnavailable","The native folder colleague is unavailable."),kind.ToString());
    }
}
