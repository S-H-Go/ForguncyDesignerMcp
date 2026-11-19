using System.Text.Json;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Host;

internal static class ToolCatalog
{
    private static object String(string description) => new { type = "string", description };
    private static object Schema(Dictionary<string, object> properties, string[] required) => new { type = "object", properties, required, additionalProperties = false };
    private static Dictionary<string, object> Instance() => new() { ["instanceId"] = String("designer.list_instances 返回的确切实例 ID") };
    private static object Tool(string name, string description, object schema, bool readOnly = true, bool remote = false) => new
    {
        name, description, inputSchema = schema,
        annotations = new { readOnlyHint = readOnly, destructiveHint = !readOnly && remote, idempotentHint = true, openWorldHint = remote }
    };
    public static object[] All()
    {
        var list = new List<object> {
            Tool("designer.list_instances", "发现当前用户下已启动桥接服务的活字格设计器，不启动或切换设计器。", Schema(new(), [])),
            Tool("designer.get_context", "读取当前工程、文档会话、协同身份、保存状态及操作基线。操作前必须核对确切工程。", Schema(Instance(), ["instanceId"])),
            new { name = "get_designer_runtime_info", description = "只读核验确切会话的进程、加载组件与原生 MCP 设置，不返回环境值或账户信息。", inputSchema = Schema(new(Instance()) { ["sessionId"] = String("当前会话"), ["projectPath"] = String("当前工程完整路径"), ["designerVersion"] = String("精确设计器版本") }, ["instanceId", "sessionId", "projectPath", "designerVersion"]), outputSchema = DesignerRuntimeInfoSchema.Output(), annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false } },
            Tool("designer.list_capabilities", "读取扩展实际开放的功能及验证层级；不代表所有设计器操作均已支持。", Schema(Instance(), ["instanceId"])),
            Tool("designer.get_operation", "按原 operationId 查询操作回执。超时不等于失败，不要用新 ID 重试签入。", Schema(new(Instance()) { ["operationId"] = String("提交时的 UUID") }, ["instanceId", "operationId"])),
            Tool("collaboration.list_modules", "读取设计器内存的模块状态并与磁盘锁比较；可按确切模块名称筛选，不执行刷新或同步。", Schema(new(Instance()) {
                ["modules"] = new { type = "array", items = new { type = "string" }, maxItems = 100 } }, ["instanceId"]))
        };
        foreach (var method in new[] { "collaboration.refresh", "collaboration.checkout", "collaboration.checkin" })
        {
            var properties = Instance();
            properties["operationId"] = String("新请求使用 UUID；同一请求重试必须复用此 ID 和完全相同的参数");
            properties["sessionId"] = String("get_context 返回的当前文档会话");
            properties["projectPath"] = String("目标工程完整路径，必须与当前实例一致");
            properties["modules"] = new { type = "array", items = new { type = "string" }, minItems = method.EndsWith("refresh") ? 0 : 1, maxItems = 100, uniqueItems = true };
            var required = new List<string> { "instanceId", "operationId", "sessionId", "projectPath", "modules" };
            if (!method.EndsWith("refresh"))
            {
                properties["expectedRevision"] = String("已审阅的 get_context.revision；变化时拒绝操作");
                properties["expectedUser"] = Schema(new() { ["userName"] = String("设计器协同用户名，保留域名"), ["email"] = String("设计器协同邮箱") }, ["userName", "email"]);
                properties["expectedRemoteUrl"] = new { type = new[] { "string", "null" }, description = "已授权的具体远端 URL；本地版本控制填 null" };
                properties["expectedBranch"] = String("已核对的当前分支");
                required.AddRange(["expectedRevision", "expectedUser", "expectedRemoteUrl", "expectedBranch"]);
            }
            if (method.EndsWith("checkin")) { properties["message"] = String("本次指定模块的签入说明"); required.Add("message"); }
            var description = method.EndsWith("refresh") ? "调用设计器原生 ReloadModules，重读本地锁并刷新界面；modules 传空数组。不保存、不拉取远端。"
                : method.EndsWith("checkout") ? "原生签出指定未锁定模块并同步锁，可能访问明确配置的远端；要求工程已保存，禁止抢占他人锁。返回操作回执。"
                : "原生签入指定的普通修改模块并同步当前协同远端；不处理新增/删除/移动，不自动保存未审阅编辑。返回回执，须查询至终态并核对范围。";
            list.Add(Tool(method, description, Schema(properties, required.ToArray()), false, !method.EndsWith("refresh")));
        }
        var report = new Dictionary<string, object>(Instance()) {
            ["sessionId"] = String("当前文档会话，必须与 get_context 一致"),
            ["projectPath"] = String("当前工程完整路径"),
            ["reportName"] = String("现有报表的确切名称；不接受文件路径")
        };
        list.Add(Tool("report.get", "读取现有报表的内存 JSON、RDLX、源文件双哈希及工程基线；不保存或修改工程。", Schema(report, ["instanceId", "sessionId", "projectPath", "reportName"])));
        var update = new Dictionary<string, object>(report) {
            ["operationId"] = String("新 UUID；超时查询原 ID，同 ID 同参数不会重复写入"),
            ["expectedRevision"] = String("report.get 返回的 context.revision"),
            ["expectedReportRevision"] = String("report.get 返回的 report.revision，包含内存和双文件哈希"),
            ["expectedUser"] = new { anyOf = new object[] {
                Schema(new() { ["userName"] = String("协同用户名，保留域名"), ["email"] = String("协同邮箱") }, ["userName", "email"]),
                new { type = "null" }
            } },
            ["json"] = String("完整报表元数据 JSON；保留名称、类型、权限、身份及目录字段"),
            ["rdlx"] = String("完整同名 RDLX XML；禁止 DTD/外部实体；两文件合计不超过256 KiB")
        };
        list.Add(Tool("report.update", "成组更新现有报表 JSON 和 RDLX，调用原生保存并回读内存和源文件。要求无未保存编辑，协同工程须本人签出，报表编辑器须关闭。不新建、删除、重命名、签入或推送。返回操作回执。", Schema(update,
            ["instanceId", "sessionId", "projectPath", "reportName", "operationId", "expectedRevision", "expectedReportRevision", "expectedUser", "json", "rdlx"]), false));
        return list.ToArray();
    }
}
