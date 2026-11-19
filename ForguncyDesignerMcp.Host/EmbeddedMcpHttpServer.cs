using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForguncyDesignerMcp.Host;

public sealed class EmbeddedMcpHttpOptions
{
    public int MaxSessions { get; set; } = 32;
    public int MaxConcurrentRequests { get; set; } = 64;
    public int MaxBodyBytes { get; set; } = PipeFrames.MaximumBytes;
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class EmbeddedMcpHttpServer : IAsyncDisposable
{
    private sealed class Session(StdioMcpServer server) { public StdioMcpServer Server=server; public long Last=DateTime.UtcNow.Ticks; public int Active; public string? Protocol; }
    private readonly object sessionLock=new();
    private int reservations;
    private readonly Func<CancellationToken,Task<StdioMcpServer>> factory;
    private readonly EmbeddedMcpHttpOptions options;
    private readonly ConcurrentDictionary<string,Session> sessions=new(StringComparer.Ordinal);
    private WebApplication? app;
    private SemaphoreSlim? concurrency;
    private byte[]? tokenHash;
    public Uri? Address { get; private set; }
    public int? Port => Address?.Port;
    public EmbeddedMcpHttpServer(Func<CancellationToken,Task<StdioMcpServer>> sessionFactory,EmbeddedMcpHttpOptions options)
    { factory=sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory)); this.options=options ?? throw new ArgumentNullException(nameof(options)); }
    public async Task StartAsync(int port,string bearerToken,CancellationToken cancellationToken=default)
    {
        if(app is not null) throw new InvalidOperationException("Already started.");
        if(port is < 0 or > 65535 || string.IsNullOrWhiteSpace(bearerToken) || Encoding.UTF8.GetByteCount(bearerToken)<32 || options.MaxSessions is < 1 or > 1024 || options.MaxConcurrentRequests is < 1 or > 1024 || options.MaxBodyBytes is < 1 or > 16777216 || options.SessionIdleTimeout <= TimeSpan.Zero) throw new ArgumentException("Invalid HTTP settings.");
        tokenHash=SHA256.HashData(Encoding.UTF8.GetBytes(bearerToken)); concurrency=new(options.MaxConcurrentRequests);
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName=typeof(EmbeddedMcpHttpServer).Assembly.GetName().Name, Args=[] }); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k=>{ k.Listen(IPAddress.Loopback,port); k.Limits.MaxRequestBodySize=options.MaxBodyBytes; k.Limits.MaxRequestHeadersTotalSize=16384; });
        var instance=builder.Build(); instance.Run(HandleAsync); app=instance;
        try { await instance.StartAsync(cancellationToken); Address=new Uri(instance.Urls.Single()+"/mcp"); }
        catch { app=null; await instance.DisposeAsync(); throw; }
    }
    private async Task HandleAsync(HttpContext ctx)
    {
        if(ctx.Request.Path!="/mcp") { ctx.Response.StatusCode=404; return; }
        var host=ctx.Request.Host.Host;
        if(host is not ("localhost" or "127.0.0.1") || ctx.Request.Host.Port!=Port) { ctx.Response.StatusCode=403; return; }
        if(ctx.Request.Headers.TryGetValue("Origin",out var origin) && (origin.Count!=1 || !Uri.TryCreate(origin.ToString(),UriKind.Absolute,out var uri) || uri.Scheme!="http" || uri.Host is not ("localhost" or "127.0.0.1") || uri.Port!=Port || uri.UserInfo.Length!=0 || uri.AbsolutePath!="/" || uri.Query.Length!=0 || uri.Fragment.Length!=0)) { ctx.Response.StatusCode=403; return; }
        var auth=ctx.Request.Headers.Authorization.ToString();
        if(!auth.StartsWith("Bearer ",StringComparison.Ordinal) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(auth[7..])),tokenHash!)) { ctx.Response.StatusCode=401; return; }
        if(ctx.Request.Method=="GET") { ctx.Response.StatusCode=405; return; }
        var id=ctx.Request.Headers["Mcp-Session-Id"].ToString();
        if(ctx.Request.Method=="DELETE") { lock(sessionLock) { if(!sessions.TryGetValue(id,out var old)) ctx.Response.StatusCode=404; else if(ctx.Request.Headers["MCP-Protocol-Version"].Count>0 && ctx.Request.Headers["MCP-Protocol-Version"].ToString()!=old.Protocol) ctx.Response.StatusCode=400; else if(old.Active!=0) ctx.Response.StatusCode=409; else { sessions.TryRemove(id,out _); ctx.Response.StatusCode=204; } } return; }
        if(ctx.Request.Method!="POST") { ctx.Response.StatusCode=405; return; }
        if(!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(ctx.Request.ContentType,out var contentType) || !string.Equals(contentType.MediaType,"application/json",StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode=415; return; }
        var accepts=ctx.Request.Headers.Accept.ToString().Split(',');
        if(!accepts.Any(item=>System.Net.Http.Headers.MediaTypeWithQualityHeaderValue.TryParse(item.Trim(),out var parsed) && string.Equals(parsed.MediaType,"application/json",StringComparison.OrdinalIgnoreCase) && parsed.Quality!=0)) { ctx.Response.StatusCode=406; return; }
        if(!await concurrency!.WaitAsync(0)) { ctx.Response.StatusCode=429; return; }
        try
        {
            using var reader=new StreamReader(ctx.Request.Body,Encoding.UTF8); var text=await reader.ReadToEndAsync();
            if(Encoding.UTF8.GetByteCount(text)>options.MaxBodyBytes) { ctx.Response.StatusCode=413; return; }
            JsonElement root; try { using var doc=JsonDocument.Parse(text); root=doc.RootElement.Clone(); } catch(JsonException) { ctx.Response.StatusCode=400; return; }
            lock(sessionLock) foreach(var pair in sessions) if(pair.Value.Active==0 && DateTime.UtcNow.Ticks-pair.Value.Last>options.SessionIdleTimeout.Ticks) sessions.TryRemove(pair.Key,out _);
            Session session;
            var created=false;
            if(string.IsNullOrEmpty(id))
            {
                if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("method",out var method) || method.ValueKind!=JsonValueKind.String || method.GetString()!="initialize" || !root.TryGetProperty("id",out _)) { ctx.Response.StatusCode=400; return; }
                lock(sessionLock) { if(sessions.Count+reservations>=options.MaxSessions) { ctx.Response.StatusCode=503; return; } reservations++; }
                try {
                    session=new(await factory(CancellationToken.None)); id=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    lock(sessionLock) { session.Active=1; if(!sessions.TryAdd(id,session)) { ctx.Response.StatusCode=503; return; } }
                } finally { lock(sessionLock) reservations--; }
                created=true;
            }
            else lock(sessionLock) { if(!sessions.TryGetValue(id,out session!)) { ctx.Response.StatusCode=404; return; } var version=ctx.Request.Headers["MCP-Protocol-Version"].ToString(); if(version.Length>0 && version!=session.Protocol) { ctx.Response.StatusCode=400; return; } session.Active++; }
            try
            {
                var response=await session.Server.DispatchAsync(text);
                if(created)
                {
                    using var init=JsonDocument.Parse(response ?? "null");
                    if(!init.RootElement.TryGetProperty("result",out var result) || !result.TryGetProperty("protocolVersion",out var version) || version.GetString() is not { Length:>0 } protocol)
                    { lock(sessionLock) sessions.TryRemove(id,out _); ctx.Response.StatusCode=400; return; }
                    session.Protocol=protocol; ctx.Response.Headers["Mcp-Session-Id"]=id;
                }
                if(response is null) { ctx.Response.StatusCode=202; return; }
                ctx.Response.ContentType="application/json"; await ctx.Response.WriteAsync(response);
            }
            finally { lock(sessionLock) { session.Active--; session.Last=DateTime.UtcNow.Ticks; } }
        }
        finally { concurrency.Release(); }
    }
    public async Task StopAsync(CancellationToken cancellationToken=default)
    {
        var current=app; app=null;
        try {
            if(current is not null)
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try { await current.StopAsync(timeout.Token); }
                finally { await current.DisposeAsync(); }
            }
        } finally { sessions.Clear(); Address=null; }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
