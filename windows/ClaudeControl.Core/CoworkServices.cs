namespace ClaudeControl.Core;

public enum CoworkServiceState
{
    Unknown = 0, Stopped = 1, StartPending = 2, StopPending = 3, Running = 4,
    ContinuePending = 5, PausePending = 6, Paused = 7
}

public sealed record CoworkServiceSnapshot(string Name, CoworkServiceState State,
    string Executable, bool Verified, string Identity = "", string Verification = "");

public sealed record CoworkServiceResult(CoworkServiceSnapshot? Snapshot,
    IReadOnlyList<string> Warnings, bool RequiresElevation = false)
{
    // Absence must be established by the backend, not inferred from a failed query.
    public bool ConfirmedStopped => Warnings.Count == 0 && !RequiresElevation &&
        (Snapshot is null || (Snapshot.Verified && Snapshot.State == CoworkServiceState.Stopped));
}

public interface ICoworkServiceSystem
{
    CoworkServiceResult Inspect();
    // Open the exact SCM object again, revalidate config/trust on the same handle,
    // compare expected.Identity, then use SCM STOP. Never terminate a process PID.
    CoworkServiceResult StopVerified(CoworkServiceSnapshot expected);
    void Delay(TimeSpan duration);
}

public sealed class CoworkServiceController(ICoworkServiceSystem system)
{
    public const string ServiceName = "CoworkVMService";
    public const int MaximumPolls = 30;

    public CoworkServiceResult Check() => Validate(system.Inspect());

    public CoworkServiceResult Close()
    {
        CoworkServiceResult current = Check();
        if (current.Warnings.Count > 0 || current.RequiresElevation || current.Snapshot is null || current.ConfirmedStopped) return current;
        CoworkServiceSnapshot expected = current.Snapshot;
        bool requested = current.Snapshot.State == CoworkServiceState.StopPending;
        for (int poll = 0; poll < MaximumPolls; poll++)
        {
            if (!requested && current.Snapshot!.State is CoworkServiceState.Running or CoworkServiceState.Paused)
            {
                current = Validate(system.StopVerified(expected));
                if (current.RequiresElevation || current.Warnings.Count > 0) return current;
                requested = true;
            }
            // Always re-read after a STOP result, including a result saying Stopped.
            system.Delay(TimeSpan.FromMilliseconds(500));
            current = Check();
            if (current.Warnings.Count > 0 || current.RequiresElevation) return current;
            if (current.Snapshot is null) return ConfirmAgain(current, expected.Identity);
            if (!string.Equals(expected.Identity, current.Snapshot.Identity, StringComparison.Ordinal))
                return Warn(current, "Cowork 服务配置或已验证文件身份在操作期间发生变化，已停止后续操作；不能确认已关闭。");
            if (current.ConfirmedStopped) return ConfirmAgain(current, expected.Identity);
            if (requested && current.Snapshot.State is CoworkServiceState.Running or CoworkServiceState.Paused)
                return Warn(current, "Cowork 服务在停止请求后仍在运行或已重新启动，不能确认已关闭。");
        }
        return Warn(current, "等待 Cowork 服务停止超时，不能确认已关闭；没有强杀服务进程，也没有更改启动方式。");
    }

    private static CoworkServiceResult Validate(CoworkServiceResult result)
    {
        if (result.Snapshot is not null &&
            (!string.Equals(result.Snapshot.Name, ServiceName, StringComparison.OrdinalIgnoreCase) || !result.Snapshot.Verified))
            return Warn(result, "Cowork 服务身份未通过严格验证，未授权发送停止控制。");
        return result;
    }

    private CoworkServiceResult ConfirmAgain(CoworkServiceResult observed, string identity)
    {
        system.Delay(TimeSpan.FromMilliseconds(500));
        CoworkServiceResult final = Check();
        if (final.Warnings.Count > 0 || final.RequiresElevation) return final;
        if (final.Snapshot is not null && !string.Equals(identity, final.Snapshot.Identity, StringComparison.Ordinal))
            return Warn(final, "Cowork 服务在停止复查期间被替换，不能确认已关闭。");
        return final.ConfirmedStopped ? final : Warn(final, "Cowork 服务在停止复查期间重新启动或仍未停止，不能确认已关闭。");
    }

    private static CoworkServiceResult Warn(CoworkServiceResult result, string warning) =>
        result with { Warnings = result.Warnings.Append(warning).Distinct().ToArray() };
}
