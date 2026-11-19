using System.Collections;
using System.Data.Common;
using System.Reflection;
using System.Runtime.Loader;

namespace ForguncyDesignerMcp.Adapters.V11;

internal static class NativeV11SemanticTableReferences
{
    private sealed class ReadCollector
    {
        internal readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
        public int Authorize(object? context, int action, string? table, string? column, string? database, string? source)
        { if (action == 20 && table is not null && database == "main") Names.Add(table); return 0; }
    }

    internal static object[] NativeOwners(Assembly designer, bool newer, object target)
    {
        var referenceBase = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "ServerDesignerCommon").GetType("Forguncy.Common.NameReferenceHelperBase", true)!;
        var ignore = referenceBase.GetProperty("IgnoreDisableCommands", NativeV11Reflection.Flags) ?? throw new MissingMemberException("IgnoreDisableCommands");
        var previous = ignore.GetValue(null);
        try
        {
        ignore.SetValue(null, false);
        var result = new List<object>();
        var locations = (IEnumerable)NativeV11Reflection.Static(designer.GetType(newer ? "th" : "tc", true)!, "FindTableReference", target, false)!;
        foreach (var location in locations.Cast<object>())
        {
            var type = location.GetType();
            while (type.FullName != "GrapeCity.Forguncy.Plugin.LocationIndicator") type = type.BaseType ?? throw new InvalidDataException("Unknown native reference location.");
            var chain = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
            foreach (var segment in (IEnumerable)NativeV11Reflection.Call(location, "Enum")!) chain.Add(segment);
            var owners = ((IEnumerable)NativeV11Reflection.Static(designer.GetType("Forguncy.Common.ReferenceFinder", true)!, "EnumEditedModulesByReferences", chain)!).Cast<object>().ToArray();
            if (owners.Length == 0) throw new InvalidDataException("Native reference has no owner.");
            result.AddRange(owners);
        }
        return result.Distinct().ToArray();
        }
        finally { ignore.SetValue(null, previous); }
    }

    internal static string[] SqliteViewOwners(Assembly designer, string database, string target)
    {
        var loadContext = AssemblyLoadContext.GetLoadContext(designer) ?? throw new InvalidOperationException("Designer load context is unavailable.");
        var sqlite = loadContext.Assemblies.SingleOrDefault(a => a.GetName().Name == "Microsoft.Data.Sqlite")
            ?? loadContext.LoadFromAssemblyName(new AssemblyName("Microsoft.Data.Sqlite"));
        var connectionString = new DbConnectionStringBuilder { ["Data Source"] = database, ["Mode"] = "ReadOnly", ["Pooling"] = false };
        using var connection = (DbConnection)Activator.CreateInstance(sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!, [connectionString.ConnectionString])!;
        connection.Open();
        var views = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='view' AND name NOT LIKE 'sqlite_%'";
            using var reader = command.ExecuteReader(); while (reader.Read()) views.Add(reader.GetString(0));
        }
        var core = loadContext.Assemblies.SingleOrDefault(a => a.GetName().Name == "SQLitePCLRaw.core")
            ?? loadContext.LoadFromAssemblyName(new AssemblyName("SQLitePCLRaw.core"));
        var raw = core.GetType("SQLitePCL.raw", true)!;
        var delegateType = core.GetType("SQLitePCL.strdelegate_authorizer", true)!;
        var register = raw.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "sqlite3_set_authorizer" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == delegateType);
        var handle = connection.GetType().GetProperty("Handle")!.GetValue(connection)!;
        var collector = new ReadCollector();
        var callback = Delegate.CreateDelegate(delegateType, collector, typeof(ReadCollector).GetMethod(nameof(ReadCollector.Authorize))!);
        var result = new List<string>();
        try
        {
            if (Convert.ToInt32(register.Invoke(null, [handle, callback, null])) != 0) throw new InvalidDataException("SQLite authorizer registration failed.");
            foreach (var view in views.Where(name => !name.Equals(target, StringComparison.OrdinalIgnoreCase)))
            {
                collector.Names.Clear();
                using var query = connection.CreateCommand();
                query.CommandText = "SELECT * FROM \"" + view.Replace("\"", "\"\"") + "\" LIMIT 0";
                query.Prepare();
                if (collector.Names.Contains(target)) result.Add(view);
            }
        }
        finally { register.Invoke(null, [handle, null, null]); GC.KeepAlive(callback); }
        return result.ToArray();
    }
}
