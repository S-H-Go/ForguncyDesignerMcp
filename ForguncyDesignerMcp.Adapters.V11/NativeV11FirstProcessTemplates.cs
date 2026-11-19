using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Read-only discovery of hidden template pages from the exact reviewed native
// resource bundle. This is one input to the owner plan, not a checkout plan.
internal static class NativeV11FirstProcessTemplates
{
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> CapturePageDirectGroups(IReadOnlyList<NativeV11ProcessInitialization.Owner> owners,
        IReadOnlyDictionary<string, IReadOnlyList<string>> templateReferences)
    {
        var folders = owners.Where(owner => owner.State == "New" && owner.Type == "Folder").ToArray();
        var masters = owners.Where(owner => owner.State == "New" && owner.Type == "MasterPageFolder").ToArray();
        if (folders.Length != 2 || masters.Length != 1 || folders.Any(owner => !owner.Name.StartsWith("Pages/", StringComparison.Ordinal)) ||
            !masters[0].Name.StartsWith("MasterPages/", StringComparison.Ordinal))
            throw new InvalidDataException("The native BPM page folder owners are incomplete or ambiguous.");
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var folder in folders.Concat(masters))
        {
            var slash = folder.Name.IndexOf('/');
            NativeV11Session.ValidateAddress(new(ModuleKind.Folder, folder.Name[(slash + 1)..], folder.Name[..slash]));
            result.Add(folder.Name, [folder.Name.TrimEnd('/') + "/"]);
        }
        foreach (var owner in owners.Where(owner => owner.State == "New" && owner.Type is "PCPage" or "MobilePage" or "MasterPCPage" or "MasterMobilePage"))
        {
            var slash = owner.Name.IndexOf('/');
            if (slash < 0 || slash == owner.Name.Length - 1) throw new InvalidDataException("A native BPM page owner identity is invalid.");
            var name = owner.Name[(slash + 1)..];
            NativeV11Session.ValidateAddress(new(ModuleKind.Page, name));
            var folder = owner.Type switch { "PCPage" => folders[0].Name, "MobilePage" => folders[1].Name, _ => masters[0].Name };
            var primary = folder.TrimEnd('/') + "/" + name;
            var files = new List<string> { primary + ".json", primary + ".rd" };
            if (owner.Type is "PCPage" or "MobilePage")
            {
                if (!templateReferences.TryGetValue(owner.Name, out var references)) throw new InvalidDataException("A native BPM page has no template reference projection.");
                files.AddRange(references);
            }
            result.Add(owner.Name, files.Order(StringComparer.Ordinal).ToArray());
        }
        if (result.Count != 25) throw new InvalidDataException("The native BPM page direct groups are incomplete.");
        return result;
    }

    // Read only after the native initializer has completed. The actual page
    // model, loaded worksheet and native file provider must agree with every
    // predispatch ancillary reference; no extra TemplatePage lock is invented.
    internal static IReadOnlyDictionary<string, object> VerifyActual(NativeV11Session session,
        NativeV11ProcessInitialization.Candidate candidate)
    {
        var designer = session.Context.DesignerAssembly;
        var factory = designer.GetType("Forguncy.Common.SimpleModule", true)!;
        var extension = designer.GetType("Forguncy.Model.Pages.PageExtensions", true)!;
        var pageBase = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Pages.PageBase", true)!;
        var provider = extension.GetMethod("GetReferFiles", NativeV11Reflection.Flags, null, [pageBase], null)
            ?? throw new InvalidDataException("The native page file provider is unavailable.");
        var sheets = (NativeV11Reflection.Get(session.Workbook, "Sheets") as IEnumerable)?.Cast<object>().ToArray()
            ?? throw new InvalidDataException("The native workbook sheets are unavailable.");
        JsonElement Body(object model)
        {
            var matches = sheets.Where(sheet => NativeV11Reflection.Get(sheet, "Tag") is object tag &&
                ReferenceEquals(NativeV11Reflection.Get(tag, "PageInfo"), model)).ToArray();
            if (matches.Length != 1 || NativeV11Reflection.Get(matches[0], "Loaded") is not true)
                throw new InvalidDataException("A native BPM page worksheet is not loaded or is ambiguous.");
            var data = NativeV11Reflection.Static(session.Generator, "GetPageSaveData", matches[0])!;
            return V11Safety.Body(Encoding.UTF8.GetBytes(session.SerializeSaveData(data)));
        }
        string[] References(object model) => ((IEnumerable)provider.Invoke(null, [model])!).Cast<string>()
            .Select(path => path.Replace('\\', '/')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var pair in candidate.TemplateReferences)
        {
            var models = session.Items(ModuleKind.Page).Where(item =>
            {
                var simple = NativeV11Reflection.Static(factory, "Create", item.Model)!;
                return NativeV11Reflection.Get(simple, "ModuleName") as string == pair.Key;
            }).ToArray();
            if (models.Length != 1) throw new InvalidDataException("A generated native BPM page owner is missing or ambiguous.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            void Walk(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                    foreach (var property in value.EnumerateObject())
                    {
                        if (property.NameEquals("TemplatePageName") && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } name) names.Add(name);
                        Walk(property.Value);
                    }
                else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Walk(child);
            }
            Walk(Body(models[0].Model));
            var actual = names.SelectMany(name => new[] { "Pages/" + name + ".json", "Pages/" + name + ".rd" }).Order(StringComparer.Ordinal);
            if (!actual.SequenceEqual(pair.Value.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new InvalidDataException("A generated native BPM page's template references differ from its predispatch candidate.");
            var expectedPageFiles = new[] { models[0].RelativePath, Path.ChangeExtension(models[0].RelativePath, ".rd").Replace('\\', '/') };
            if (!candidate.PageDirectGroups.TryGetValue(pair.Key, out var predicted) ||
                expectedPageFiles.Any(path => !predicted.Contains(path, StringComparer.Ordinal)) ||
                !References(models[0].Model).SequenceEqual(predicted.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new InvalidDataException("A generated native BPM page's direct file provider differs from its reviewed group.");
        }
        foreach (var owner in candidate.Owners.Where(owner => owner.State == "New" && owner.Type is "MasterPCPage" or "MasterMobilePage"))
        {
            var models = session.Items(ModuleKind.MasterPage).Where(item =>
            {
                var simple = NativeV11Reflection.Static(factory, "Create", item.Model)!;
                return NativeV11Reflection.Get(simple, "ModuleName") as string == owner.Name &&
                    NativeV11Reflection.Get(simple, "ModuleType")?.ToString() == owner.Type;
            }).ToArray();
            if (models.Length != 1 || !candidate.PageDirectGroups.TryGetValue(owner.Name, out var predicted))
                throw new InvalidDataException("A generated native BPM master-page owner is missing or ambiguous.");
            _ = Body(models[0].Model);
            var expected = new[] { models[0].RelativePath, Path.ChangeExtension(models[0].RelativePath, ".rd").Replace('\\', '/') }.Order(StringComparer.Ordinal);
            if (!References(models[0].Model).SequenceEqual(expected, StringComparer.Ordinal) ||
                !predicted.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
                throw new InvalidDataException("A generated native BPM master-page provider differs from its reviewed group.");
        }
        var route = session.Route(ModuleKind.Page);
        var templates = (NativeV11Reflection.Get(route.Colleague, "TemplatePages") as IEnumerable)?.Cast<object>().ToArray()
            ?? throw new InvalidDataException("Native template-page registrations are unavailable.");
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var primary in candidate.TemplatePaths.Where(path => path.EndsWith(".json", StringComparison.Ordinal)))
        {
            var name = Path.GetFileNameWithoutExtension(primary);
            var matching = templates.Where(model => NativeV11Reflection.Get(model, "Name") as string == name).ToArray();
            if (matching.Length != 1) throw new InvalidDataException("A generated native BPM template model is missing or ambiguous.");
            _ = Body(matching[0]);
            var expected = new[] { primary, Path.ChangeExtension(primary, ".rd").Replace('\\', '/') }.Order(StringComparer.Ordinal);
            if (!References(matching[0]).SequenceEqual(expected, StringComparer.Ordinal))
                throw new InvalidDataException("A native BPM template file provider differs from the reviewed group.");
            result.Add(primary, matching[0]);
        }
        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> CaptureByOwner(Assembly designer,
        IReadOnlyList<NativeV11ProcessInitialization.Owner> owners, string? resources = null)
    {
        resources ??= Path.Combine(Directory.GetParent(Path.GetDirectoryName(designer.Location)!)!.FullName, "DesignerResources", "BuiltInPages");
        var all = Capture(designer, resources).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (folder, type) in new[] { ("BPMPages", "PCPage"), ("BPMPages_Mobile", "MobilePage") })
        {
            var files = Directory.GetFiles(Path.Combine(resources, folder), "FGC_PageName_BPM_*.json")
                .Where(path => !Path.GetFileName(path).Contains("MasterPage", StringComparison.Ordinal))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
            var pageOwners = owners.Where(owner => owner.Type == type && owner.State == "New").ToArray();
            if (files.Length != 10 || pageOwners.Length != files.Length) throw new InvalidDataException("The native BPM page owner/template sequence differs from the reviewed bundle.");
            for (var i = 0; i < files.Length; i++)
            {
                var references = new HashSet<string>(StringComparer.Ordinal);
                void Walk(JsonElement element)
                {
                    if (element.ValueKind == JsonValueKind.Object)
                        foreach (var property in element.EnumerateObject())
                        {
                            if (property.NameEquals("TemplatePageName") && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } name)
                            {
                                var json = "Pages/" + name + ".json"; var rd = "Pages/" + name + ".rd";
                                if (!all.Contains(json) || !all.Contains(rd)) throw new InvalidDataException("A BPM page references an unreviewed native template.");
                                references.Add(json); references.Add(rd);
                            }
                            Walk(property.Value);
                        }
                    else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Walk(child);
                }
                Walk(V11Safety.Body(File.ReadAllBytes(files[i])));
                result.Add(pageOwners[i].Name, references.Order(StringComparer.Ordinal).ToArray());
            }
        }
        if (!result.Values.SelectMany(paths => paths).ToHashSet(StringComparer.Ordinal).SetEquals(all))
            throw new InvalidDataException("The native BPM template reference projection is incomplete.");
        if (!Capture(designer, resources).ToHashSet(StringComparer.Ordinal).SetEquals(all))
            throw new InvalidDataException("The native BPM template bundle changed during projection.");
        return result;
    }

    internal static IReadOnlyList<string> Capture(Assembly designer, string? resources = null)
    {
        var profile = V11BuildProfile.Resolve(designer);
        resources ??= Path.Combine(Directory.GetParent(Path.GetDirectoryName(designer.Location)!)!.FullName, "DesignerResources", "BuiltInPages");
        var files = new[] { "BPMPages", "BPMPages_Mobile" }.SelectMany(folder =>
            Directory.GetFiles(Path.Combine(resources, folder), "*.json").Select(path => (Key: folder + "/" + Path.GetFileName(path), Path: path)))
            .OrderBy(file => file.Key, StringComparer.Ordinal).ToArray();
        using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file.Path);
            manifest.AppendData(Encoding.UTF8.GetBytes(file.Key + "\n" + Convert.ToHexString(SHA256.HashData(bytes)) + "\n"));
            documents.Add(file.Key, V11Safety.Body(bytes));
        }
        var expected = profile.Version == "11.0.104.0" ? "4EE9695F32D8644ED743C3BED86A210B8332E72DFD126071AEC9B5031B8AC37D" :
            "543515420A4ECA7C0244EFE52AD2BA640D8EE2147978C3C6D47C40CFD7C06718";
        if (files.Length != 31 || Convert.ToHexString(manifest.GetHashAndReset()) != expected)
            throw new InvalidDataException("The native BPM page template bundle differs from the reviewed build.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement element, string folder)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("TemplatePageName") && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } name)
                    {
                        if (name.Length != 31 || name.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) || !documents.ContainsKey(folder + "/" + name + ".json"))
                            throw new InvalidDataException("A native BPM template reference has no matching reviewed template.");
                        found.Add(name);
                    }
                    Walk(property.Value, folder);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Walk(item, folder);
        }
        foreach (var pair in documents) Walk(pair.Value, pair.Key[..pair.Key.IndexOf('/')]);
        return found.Order(StringComparer.Ordinal).SelectMany(name => new[] { "Pages/" + name + ".json", "Pages/" + name + ".rd" }).ToArray();
    }
}
