using System.Net.Sockets;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Host;
using Microsoft.AspNetCore.Connections;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForguncyDesignerMcp.Designer;

internal sealed class EmbeddedMcpController : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string settingsPath;
    private EmbeddedMcpHttpServer? server;
    private InstanceInfo? instance;
    private bool disposed;
    private bool stopUnconfirmed;
    private CancellationTokenSource startup = new();
    private byte[]? damagedSettings;
    internal EmbeddedMcpSettings Settings { get; private set; }
    internal Uri? Address => server?.Address;
    internal int? Port => server?.Port;
    internal string? Error { get; private set; }
    internal string? SettingsError { get; private set; }
    internal string? ProjectPath => Volatile.Read(ref instance)?.ProjectPath;
    internal string TrayText
    {
        get
        {
            var current = Volatile.Read(ref instance);
            var path = current?.ProjectPath;
            var name = string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            if (name?.EndsWith(".fgcc", StringComparison.OrdinalIgnoreCase) == true) name = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrEmpty(name)) name = current?.SessionId is null ? "未打开工程" : "未命名工程";

            const string prefix = "Forguncy MCP · ";
            var portText = stopUnconfirmed ? "端口状态待确认" : Port is int port ? $"端口 {port}" : "服务已停止";
            var suffix = "\n" + portText;
            // NotifyIcon.Text allows 127 UTF-16 units. Reserve the port line even for long names.
            int room = 127 - prefix.Length - suffix.Length;
            if (name.Length > room)
            {
                int cut = 0;
                foreach (int start in System.Globalization.StringInfo.ParseCombiningCharacters(name))
                {
                    if (start > room - 1) break;
                    cut = start;
                }
                name = name[..cut] + "…";
            }
            return prefix + name + suffix;
        }
    }
    internal event Action? Changed;
    internal EmbeddedMcpController(string? path = null)
    {
        settingsPath = path ?? EmbeddedMcpSettings.DefaultPath;
        try { Settings = EmbeddedMcpSettings.Load(settingsPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SettingsError = "设置无法读取，请检查后保存修复；自动启动已关闭。";
            Settings = new(false, EmbeddedMcpSettings.DefaultPort, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            try { using var input = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var bytes = new MemoryStream(); input.CopyTo(bytes); damagedSettings = bytes.ToArray(); }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException) { }
        }
    }
    internal void UpdateInstance(InstanceInfo value) { Volatile.Write(ref instance, value); Changed?.Invoke(); }
    internal void SaveSettings(bool autoStart, int port)
    {
        var next = Settings with { AutoStart = autoStart, PreferredPort = port };
        if (SettingsError is not null)
        {
            if (damagedSettings is null) throw new IOException("无法备份原设置，未执行修复。");
            using var input = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream(); input.CopyTo(bytes);
            if (!bytes.ToArray().AsSpan().SequenceEqual(damagedSettings)) throw new IOException("原设置已变化，请重新打开管理入口后检查。");
            var backup = settingsPath + ".damaged." + Guid.NewGuid().ToString("N") + ".bak";
            using var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(damagedSettings); output.Flush(true);
        }
        EmbeddedMcpSettings.Save(settingsPath, next); Settings = next; SettingsError = null; damagedSettings = null; Changed?.Invoke();
    }
    internal async Task StartAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); if (SettingsError is not null || stopUnconfirmed) throw new InvalidOperationException(SettingsError ?? "上次停止尚未确认，请重新启动设计器后检查。");
            if (server is not null) return; Error = null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, startup.Token);
            var settings = Settings;
            for (var port = settings.PreferredPort; port < settings.PreferredPort + 16; port++)
            {
                var candidate = new EmbeddedMcpHttpServer(ct => EmbeddedMcpSessionFactory.CreateAsync(
                    Volatile.Read(ref instance) ?? throw new InvalidOperationException("当前设计器实例尚未就绪。"), ct), new EmbeddedMcpHttpOptions());
                try { await candidate.StartAsync(port, settings.BearerToken, linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested(); if (disposed || stopUnconfirmed) throw new OperationCanceledException("MCP startup was cancelled."); server = candidate; Changed?.Invoke(); return; }
                catch (Exception error)
                {
                    try { await candidate.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                    catch (TimeoutException) { stopUnconfirmed = true; Error = "启动失败后的释放超时，服务状态未确认。"; Changed?.Invoke(); throw; }
                    if (!AddressInUse(error) || port == settings.PreferredPort + 15) { Error = error.Message; Changed?.Invoke(); throw; }
                }
            }
        }
        finally { gate.Release(); }
    }
    private static bool AddressInUse(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is AddressInUseException || current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) return true;
        return false;
    }
    internal async Task StopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await Task.Run(() => startup.Cancel()).WaitAsync(timeout.Token).ConfigureAwait(false); await gate.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { stopUnconfirmed = true; Error = "停止超时，服务状态未确认；请勿重复启动。"; Changed?.Invoke(); return; }
        try
        {
            var previous = server; server = null;
            if (previous is not null)
            {
                try { await previous.StopAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); await previous.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { stopUnconfirmed = true; Error = "停止超时，服务状态未确认；请勿重复启动。"; }
                catch (Exception) { stopUnconfirmed = true; Error = "停止失败，服务状态未确认；请勿重复启动。"; Changed?.Invoke(); throw; }
            }
            if (!disposed && !stopUnconfirmed) startup = new CancellationTokenSource();
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() { disposed = true; await StopAsync().ConfigureAwait(false); }
}
