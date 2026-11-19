namespace ForguncyDesignerMcp.Core;

public static class SessionGuard
{
    public static void CheckContext(DesignerContext context, MutationRequest request)
    {
        if (context.SessionId != request.SessionId ||
            !string.Equals(Path.GetFullPath(context.ProjectPath), Path.GetFullPath(request.ProjectPath), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("DocumentChanged", "设计器已切换或重开工程，请重新读取当前会话。");
        if (context.Busy)
            throw new BridgeException("DesignerBusy", "设计器正在打开、导入或执行其他操作。");
        if (!context.InCollaborationMode)
            throw new BridgeException("NotCollaborationProject", "当前工程不是协同工程。");
    }

    public static void CheckMutation(DesignerContext context, MutationRequest request, string operation)
    {
        CheckContext(context, request);
        if (context.HasUnsavedChanges)
            throw new BridgeException("UnsavedChanges", "请先在设计器保存工程；本工具不会连带保存未审阅的编辑。");
        if (context.ModuleStateStale)
            throw new BridgeException("ModuleStateStale", "磁盘与设计器内存中的模块状态不同，请先调用 collaboration.refresh。");
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision) || context.Revision != request.ExpectedRevision)
            throw new BridgeException("BaselineChanged", "工程、协同锁或 Git 基线已变化，请重新读取并核对。");
        if (context.CurrentUser is null || request.ExpectedUser != context.CurrentUser ||
            string.IsNullOrWhiteSpace(context.CurrentUser.UserName) || string.IsNullOrWhiteSpace(context.CurrentUser.Email))
            throw new BridgeException("IdentityChanged", "请求身份与设计器当前协同身份不一致。");
        var git = context.Git ?? throw new BridgeException("GitUnavailable", "无法核实当前工程的 Git 状态。");
        if (!git.IndexClean)
            throw new BridgeException("StagedChanges", "暂存区已有内容，请先处理；不会把其他暂存文件纳入原生提交。");
        if (request.ExpectedRemoteUrl != git.RemoteUrl || request.ExpectedBranch != git.Branch)
            throw new BridgeException("DestinationChanged", "请求中的远端地址或分支与当前工程不一致。");
        if (git.RemoteUrl is not null && (git.Upstream is null || git.UpstreamHead != git.Head))
            throw new BridgeException("UpstreamDiverged", "当前分支与上游不同步，请先审阅和处理，避免连带推送其他提交。");
        if (request.Modules.Length is < 1 or > 100 || request.Modules.Distinct(StringComparer.Ordinal).Count() != request.Modules.Length)
            throw new BridgeException("InvalidModules", "请指定 1 至 100 个不重复的确切模块名称。");
        var modules = context.Modules.ToDictionary(x => x.Name, StringComparer.Ordinal);
        foreach (var name in request.Modules)
        {
            if (!modules.TryGetValue(name, out var module))
                throw new BridgeException("UnknownModule", "未找到模块：" + name);
            if (module.State != 0 && module.LockedBy != context.CurrentUser)
                throw new BridgeException("LockedByOtherUser", "模块由其他用户签出：" + name);
            if (operation == "collaboration.checkout" && module.State != 0)
                throw new BridgeException("AlreadyCheckedOut", "模块已签出，无需重复操作：" + name);
            if (operation == "collaboration.checkin" && module.State != 1)
                throw new BridgeException("UnsupportedModuleState", "首版仅支持普通修改模块的签入，不处理新增、删除或移动：" + name);
        }
        if (operation == "collaboration.checkin" && string.IsNullOrWhiteSpace(request.Message))
            throw new BridgeException("MessageRequired", "签入必须提供明确的提交说明。");
    }

    public static string RelativeFile(string projectPath, string file)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        var full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(root, file));
        var relative = Path.GetRelativePath(root, full);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
            throw new BridgeException("OutsideProject", "原生模块文件不属于当前工程。");
        return relative.Replace('\\', '/');
    }
}
