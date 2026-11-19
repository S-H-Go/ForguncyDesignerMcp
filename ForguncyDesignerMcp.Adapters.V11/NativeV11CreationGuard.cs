using System.Collections;
using System.IO;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// An absent live tree node is insufficient: an orphaned native save entry or
// file still owns its path. Creation must never adopt/overwrite that content.
internal static class NativeV11CreationGuard
{
    internal static void Verify(NativeV11Session session, ModuleAddress address)
    {
        session.VerifyContext();
        VerifyCore(session.Route(address.Kind == ModuleKind.View ? ModuleKind.Table : address.Kind).Loader,
            session.SourcePath, session.LocalPath, session.LastPath,
            NativeV11PendingSave.PathsForAddress(session, address));
    }

    internal static void VerifyCore(object loader, string source, string local, string last, IReadOnlyList<string> paths)
    {
        if (loader is not IDictionary registry || paths.Count == 0)
            throw new InvalidDataException("The native creation save registry or source file group is unavailable.");
        var names = paths.Select(path => path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        VerifyKeys(registry, names);
        var memento = NativeV11Reflection.Get(loader, "Memento")
            ?? throw new InvalidDataException("The native creation memento is unavailable.");
        var accessor = NativeV11Reflection.Method(loader.GetType(), "GetSaveData", memento);
        var body = accessor.GetMethodBody()?.GetILAsByteArray();
        // Exact pinned V11 overrides are pure: ldarg.1; ldfld <slot>; ret.
        // GetValidateSaveData would allocate/change a slot and is never used.
        if (accessor.DeclaringType != loader.GetType() || accessor.IsStatic || body is not { Length: 7 } ||
            body[0] != 0x03 || body[1] != 0x7B || body[6] != 0x2A ||
            accessor.Module.ResolveField(BitConverter.ToInt32(body, 2)) is not FieldInfo slotField ||
            slotField.DeclaringType != memento.GetType() || slotField.FieldType != accessor.ReturnType)
            throw new InvalidDataException("The native readonly creation-memento accessor differs from the pinned V11 contract.");
        if (accessor.Invoke(loader, [memento]) is object slot)
        {
            var pending = slot.GetType().GetField("SaveDataItems", NativeV11Reflection.Flags)?.GetValue(slot) as IDictionary
                ?? throw new InvalidDataException("The native creation-memento registry is unavailable.");
            VerifyKeys(pending, names);
        }
        VerifySourceGroupAbsent(source, local, last, paths);
    }

    internal static void VerifySourceGroupAbsent(string source, string local, string last, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new InvalidDataException("The native creation source file group is empty.");
        if (NativeV11PendingSave.ReadFiles(source, paths).Values.Any(bytes => bytes is not null))
            throw Occupied();
        // A directory at a prospective filename is also occupied, including
        // an implicit directory in FGCC. Never treat it as an absent file.
        if (NativeV11PendingSave.ReadFiles(source, paths.Select(path => path + "/")).Values.Any(bytes => bytes is not null))
            throw Occupied();
        foreach (var root in new[] { local, last })
            foreach (var path in paths)
            {
                var target = V11Safety.Under(root, path);
                if (File.Exists(target) || Directory.Exists(target)) throw Occupied();
            }
    }

    private static void VerifyKeys(IDictionary registry, HashSet<string> names)
    {
        foreach (var key in registry.Keys)
        {
            if (key is not string name)
                throw new InvalidDataException("The native save registry contains an unknown path identity.");
            if (names.Contains(name.Replace('\\', '/').TrimEnd('/')))
                throw Occupied();
        }
    }

    private static BridgeException Occupied() => new("ModulePathOccupied",
        "新增模块的原生保存路径仍有登记或文件，请先核对已有定义，不能覆盖创建。");
}
