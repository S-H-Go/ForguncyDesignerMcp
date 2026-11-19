using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

internal sealed class NativeV11PendingSave
{
    internal string OperationId { get; }
    internal ModuleAddress Address { get; }
    internal ModuleOperation Operation { get; }
    internal string? ProjectSourceRevision { get; set; }
    internal JsonElement? ExpectedSourceMetadata { get; set; }
    internal NativeV11BuiltInTableLifecycle.CreatedTable? BuiltInTable { get; set; }
    internal NativeV11BuiltInTableLifecycle.DeletedTable? DeletedBuiltInTable { get; set; }
    internal NativeV11FirstProcessSaveJournal? FirstProcessJournal { get; set; }
    internal NativeV11CommandSaveSnapshot? CommandSaveSnapshot { get; set; }
    internal NativeV11TableSharedSaveJournal? SharedTableJournal { get; set; }
    internal NativeV11UnchangedDatabaseProof? UnchangedProcessDatabase { get; set; }
    internal NativeV11FolderMoveJournal? FolderMoveJournal { get; set; }
    internal NativeV11FolderDeleteJournal? FolderDeleteJournal { get; set; }
    internal NativeV11ImageFolderLifecycle.UpdatePlan? ImageFolderUpdate { get; set; }
    internal NativeV11ImageFolderLifecycle.DeletePlan? ImageFolderDelete { get; set; }
    internal NativeV11TableFolderDelete.Completed? TableFolderDelete { get; set; }
    private readonly ModuleDocument? document;
    private readonly object? deletedModel;
    private readonly NativeV11Settings? settings;
    private readonly IReadOnlyDictionary<string, byte[]>? settingsFiles;
    private readonly NativeV11AuxiliaryStore? auxiliary;
    private readonly ModuleMutationRequest? mutation;
    private readonly IReadOnlyDictionary<string, byte[]>? auxiliaryFiles;
    private readonly IReadOnlyDictionary<string, byte[]?> before;
    internal string[] Paths => before.Keys.Concat(BuiltInTable is not null ? [BuiltInTable.Plan.SourceBefore.DatabasePath] :
        DeletedBuiltInTable is not null ? [DeletedBuiltInTable.Plan.SourceBefore.DatabasePath] : Array.Empty<string>())
        .Concat(BuiltInTable?.RelatedTable is { } related ? [related.Item.RelativePath] : Array.Empty<string>())
        .Concat(BuiltInTable?.PreviousRelatedTable is { } previous ? [previous.Item.RelativePath] : Array.Empty<string>())
        .Concat(FirstProcessJournal?.Paths ?? Array.Empty<string>()).Concat(SharedTableJournal?.Paths ?? Array.Empty<string>())
        .Concat(UnchangedProcessDatabase is null ? Array.Empty<string>() : [UnchangedProcessDatabase.Path])
        .Concat(FolderMoveJournal?.Paths ?? Array.Empty<string>())
        .Concat(FolderDeleteJournal?.Paths ?? Array.Empty<string>())
        .Concat(ImageFolderUpdate?.Journal.Paths ?? Array.Empty<string>())
        .Concat(TableFolderDelete is null ? Array.Empty<string>() : [TableFolderDelete.Plan.Database])
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal NativeV11PendingSave(string operationId, ModuleAddress address, ModuleOperation operation,
        ModuleDocument? document, IReadOnlyDictionary<string, byte[]?> before, object? deletedModel = null, NativeV11Settings? settings = null,
        NativeV11AuxiliaryStore? auxiliary = null, ModuleMutationRequest? mutation = null)
    {
        OperationId = operationId; Address = address; Operation = operation;
        this.document = document; this.before = before; this.deletedModel = deletedModel;
        this.settings = settings; settingsFiles = settings?.CaptureSaveFiles(address.Kind);
        this.auxiliary = auxiliary; this.mutation = mutation;
        auxiliaryFiles = auxiliary is null ? null : auxiliary.CaptureSaveFiles(mutation ?? throw new ArgumentNullException(nameof(mutation)));
        if (auxiliaryFiles is not null && auxiliaryFiles.Keys.Any(path => !before.ContainsKey(path)))
            throw new InvalidDataException("A native auxiliary file group changed outside its reviewed source baseline.");
    }

    internal static IReadOnlyDictionary<string, byte[]?> CaptureSource(NativeV11Session session, ModuleAddress address)
        => ReadFiles(session.SourcePath, PathsForAddress(session, address));

