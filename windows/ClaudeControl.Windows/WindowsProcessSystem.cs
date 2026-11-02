using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ClaudeControl.Core;
using Microsoft.Win32.SafeHandles;

namespace ClaudeControl.Windows;

// This adapter uses only local Windows process/token APIs and local WMI. No HTTP or telemetry.
public sealed class WindowsProcessSystem : IProcessSystem
{
    private const uint Query = 0x1000, TerminateRight = 0x0001, Synchronize = 0x00100000;
    public int CurrentId => Environment.ProcessId;
    public string CurrentSid { get; } = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法读取当前用户身份。");
    public int CurrentSession { get; } = Process.GetCurrentProcess().SessionId;

    public Snapshot Capture(IReadOnlyCollection<ProcessRecord>? tracked = null)
    {
        var processes = new Dictionary<int, ProcessRecord>();
        var warnings = new List<string>();
        var trackedIds = (tracked ?? []).Select(p => p.Id).ToHashSet();
        var parents = new Dictionary<int, int>();
        var unreadable = new Dictionary<int, (string Name, int Error)>();
        var unjoined = new Dictionary<int, string>();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"),
            new ObjectQuery($"SELECT ProcessId, ParentProcessId, Name, CreationDate, CommandLine FROM Win32_Process WHERE SessionId = {CurrentSession}"),
            new System.Management.EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(10) });
        using ManagementObjectCollection rows = searcher.Get();
        foreach (ManagementObject row in rows)
        {
            using (row)
            {
                int id = checked((int)(uint)row["ProcessId"]);
                int parent = checked((int)(uint)row["ParentProcessId"]);
                parents[id] = parent;
                string name = row["Name"] as string ?? "";
                ProcessRecord? process = Read(id, parent, out int error);
                bool relevantName = name.StartsWith("claude", StringComparison.OrdinalIgnoreCase) || IsRuntime(name);
                if (process is null)
                {
                    if (error != 0 && error != 87 && error != 1168) unreadable[id] = (name, error);
                    if ((relevantName || trackedIds.Contains(id)) && error != 0 && error != 87 && error != 1168)
                        warnings.Add($"PID {id}（{name}）无法读取身份或路径：{new Win32Exception(error).Message}。无法完整确认。");
                    continue;
                }
                if (process.OwnerSid != CurrentSid || process.SessionId != CurrentSession) continue;
                bool joined = false;
                try
                {
                    string? creation = row["CreationDate"] as string;
                    if (creation is not null)
                        joined = ProcessRules.SameCreationMicroseconds(process.Created,
                            ManagementDateTimeConverter.ToDateTime(creation).ToUniversalTime().ToFileTimeUtc());
                }
                catch (ArgumentException) { }
                if (!joined)
                {
                    unjoined[id] = name;
                    if (relevantName || trackedIds.Contains(id) || ProcessRules.RootKind(process) is not null || IsRuntime(Path.GetFileName(process.ImagePath)))
                        warnings.Add($"PID {id} 的 WMI 与本机创建时间不一致，未采用该行的父进程或命令参数，无法完整确认。");
                    continue;
                }
                if (IsRuntime(Path.GetFileName(process.ImagePath)))
                {
                    string? commandLine = row["CommandLine"] as string;
                    if (string.IsNullOrWhiteSpace(commandLine))
                        warnings.Add($"PID {id}（{name}）无法读取脚本入口，无法完整检查该 Node/Bun 进程。");
                    else process = process with { Arguments = SplitArguments(commandLine) };
                }
                processes[id] = process;
            }
        }
        var possibleChildren = ProcessRules.PotentialDescendants(parents,
            processes.Values.Where(p => ProcessRules.RootKind(p) is not null).Select(p => p.Id).Concat(trackedIds));
        foreach (var (id, failure) in unreadable)
            if (possibleChildren.Contains(id) && !trackedIds.Contains(id) &&
                !failure.Name.StartsWith("claude", StringComparison.OrdinalIgnoreCase) && !IsRuntime(failure.Name))
                warnings.Add($"可能属于 Claude 的子进程 PID {id}（{failure.Name}）无法读取身份或路径：{new Win32Exception(failure.Error).Message}。未结束身份未知的进程。");
        foreach (var (id, name) in unjoined)
            if (possibleChildren.Contains(id) && !trackedIds.Contains(id))
                warnings.Add($"可能属于 Claude 的子进程 PID {id}（{name}）无法将快照与实际创建时间关联，未结束身份未知的进程。");
        foreach (ProcessRecord expected in tracked ?? [])
        {
            if (processes.ContainsKey(expected.Id)) continue;
            ProcessRecord? current = Read(expected.Id, expected.ParentId, out int error);
            if (current is not null && expected.SameIdentity(current))
                processes[expected.Id] = current with { Arguments = expected.Arguments, Kind = expected.Kind };
            else if (current is null && error != 0 && error != 87 && error != 1168)
                warnings.Add($"已追踪的 PID {expected.Id} 无法复核身份或路径：{new Win32Exception(error).Message}。无法确认它已退出。");
        }
        bool complete = ProcessRules.CanEstablishAncestors(processes, parents, CurrentId);
        if (!complete) warnings.Add("本工具的活动祖先链无法完整核实；本次只检查，不执行关闭。请从桌面独立打开工具后重试。");
        return new(processes, warnings, complete);
    }

    private static bool IsRuntime(string name) => new[] { "node.exe", "node", "bun.exe", "bun" }.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> SplitArguments(string commandLine)
    {
        IntPtr vector = CommandLineToArgvW(commandLine, out int count);
        if (vector == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法解析本地进程参数。");
        try
        {
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(vector, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally { LocalFree(vector); }
    }

    public ProcessRecord? Read(int id, int parent, out int error)
    {
        using SafeProcessHandle handle = OpenProcess(Query, false, id);
        if (handle.IsInvalid) { error = Marshal.GetLastWin32Error(); return null; }
        return ReadHandle(handle, id, parent, out error);
    }

    private static ProcessRecord? ReadHandle(SafeProcessHandle handle, int id, int parent, out int error)
    {
        error = 0;
        if (!GetProcessTimes(handle, out long created, out long exited, out _, out _)) { error = Marshal.GetLastWin32Error(); return null; }
        if (exited != 0) return null;
        var path = new StringBuilder(32768);
        uint capacity = (uint)path.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, path, ref capacity)) { error = Marshal.GetLastWin32Error(); return null; }
        if (!ProcessIdToSessionId((uint)id, out uint session)) { error = Marshal.GetLastWin32Error(); return null; }
        if (!OpenProcessToken(handle, 0x0008, out SafeFileHandle token)) { error = Marshal.GetLastWin32Error(); return null; }
        using (token)
        {
            GetTokenInformation(token, 1, IntPtr.Zero, 0, out int bytes);
            if (bytes <= 0) { error = Marshal.GetLastWin32Error(); return null; }
            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                if (!GetTokenInformation(token, 1, buffer, bytes, out _)) { error = Marshal.GetLastWin32Error(); return null; }
                string sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                return new(id, parent, created, sid, checked((int)session), path.ToString(), []);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public string? RequestClose(ProcessRecord expected)
    {
        using SafeProcessHandle handle = OpenProcess(Query | Synchronize, false, expected.Id);
        if (handle.IsInvalid) return ActionError(expected.Id, "请求退出", Marshal.GetLastWin32Error());
        ProcessRecord? current = ReadHandle(handle, expected.Id, expected.ParentId, out int error);
        if (current is null) return error == 0 ? null : ActionError(expected.Id, "复核身份", error);
        if (!expected.SameIdentity(current)) return null;
        // Holding this verified process handle prevents a PID-only action on a replacement.
        try
        {
            using var process = Process.GetProcessById(expected.Id);
            if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != expected.Created) return null;
            process.CloseMainWindow();
            return null;
        }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
        catch (Win32Exception exception) { return ActionError(expected.Id, "请求退出", exception.NativeErrorCode); }
    }

    public string? Terminate(ProcessRecord expected)
    {
        using SafeProcessHandle handle = OpenProcess(Query | TerminateRight | Synchronize, false, expected.Id);
        if (handle.IsInvalid) return ActionError(expected.Id, "结束进程", Marshal.GetLastWin32Error());
        ProcessRecord? current = ReadHandle(handle, expected.Id, expected.ParentId, out int error);
        if (current is null) return error == 0 ? null : ActionError(expected.Id, "复核身份", error);
        if (!expected.SameIdentity(current)) return null;
        if (!TerminateProcess(handle, 0))
        {
            int failure = Marshal.GetLastWin32Error();
            if (WaitForSingleObject(handle, 0) == 0) return null;
            return ActionError(expected.Id, "结束进程", failure);
        }
        WaitForSingleObject(handle, 1000);
        return null;
    }

    private static string? ActionError(int pid, string action, int error) =>
        error is 0 or 87 or 1168 ? null : $"PID {pid} {action}失败：{new Win32Exception(error).Message}";
    public void Delay(TimeSpan duration) => Thread.Sleep(duration);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint id, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeFileHandle token, int kind, IntPtr buffer, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
