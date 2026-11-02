using System.Text;
using System.Text.Json;
using ClaudeControl.Core;

namespace ClaudeControl.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A file-based read-only smoke interface works for WinExe without console attachment.
        if (args.Length == 2 && args[0] == "--status-json")
        {
            try
            {
                var result = new Controller(new WindowsProcessSystem()).Check();
                File.WriteAllText(args[1], JsonSerializer.Serialize(new
                {
                    scan_complete = result.Warnings.Count == 0,
                    initial_count = result.InitialCount,
                    remaining_count = result.Remaining.Count,
                    processes = result.Remaining.Select(p => new { pid = p.Id, type = p.Kind, executable = p.ImagePath }),
                    warnings = result.Warnings
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
    private readonly Label status = new() { AutoSize = true, Text = "选择一键关闭，或检查当前进程。", Dock = DockStyle.Top, Padding = new(0, 8, 0, 10) };
    private readonly Button close = new() { Text = "一键关闭 Claude", AutoSize = true, Padding = new(12, 7, 12, 7) };
    private readonly Button check = new() { Text = "检查 Claude 进程", AutoSize = true, Padding = new(12, 7, 12, 7) };
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
    private readonly ProgressBar progress = new() { Style = ProgressBarStyle.Marquee, Height = 12, Dock = DockStyle.Bottom, Visible = false };

    public MainForm(string? mode)
    {
        Text = "Claude 一键工具";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(660, 390);
        MinimumSize = new(520, 330);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new(20);
        Font = new("Microsoft YaHei UI", 10);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(0, 0, 0, 14) };
        actions.Controls.AddRange([close, check]);
        var notice = new Label { Text = "关闭会中断正在进行的 Claude 任务。请先保存工作。", AutoSize = true, Dock = DockStyle.Bottom, Padding = new(0, 10, 0, 0) };
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
        status.Text = closing ? "正在关闭 Claude 并复查…" : "正在检查 Claude 进程…";
        details.Clear();
        try
        {
            ControlResult result = await Task.Run(() =>
            {
                var controller = new Controller(new WindowsProcessSystem());
                return closing ? controller.Close() : controller.Check();
            });
            status.Text = result.Warnings.Count > 0 ? "未能完整确认，请查看下方详情。" :
                result.Remaining.Count > 0 ? $"Claude 仍在运行：{result.Remaining.Count} 个进程。" :
                closing && result.InitialCount > 0 ? "Claude 已关闭，复查未检测到进程。" : "未检测到 Claude 进程。";
            var text = new StringBuilder();
            foreach (var process in result.Remaining)
                text.AppendLine($"PID {process.Id} · {process.Kind}\r\n{process.ImagePath}\r\n");
            foreach (string warning in result.Warnings) text.AppendLine(warning + "\r\n");
            if (text.Length == 0) text.Append("本次检查范围：当前用户、当前 Windows 登录会话中的本地 Claude 和 Claude Code。\r\n不包含 Cowork 虚拟机及系统级后台服务。\r\n\r\n结果表示复查时的状态；本工具不阻止软件之后重新启动，也不提供网络隔离。");
            details.Text = text.ToString();
        }
        catch (Exception exception)
        {
            status.Text = "无法完成检查，不能确认 Claude 是否已关闭。";
            details.Text = exception.Message;
        }
        finally { close.Enabled = check.Enabled = true; progress.Visible = false; }
    }
}