    internal static IReadOnlyList<string> PathsForAddress(NativeV11Session session, ModuleAddress address)
    {
        var path = session.NativePath(address);
        var paths = new List<string> { path };
        if (address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage)
            paths.Add(Path.ChangeExtension(path, ".rd").Replace('\\', '/'));
        else if (address.Kind == ModuleKind.Report) paths.Add(Path.ChangeExtension(path, ".rdlx").Replace('\\', '/'));
        else if (address.Kind == ModuleKind.BusinessProcess) paths.Add(Path.ChangeExtension(path, ".bpmn").Replace('\\', '/'));
        return paths;
    }

    internal void VerifySourceUnchanged(NativeV11Session session)
    {
        FirstProcessJournal?.VerifySourceUnchanged(session);
        UnchangedProcessDatabase?.Verify(session);
        if (ProjectSourceRevision is not null && NativeV11SourceRevision.Capture(session.SourcePath) != ProjectSourceRevision)
            throw new BridgeException("ProjectSourceChanged", "工程源目录在原生保存前发生变化，请核对外部修改。");
        var current = ReadFiles(session.SourcePath, before.Keys);
        foreach (var path in before.Keys)
        {
            var a = before[path]; var b = current[path];
            if (a is null ? b is not null : b is null || !a.AsSpan().SequenceEqual(b))
                throw new BridgeException("SourceRevisionChanged", "原生保存前源文件已变化，请核对原操作：" + path);
        }
    }

    internal void VerifyMemory(NativeV11Session session)
    {
        if (ImageFolderUpdate is not null) { NativeV11ImageFolderLifecycle.VerifyMemory(ImageFolderUpdate); return; }
        if (ImageFolderDelete is not null) { ImageFolderDelete.Journal.VerifyMemory(session); return; }
        if (TableFolderDelete is not null) { NativeV11TableFolderDelete.VerifyMemory(TableFolderDelete); SharedTableJournal?.VerifyMemory(session); return; }
        FirstProcessJournal?.VerifyMemory(session);
        SharedTableJournal?.VerifyMemory(session);
        FolderMoveJournal?.VerifyMemory(session);
        FolderDeleteJournal?.VerifyMemory(session);
        if (BuiltInTable is not null)
        {
            var item = session.Find(Address);
            if (!ReferenceEquals(item.Model, BuiltInTable.Model) ||
                !NativeV11DefinitionWriter.Equivalent(BuiltInTable.ExpectedNativeJson, session.Serialize(item.Route, item.Model)) ||
                BuiltInTable.RelatedTable is { } related && !NativeV11BuiltInTableLifecycle.RelatedTableMemoryMatches(session, related) ||
                BuiltInTable.PreviousRelatedTable is { } previous && !NativeV11BuiltInTableLifecycle.RelatedTableMemoryMatches(session, previous) ||
                BuiltInTable.ExpectedRelationGraph is { } graph && !NativeV11TableRelationLifecycle.GraphMatches(BuiltInTable.Plan.TableSet, graph))
                throw new BridgeException("MemoryRevisionChanged", "原生表模型在保存前发生变化。");
            if (BuiltInTable.PreviousRelatedTable is { } previousDetail)
            {
                NativeV11StatisticColumnLifecycle.VerifyMemento(session, item);
                NativeV11StatisticColumnLifecycle.VerifyMemento(session, BuiltInTable.RelatedTable!.Item);
                NativeV11StatisticColumnLifecycle.VerifyMemento(session, previousDetail.Item);
            }
            return;
        }
        if (auxiliary is not null)
        {
            var current = auxiliary.CaptureSaveFiles(mutation!);
            if (current.Count != auxiliaryFiles!.Count || auxiliaryFiles.Any(pair =>
                !current.TryGetValue(pair.Key, out var bytes) || !FileMatches(pair.Key, pair.Value, bytes, auxiliary.IsJsonSaveFile)))
                throw new BridgeException("MemoryRevisionChanged", "原生资源或目录保存登记在保存前发生变化。");
            if (Operation == ModuleOperation.Delete)
            {
                if (auxiliary.VerifyDeleted is not null) auxiliary.VerifyDeleted(mutation!);
                else if (auxiliary.List(Address.Kind).Any(item => item.Address == Address))
                    throw new BridgeException("MemoryRevisionChanged", "已删除的原生资源或目录重新出现在内存中。");
            }
            else
            {
                var actual = auxiliary.Read(Address);
                if (!V11Safety.Equivalent(actual.Metadata, document!.Metadata) || actual.Companions.Count != document.Companions.Count ||
                    actual.Companions.Any(asset => !document.Companions.Any(expected => expected.RelativePath == asset.RelativePath && expected.Sha256 == asset.Sha256)))
                    throw new BridgeException("MemoryRevisionChanged", "原生资源或目录内存已经改变。");
            }
            return;
        }
        if (settings is not null)
        {
            var files = settings.CaptureSaveFiles(Address.Kind);
            foreach (var pair in settingsFiles!)
                if (!files.TryGetValue(pair.Key, out var current) || !V11Safety.Equivalent(V11Safety.Body(pair.Value), V11Safety.Body(current)))
                    throw new BridgeException("MemoryRevisionChanged", "原生设置在保存前发生变化。");
            return;
        }
        if (Operation == ModuleOperation.Delete)
        {
            if (session.Items(Address.Kind).Any(item => item.Address == Address))
                throw new BridgeException("MemoryRevisionChanged", "删除后的模块重新出现在原生内存中。");
            var references = NativeV11References.Find(session, Address, deletedModel ?? throw new InvalidOperationException("缺少原生删除模型。"));
            if (!references.Complete || references.References.Count > 0)
                throw new BridgeException("DeleteReferencesChanged", "删除后的引用状态尚未确认。");
        }
        else
        {
            if (CommandSaveSnapshot is not null)
            {
                CommandSaveSnapshot.Verify(session.Find(Address).Model);
                return;
            }
            var current = session.Read(session.Find(Address), true);
            if (!V11Safety.Equivalent(current.Metadata, document!.Metadata) || current.Companions.Count != document.Companions.Count ||
                current.Companions.Any(asset => !document.Companions.Any(expected => expected.RelativePath == asset.RelativePath && expected.Sha256 == asset.Sha256)))
                throw new BridgeException("MemoryRevisionChanged", "模块内存或配套文件在保存前发生变化。");
            if (ExpectedSourceMetadata is JsonElement sourceExpected && !V11Safety.Equivalent(sourceExpected, NativeV11SourceMetadata.Capture(session, Address)))
                throw new BridgeException("MemoryRevisionChanged", "模块原生源序列化数据在保存前发生变化。");
        }
    }

