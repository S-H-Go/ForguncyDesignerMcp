using System.Collections;
using System.IO.Compression;

namespace ForguncyDesignerMcp.Adapters.V12;

// Page/command folders are registered as native FolderSaveData; unlike
// report/process static layouts they need not exist in the working cache.
internal static class NativeRegisteredFolderEvidence
{
    internal static bool HasRegistration(object loader,string key)
    {
        key=CanonicalKey(key);
        if(loader is not IDictionary entries) return false;
        var matches=entries.Keys.Cast<object>().OfType<string>()
            .Where(candidate=>candidate.Replace('\\','/').Equals(key,StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length==1 && matches[0].Replace('\\','/')==key && entries[matches[0]]?.GetType().FullName=="Forguncy.SaveLoad.FolderSaveData";
    }

    internal static bool EmptyDirectory(string source,string key,bool required)
    {
        key=CanonicalKey(key); var path=key.TrimEnd('/');
        if(Directory.Exists(source))
        {
            var full=NativeSaveVerification.Under(source,path);
            if(File.Exists(full)) return false;
            return Directory.Exists(full) ? required && !Directory.EnumerateFileSystemEntries(full).Any() : !required;
        }
        if(!File.Exists(source)) return false;
        using var archive=ZipFile.OpenRead(source);
        var related=archive.Entries.Select(entry=>entry.FullName.Replace('\\','/'))
            .Where(name=>name.Equals(path,StringComparison.OrdinalIgnoreCase) || name.StartsWith(key,StringComparison.OrdinalIgnoreCase)).ToArray();
        return required ? related.Length==1 && related[0]==key : related.Length==0;
    }

    internal static bool DirectoryPresence(string source,string key,bool required)
    {
        key=CanonicalKey(key); var path=key.TrimEnd('/');
        if(Directory.Exists(source))
        {
            var full=NativeSaveVerification.Under(source,path);
            return !File.Exists(full) && Directory.Exists(full)==required;
        }
        if(!File.Exists(source)) return false;
        using var archive=ZipFile.OpenRead(source);
        var related=archive.Entries.Select(entry=>entry.FullName.Replace('\\','/'))
            .Where(name=>name.Equals(path,StringComparison.OrdinalIgnoreCase) || name.StartsWith(key,StringComparison.OrdinalIgnoreCase)).ToArray();
        return required ? related.Length>0 && related.All(name=>name.StartsWith(key,StringComparison.Ordinal)) && related.Distinct(StringComparer.OrdinalIgnoreCase).Count()==related.Length : related.Length==0;
    }

    internal static bool TreeMatches(string source,string key,IEnumerable<string> directories,IEnumerable<string> files)
    {
        key=CanonicalKey(key);
        var expected=directories.Select(value=>CanonicalKey(value)).Concat(files.Select(value=>value.Replace('\\','/'))).Order(StringComparer.Ordinal).ToArray();
        if(expected.GroupBy(value=>value,StringComparer.OrdinalIgnoreCase).Any(group=>group.Count()!=1)) return false;
        string[] actual;
        if(Directory.Exists(source))
        {
            var root=NativeSaveVerification.Under(source,key.TrimEnd('/'));
            if(!Directory.Exists(root)) return false;
            var values=new List<string>{key};
            values.AddRange(Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories).Select(path=>Path.GetRelativePath(source,path).Replace('\\','/')+"/"));
            values.AddRange(Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Select(path=>Path.GetRelativePath(source,path).Replace('\\','/')));
            actual=values.Order(StringComparer.Ordinal).ToArray();
        }
        else
        {
            if(!File.Exists(source)) return false;
            using var archive=ZipFile.OpenRead(source);
            var entries=archive.Entries.Select(entry=>entry.FullName.Replace('\\','/')).Where(path=>path.Equals(key.TrimEnd('/'),StringComparison.OrdinalIgnoreCase) || path.StartsWith(key,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(entries.Any(path=>!path.StartsWith(key,StringComparison.Ordinal)) || entries.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=entries.Length) return false;
            var values=new HashSet<string>(entries,StringComparer.Ordinal);
            foreach(var path in entries)
            {
                var index=path.EndsWith('/')?path.Length-2:path.Length-1;
                while(index>=key.Length-1 && (index=path.LastIndexOf('/',index))>=key.Length-1) { values.Add(path[..(index+1)]); index--; }
            }
            actual=values.Order(StringComparer.Ordinal).ToArray();
        }
        return expected.SequenceEqual(actual,StringComparer.Ordinal);
    }

    internal static string CanonicalKey(string value)
    {
        var normalized=value.Replace('\\','/');
        if(!normalized.EndsWith('/')) throw new InvalidOperationException("A native registered folder key must end in '/'.");
        return NativeSaveVerification.Relative(".",normalized.TrimEnd('/'))+"/";
    }
}
