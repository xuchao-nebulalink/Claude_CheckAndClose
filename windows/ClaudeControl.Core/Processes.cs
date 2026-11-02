namespace ClaudeControl.Core;

public sealed record ProcessRecord(int Id, int ParentId, long Created, string OwnerSid,
    int SessionId, string ImagePath, IReadOnlyList<string> Arguments, string Kind = "")
{
    // The executable may change without a new process identity. Never match a PID alone.
    public bool SameIdentity(ProcessRecord? other) => other is not null && Id == other.Id &&
        Created == other.Created && OwnerSid == other.OwnerSid && SessionId == other.SessionId;
}

public sealed record Snapshot(IReadOnlyDictionary<int, ProcessRecord> Processes, IReadOnlyList<string> Warnings,
    bool ProtectionComplete = true);
public sealed record ControlResult(int InitialCount, IReadOnlyList<ProcessRecord> Remaining,
    IReadOnlyList<string> Warnings)
{
    public bool ConfirmedClosed => Remaining.Count == 0 && Warnings.Count == 0;
}

public interface IProcessSystem
{
    int CurrentId { get; }
    string CurrentSid { get; }
    int CurrentSession { get; }
    Snapshot Capture(IReadOnlyCollection<ProcessRecord>? tracked = null);
    // Both methods must recheck identity using the same native handle used for the action.
    string? RequestClose(ProcessRecord expected);
    string? Terminate(ProcessRecord expected);
    void Delay(TimeSpan duration);
}

public static class ProcessRules
{
    private static string Normalize(string value) => value.Replace('/', '\\').TrimEnd('\\');
    private static string FileName(string value) => Normalize(value).Split('\\').LastOrDefault() ?? "";
    public static bool Under(string path, string directory) =>
        Normalize(path).StartsWith(Normalize(directory) + "\\", StringComparison.OrdinalIgnoreCase);

    public static bool ClaudeScript(string path)
    {
        const string relative = "node_modules\\@anthropic-ai\\claude-code\\cli.js";
        string normalized = Normalize(path);
        return normalized.Equals(relative, StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("\\" + relative, StringComparison.OrdinalIgnoreCase);
    }

    public static string? RootKind(ProcessRecord process)
    {
        string name = FileName(process.ImagePath);
        if (name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("claude", StringComparison.OrdinalIgnoreCase)) return "Claude / Claude Code";
        string path = Normalize(process.ImagePath);
        // Dedicated desktop installation directories. No command-line substring matching.
        if ((path.Contains("\\AnthropicClaude\\", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("\\Programs\\Claude\\", StringComparison.OrdinalIgnoreCase)) &&
            (name.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase) || name.Equals("chrome_crashpad_handler.exe", StringComparison.OrdinalIgnoreCase)))
            return "Claude 桌面辅助进程";

        if (!new[] { "node.exe", "node", "bun.exe", "bun" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        var valueOptions = new HashSet<string>(StringComparer.Ordinal) { "-r", "--require", "--import", "--loader", "--experimental-loader", "--conditions", "-C", "--icu-data-dir", "--openssl-config", "--title" };
        var switches = new HashSet<string>(StringComparer.Ordinal) { "--enable-source-maps", "--no-warnings", "--trace-warnings", "--trace-deprecation", "--no-deprecation", "--inspect", "--inspect-brk", "--preserve-symlinks", "--preserve-symlinks-main" };
        for (int i = 1; i < process.Arguments.Count; i++)
        {
            string argument = process.Arguments[i];
            if (argument is "--eval" or "--print" || argument.StartsWith("-e", StringComparison.Ordinal) || argument.StartsWith("-p", StringComparison.Ordinal) || argument.StartsWith("--eval=", StringComparison.Ordinal) || argument.StartsWith("--print=", StringComparison.Ordinal)) return null;
            if (argument == "--") return i + 1 < process.Arguments.Count && ClaudeScript(process.Arguments[i + 1]) ? "Claude Code（Node）" : null;
            if (valueOptions.Contains(argument)) { i++; continue; }
            if (switches.Contains(argument)) continue;
            if (argument.StartsWith("--", StringComparison.Ordinal) && argument.Contains('=')) continue;
            if (argument.StartsWith('-')) return null;
            return ClaudeScript(argument) ? "Claude Code（Node）" : null;
        }
        return null;
    }

    public static HashSet<int> Protected(IReadOnlyDictionary<int, ProcessRecord> snapshot, int self)
    {
        var result = new HashSet<int> { 0, 4 };
        while (self > 0 && result.Add(self) && snapshot.TryGetValue(self, out var process)) self = process.ParentId;
        return result;
    }

    public static bool SameCreationMicroseconds(long nativeFileTime, long observedFileTime) =>
        nativeFileTime > 0 && observedFileTime > 0 && nativeFileTime / 10 == observedFileTime / 10;

    public static bool CanEstablishAncestors(IReadOnlyDictionary<int, ProcessRecord> verified,
        IReadOnlyDictionary<int, int> observedParents, int self)
    {
        if (!verified.ContainsKey(self)) return false;
        var visited = new HashSet<int>();
        while (self is not 0 and not 4)
        {
            if (!visited.Add(self)) return false;
            if (!verified.TryGetValue(self, out var process)) return !observedParents.ContainsKey(self);
            self = process.ParentId;
        }
        return true;
    }

    // Used only to flag incomplete coverage, never to authorize a signal without identity.
    public static HashSet<int> PotentialDescendants(IReadOnlyDictionary<int, int> parents, IEnumerable<int> roots)
    {
        var possible = roots.ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var (child, parent) in parents)
                if (possible.Contains(parent) && possible.Add(child)) changed = true;
        } while (changed);
        return possible;
    }

    public static void Expand(Snapshot snapshot, Dictionary<int, ProcessRecord> targets,
        int self, string sid, int session)
    {
        var protectedIds = Protected(snapshot.Processes, self);
        foreach (int pid in targets.Keys.ToArray())
        {
            if (!snapshot.Processes.TryGetValue(pid, out var current) || !targets[pid].SameIdentity(current)) targets.Remove(pid);
            else targets[pid] = current with { Kind = targets[pid].Kind };
        }
        foreach (var process in snapshot.Processes.Values)
        {
            if (process.OwnerSid != sid || process.SessionId != session) continue;
            string? kind = RootKind(process);
            if (kind is not null) targets[process.Id] = process with { Kind = protectedIds.Contains(process.Id) ? "受保护的祖先进程：" + kind : kind };
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var process in snapshot.Processes.Values)
            {
                if (targets.ContainsKey(process.Id) || protectedIds.Contains(process.Id) ||
                    process.OwnerSid != sid || process.SessionId != session ||
                    !targets.TryGetValue(process.ParentId, out var parent) ||
                    !snapshot.Processes.TryGetValue(parent.Id, out var currentParent) ||
                    !parent.SameIdentity(currentParent) || protectedIds.Contains(parent.Id) || process.Created < parent.Created) continue;
                targets[process.Id] = process with { Kind = "Claude 子进程" };
                changed = true;
            }
        } while (changed);
    }
}