    internal bool SourceMatches(NativeV11Session session)
    {
        VerifyMemory(session);
        if (ImageFolderUpdate is not null) return ImageFolderUpdate.Journal.VerifyMoved(session.LocalPath, session.LastPath, session.SourcePath);
        if (ImageFolderDelete is not null) return ImageFolderDelete.Journal.VerifyDeleted(session);
        if (TableFolderDelete is not null) return NativeV11TableFolderDelete.SourceMatches(TableFolderDelete) && (SharedTableJournal?.SourceMatches(session) ?? true);
        if (FirstProcessJournal is not null && !FirstProcessJournal.SourceMatches(session)) return false;
        if (SharedTableJournal is not null && !SharedTableJournal.SourceMatches(session)) return false;
        if (FolderMoveJournal is not null && !FolderMoveJournal.VerifyMoved(session.LocalPath, session.LastPath, session.SourcePath)) return false;
        if (FolderDeleteJournal is not null && !FolderDeleteJournal.VerifyDeleted(session)) return false;
        UnchangedProcessDatabase?.Verify(session);
        if (BuiltInTable is not null)
            return NativeV11BuiltInTableLifecycle.VerifySavedSourceHash(BuiltInTable) is not null;
        if (DeletedBuiltInTable is not null)
            return NativeV11BuiltInTableLifecycle.VerifyDeletedSource(DeletedBuiltInTable);
        var source = ReadFiles(session.SourcePath, before.Keys.Where(path => FirstProcessJournal is null ||
            !path.Equals(NativeV11ProcessInitialization.Database, StringComparison.OrdinalIgnoreCase)));
        if (auxiliaryFiles is not null)
        {
            if (FolderMoveJournal is not null)
                return auxiliaryFiles.All(pair => source.TryGetValue(pair.Key, out var value) && value is byte[] bytes &&
                    FileMatches(pair.Key, pair.Value, bytes, auxiliary!.IsJsonSaveFile));
            return source.All(pair => auxiliaryFiles.TryGetValue(pair.Key, out var expected)
                ? pair.Value is byte[] bytes && FileMatches(pair.Key, expected, bytes, auxiliary!.IsJsonSaveFile)
                : pair.Value is null);
        }
        if (settingsFiles is not null) return settingsFiles.All(pair => source[pair.Key] is byte[] actual && V11Safety.Equivalent(V11Safety.Body(pair.Value), V11Safety.Body(actual)));
        if (Operation == ModuleOperation.Delete)
            return source.All(pair => Address.Kind == ModuleKind.BusinessProcess && pair.Key.Equals(NativeV11ProcessInitialization.Database, StringComparison.OrdinalIgnoreCase)
                ? before[pair.Key] is byte[] original && pair.Value is byte[] current && original.AsSpan().SequenceEqual(current)
                : pair.Value is null);
        var expectedDocument = document ?? throw new InvalidOperationException("The pending native module document is unavailable.");
        var main = source[session.NativePath(Address)];
        if (CommandSaveSnapshot is not null)
        {
            var path = session.NativePath(Address);
            var staged = ReadFiles(session.LastPath, [path]);
            return main is not null && staged[path] is byte[] bytes && CommandSaveSnapshot.MatchesSource(main, bytes);
        }
        if (main is null || !V11Safety.Equivalent(ExpectedSourceMetadata ?? expectedDocument.Metadata, V11Safety.Body(main))) return false;
        foreach (var companion in expectedDocument.Companions)
        {
            var actual = source[companion.RelativePath];
            if (actual is null) return false;
            var expected = Convert.FromBase64String(companion.ContentBase64);
            if (companion.RelativePath.EndsWith(".rd", StringComparison.OrdinalIgnoreCase))
            {
                if (!V11Safety.Equivalent(V11Safety.Body(expected), V11Safety.Body(actual))) return false;
            }
            else if (!actual.AsSpan().SequenceEqual(expected)) return false;
        }
        const string processDatabase = NativeV11ProcessInitialization.Database;
        if (FirstProcessJournal is null && Address.Kind == ModuleKind.BusinessProcess && before.TryGetValue(processDatabase, out var databaseBefore) &&
            (databaseBefore is null || source[processDatabase] is not byte[] databaseAfter || !databaseBefore.AsSpan().SequenceEqual(databaseAfter)))
            return false;
        return true;
    }

