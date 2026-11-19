using System.Security.Cryptography;

namespace ForguncyDesignerMcp.Adapters.V12;

// Keep the source archive open without write/delete sharing throughout the
// native model/source verification. A later independent hash is not a save receipt.
internal static class StableNativeArchiveSave
{
    internal static (bool Verified,string? Sha256) Verify(string path,Func<bool> contextStable,Func<bool> verifySource)
    {
        if(!Path.GetExtension(path).Equals(".fgcc",StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return (false,null);
        try
        {
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(!contextStable()) return (false,null);
            var first=Convert.ToHexString(SHA256.HashData(input));
            if(!verifySource() || !contextStable()) return (false,null);
            input.Position=0;
            var second=Convert.ToHexString(SHA256.HashData(input));
            return first==second && contextStable() ? (true,first) : (false,null);
        }
        catch(IOException) { return (false,null); }
        catch(UnauthorizedAccessException) { return (false,null); }
    }
}
