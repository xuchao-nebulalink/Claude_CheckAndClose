using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using ClaudeControl.Core;

namespace ClaudeControl.Windows;

internal sealed record DesktopResult(ControlResult Processes, CoworkServiceResult Service,
    IReadOnlyList<string> OperationWarnings)
{
    public IReadOnlyList<string> Warnings => Processes.Warnings.Concat(Service.Warnings)
        .Concat(OperationWarnings).Distinct().ToArray();
    public bool ConfirmedClosed => Processes.Remaining.Count == 0 && Service.ConfirmedStopped && Warnings.Count == 0;
}

internal static class WindowsOperations
{
    // The elevated entry point accepts no service name, PID, path or output file.
    public const string ServiceHelperArgument = "--stop-cowork-service";

    public static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    public static DesktopResult Check() => new(new Controller(new WindowsProcessSystem()).Check(),
        new CoworkServiceController(new CoworkServiceSystem()).Check(), []);

    public static async Task<DesktopResult> CloseAsync()
    {
        // Keep process management in the original user's unelevated application.
        // UAC can use a different administrator account; the helper must never
        // rescan or terminate that account's ordinary processes.
        ControlResult processes = await Task.Run(() => new Controller(new WindowsProcessSystem()).Close());
        var serviceController = new CoworkServiceController(new CoworkServiceSystem());
        CoworkServiceResult service = await Task.Run(serviceController.Close);
        var warnings = new List<string>();
        if (service.RequiresElevation && !IsAdministrator())
        {
            string? failure = await RequestServiceElevation();
            service = await Task.Run(serviceController.Check);
            if (failure is not null) warnings.Add(failure);
        }
        // Check again in the original account after the service operation/UAC.
        // Preserve earlier failures rather than hiding them with a later zero.
        ControlResult final = await Task.Run(() => new Controller(new WindowsProcessSystem()).Check(processes.Remaining));
        processes = final with { InitialCount = processes.InitialCount,
            Warnings = processes.Warnings.Concat(final.Warnings).Distinct().ToArray() };
        service = MergeFinalService(service, await Task.Run(serviceController.Check));
        return new(processes, service, warnings);
    }

    private static CoworkServiceResult MergeFinalService(CoworkServiceResult operation, CoworkServiceResult final) =>
        final with { Warnings = operation.Warnings.Concat(final.Warnings).Distinct().ToArray(),
            RequiresElevation = operation.RequiresElevation || final.RequiresElevation };

    private static async Task<string?> RequestServiceElevation()
    {
        try
        {
            string? executable = Environment.ProcessPath;
            if (executable is null || !Path.GetFileName(executable).Equals("ClaudeControl.exe", StringComparison.OrdinalIgnoreCase))
                return "需要管理员权限停止 Cowork 服务；请使用发布包中的 ClaudeControl.exe，当前启动方式不能安全调用服务辅助程序。";
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true, Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            start.ArgumentList.Add(ServiceHelperArgument);
            using Process? helper = Process.Start(start);
            if (helper is null) return "未能启动 Cowork 服务管理员辅助程序，不能确认已关闭。";
            await helper.WaitForExitAsync();
            return helper.ExitCode == 0 ? null : "Cowork 服务管理员辅助程序未成功完成；请查看服务结果，不能确认已关闭。";
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return "已取消管理员授权；Cowork 服务未确认停止。普通用户进程的结果见下方。";
        }
        catch (Exception exception)
        {
            return "Cowork 服务管理员辅助程序启动失败：" + exception.Message;
        }
    }
}
