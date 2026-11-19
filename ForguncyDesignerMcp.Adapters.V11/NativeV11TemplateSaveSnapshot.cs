using System.Collections;
using System.IO;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Freezes the actual V11 native page-loader save DTO, including its original
// IDs. The narrow typed clone projection is used only to compare that cache
// with the live model, never to compare saved source with the cache.
internal sealed class NativeV11TemplateSaveSnapshot
{
    private sealed record Entry(ModuleKind Kind, string Primary, string Redundant, string Key, object Model, JsonElement Cache, JsonElement Rd);
    private readonly Entry[] entries;
    private readonly NativeV11ProcessInitialization.Candidate candidate;
    internal IReadOnlyList<string> Paths => entries.SelectMany(entry => new[] { entry.Primary, entry.Redundant }).ToArray();
    internal IReadOnlyList<string> TemplatePaths => candidate.TemplatePaths;

    private NativeV11TemplateSaveSnapshot(Entry[] entries, NativeV11ProcessInitialization.Candidate candidate)
    { this.entries = entries; this.candidate = candidate; }

    internal static NativeV11TemplateSaveSnapshot Capture(NativeV11Session session, NativeV11ProcessInitialization.Candidate candidate)
    {
        var models = CollectModels(session, candidate);
        var entries = new List<Entry>();
        foreach (var pair in models)
        {
            var route = session.Route(pair.Value.Kind);
            if (route.Loader is not IDictionary cache) throw new InvalidDataException("The native page save cache is unavailable.");
            var key = NativeV11Reflection.Call(route.Colleague, "GetKey", pair.Value.Model) as string
                ?? throw new InvalidDataException("The native template page key is unavailable.");
            if (!key.Replace('\\', '/').Equals(pair.Key, StringComparison.Ordinal) || cache[key] is not object cached)
                throw new InvalidDataException("The native template page save cache does not match its actual model.");
            var cacheJson = NativeV11SourceMetadata.SerializeSaveData(session.Context.DesignerAssembly, route, cached);
            var sheet = FindSheet(session, pair.Value.Model);
            var live = NativeV11Reflection.Static(session.Generator, "GetPageSaveData", sheet) ?? throw new InvalidDataException("The native template worksheet save data is unavailable.");
            var liveJson = NativeV11SourceMetadata.SerializeSaveData(session.Context.DesignerAssembly, route, live);
            if (!NativeV11ConditionPairProjection.Equivalent(cacheJson.GetRawText(), liveJson.GetRawText()))
                throw new InvalidDataException("The native template model does not match its exact save-cache projection.");
            var rd = NativeV11Reflection.Call(route.Loader, session.Profile.RedundantProjection, cached)
                ?? throw new InvalidDataException("The native template redundant projection is unavailable.");
            entries.Add(new(pair.Value.Kind, pair.Key, Path.ChangeExtension(pair.Key, ".rd").Replace('\\', '/'), key, pair.Value.Model, cacheJson,
                V11Safety.Body(Encoding.UTF8.GetBytes(session.SerializeSaveData(rd)))));
        }
        var expected = candidate.TemplatePaths.Count / 2 + candidate.Owners.Count(owner => owner.State == "New" && owner.Type is "PCPage" or "MobilePage" or "MasterPCPage" or "MasterMobilePage");
        if (entries.Count != expected) throw new InvalidDataException("The native BPM page save snapshot is incomplete.");
        return new(entries.ToArray(), candidate);
    }

    internal void VerifyMemory(NativeV11Session session)
    {
        var models = CollectModels(session, candidate);
        foreach (var entry in entries)
        {
            var route = session.Route(entry.Kind);
            if (route.Loader is not IDictionary cache || !models.TryGetValue(entry.Primary, out var model) || !ReferenceEquals(model.Model, entry.Model) || cache[entry.Key] is not object current ||
                !V11Safety.Equivalent(entry.Cache, NativeV11SourceMetadata.SerializeSaveData(session.Context.DesignerAssembly, route, current)))
                throw new InvalidDataException("The native template save cache or model identity changed.");
            var sheet = FindSheet(session, entry.Model);
            var live = NativeV11Reflection.Static(session.Generator, "GetPageSaveData", sheet) ?? throw new InvalidDataException("The native template worksheet is unavailable.");
            var liveJson = NativeV11SourceMetadata.SerializeSaveData(session.Context.DesignerAssembly, route, live);
            if (!NativeV11ConditionPairProjection.Equivalent(entry.Cache.GetRawText(), liveJson.GetRawText()))
                throw new InvalidDataException("The native template worksheet changed since its save-cache snapshot.");
        }
    }

    private static IReadOnlyDictionary<string, (ModuleKind Kind, object Model)> CollectModels(NativeV11Session session,
        NativeV11ProcessInitialization.Candidate candidate)
    {
        var result = NativeV11FirstProcessTemplates.VerifyActual(session, candidate)
            .ToDictionary(pair => pair.Key, pair => (ModuleKind.Page, pair.Value), StringComparer.Ordinal);
        var factory = session.Context.DesignerAssembly.GetType("Forguncy.Common.SimpleModule", true)!;
        foreach (var kind in new[] { ModuleKind.Page, ModuleKind.MasterPage })
        foreach (var item in session.Items(kind))
        {
            var simple = NativeV11Reflection.Static(factory, "Create", item.Model)!;
            var name = NativeV11Reflection.Get(simple, "ModuleName") as string;
            var type = NativeV11Reflection.Get(simple, "ModuleType")?.ToString();
            if (!candidate.Owners.Any(owner => owner.State == "New" && owner.Name == name && owner.Type == type)) continue;
            if (!result.TryAdd(item.RelativePath, (kind, item.Model))) throw new InvalidDataException("A generated BPM page path is ambiguous.");
        }
        return result;
    }

    private static object FindSheet(NativeV11Session session, object model)
    {
        var sheets = NativeV11Reflection.Get(session.Workbook, "Sheets") as IEnumerable
            ?? throw new InvalidDataException("The native page worksheet collection is unavailable.");
        var matches = sheets.Cast<object>().Where(sheet => NativeV11Reflection.Get(sheet, "Tag") is object tag &&
            ReferenceEquals(NativeV11Reflection.Get(tag, "PageInfo"), model) && NativeV11Reflection.Get(sheet, "Loaded") is true).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The native page worksheet identity is absent or ambiguous.");
        return matches[0];
    }

    internal bool SourceMatches(NativeV11Session session)
    {
        VerifyMemory(session);
        var source = NativeV11PendingSave.ReadFiles(session.SourcePath, Paths);
        var staged = NativeV11PendingSave.ReadFiles(session.LastPath, Paths);
        foreach (var entry in entries)
        {
            if (source[entry.Primary] is not byte[] a || staged[entry.Primary] is not byte[] b ||
                source[entry.Redundant] is not byte[] c || staged[entry.Redundant] is not byte[] d || !MatchesFrozen(entry.Cache, entry.Rd, a, b, c, d)) return false;
        }
        return true;
    }

    internal static bool MatchesFrozen(JsonElement cache, JsonElement rd, byte[] source, byte[] stage, byte[] sourceRd, byte[] stageRd) =>
        V11Safety.Equivalent(cache, V11Safety.Body(source)) && V11Safety.Equivalent(cache, V11Safety.Body(stage)) &&
        V11Safety.Equivalent(rd, V11Safety.Body(sourceRd)) && V11Safety.Equivalent(rd, V11Safety.Body(stageRd));
}
