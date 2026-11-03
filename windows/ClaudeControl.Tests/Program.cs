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
            CoworkServiceTests.Run(Assert);
            foreach (uint type in new uint[] { 0x10, 0x210 })
                Assert(CoworkServiceSystem.IsSupportedServiceType(type), "accept independent ordinary/packaged Cowork service type " + type);
            foreach (uint type in new uint[] { 0, 1, 2, 0x20, 0x110, 0x220, 0x410, 0x201 })
                Assert(!CoworkServiceSystem.IsSupportedServiceType(type), "reject shared/driver/unknown service type " + type);
            if (OperatingSystem.IsWindows()) { NativeIntegration(); NativeServiceReadOnly(); }
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
        foreach (string[] argv in new[] { new[] { "node.exe", "--trace-uncaught", script }, ["node.exe", "--abort-on-uncaught-exception", script], ["node.exe", "--expose-gc", script], ["node.exe", "--unhandled-rejections", "strict", script], ["node.exe", "--dns-result-order", "ipv4first", script], ["node.exe", "--title=claude-task", "--inspect=127.0.0.1:0", script] })
            Assert(ProcessRules.RootKind(P(10, 1, @"C:\node.exe", argv)) is not null, "detect supported Node flags before true entry");
        foreach (string[] argv in new[] { new[] { "node.exe", "other.js", script }, ["node.exe", "-e", script], ["node.exe", "-econsole.log(1)", script], ["node.exe", "-p1+1", script], ["node.exe", "-rmodule", script], ["node.exe", "--title", script, "other.js"], ["node.exe", "--unknown-value-option", script, "other.js"], ["node.exe", "--eval=" + script], ["node.exe", "--require", script, "other.js"], ["node.exe", "--", "other.js", script], ["node.exe", "https://claude.ai"], ["node.exe", script + ".bak"] })
            Assert(ProcessRules.RootKind(P(10, 1, @"C:\node.exe", argv)) is null, "exclude script text in data");
        Assert(ProcessRules.RootKind(P(10, 1, @"C:\cmd.exe", ["cmd", script])) is null, "exclude non-runtime script-like args");
        Assert(ProcessRules.RootKind(P(10, 1, @"C:\bun.exe", ["bun", script])) is not null, "detect Bun");
        Assert(ProcessRules.RootKind(P(10, 1, @"C:\bun.exe", ["bun", "run", script])) is not null, "Bun run absolute source entry");
        Assert(ProcessRules.RootKind(P(10, 1, @"C:\bun.exe", ["bun", "run", @".\node_modules\@anthropic-ai\claude-code\cli.js"])) is not null, "Bun run explicit relative source entry");
        var bunAmbiguous = P(10, 1, @"C:\bun.exe", ["bun", "run", @"node_modules\@anthropic-ai\claude-code\cli.js"]);
        Assert(ProcessRules.RootKind(bunAmbiguous) is null && ProcessRules.RootWarning(bunAmbiguous) is not null, "Bun run unprefixed name could be package script and is not authorized");
        Assert(ProcessRules.RootKind(P(10, 1, @"C:\bun.exe", ["bun", "run", "dev", script])) is null, "Bun package-script tail data is not a source entry");
        foreach (string[] argv in new[] { new[] { "node.exe", "--run=serve", script }, ["node.exe", "--run", "serve", script], ["node.exe", "--eval=code", script] })
        {
            var process = P(10, 1, @"C:\node.exe", argv);
            Assert(ProcessRules.RootKind(process) is null && ProcessRules.RootWarning(process) is null, "explicit non-entry mode is neither target nor uncertain script");
        }
        foreach (string option in new[] { "--unknown-value-option", "--unknown=value", "-rmodule" })
        {
            var process = P(10, 1, @"C:\node.exe", ["node", option, script]);
            Assert(ProcessRules.RootKind(process) is null && ProcessRules.RootWarning(process) is not null, "unknown options with exact script clue warn without authorizing close");
            Assert(ProcessRules.RootWarning(P(10, 1, @"C:\node.exe", ["node", option, "unrelated.js"])) is null, "unknown ordinary Node process does not produce Claude warning");
        }
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
        var lateParent = root with { Created = 2000 };
        var olderSelf = P(90, 10, @"C:\ClaudeControl.exe", created: 1000);
        var reusedAncestors = new Dictionary<int, ProcessRecord> { [90] = olderSelf, [10] = lateParent };
        Assert(!ProcessRules.CanEstablishAncestors(reusedAncestors, new Dictionary<int, int> { [90] = 10, [10] = 1 }, 90), "reused newer parent PID cannot claim safe ancestry");
        Assert(!ProcessRules.Protected(reusedAncestors, 90).Contains(10), "newer unrelated parent PID is not labeled an actual ancestor");
        Assert(!ProcessRules.CanEstablishAncestors(new Dictionary<int, ProcessRecord> { [90] = olderSelf }, new Dictionary<int, int> { [90] = 10 }, 90, new HashSet<int> { 10 }), "tracked unreadable ancestor remains unsafe even if WMI omitted its row");

        targets.Clear();
        ProcessRules.Expand(S(root, child), targets, 90, Sid, 1);
        ProcessRules.Expand(new Snapshot(new Dictionary<int, ProcessRecord>(), ["child unreadable"], true, new HashSet<int> { child.Id }), targets, 90, Sid, 1);
        Assert(targets.Count == 1 && targets[child.Id].SameIdentity(child), "temporary unreadability retains previously captured child identity");
        ProcessRules.Expand(S(child with { ParentId = 1 }, grandchild), targets, 90, Sid, 1);
        Assert(targets.Count == 2 && targets.ContainsKey(grandchild.Id), "recovered orphan child is rechecked and can identify its new descendants");
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
        var recovery = new RecoveringSystem(root, child);
        close = new Controller(recovery).Close();
        Assert(recovery.Killed.SequenceEqual([child.Id]), "full close flow retries only the captured child after transient read denial");
        Assert(recovery.ReceivedTrackedAfterFailure, "captured uncertain identity is supplied to the next native refresh");
        Assert(close.Remaining.Count == 0 && close.Warnings.Count > 0, "transient failure remains reported even after retry succeeds");

        var detached = child with { ParentId = 1, Kind = "Claude 子进程" };
        var detachedSystem = new FakeSystem(S(detached));
        Assert(new Controller(detachedSystem).Check().Remaining.Count == 0, "fresh root scan cannot rediscover a detached arbitrary child");
        check = new Controller(detachedSystem).Check([detached]);
        Assert(check.Remaining.Count == 1 && check.Remaining[0].SameIdentity(detached) && detachedSystem.Killed.Count == 0,
            "post-service read-only check retains a captured detached child without terminating it");
        var replacedChild = new FakeSystem(S(detached with { Created = detached.Created + 1 }));
        Assert(new Controller(replacedChild).Check([detached]).Remaining.Count == 0 && replacedChild.Killed.Count == 0,
            "post-service check drops a reused child PID without treating replacement as a target");
        var unreadableChild = new FakeSystem(new Snapshot(new Dictionary<int, ProcessRecord>(), ["child unreadable"], true, new HashSet<int> { detached.Id }));
        check = new Controller(unreadableChild).Check([detached]);
        Assert(check.Remaining.Count == 1 && !check.ConfirmedClosed, "post-service read failure preserves the captured child and prevents false zero");
        var foreignTracked = new FakeSystem(S());
        new Controller(foreignTracked).Check([detached with { OwnerSid = "other" }, root with { SessionId = 2 }]);
        Assert(foreignTracked.CapturedTracked.Count == 0, "post-service tracking does not widen original user/session scope");
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
            // Verify ambiguous runtime options reach the real capture warning path,
            // rather than silently producing a complete zero-process result.
            var ambiguousStart = new ProcessStartInfo(Path.Combine(directory, "node.exe")) { UseShellExecute = false, CreateNoWindow = true };
            ambiguousStart.ArgumentList.Add("--claude-control-unknown=1");
            ambiguousStart.ArgumentList.Add(@"C:\fixture path\node_modules\@anthropic-ai\claude-code\cli.js");
            ambiguousStart.ArgumentList.Add("--fixture");
            using Process ambiguousOwned = Process.Start(ambiguousStart) ?? throw new Exception("cannot start ambiguous owned fixture");
            try
            {
                Thread.Sleep(250);
                Assert(!ambiguousOwned.HasExited, "ambiguous owned fixture reaches sleep branch");
                var system = new WindowsProcessSystem();
                ProcessRecord identity = system.Read(ambiguousOwned.Id, Environment.ProcessId, out int error) ?? throw new Exception("cannot read ambiguous fixture: " + error);
                Snapshot snapshot = system.Capture();
                Assert(ProcessRules.RootKind(snapshot.Processes[ambiguousOwned.Id]) is null, "unknown runtime option never authorizes native fixture close");
                Assert(snapshot.Warnings.Any(warning => warning.Contains($"PID {ambiguousOwned.Id}")), "native capture reports uncertain script entry");
                Assert(system.Terminate(identity) is null && ambiguousOwned.WaitForExit(5000), "clean up only ambiguous owned fixture");
            }
            finally { if (!ambiguousOwned.HasExited) { ambiguousOwned.Kill(); ambiguousOwned.WaitForExit(); } }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void NativeServiceReadOnly()
    {
        // All service tests here are read-only: never call StopVerified/Close on
        // a Windows backend, and never install a fixture service on the host.
        CoworkServiceResult service = new CoworkServiceController(new CoworkServiceSystem()).Check();
        Assert(service.Warnings.Count == 0, "native read-only Cowork SCM query completes on the CI host");
        Assert(service.Snapshot is null || service.Snapshot.Name == CoworkServiceController.ServiceName,
            "native Cowork inspection cannot return an unrelated service");
        string microsoft = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        Assert(File.Exists(microsoft), "native publisher rejection fixture is a system binary");
        CoworkPublisherVerification other = CoworkServiceSystem.VerifyPublisherFile(microsoft);
        Assert(!other.IsAnthropic, "native offline signature validation never accepts Microsoft as Anthropic");
        Assert(!other.SignatureValid || other.CertificateFingerprint.Length == 64,
            "a trusted native signer is extracted from the verified chain");
        string unsigned = Path.Combine(Path.GetTempPath(), "claude-control-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(unsigned, [0x4d, 0x5a, 0, 0]);
            CoworkPublisherVerification invalid = CoworkServiceSystem.VerifyPublisherFile(unsigned);
            Assert(!invalid.SignatureValid && !invalid.IsAnthropic, "unsigned native lookalike cannot pass publisher verification");
        }
        finally { if (File.Exists(unsigned)) File.Delete(unsigned); }
        CoworkPublisherVerification missing = CoworkServiceSystem.VerifyPublisherFile(unsigned);
        Assert(!missing.SignatureValid && !missing.IsAnthropic, "missing publisher file is rejected without any service action");
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
            state = new(values, state.Warnings, state.ProtectionComplete, state.UncertainIds);
            return null;
        }
        public void Delay(TimeSpan duration) { }
    }

    private sealed class RecoveringSystem(ProcessRecord root, ProcessRecord child) : IProcessSystem
    {
        private int capture;
        private bool readable;
        public int CurrentId => 90;
        public string CurrentSid => Sid;
        public int CurrentSession => 1;
        public List<int> Killed { get; } = [];
        public bool ReceivedTrackedAfterFailure { get; private set; }
        public Snapshot Capture(IReadOnlyCollection<ProcessRecord>? tracked = null)
        {
            int current = capture++;
            if (current == 0) return S(root, child);
            if (current == 1) return new(new Dictionary<int, ProcessRecord>(), ["owned child temporarily unreadable"], true, new HashSet<int> { child.Id });
            ReceivedTrackedAfterFailure |= tracked?.Any(p => p.SameIdentity(child)) == true;
            readable = true;
            return Killed.Count == 0 ? S(child with { ParentId = 1 }) : S();
        }
        public string? RequestClose(ProcessRecord expected) => null;
        public string? Terminate(ProcessRecord expected)
        {
            if (!readable) return "owned child temporarily unreadable";
            if (expected.SameIdentity(child)) Killed.Add(expected.Id);
            return null;
        }
        public void Delay(TimeSpan duration) { }
    }
}