public sealed class Controller(IProcessSystem system)
{
    private Snapshot Refresh(Dictionary<int, ProcessRecord> targets)
    {
        Snapshot snapshot = system.Capture(targets.Values.ToArray());
        ProcessRules.Expand(snapshot, targets, system.CurrentId, system.CurrentSid, system.CurrentSession);
        return snapshot;
    }

    public ControlResult Check()
    {
        var targets = new Dictionary<int, ProcessRecord>();
        var snapshot = Refresh(targets);
        return new(targets.Count, targets.Values.OrderBy(p => p.Id).ToArray(), snapshot.Warnings);
    }

    public ControlResult Close()
    {
        var targets = new Dictionary<int, ProcessRecord>();
        var warnings = new HashSet<string>();
        Snapshot snapshot = Refresh(targets);
        int initial = targets.Count;
        foreach (string warning in snapshot.Warnings) warnings.Add(warning);
        if (!snapshot.ProtectionComplete)
            return new(initial, targets.Values.OrderBy(p => p.Id).ToArray(), snapshot.Warnings);
        // WM_CLOSE is directed only to identified target windows. Never send Ctrl+C to a shared console.
        foreach (var target in targets.Values.ToArray())
        {
            if (ProcessRules.Protected(snapshot.Processes, system.CurrentId).Contains(target.Id))
            {
                warnings.Add($"PID {target.Id} 是本工具的祖先进程，已保护；请从桌面独立打开工具后再关闭。");
                continue;
            }
            string? error = system.RequestClose(target);
            if (error is not null) warnings.Add(error);
        }
        if (targets.Count > 0) system.Delay(TimeSpan.FromMilliseconds(1200));
        for (int pass = 0; pass < 2; pass++)
        {
            snapshot = Refresh(targets);
            foreach (string warning in snapshot.Warnings) warnings.Add(warning);
            if (!snapshot.ProtectionComplete) break;
            foreach (var target in targets.Values.ToArray())
            {
                if (ProcessRules.Protected(snapshot.Processes, system.CurrentId).Contains(target.Id)) continue;
                string? error = system.Terminate(target);
                if (error is not null) warnings.Add(error);
            }
            system.Delay(TimeSpan.FromMilliseconds(500));
        }
        snapshot = Refresh(targets);
        foreach (string warning in snapshot.Warnings) warnings.Add(warning);
        return new(initial, targets.Values.OrderBy(p => p.Id).ToArray(), warnings.Order().ToArray());
    }
}
