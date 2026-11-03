using System.Text;
using System.Text.Json;
using ClaudeControl.Core;

namespace ClaudeControl.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Fixed-purpose elevated entry point. It never calls the process controller,
        // writes a caller-selected file, or accepts a caller-selected service.
        if (args.Length == 1 && args[0] == WindowsOperations.ServiceHelperArgument)
        {
            ApplicationConfiguration.Initialize();
            try
            {
                if (!WindowsOperations.IsAdministrator())
                {
                    MessageBox.Show("停止 Cowork 服务需要管理员权限，请从普通窗口点击一键关闭并接受 Windows 授权提示。", "Claude 一键工具");
                    return 2;
                }
                CoworkServiceResult service = new CoworkServiceController(new CoworkServiceSystem()).Close();
                if (service.ConfirmedStopped) return 0;
                MessageBox.Show(string.Join("\r\n", service.Warnings.Append("未确认 Cowork 服务停止；没有强杀进程或更改启动方式。")), "Cowork 服务未完成");
                return 2;
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "Cowork 服务停止失败");
                return 2;
            }
        }
        // A file-based read-only smoke interface works for WinExe without console attachment.
        if (args.Length == 2 && args[0] == "--status-json")
        {
            try
            {
                DesktopResult result = WindowsOperations.Check();
                var service = result.Service.Snapshot;
                File.WriteAllText(args[1], JsonSerializer.Serialize(new
                {
                    scan_complete = result.Warnings.Count == 0,
                    initial_count = result.Processes.InitialCount,
                    remaining_count = result.Processes.Remaining.Count,
                    processes = result.Processes.Remaining.Select(p => new { pid = p.Id, type = p.Kind, executable = p.ImagePath }),
                    cowork_service = new { installed = service is not null, name = service?.Name,
                        state = service?.State.ToString(), executable = service?.Executable,
                        verified = service?.Verified, confirmed_stopped = result.Service.ConfirmedStopped },
                    warnings = result.Warnings,
                    confirmed_closed = result.ConfirmedClosed,
                    network_isolation = false
                }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                return result.Warnings.Count == 0 ? 0 : 2;
            }
            catch (Exception exception)
            {
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { scan_complete = false, error = exception.Message }), new UTF8Encoding(false));
                return 2;
            }
        }
        ApplicationConfiguration.Initialize();
        if (args.Length > 1 || (args.Length == 1 && args[0] is not "--close" and not "--check"))
        {
            MessageBox.Show("请使用桌面的关闭/检查入口，或直接打开本程序。", "Claude 一键工具");
            return 1;
        }
        Application.Run(new MainForm(args.FirstOrDefault()));
        return 0;
    }
}

internal sealed class MainForm : Form
{
    private readonly Label status = new() { AutoSize = false, Height = 60, Text = "选择一键关闭，或检查进程和 Cowork 服务。", Dock = DockStyle.Top, Padding = new(0, 8, 0, 10) };
    private readonly Button close = new() { Text = "一键关闭 Claude", AutoSize = true, Padding = new(12, 7, 12, 7) };
    private readonly Button check = new() { Text = "检查 Claude 进程", AutoSize = true, Padding = new(12, 7, 12, 7) };
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
    private readonly ProgressBar progress = new() { Style = ProgressBarStyle.Marquee, Height = 12, Dock = DockStyle.Bottom, Visible = false };

    public MainForm(string? mode)
    {
        Text = "Claude 一键工具";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(720, 470);
        MinimumSize = new(520, 330);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new(20);
        Font = new("Microsoft YaHei UI", 10);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(0, 0, 0, 14) };
        actions.Controls.AddRange([close, check]);
        var notice = new Label { Text = "请先保存工作。Cowork 服务由本机各用户共享，停止会中断相关任务；可能需要管理员授权。", AutoSize = false, Height = 64, Dock = DockStyle.Bottom, Padding = new(0, 10, 0, 0) };
        Controls.Add(details);
        Controls.Add(actions);
        Controls.Add(status);
        Controls.Add(notice);
        Controls.Add(progress);
        close.Click += async (_, _) => await Run(true);
        check.Click += async (_, _) => await Run(false);
        Shown += async (_, _) => { if (mode is not null) await Run(mode == "--close"); };
    }

    private async Task Run(bool closing)
    {
        close.Enabled = check.Enabled = false;
        progress.Visible = true;
        status.Text = closing ? "正在关闭 Claude 和 Cowork 服务；需要时请接受 Windows 管理员授权…" : "正在检查 Claude 进程和 Cowork 服务…";
        details.Clear();
        try
        {
            DesktopResult result = closing ? await WindowsOperations.CloseAsync() : await Task.Run(WindowsOperations.Check);
            status.Text = result.Warnings.Count > 0 ? "未能完整确认，请查看下方详情。" :
                result.Processes.Remaining.Count > 0 ? $"仍有 {result.Processes.Remaining.Count} 个用户进程；Cowork 服务状态见下方。" :
                !result.Service.ConfirmedStopped ? "用户进程为零，Cowork 服务尚未确认停止。" :
                "用户进程为零；Cowork 服务已停止或未安装。";
            var text = new StringBuilder();
            text.AppendLine($"已识别的用户进程：{result.Processes.Remaining.Count}\r\n");
            foreach (var process in result.Processes.Remaining)
                text.AppendLine($"PID {process.Id} · {process.Kind}\r\n{process.ImagePath}\r\n");
            CoworkServiceSnapshot? service = result.Service.Snapshot;
            if (service is null)
                text.AppendLine(result.Service.ConfirmedStopped ? "Cowork 服务：未安装受支持的服务。\r\n" : "Cowork 服务：未能确认状态。\r\n");
            else
                text.AppendLine($"Cowork 服务：{ServiceState(service.State)}\r\n{service.Name}\r\n{service.Executable}\r\n身份核验：{(service.Verified ? "通过" : "未通过")}\r\n");
            foreach (string warning in result.Warnings) text.AppendLine(warning + "\r\n");
            text.Append("范围：当前用户/登录会话中的可识别 Claude 进程，以及身份核验通过的 Cowork 后台服务。Cowork 服务由整台电脑共享，停止可能影响其他登录用户的 Cowork 任务。\r\n服务停止不代表已核实虚拟机内部、远程任务或所有网络活动停止。\r\n本工具不更改服务启动方式，不阻止软件重新启动，也不提供网络隔离。");
            details.Text = text.ToString();
        }
        catch (Exception exception)
        {
            status.Text = "无法完成检查，不能确认 Claude 是否已关闭。";
            details.Text = exception.Message;
        }
        finally { close.Enabled = check.Enabled = true; progress.Visible = false; }
    }

    private static string ServiceState(CoworkServiceState state) => state switch
    {
        CoworkServiceState.Stopped => "已停止", CoworkServiceState.Running => "运行中",
        CoworkServiceState.StopPending => "正在停止", CoworkServiceState.StartPending => "正在启动",
        CoworkServiceState.Paused => "已暂停", _ => "状态未确认（" + state + "）"
    };
}