    private static bool FileMatches(string path, byte[] expected, byte[] actual, Func<string, bool> isJson) =>
        path.EndsWith('/') ? expected.Length == 0 && actual.Length == 0 :
        isJson(path) ? V11Safety.Equivalent(V11Safety.Body(expected), V11Safety.Body(actual)) : expected.AsSpan().SequenceEqual(actual);

    internal static IReadOnlyDictionary<string, byte[]?> ReadFiles(string project, IEnumerable<string> paths)
    {
        var names = paths.Distinct(StringComparer.Ordinal).ToArray();
        const long maximum = 32 * 1024 * 1024;
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        if (Directory.Exists(project))
        {
            foreach (var name in names)
            {
                if (name.EndsWith('/'))
                {
                    var directory = V11Safety.Under(project, name[..^1]);
                    try
                    {
                        var attributes = File.GetAttributes(directory);
                        if ((attributes & FileAttributes.Directory) == 0) throw new BridgeException("SourceKindMismatch", "原生目录标记被同名文件占用。");
                        result[name] = [];
                    }
                    catch (FileNotFoundException) { result[name] = null; }
                    catch (DirectoryNotFoundException) { result[name] = null; }
                    continue;
                }
                var file = V11Safety.Under(project, name);
                if (!File.Exists(file)) { result[name] = null; continue; }
                if (new FileInfo(file).Length > maximum) throw new BridgeException("SourceFileTooLarge", "模块源文件过大。");
                result[name] = File.ReadAllBytes(file);
            }
        }
        else
        {
            if (!File.Exists(project) || !Path.GetExtension(project).Equals(".fgcc", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ProjectSourceUnavailable", "源工程不是现有协同目录或 FGCC 文件。");
            using var stream = File.OpenRead(project);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var name in names)
            {
                if (name.EndsWith('/'))
                {
                    _ = V11Safety.Under(Path.GetDirectoryName(project)!, name[..^1]);
                    if (zip.Entries.Any(entry => entry.FullName.Replace('\\', '/').Equals(name[..^1], StringComparison.OrdinalIgnoreCase)))
                        throw new BridgeException("SourceKindMismatch", "原生目录标记被同名归档文件占用。");
                    var markers = zip.Entries.Where(entry => entry.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (markers.Length > 1 || markers.Any(entry => entry.Length != 0))
                        throw new BridgeException("InvalidProjectArchive", "原生目录归档标记重复或格式无效。");
                    result[name] = markers.Length == 1 || zip.Entries.Any(entry => entry.FullName.Replace('\\', '/').StartsWith(name, StringComparison.OrdinalIgnoreCase)) ? [] : null;
                    continue;
                }
                _ = V11Safety.Under(Path.GetDirectoryName(project)!, name);
                var entries = zip.Entries.Where(entry => entry.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (entries.Length == 0) { result[name] = null; continue; }
                if (entries.Length != 1 || entries[0].Length > maximum)
                    throw new BridgeException("InvalidProjectArchive", "模块归档存在重复或过大的条目。");
                using var input = entries[0].Open();
                using var memory = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    if (memory.Length + count > maximum) throw new BridgeException("InvalidProjectArchive", "模块归档条目过大。");
                    memory.Write(buffer, 0, count);
                }
                result[name] = memory.ToArray();
            }
        }
        return result;
    }
}
