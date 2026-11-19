using System.Text.Json;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace ForguncyDesignerMcp.Designer;

internal sealed class EmbeddedMcpUi : IDisposable
{
    private static readonly int[] ClipboardRetryDelays = [75, 150, 250, 400, 600];
    private readonly EmbeddedMcpController controller;
    private readonly Dispatcher dispatcher;
    private readonly Forms.NotifyIcon tray;
    private bool disposed;
    private DispatcherTimer? waitForRelease;
    private Window? window;
    private TextBlock? status;
    private TextBlock? feedback;
    private bool copyInProgress;
    internal EmbeddedMcpUi(EmbeddedMcpController controller, Dispatcher dispatcher)
    {
        this.controller = controller; this.dispatcher = dispatcher;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("管理 MCP", null, (_, _) => dispatcher.BeginInvoke(new Action(Show)));
        menu.Items.Add("启动", null, (_, _) => dispatcher.BeginInvoke(new Action(async () => await Execute(() => controller.StartAsync()))));
        menu.Items.Add("停止", null, (_, _) => dispatcher.BeginInvoke(new Action(async () => await Execute(controller.StopAsync))));
        tray = new Forms.NotifyIcon { Text = controller.TrayText, ContextMenuStrip = menu };
        TaskbarIcon.Update(tray, TaskbarIcon.ReadPixelSize());
        tray.Visible = true;
        tray.DoubleClick += (_, _) => dispatcher.BeginInvoke(new Action(Show));
        controller.Changed += Refresh;
    }
    internal void ShowAfterCellEdit(Window designer)
    {
        if (disposed || !designer.IsActive || waitForRelease is not null) return;
        // A double click starts editing on the second mouse-down. Let the sheet
        // finish processing its mouse-up before activating the owned window.
        if (!LeftButtonPressed())
        {
            Show(designer);
            return;
        }
        waitForRelease = new DispatcherTimer(DispatcherPriority.ContextIdle, dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        waitForRelease.Tick += (_, _) =>
        {
            if (designer.IsActive && LeftButtonPressed()) return;
            waitForRelease!.Stop();
            waitForRelease = null;
            if (designer.IsActive && !disposed) Show(designer);
        };
        waitForRelease.Start();
    }

    private void Show() => Show(null);

    private void Show(Window? designer)
    {
        if (disposed) return;
        if (window is not null)
        {
            if (designer is not null && window.Owner != designer) window.Owner = designer;
            if (window.Owner is { } owner) RestoreIfMinimized(owner);
            RestoreIfMinimized(window);
            window.Activate();
            return;
        }
        var stack = new StackPanel { Margin = new Thickness(16) };
        status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }; stack.Children.Add(status);
        var auto = new CheckBox { Content = "加载 MCP 启动器后自动启动服务", IsChecked = controller.Settings.AutoStart }; stack.Children.Add(auto);
        stack.Children.Add(new TextBlock { Text = "首选端口（更改后重新启动服务）", Margin = new Thickness(0, 10, 0, 3) });
        var port = new TextBox { Text = controller.Settings.PreferredPort.ToString() }; stack.Children.Add(port);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; stack.Children.Add(actions);
        void Add(string text, Action action) { var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(8, 3, 8, 3) }; button.Click += (_, _) => action(); actions.Children.Add(button); }
        Add("保存设置", () => { try { if (!int.TryParse(port.Text, out var value)) throw new InvalidDataException("请输入有效端口。"); controller.SaveSettings(auto.IsChecked == true, value); } catch (Exception error) { System.Windows.MessageBox.Show(error.Message, "MCP"); } });
        Add("启动", async () => await Execute(() => controller.StartAsync()));
        Add("停止", async () => await Execute(controller.StopAsync));
        Add("复制地址", async () => { if (McpUrl() is string url) await CopyAsync(url, "复制成功：MCP 地址已写入剪贴板。", isClientConfig: false); });
        Add("复制客户端配置", async () => { if (McpUrl() is string url) await CopyAsync(ClientConfiguration(url), "复制成功：客户端配置已写入剪贴板。", isClientConfig: true); });
        Add("保存客户端配置", () => { if (McpUrl() is string url && window is Window owner && SaveConfiguration(owner, ClientConfiguration(url))) SetFeedback("客户端配置已保存。"); });
        feedback = new TextBlock { MinHeight = 20, TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(feedback);
        window = new Window { Title = "Forguncy MCP 管理", Width = 540, MinHeight = 290, SizeToContent = SizeToContent.Height, Content = stack,
            Owner = designer, WindowStartupLocation = designer is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        window.Closed += (_, _) => { window = null; status = null; feedback = null; }; Refresh(); window.Show();
    }
    private static void RestoreIfMinimized(Window target)
    {
        const int swRestore = 9;
        if (target.WindowState == WindowState.Minimized) ShowWindow(new WindowInteropHelper(target).Handle, swRestore);
    }
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);
    // Read the button state of the designer's input queue, after its mouse messages.
    private static bool LeftButtonPressed() => GetKeyState(0x01) < 0;
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
    private string? McpUrl() => controller.Address is Uri address ? new Uri(address, "mcp").ToString() : null;
    private string ClientConfiguration(string url) => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            ["forguncy"] = new { url, headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + controller.Settings.BearerToken } }
        }
    }, new JsonSerializerOptions { WriteIndented = true });

    private void SetFeedback(string message)
    {
        if (feedback is not null) feedback.Text = message;
    }

    private async Task CopyAsync(string value, string successMessage, bool isClientConfig)
    {
        if (copyInProgress || window is not Window owner) return;
        copyInProgress = true;
        try
        {
            SetFeedback("");
            var handle = new WindowInteropHelper(owner).Handle;
            var result = NativeClipboard.WriteText(handle, value, out int errorCode);
            foreach (int delay in ClipboardRetryDelays)
            {
                if (result != ClipboardWriteStatus.CannotOpen) break;
                SetFeedback("剪贴板暂时无法打开，正在重试…");
                await Task.Delay(delay);
                if (window != owner || !owner.IsVisible) return;
                result = NativeClipboard.WriteText(handle, value, out errorCode);
            }
            if (result == ClipboardWriteStatus.Copied)
            {
                SetFeedback(successMessage);
                return;
            }

            SetFeedback("未能复制到剪贴板。");
            ShowCopyFallback(owner, value, result, errorCode, isClientConfig);
        }
        finally { copyInProgress = false; }
    }

    private void ShowCopyFallback(Window owner, string value, ClipboardWriteStatus result, int errorCode, bool isClientConfig)
    {
        var content = new DockPanel { Margin = new Thickness(12) };
        string reason = result == ClipboardWriteStatus.CannotOpen ? "系统剪贴板无法打开" : "无法写入系统剪贴板";
        var instructions = new TextBlock
        {
            Text = isClientConfig
                ? $"{reason}（Windows 错误 {errorCode}）。也可以直接保存客户端配置。"
                : $"{reason}（Windows 错误 {errorCode}）。你可以查看下方地址，稍后重试复制。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(instructions, Dock.Top);
        content.Children.Add(instructions);
        var text = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            AcceptsReturn = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var dialog = new Window
        {
            Title = "查看 MCP 内容", Owner = owner, ShowInTaskbar = false,
            Width = 480, Height = 250, Content = content,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (isClientConfig)
        {
            var save = new Button { Content = "保存客户端配置", HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
            save.Click += (_, _) =>
            {
                if (!SaveConfiguration(dialog, value)) return;
                SetFeedback("客户端配置已保存。");
                dialog.Close();
            };
            DockPanel.SetDock(save, Dock.Bottom);
            content.Children.Add(save);
        }
        content.Children.Add(text);
        dialog.Loaded += (_, _) => { text.Focus(); text.SelectAll(); };
        dialog.ShowDialog();
    }

    private static bool SaveConfiguration(Window owner, string value)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存 MCP 客户端配置",
            Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "forguncy-mcp.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(owner) != true) return false;
        try
        {
            File.WriteAllText(dialog.FileName, value);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(owner, "保存配置失败：" + error.Message, "MCP");
            return false;
        }
    }
    private async Task Execute(Func<Task> operation) { try { await operation(); } catch (Exception error) { System.Windows.MessageBox.Show(error.Message, "MCP"); } }
    private void Refresh()
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (!dispatcher.CheckAccess()) { try { _ = dispatcher.BeginInvoke(new Action(Refresh)); } catch (InvalidOperationException) { } return; }
        if (disposed) return;
        // The controller publishes on the designer's three-second heartbeat, even with the management window closed.
        TaskbarIcon.Update(tray, TaskbarIcon.ReadPixelSize());
        var text = controller.TrayText;
        if (tray.Text != text) tray.Text = text;
        if (status is not null) status.Text = (controller.Port is int port ? "运行中，端口 " + port + "\n" + McpUrl() : "已停止") +
            "\n当前工程：" + (controller.ProjectPath ?? "未打开") + (controller.SettingsError is null ? "" : "\n" + controller.SettingsError) + (controller.Error is null ? "" : "\n错误：" + controller.Error);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        waitForRelease?.Stop();
        waitForRelease = null;
        controller.Changed -= Refresh;
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        var icon = tray.Icon;
        tray.Dispose();
        icon?.Dispose();
        window?.Close();
    }
}
