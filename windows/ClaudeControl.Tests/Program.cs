using System.Diagnostics;
using ClaudeControl.Core;
using ClaudeControl.Windows;

if (args.Contains("--fixture")) { Thread.Sleep(60000); return 0; }
return Tests.Run();

internal static class Tests
{
    private static int checks;
    private const string Sid = "S-1-5-21-100";
    private static void Assert(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception("FAIL: " + message);
    }
    private static ProcessRecord P(int id, int parent, string path, string[]? arguments = null, long created = 1000, string sid = Sid, int session = 1) =>
        new(id, parent, created, sid, session, path, arguments ?? []);
    private static Snapshot S(params ProcessRecord[] processes) => new(processes.ToDictionary(p => p.Id), []);

    public static int Run()
    {
        try
        {
            Matching(); Identity(); Descendants(); Flow();
            if (OperatingSystem.IsWindows()) NativeIntegration();
            else Console.WriteLine("Windows native integration skipped on this OS; run test.ps1 on Windows.");
            Console.WriteLine($"{checks} checks passed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Matching()
    {
        foreach (string path in new[] { @"C:\Users\me\AppData\Local\AnthropicClaude\Claude.exe", @"C:\bin\claude.exe", @"C:\bin\CLAUDE.EXE", @"C:\Users\me\.local\share\claude\versions\2.1\claude.exe", @"C:\Users\me\AppData\Local\AnthropicClaude\app-1\Claude Helper.exe" })
            Assert(ProcessRules.RootKind(P(10, 1, path)) is not null, "detect exact executable " + path);
        foreach (string path in new[] { @"C:\notes\Claude\editor.exe", @"C:\bin\claude-helper.exe", @"C:\bin\claude.exe.bak", @"C:\ClaudeControl.exe", @"C:\Google\Chrome\chrome.exe" })
            Assert(ProcessRules.RootKind(P(10, 1, path, ["program", "claude"])) is null, "exclude unrelated executable " + path);
        const string script = @"C:\Program Files\nodejs\node_modules\@anthropic-ai\claude-code\cli.js";
        foreach (string[] argv in new[] { new[] { "node.exe", script }, ["node.exe", "--enable-source-maps", script], ["node.exe", "--require", "module", script], ["node.exe", "--", script], ["node.exe", "node_modules/@anthropic-ai/claude-code/cli.js"] })
            Assert(ProcessRules.RootKind(P(10, 1, @"C:\node.exe", argv)) is not null, "detect true Node script entry");
        foreach (string[] argv in new[] { new[] { "node.exe", "other.js", script }, ["node.exe", "-e", script], ["node.exe", "-econsole.log(1)", script], ["node.exe", "-p1+1", script], ["node.exe", "-rmodule", script], ["node.exe", "--title", script, "other.js"], ["node.exe", "--unknown-value-option", script, "other.js"], ["node.exe", "--eval=" + script], ["node.exe", "--require", script, "other.js"], ["node.exe", "--", "other.js", script], ["node.exe", "https://claude.ai"], ["node.exe", script + ".bak"] })
            Assert(ProcessRules.RootKind(P(10, 1, @"C:\node.exe", argv)) is null, "exclude script text in data");
        Assert(ProcessRules.RootKind(P(10, 1, @"C:\cmd.exe", ["cmd", script])) is null, "exclude non-runtime script-like args");
        Assert(ProcessRules.RootKind(P(10, 1, @"C:\bun.exe", ["bun", script])) is not null, "detect Bun");
        Assert(ProcessRules.Under(@"C:\Claude\a.exe", @"C:\Claude"), "directory boundary positive");
        Assert(!ProcessRules.Under(@"C:\Claude-other\a.exe", @"C:\Claude"), "directory boundary negative");
    }

    private static void Identity()
    {
        ProcessRecord original = P(10, 1, @"C:\claude.exe");
        Assert(original.SameIdentity(original), "same identity");
        Assert(!original.SameIdentity(null), "missing identity");
        Assert(!original.SameIdentity(original with { Id = 11 }), "PID differs");
        Assert(!original.SameIdentity(original with { Created = 1001 }), "birth differs");
        Assert(!original.SameIdentity(original with { OwnerSid = "other" }), "owner differs");
        Assert(!original.SameIdentity(original with { SessionId = 2 }), "session differs");
        Assert(original.SameIdentity(original with { ImagePath = @"C:\python.exe" }), "exec keeps identity");
        Assert(ProcessRules.SameCreationMicroseconds(123456789, 123456780), "join WMI/native at microsecond precision");
        Assert(!ProcessRules.SameCreationMicroseconds(123456789, 123456790), "reject WMI row from reused PID");
    }

    private static void Descendants()
    {
        var root = P(10, 1, @"C:\claude.exe");
        var child = P(11, 10, @"C:\cmd.exe", created: 1001);
        var grandchild = P(12, 11, @"C:\python.exe", created: 1002);
        var unrelated = P(13, 1, @"C:\python.exe");
        var targets = new Dictionary<int, ProcessRecord>();
        ProcessRules.Expand(S(root, child, grandchild, unrelated), targets, 90, Sid, 1);
        Assert(targets.Count == 3 && !targets.ContainsKey(13), "only descendants");
        ProcessRules.Expand(S(child with { ParentId = 1, ImagePath = @"C:\node.exe" }, grandchild), targets, 90, Sid, 1);
        Assert(targets.Count == 2 && targets.ContainsKey(12), "keep captured orphans after parent exit and exec");
        var reused = child with { Created = 2000 };
        ProcessRules.Expand(S(reused, unrelated with { ParentId = 11 }), targets, 90, Sid, 1);
        Assert(targets.Count == 0, "reused parent cannot claim descendants");
        ProcessRules.Expand(S(root, child with { OwnerSid = "other" }, grandchild, unrelated with { SessionId = 2 }), targets, 90, Sid, 1);
        Assert(targets.Count == 1 && targets.ContainsKey(10), "exclude other user/session");
        var olderChild = child with { Created = 900 };
        targets.Clear();
        ProcessRules.Expand(S(root, olderChild), targets, 90, Sid, 1);
        Assert(targets.Count == 1, "parent PID cannot adopt older unrelated child");
        targets.Clear();
        ProcessRules.Expand(S(root, child, P(90, 10, @"C:\ClaudeControl.exe")), targets, 90, Sid, 1);
        Assert(targets.Count == 1 && targets[10].Kind.Contains("受保护"), "observable ancestor remains visible without claiming siblings");
        var possible = ProcessRules.PotentialDescendants(new Dictionary<int, int> { [10] = 1, [11] = 10, [12] = 11, [13] = 1 }, [10]);
        Assert(possible.SetEquals([10, 11, 12]), "unreadable child lineage produces coverage warning candidates without unrelated processes");
        Assert(!ProcessRules.CanEstablishAncestors(new Dictionary<int, ProcessRecord> { [90] = P(90, 15, "tool") }, new Dictionary<int, int> { [90] = 15, [15] = 10, [10] = 1 }, 90), "unreadable live intermediate ancestor is unsafe");
        Assert(ProcessRules.CanEstablishAncestors(new Dictionary<int, ProcessRecord> { [90] = P(90, 15, "tool") }, new Dictionary<int, int> { [90] = 15 }, 90), "already-exited parent ends observable live chain");
        Assert(!ProcessRules.CanEstablishAncestors(new Dictionary<int, ProcessRecord>(), new Dictionary<int, int>(), 90), "missing self cannot establish safe ancestry");
    }

    private static void Flow()
    {
        var root = P(10, 1, @"C:\claude.exe");
        var child = P(11, 10, @"C:\cmd.exe", created: 1001);
        var fake = new FakeSystem(S(root, child));
        ControlResult check = new Controller(fake).Check();
        Assert(check.Remaining.Count == 2 && fake.Killed.Count == 0, "check never kills");
        ControlResult close = new Controller(fake).Close();
        Assert(close.ConfirmedClosed && close.InitialCount == 2 && fake.Killed.Count == 2, "close tracked tree and recheck");
        Assert(fake.CapturedTracked.Contains(11), "refresh explicitly supplies tracked arbitrary child identities");
        var denied = new FakeSystem(S(root)) { DenyKill = true };
        close = new Controller(denied).Close();
        Assert(!close.ConfirmedClosed && close.Remaining.Count == 1 && close.Warnings.Count > 0, "access failure cannot claim success");
        var incomplete = new FakeSystem(new Snapshot(new Dictionary<int, ProcessRecord>(), ["unreadable Claude"]));
        Assert(!new Controller(incomplete).Check().ConfirmedClosed, "incomplete scan cannot claim zero success");
        var uncertainAncestor = new FakeSystem(new Snapshot(S(root).Processes, ["ancestor unreadable"], false));
        close = new Controller(uncertainAncestor).Close();
        Assert(uncertainAncestor.Killed.Count == 0 && close.Remaining.Count == 1, "incomplete ancestor protection forbids all close actions");
        var protectedSystem = new FakeSystem(S(root, P(90, 10, @"C:\ClaudeControl.exe"), child));
        close = new Controller(protectedSystem).Close();
        Assert(close.Remaining.Count == 1 && protectedSystem.Killed.Count == 0 && close.Warnings.Count > 0, "ancestor stays visible and is never killed");
        var reused = new FakeSystem(S(root)) { ReplaceBeforeKill = true };
        close = new Controller(reused).Close();
        Assert(reused.Killed.Count == 0, "backend identity guard refuses replacement PID");
    }

    private static void NativeIntegration()
    {
        // Isolated fixture apphosts only. Never call Controller.Close() on the real system.
        string source = Environment.ProcessPath ?? throw new Exception("no apphost path");
        Assert(Path.GetFileName(source).EndsWith(".exe", StringComparison.OrdinalIgnoreCase), "Windows fixture has apphost");
        string directory = Path.Combine(Path.GetTempPath(), "ClaudeControlTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string folder in Directory.GetDirectories(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(directory, Path.GetRelativePath(AppContext.BaseDirectory, folder)));
            foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(directory, Path.GetRelativePath(AppContext.BaseDirectory, file)));
            foreach (string name in new[] { "claude.exe", "node.exe" })
            {
                string executable = Path.Combine(directory, name);
                File.Copy(source, executable, true);
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                if (name == "node.exe") start.ArgumentList.Add(@"C:\fixture path\node_modules\@anthropic-ai\claude-code\cli.js");
                start.ArgumentList.Add("--fixture");
                using Process owned = Process.Start(start) ?? throw new Exception("cannot start owned fixture");
                try
                {
                    Thread.Sleep(250);
                    Assert(!owned.HasExited, "owned fixture reaches its sleep branch");
                    var system = new WindowsProcessSystem();
                    ProcessRecord identity = system.Read(owned.Id, Environment.ProcessId, out int error) ?? throw new Exception("cannot read fixture: " + error);
                    Assert(identity.OwnerSid == system.CurrentSid && identity.SessionId == system.CurrentSession, "native owner/session");
                    Assert(Path.GetFileName(identity.ImagePath) == name, "native executable path");
                    Snapshot snapshot = system.Capture();
                    Assert(snapshot.Processes.TryGetValue(owned.Id, out var captured) && identity.SameIdentity(captured), "WMI snapshot joins real identity");
                    Assert(ProcessRules.RootKind(snapshot.Processes[owned.Id]) is not null, "native exact name/script detection");
                    if (name == "node.exe")
                        Assert(snapshot.Processes[owned.Id].Arguments.Count == 3 && snapshot.Processes[owned.Id].Arguments[1] == @"C:\fixture path\node_modules\@anthropic-ai\claude-code\cli.js", "CommandLineToArgvW preserves actual quoted script entry");
                    Assert(system.Terminate(identity with { Created = identity.Created + 1 }) is null && !owned.HasExited, "native mismatch does not terminate");
                    Assert(system.Terminate(identity) is null, "terminate only owned verified fixture");
                    Assert(owned.WaitForExit(5000), "owned fixture exits");
                }
                finally { if (!owned.HasExited) { owned.Kill(); owned.WaitForExit(); } }
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FakeSystem(Snapshot snapshot) : IProcessSystem
    {
        private Snapshot state = snapshot;
        public int CurrentId => 90;
        public string CurrentSid => Sid;
        public int CurrentSession => 1;
        public bool DenyKill { get; init; }
        public bool ReplaceBeforeKill { get; init; }
        public List<int> Killed { get; } = [];
        public HashSet<int> CapturedTracked { get; } = [];
        public Snapshot Capture(IReadOnlyCollection<ProcessRecord>? tracked = null)
        {
            foreach (var process in tracked ?? []) CapturedTracked.Add(process.Id);
            return state;
        }
        public string? RequestClose(ProcessRecord expected) => null;
        public string? Terminate(ProcessRecord expected)
        {
            if (DenyKill) return "access denied";
            var values = state.Processes.ToDictionary(p => p.Key, p => p.Value);
            if (ReplaceBeforeKill && values.TryGetValue(expected.Id, out var replaced)) values[expected.Id] = replaced with { Created = replaced.Created + 1, ImagePath = @"C:\unrelated.exe" };
            if (values.TryGetValue(expected.Id, out var current) && expected.SameIdentity(current)) { values.Remove(expected.Id); Killed.Add(expected.Id); }
            state = new(values, state.Warnings);
            return null;
        }
        public void Delay(TimeSpan duration) { }
    }
}
