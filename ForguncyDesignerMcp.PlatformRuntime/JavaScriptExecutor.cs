using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForguncyDesignerMcp.Core.Platform;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace ForguncyDesignerMcp.PlatformRuntime;

internal sealed class JavaScriptExecutor(RuntimeLimits limits)
{
    private sealed record Pending(int Id, string Api, bool Mutates, Task<JsonElement> Task);

    internal async Task<JsonElement> ExecuteAsync(string code, PlatformRuntimeContext context,
        IPlatformApiInvoker api, CancellationToken cancellationToken, bool readOnly = false)
    {
        if (code.Length > limits.MaxCodeChars) throw new ArgumentException("Code exceeds MaxCodeChars.");
        // Never schedule on the caller's UI synchronization context; Engine access stays serialized.
        return await Task.Run(() => ExecuteCoreAsync(code, context, api, cancellationToken, readOnly), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecuteCoreAsync(string code, PlatformRuntimeContext context,
        IPlatformApiInvoker api, CancellationToken cancellationToken, bool readOnly)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.ExecutionTimeout);
        var token = deadline.Token;
        var pending = new List<Pending>();
        var dispatches = new List<object>();
        var logs = new List<JsonElement>();
        var apiErrors = new List<object>();
        var logChars = 0;
        var nextId = 0;
        var mutationDispatched = false;
        var rejectedCalls = 0;
        string? firstBridgeError = null;
        string? rootValue = null;
        string? rootError = null;
        bool rootFinished = false;
        var catalog = context.Apis.ToDictionary(d => (d.Namespace, d.Method));
        foreach (var d in context.Apis)
            if (!SafeIdentifier(d.Method) || d.Namespace.Split('.').Any(s => !SafeIdentifier(s)))
                throw new ArgumentException($"Unsafe API catalog name: {d.Namespace}.{d.Method}");
        var methodPaths = context.Apis.Select(d => d.Namespace + "." + d.Method).ToHashSet(StringComparer.Ordinal);
        foreach(var d in context.Apis)
            if(methodPaths.Any(p => d.Namespace == p || d.Namespace.StartsWith(p + ".", StringComparison.Ordinal)))
                throw new ArgumentException("An API method cannot also be a namespace prefix.");
        var budget = new TotalBudget(limits.MaxStatements, limits.MemoryBytes, token);
        using var engine = new Engine(o =>
        {
            o.Strict().LimitMemory(limits.MemoryBytes).LimitRecursion(limits.MaxRecursion)
                .DisableStringCompilation().Constraint(budget);
            o.Interop.Enabled = false;
            o.Interop.AllowGetType = false;
            o.Interop.AllowSystemReflection = false;
            o.Interop.AllowWrite = false;
            o.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(100);
            o.Constraints.PromiseTimeout = limits.ExecutionTimeout;
            o.Constraints.MaxArraySize = 1_000_000;
            o.Constraints.StackOverflowGuard = true;
            o.AgentCanSuspend = false;
            o.Json.MaxParseDepth = 64;
        });
        engine.SetValue("__nativeDispatch", new ClrFunction(engine, "dispatch", (_, args) =>
        {
            token.ThrowIfCancellationRequested();
            var ns = PrimitiveString(args, 0);
            var method = PrimitiveString(args, 1);
            var json = PrimitiveString(args, 2);
            if (!catalog.TryGetValue((ns, method), out var descriptor))
                return RejectBridge($"Unregistered API: {ns}.{method}");
            if (readOnly && PlatformToolRuntime.HasSideEffect(descriptor))
                return RejectBridge($"Read-only execution forbids side-effect API: {ns}.{method}");
            if (nextId >= limits.MaxApiCalls || pending.Count >= limits.MaxPendingCalls)
                return RejectBridge("API call budget exceeded.");
            if (json.Length > limits.MaxJsonChars)
                return RejectBridge("API arguments exceed MaxJsonChars.");
            var parameters = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            using (parameters)
            {
                if (parameters.RootElement.ValueKind != JsonValueKind.Object)
                    return RejectBridge("API parameters must be a JSON object.");
                var id = ++nextId;
                // Invoke on worker pool: a misbehaving synchronous callback cannot block the engine's deadline wait.
                var payload = parameters.RootElement.Clone();
                var operationId = PlatformToolRuntime.ReadOperationId(payload);
                object? remoteBaseline = null;
                if (ns == "collaboration" && method == "checkin" &&
                    payload.TryGetProperty("expectedRemoteUrl", out var remoteUrl) && remoteUrl.ValueKind == JsonValueKind.String &&
                    payload.TryGetProperty("expectedBranch", out var branch) && branch.ValueKind == JsonValueKind.String &&
                    payload.TryGetProperty("expectedRemoteHead", out var head) && head.ValueKind == JsonValueKind.String)
                    remoteBaseline = new { url = remoteUrl.GetString(), branch = branch.GetString(), head = head.GetString() };
                var task = Task.Run(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    return (await api.InvokeAsync(ns, method, payload, token).ConfigureAwait(false)).Clone();
                }, token);
                var mutates = PlatformToolRuntime.HasSideEffect(descriptor);
                mutationDispatched |= mutates;
                pending.Add(new Pending(id, ns + "." + method, mutates, task));
                dispatches.Add(new { id, api = ns + "." + method, mutatesProject = descriptor.MutatesProject,
                    scope = descriptor.Scope, mayHaveSideEffects = mutates, operationId,
                    reviewedExpectedUser = ns == "collaboration" && method is "checkout" or "checkin" && payload.TryGetProperty("expectedUser", out var auditedUser) ? auditedUser.Clone() : (JsonElement?)null,
                    reviewedRemoteBaseline = remoteBaseline });
                return JsonSerializer.Serialize(new { id });
            }
        }));
        engine.SetValue("__nativeLog", new ClrFunction(engine, "log", (_, args) =>
        {
            var level = PrimitiveString(args, 0);
            var json = PrimitiveString(args, 1);
            using var doc = JsonDocument.Parse(json);
            var entry = RuntimeJson.Serialize(new { level, values = doc.RootElement });
            var size = entry.GetRawText().Length;
            if (size > limits.MaxLogChars - logChars) throw new RuntimeBudgetException("Log budget exceeded.");
            logChars += size;
            logs.Add(entry);
            return JsValue.Undefined;
        }));
        engine.SetValue("__nativeFinish", new ClrFunction(engine, "finish", (_, args) =>
        {
            rootFinished = true;
            var ok = args[0].AsBoolean();
            var text = PrimitiveString(args, 1);
            if (text.Length > limits.MaxResultChars) throw new RuntimeBudgetException("Result exceeds MaxResultChars.");
            if (ok) rootValue = text; else rootError = text;
            return JsValue.Undefined;
        }));
        try
        {
            var callbacks = budget.Run(() => engine.Evaluate(Bootstrap.Replace("/*CATALOG*/", RuntimeJson.Serialize(context.Apis.Select(d => new
                { ns = d.Namespace, method = d.Method })).GetRawText()))).AsObject();
            var complete = callbacks.Get("complete");
            var run = callbacks.Get("run");
            // Promise rejections become a plain JSON error; the user script is parsed by Jint, never eval().
            var userPromise = budget.Run(() => engine.Evaluate("(async function(){\n\"use strict\";\n" + code + "\n})()"));
            budget.Run(() => engine.Invoke(run, userPromise));
            while (!rootFinished || pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                budget.Run(() => { engine.Advanced.ProcessTasks(); return 0; });
                foreach (var call in pending.Where(p => p.Task.IsCompleted).ToArray())
                {
                    pending.Remove(call);
                    bool ok;
                    string result;
                    try
                    {
                        var value = await call.Task.ConfigureAwait(false);
                        result = value.GetRawText();
                        if (result.Length > limits.MaxJsonChars) throw new RuntimeBudgetException("API result exceeds MaxJsonChars.");
                        ok = true;
                    }
                    catch (Exception e)
                    {
                        var message = BoundedMessage(e.Message);
                        result = RuntimeJson.Serialize(new { code = e is OperationCanceledException ? "ApiCancelled" : "ApiError",
                            message, executionMayContinue = call.Mutates }).GetRawText();
                        ok = false;
                        apiErrors.Add(new { call.Id, call.Api, message, executionMayContinue = call.Mutates });
                    }
                    budget.Run(() => engine.Invoke(complete, call.Id, ok, result));
                }
                if (!rootFinished || pending.Count > 0)
                {
                    if (pending.Count == 0) await Task.Delay(5, token).ConfigureAwait(false);
                    else await Task.WhenAny(Task.WhenAny(pending.Select(p => p.Task)), Task.Delay(5, token)).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            return Final(new { value = ParseJson(rootValue), logs, apiCalls = dispatches, apiErrors,
                bridgeErrors = new { count = rejectedCalls, firstError = firstBridgeError }, error = rootError == null ? (JsonElement?)null : ParseJson(rootError),
                status = rejectedCalls > 0 ? "ApiError" : rootError != null ? "ScriptError" : apiErrors.Count > 0 ? "ApiError" : "Completed",
                executionMayContinue = apiErrors.Count > 0 && mutationDispatched });
        }
        catch (Exception e)
        {
            // Adapter side effects cannot be undone by stopping the JS interpreter. Never auto-retry.
            return Final(new { status = cancellationToken.IsCancellationRequested ? "Cancelled" :
                    deadline.IsCancellationRequested ? "TimedOut" : e is RuntimeBudgetException or MemoryLimitExceededException or RecursionDepthOverflowException ? "LimitExceeded" : "ScriptError",
                error = new { code = e.GetType().Name, message = BoundedMessage(e.Message) }, logs, apiCalls = dispatches,
                executionMayContinue = mutationDispatched && (pending.Any(p => p.Mutates) || token.IsCancellationRequested),
                pendingApiCalls = pending.Select(p => new { p.Id, p.Api, p.Mutates }).ToArray() });
        }

        finally
        {
            deadline.Cancel();
            foreach (var task in pending.Select(p => p.Task))
                _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        string RejectBridge(string message)
        {
            rejectedCalls++;
            firstBridgeError ??= message;
            return RuntimeJson.Serialize(new { error = message }).GetRawText();
        }
        JsonElement Final(object result)
        {
            var serialized = RuntimeJson.Serialize(result);
            return serialized.GetRawText().Length <= limits.MaxResultChars ? serialized : RuntimeJson.Serialize(new
            {
                status = "LimitExceeded", error = new { code = "ResultBudget", message = "Combined execution result exceeds MaxResultChars." },
                executionMayContinue = mutationDispatched && (pending.Any(p => p.Mutates) || apiErrors.Count > 0 || token.IsCancellationRequested),
                dispatchedApiCalls = nextId, mutationDispatched, detailsTruncated = true
            });
        }
    }

    private static string PrimitiveString(JsValue[] args, int index) =>
        index < args.Length && args[index].IsString() ? args[index].AsString() : throw new ArgumentException("Bridge accepts strings only.");
    private static bool SafeIdentifier(string name) => name is not ("__proto__" or "prototype" or "constructor") &&
        Regex.IsMatch(name, "^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static string BoundedMessage(string value) => value.Length <= 4096 ? value : value[..4096] + "…";
    private static JsonElement ParseJson(string? value)
    {
        using var doc = JsonDocument.Parse(value ?? "null");
        return doc.RootElement.Clone();
    }

    private sealed class TotalBudget(int max, long maxMemory, CancellationToken token) : Constraint
    {
        private int count;
        private long allocated;
        private long entryStart;
        private bool active;
        private int entryThread;
        public override void Check()
        {
            token.ThrowIfCancellationRequested();
            if (++count > max) throw new RuntimeBudgetException("Total statement budget exceeded.");
            CheckMemory();
        }
        public override void Reset() { } // Deliberately cumulative across all engine entries.
        public T Run<T>(Func<T> action)
        {
            // Each entry is synchronous. Awaiting adapters happens outside this region, so its
            // allocation delta is measured on one thread even if later entries resume elsewhere.
            if (active) throw new InvalidOperationException("JavaScript engine entries must not nest.");
            CheckMemory();
            entryThread = Environment.CurrentManagedThreadId;
            entryStart = GC.GetAllocatedBytesForCurrentThread();
            active = true;
            var failed = false;
            try { return action(); }
            catch { failed = true; throw; }
            finally
            {
                EnsureEntryThread();
                var delta = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - entryStart);
                allocated = delta > long.MaxValue - allocated ? long.MaxValue : allocated + delta;
                active = false;
                if (!failed) CheckMemory();
            }
        }
        private void CheckMemory()
        {
            if (active) EnsureEntryThread();
            var current = active ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - entryStart) : 0;
            if (allocated > maxMemory || current > maxMemory - allocated)
                throw new RuntimeBudgetException("Cumulative JavaScript allocation budget exceeded.");
        }
        private void EnsureEntryThread()
        {
            if (Environment.CurrentManagedThreadId != entryThread)
                throw new InvalidOperationException("JavaScript engine access changed thread within an entry.");
        }
    }
    private sealed class RuntimeBudgetException(string message) : Exception(message);

    private const string Bootstrap = """
    (() => {
      'use strict';
      const dispatch = globalThis.__nativeDispatch, log = globalThis.__nativeLog, finish = globalThis.__nativeFinish;
      delete globalThis.__nativeDispatch; delete globalThis.__nativeLog; delete globalThis.__nativeFinish;
      const parse = JSON.parse, stringify = JSON.stringify, P = Promise, resolve = Promise.resolve.bind(Promise);
      const then = Function.prototype.call.bind(Promise.prototype.then);
      const pending = new Map(), get = Function.prototype.call.bind(Map.prototype.get),
            set = Function.prototype.call.bind(Map.prototype.set), remove = Function.prototype.call.bind(Map.prototype.delete);
      const freeze = Object.freeze, create = Object.create, define = Object.defineProperty;
      const root = create(null);
      function invoke(ns, method, params) {
        return new P((res, rej) => {
          const response = parse(dispatch(ns, method, stringify(params === undefined ? {} : params)));
          if (response.error) { rej(new Error(response.error)); return; }
          set(pending, response.id, [res, rej]);
        });
      }
      for (const d of /*CATALOG*/) {
        let current = root;
        for (const part of d.ns.split('.')) {
          if (!Object.hasOwn(current, part)) current[part] = create(null);
          current = current[part];
        }
        if (Object.hasOwn(current, d.method)) throw new Error('Conflicting API catalog path');
        current[d.method] = (params) => invoke(d.ns, d.method, params);
      }
      function deepFreeze(o) { for(const k of Object.keys(o)) if(typeof o[k]==='object') deepFreeze(o[k]); freeze(o); }
      deepFreeze(root); define(globalThis, 'api', {value:root, writable:false, configurable:false});
      const logger = create(null);
      for(const level of ['log','info','warn','error','debug']) logger[level] = (...args) => log(level, stringify(args));
      define(globalThis,'console',{value:freeze(logger),writable:false,configurable:false});
      define(globalThis,'assert',{value:(condition,message='Assertion failed')=>{if(!condition)throw new Error(message);},writable:false});
      define(globalThis,'log',{value:logger.log,writable:false});
      function fail(error) {
        let record;
        try { record={name:typeof error?.name==='string'?error.name:'Error',message:String(error?.message??error)}; }
        catch { record={name:'Error',message:'Unserializable thrown value'}; }
        finish(false,stringify(record));
      }
      const run = (promise)=> then(resolve(promise),
        value=>{try{finish(true,stringify(value === undefined ? null : value));}catch(e){fail(e);}}, fail);
      const complete = (id,ok,json) => {
        const pair=get(pending,id); if(!pair)return; remove(pending,id);
        if(ok)pair[0](parse(json)); else {const e=parse(json);const error=new Error(e.message);error.code=e.code;error.executionMayContinue=e.executionMayContinue;pair[1](error);}
      };
      return {run, complete};
    })()
    """;
}
