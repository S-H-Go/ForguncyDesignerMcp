using System.Data.Common;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed partial class NativeRegisteredFolders
{
    private sealed class SqliteReadCollector
    {
        internal readonly HashSet<string> Names=new(StringComparer.OrdinalIgnoreCase);
        // SQLite authorizer runs while the statement is prepared, not while a
        // view or any user function is executed. SQLITE_READ is 20; SQLITE_OK is 0.
        public int OnAuthorize(object? context,int action,string? table,string? column,string? database,string? source)
        { if(action==20 && table is not null && database=="main") Names.Add(table); return 0; }
    }

    private void RequireSqliteViewsContained(IReadOnlyList<object> deleting)
    {
        if(deleting.Count==0) return;
        var selected=deleting.Select(model=>(string)R.Get(model,"Name")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dbPath=NativeSaveVerification.Under(Local,"ForguncyDB.sqlite3");
        var configPath=(string)R.Static(assembly.GetType("Forguncy.Common.Config",true)!,"get_LocalAppDBSqliteFilePath")!;
        if(!Path.GetFullPath(configPath).Equals(Path.GetFullPath(dbPath),StringComparison.OrdinalIgnoreCase) || !File.Exists(dbPath))
            throw new BridgeException("NativeFolderReferenceUnavailable","The native internal SQLite database is not bound to this project cache.");
        using var connection=AccountAuthoritySnapshot.OpenSqlite(dbPath,true);
        var views=new List<string>();
        using(var command=connection.CreateCommand())
        {
            command.CommandText="SELECT name FROM sqlite_master WHERE type='view' AND name NOT LIKE 'sqlite_%'";
            using var reader=command.ExecuteReader(); while(reader.Read()) views.Add(reader.GetString(0));
        }
        var core=Assembly.Load("SQLitePCLRaw.core"); var raw=core.GetType("SQLitePCL.raw",true)!; var delegateType=core.GetType("SQLitePCL.strdelegate_authorizer",true)!;
        var register=raw.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(method=>method.Name=="sqlite3_set_authorizer" && method.GetParameters().Length==3 && method.GetParameters()[1].ParameterType==delegateType);
        var handle=connection.GetType().GetProperty("Handle",BindingFlags.Public | BindingFlags.Instance)?.GetValue(connection)
            ?? throw new BridgeException("NativeFolderReferenceUnavailable","The internal SQLite connection handle is unavailable.");
        var collector=new SqliteReadCollector(); var callback=Delegate.CreateDelegate(delegateType,collector,typeof(SqliteReadCollector).GetMethod(nameof(SqliteReadCollector.OnAuthorize))!);
        try
        {
            if(Convert.ToInt32(register.Invoke(null,[handle,callback,null]))!=0) throw new BridgeException("NativeFolderReferenceUnavailable","SQLite rejected the read-only view dependency scanner.");
            foreach(var view in views.Where(name=>!selected.Contains(name)))
            {
                collector.Names.Clear();
                using var query=connection.CreateCommand(); query.CommandText="SELECT * FROM \""+view.Replace("\"","\"\"")+"\" LIMIT 0";
                try { query.Prepare(); }
                catch(DbException error) { throw new BridgeException("NativeFolderReferenceUnavailable","A SQLite view could not be inspected: "+view+" ("+error.GetType().Name+")."); }
                if(collector.Names.Overlaps(selected)) throw new BridgeException("NativeFolderExternalReference","A SQLite view outside the selected folder depends on a selected table or view: "+view);
            }
        }
        finally { register.Invoke(null,[handle,null,null]); GC.KeepAlive(callback); }
    }
}
