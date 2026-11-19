using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace ForguncyDesignerMcp.Core;

public static class PipeFrames
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > MaximumBytes) throw new BridgeException("InvalidFrame", "通信数据长度无效。");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, token);
        return JsonSerializer.Deserialize<T>(buffer, Wire.Json) ?? throw new BridgeException("InvalidJson", "通信数据为空。");
    }
    public static async Task WriteAsync<T>(Stream stream, T data, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, Wire.Json);
        if (bytes.Length > MaximumBytes) throw new BridgeException("ResponseTooLarge", "结果过大，请按模块名称筛选。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
}

public sealed class BridgeServer(string pipeName, Func<BridgeRequest, Task<BridgeResponse>> handler) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? listener;
    public void Start() => listener = ListenAsync();
    private async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new(pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                _ = ServeAsync(pipe); pipe = null;
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { await Task.Delay(200, stop.Token); }
            finally { pipe?.Dispose(); }
        }
    }
    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var request = await PipeFrames.ReadAsync<BridgeRequest>(pipe, timeout.Token);
                if (request.Method is "native.request" or "native.describe" or "platform.describe" or "platform.invoke") timeout.CancelAfter(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(5)));
                BridgeResponse response;
                try { response = request.ProtocolVersion == Wire.Version ? await handler(request)
                    : BridgeResponse.Failure("ProtocolMismatch", "设计器扩展和 MCP 主机的协议版本不同。"); }
                catch (BridgeException error) { response = BridgeResponse.Failure(error.Code, error.Message); }
                catch (Exception) { response = BridgeResponse.Failure("BridgeError", "设计器桥接操作失败，请检查扩展日志。"); }
                await PipeFrames.WriteAsync(pipe, response, timeout.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or BridgeException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (listener is not null) { try { await listener; } catch (OperationCanceledException) { } }
        stop.Dispose();
    }
}

public static class BridgeClient
{
    public static async Task<BridgeResponse> CallAsync(InstanceInfo instance, string method, object arguments,
        CancellationToken token = default, TimeSpan? requestTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(20));
        await using var pipe = new NamedPipeClientStream(".", instance.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            await PipeFrames.WriteAsync(pipe, new BridgeRequest(Wire.Version, method, Wire.Element(arguments)), timeout.Token);
            return await PipeFrames.ReadAsync<BridgeResponse>(pipe, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new BridgeException("TransportTimeout", "连接或响应超时。写操作可能已排队；请用原 operationId 查询回执，不要创建新 ID 重试。");
        }
    }
}

public static class InstanceRegistry
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForguncyDesignerMcp", "instances");
    public static void Write(string directory, InstanceInfo instance)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, instance.ProcessId + "-" + instance.ProcessStartTicks + ".json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(instance, Wire.Json));
        File.Move(temporary, path, true);
    }
    public static InstanceInfo[] List(string directory, bool includeStale = false)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<InstanceInfo>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(file), Wire.Json);
                if (item is null || item.ProtocolVersion != Wire.Version || (!includeStale && item.Heartbeat < DateTimeOffset.UtcNow.AddSeconds(-20))) continue;
                using var process = Process.GetProcessById(item.ProcessId);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != item.ProcessStartTicks) continue;
                result.Add(item);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return result.OrderBy(x => x.ProcessId).ToArray();
    }
}
