using ClaudeControl.Core;

// This suite never creates a Windows backend, opens SCM, or starts/stops a real
// service. Every status, STOP reply and delay comes from the in-memory fake.
internal static class CoworkServiceTests
{
    private const string Executable = @"C:\Program Files\Claude\CoworkVMService.exe";
    private const string Identity = "verified-config-and-file-identity-v1";

    public static void Run(Action<bool, string> assert)
    {
        ReadOnlyChecks(assert);
        NoStopWithoutAuthority(assert);
        SuccessfulStopAndPolling(assert);
        FailedStopsAndReadback(assert);
        ConfigurationChanges(assert);
        RestartAfterStopped(assert);
    }

    private static CoworkServiceSnapshot View(CoworkServiceState state,
        bool verified = true, string identity = Identity, string name = CoworkServiceController.ServiceName) =>
        new(name, state, Executable, verified, identity, "fixture trust result");

    private static CoworkServiceResult Result(CoworkServiceState state,
        bool verified = true, string identity = Identity, string name = CoworkServiceController.ServiceName) =>
        new(View(state, verified, identity, name), []);

    private static CoworkServiceResult Absent() => new(null, []);

    private static void ReadOnlyChecks(Action<bool, string> assert)
    {
        foreach (CoworkServiceState state in Enum.GetValues<CoworkServiceState>())
        {
            var fake = new FakeSystem(Result(state));
            CoworkServiceResult result = new CoworkServiceController(fake).Check();
            assert(result.Snapshot?.State == state, "Cowork check preserves observed state " + state);
            assert(result.ConfirmedStopped == (state == CoworkServiceState.Stopped),
                "Cowork check confirms only verified stopped state " + state);
            assert(fake.StopCalls.Count == 0 && fake.Delays.Count == 0 && fake.InspectCalls == 1,
                "Cowork check is a single read with no STOP or wait " + state);
        }
        var absent = new FakeSystem(Absent());
        assert(new CoworkServiceController(absent).Check().ConfirmedStopped,
            "Cowork check accepts explicitly established service absence");
        assert(absent.StopCalls.Count == 0, "Cowork absent check never sends STOP");

        var unreadable = new FakeSystem(new CoworkServiceResult(null, ["fixture: service query failed"]));
        CoworkServiceResult failed = new CoworkServiceController(unreadable).Check();
        assert(!failed.ConfirmedStopped && failed.Warnings.Contains("fixture: service query failed"),
            "Cowork failed query with no snapshot is not service absence");
    }

    private static void NoStopWithoutAuthority(Action<bool, string> assert)
    {
        foreach (CoworkServiceResult initial in new[] { Absent(), Result(CoworkServiceState.Stopped) })
        {
            var fake = new FakeSystem(initial);
            CoworkServiceResult closed = new CoworkServiceController(fake).Close();
            assert(closed.ConfirmedStopped, "Cowork absent/already stopped service is confirmed without mutation");
            assert(fake.StopCalls.Count == 0 && fake.Delays.Count == 0,
                "Cowork absent/already stopped service does not receive STOP or polling");
        }
        foreach (CoworkServiceState state in new[] { CoworkServiceState.Running, CoworkServiceState.Stopped })
        {
            var fake = new FakeSystem(Result(state, verified: false));
            CoworkServiceResult closed = new CoworkServiceController(fake).Close();
            assert(!closed.ConfirmedStopped && closed.Warnings.Count > 0,
                "Cowork unverified identity never confirms closed even with state " + state);
            assert(fake.StopCalls.Count == 0 && fake.Delays.Count == 0,
                "Cowork unverified identity is rejected before STOP or waiting " + state);
        }
        var wrongName = new FakeSystem(Result(CoworkServiceState.Running, name: "OtherService"));
        CoworkServiceResult wrong = new CoworkServiceController(wrongName).Close();
        assert(!wrong.ConfirmedStopped && wrong.Warnings.Count > 0 && wrongName.StopCalls.Count == 0,
            "Cowork a verified unrelated service name is not authorized for STOP");

        var warning = new FakeSystem(new CoworkServiceResult(View(CoworkServiceState.Running), ["fixture: trust read incomplete"]));
        CoworkServiceResult warned = new CoworkServiceController(warning).Close();
        assert(!warned.ConfirmedStopped && warned.Warnings.Contains("fixture: trust read incomplete") && warning.StopCalls.Count == 0,
            "Cowork initial coverage/trust warning is retained and forbids STOP");

        foreach (CoworkServiceSnapshot? snapshot in new CoworkServiceSnapshot?[]
        {
            null, View(CoworkServiceState.Running), View(CoworkServiceState.Stopped)
        })
        {
            var denied = new FakeSystem(new CoworkServiceResult(snapshot, [], RequiresElevation: true));
            CoworkServiceResult closed = new CoworkServiceController(denied).Close();
            assert(closed.RequiresElevation && !closed.ConfirmedStopped,
                "Cowork access-denied query preserves elevation requirement independently of snapshot");
            assert(denied.StopCalls.Count == 0 && denied.Delays.Count == 0,
                "Cowork access-denied query never authorizes STOP");
        }
    }

    private static void SuccessfulStopAndPolling(Action<bool, string> assert)
    {
        foreach (CoworkServiceState initial in new[] { CoworkServiceState.Running, CoworkServiceState.Paused })
        {
            var fake = new FakeSystem(Result(initial), Result(CoworkServiceState.StopPending), Result(CoworkServiceState.Stopped));
            CoworkServiceResult closed = new CoworkServiceController(fake).Close();
            assert(closed.ConfirmedStopped && closed.Warnings.Count == 0,
                "Cowork verified " + initial + " service stops and passes readback");
            assert(fake.StopCalls.Count == 1 && fake.StopCalls[0] == View(initial),
                "Cowork STOP receives the exact originally verified service identity " + initial);
            assert(fake.InspectCalls >= 4 && fake.Delays.Count >= 3,
                "Cowork successful STOP is polled and stopped is independently checked again " + initial);
            assert(fake.Events.Take(4).SequenceEqual(["inspect", "stop", "delay", "inspect"]),
                "Cowork validates before STOP and reads again after a real delay " + initial);
        }
        var synchronous = new FakeSystem(Result(CoworkServiceState.Running), Result(CoworkServiceState.Stopped))
        {
            StopReply = _ => Result(CoworkServiceState.Stopped)
        };
        assert(new CoworkServiceController(synchronous).Close().ConfirmedStopped,
            "Cowork synchronous STOP result can succeed after independent stopped readbacks");
        assert(synchronous.StopCalls.Count == 1 && synchronous.InspectCalls >= 3,
            "Cowork STOP reply saying stopped never replaces the post-STOP observations");

        var alreadyPending = new FakeSystem(Result(CoworkServiceState.StopPending), Result(CoworkServiceState.Stopped));
        assert(new CoworkServiceController(alreadyPending).Close().ConfirmedStopped,
            "Cowork preexisting STOP pending can finish successfully");
        assert(alreadyPending.StopCalls.Count == 0 && alreadyPending.InspectCalls >= 3,
            "Cowork preexisting STOP pending is observed without duplicate STOP");

        var startPending = new FakeSystem(Result(CoworkServiceState.StartPending),
            Result(CoworkServiceState.Running), Result(CoworkServiceState.Stopped));
        assert(new CoworkServiceController(startPending).Close().ConfirmedStopped,
            "Cowork start-pending service is stopped once it reaches running");
        assert(startPending.StopCalls.Count == 1 && startPending.Events.Take(4).SequenceEqual(["inspect", "delay", "inspect", "stop"]),
            "Cowork waits through start-pending before authorizing STOP");

        var lowerCase = new FakeSystem(Result(CoworkServiceState.Running, name: "coworkvmservice"),
            Result(CoworkServiceState.Stopped, name: "coworkvmservice"));
        assert(new CoworkServiceController(lowerCase).Close().ConfirmedStopped && lowerCase.StopCalls.Count == 1,
            "Cowork Windows SCM service-name comparison accepts case differences");

        foreach (CoworkServiceState state in new[]
        {
            CoworkServiceState.StopPending, CoworkServiceState.StartPending,
            CoworkServiceState.ContinuePending, CoworkServiceState.PausePending, CoworkServiceState.Unknown
        })
        {
            var pending = new FakeSystem(Result(state));
            CoworkServiceResult closed = new CoworkServiceController(pending).Close();
            assert(!closed.ConfirmedStopped && closed.Warnings.Count > 0 && closed.Snapshot?.State == state,
                "Cowork unresolved state fails with its final observation " + state);
            assert(pending.StopCalls.Count == 0, "Cowork pending/unknown state is never sent unsupported STOP " + state);
            assert(pending.Delays.Count == CoworkServiceController.MaximumPolls &&
                pending.InspectCalls == CoworkServiceController.MaximumPolls + 1,
                "Cowork unresolved state respects bounded polling " + state);
            assert(pending.Delays.All(delay => delay > TimeSpan.Zero) &&
                pending.Delays.Aggregate(TimeSpan.Zero, (total, delay) => total + delay) <= TimeSpan.FromSeconds(30),
                "Cowork polling has positive virtual delays and a bounded total wait " + state);
        }
        var stopTimeout = new FakeSystem(Result(CoworkServiceState.Running), Result(CoworkServiceState.StopPending));
        CoworkServiceResult timeout = new CoworkServiceController(stopTimeout).Close();
        assert(!timeout.ConfirmedStopped && timeout.Warnings.Count > 0,
            "Cowork accepted STOP that never finishes times out without success");
        assert(stopTimeout.StopCalls.Count == 1 && stopTimeout.Delays.Count == CoworkServiceController.MaximumPolls,
            "Cowork timeout never repeats STOP or exceeds its poll budget");
    }

    private static void FailedStopsAndReadback(Action<bool, string> assert)
    {
        var denied = new FakeSystem(Result(CoworkServiceState.Running))
        {
            StopReply = expected => new(expected, ["fixture: ERROR_ACCESS_DENIED"], RequiresElevation: true)
        };
        CoworkServiceResult deniedResult = new CoworkServiceController(denied).Close();
        assert(deniedResult.RequiresElevation && !deniedResult.ConfirmedStopped &&
            deniedResult.Warnings.Contains("fixture: ERROR_ACCESS_DENIED"),
            "Cowork denied STOP retains the reason and requests elevation");
        assert(denied.StopCalls.Count == 1 && denied.InspectCalls == 1 && denied.Delays.Count == 0,
            "Cowork denied STOP does not retry or present an unverified stopped result");

        var failure = new FakeSystem(Result(CoworkServiceState.Running))
        {
            StopReply = _ => new(View(CoworkServiceState.Stopped), ["fixture: STOP operation failed"])
        };
        CoworkServiceResult failed = new CoworkServiceController(failure).Close();
        assert(!failed.ConfirmedStopped && failed.Warnings.Contains("fixture: STOP operation failed"),
            "Cowork failed STOP warning cannot be erased by a stopped snapshot");
        assert(failure.StopCalls.Count == 1, "Cowork failed STOP is never retried without new authority");

        var readFailure = new FakeSystem(Result(CoworkServiceState.Running),
            new(null, ["fixture: read after STOP failed"]));
        CoworkServiceResult unreadable = new CoworkServiceController(readFailure).Close();
        assert(!unreadable.ConfirmedStopped && unreadable.Warnings.Contains("fixture: read after STOP failed"),
            "Cowork unreadable post-STOP query is not mistaken for service absence");
        assert(readFailure.StopCalls.Count == 1, "Cowork post-STOP read failure cannot authorize another STOP");

        var readDenied = new FakeSystem(Result(CoworkServiceState.Running), new(null, [], RequiresElevation: true));
        CoworkServiceResult deniedRead = new CoworkServiceController(readDenied).Close();
        assert(deniedRead.RequiresElevation && !deniedRead.ConfirmedStopped && readDenied.StopCalls.Count == 1,
            "Cowork denied post-STOP read preserves elevation and cannot confirm shutdown");

        var lostTrust = new FakeSystem(Result(CoworkServiceState.Running), Result(CoworkServiceState.Stopped, verified: false));
        CoworkServiceResult unverified = new CoworkServiceController(lostTrust).Close();
        assert(!unverified.ConfirmedStopped && unverified.Warnings.Count > 0 && lostTrust.StopCalls.Count == 1,
            "Cowork stopped status with lost trust cannot confirm shutdown");

        var gone = new FakeSystem(Result(CoworkServiceState.Running), Absent());
        assert(new CoworkServiceController(gone).Close().ConfirmedStopped && gone.StopCalls.Count == 1,
            "Cowork backend-confirmed service disappearance after STOP is accepted");
    }

    private static void ConfigurationChanges(Action<bool, string> assert)
    {
        // The real backend owns the atomic same-handle identity guard. This fake
        // rejection proves the controller supplies its captured identity and
        // preserves that rejection; it does not claim to test native SCM trust.
        var beforeStop = new FakeSystem(Result(CoworkServiceState.Running))
        {
            StopReply = _ => new(View(CoworkServiceState.Running, identity: "replacement-config"),
                ["fixture: service config changed before STOP; no control sent"])
        };
        CoworkServiceResult rejected = new CoworkServiceController(beforeStop).Close();
        assert(beforeStop.StopCalls.Count == 1 && beforeStop.StopCalls[0].Identity == Identity,
            "Cowork STOP identity guard receives the originally verified config");
        assert(!rejected.ConfirmedStopped && rejected.Warnings.Contains("fixture: service config changed before STOP; no control sent"),
            "Cowork native identity-guard refusal is preserved as a failure");
        assert(beforeStop.Delays.Count == 0, "Cowork refused changed config is not subsequently polled as a successful STOP");

        foreach (CoworkServiceState changedState in new[] { CoworkServiceState.Stopped, CoworkServiceState.Running })
        {
            var afterStop = new FakeSystem(Result(CoworkServiceState.Running), Result(changedState, identity: "replacement-config"));
            CoworkServiceResult changed = new CoworkServiceController(afterStop).Close();
            assert(!changed.ConfirmedStopped && changed.Warnings.Count > 0,
                "Cowork config change on post-STOP observation prevents success even if " + changedState);
            assert(afterStop.StopCalls.Count == 1, "Cowork config replacement never authorizes a second STOP " + changedState);
        }
        var beforeRunning = new FakeSystem(Result(CoworkServiceState.StartPending),
            Result(CoworkServiceState.Running, identity: "replacement-config"));
        CoworkServiceResult changedBeforeAction = new CoworkServiceController(beforeRunning).Close();
        assert(!changedBeforeAction.ConfirmedStopped && changedBeforeAction.Warnings.Count > 0 && beforeRunning.StopCalls.Count == 0,
            "Cowork config change while waiting to become running forbids first STOP");
    }

    private static void RestartAfterStopped(Action<bool, string> assert)
    {
        foreach (CoworkServiceState residual in new[] { CoworkServiceState.Running, CoworkServiceState.Paused })
        {
            var stillRunning = new FakeSystem(Result(CoworkServiceState.Running), Result(residual))
            {
                StopReply = _ => Result(CoworkServiceState.Stopped)
            };
            CoworkServiceResult result = new CoworkServiceController(stillRunning).Close();
            assert(!result.ConfirmedStopped && result.Warnings.Count > 0 && result.Snapshot?.State == residual,
                "Cowork stopped STOP reply cannot hide actual residual/restarted state " + residual);
            assert(stillRunning.StopCalls.Count == 1, "Cowork residual state never causes repeated STOP " + residual);
        }
        var restarted = new FakeSystem(Result(CoworkServiceState.Running), Result(CoworkServiceState.Stopped),
            Result(CoworkServiceState.Running));
        CoworkServiceResult restart = new CoworkServiceController(restarted).Close();
        assert(!restart.ConfirmedStopped && restart.Warnings.Count > 0 && restart.Snapshot?.State == CoworkServiceState.Running,
            "Cowork immediate restart after first stopped readback is detected by final observation");
        assert(restarted.InspectCalls >= 3 && restarted.StopCalls.Count == 1,
            "Cowork final restart check is a fresh read and does not keep stopping a restarting service");

        var finalReadFailure = new FakeSystem(Result(CoworkServiceState.Running), Result(CoworkServiceState.Stopped),
            new(null, ["fixture: final query failed"]));
        CoworkServiceResult finalFailure = new CoworkServiceController(finalReadFailure).Close();
        assert(!finalFailure.ConfirmedStopped && finalFailure.Warnings.Contains("fixture: final query failed"),
            "Cowork final read failure cannot reuse an earlier stopped result");

        var finalReadDenied = new FakeSystem(Result(CoworkServiceState.Running), Result(CoworkServiceState.Stopped),
            new(null, [], RequiresElevation: true));
        CoworkServiceResult finalDenied = new CoworkServiceController(finalReadDenied).Close();
        assert(finalDenied.RequiresElevation && !finalDenied.ConfirmedStopped,
            "Cowork final confirmation access denial preserves elevation instead of earlier success");

        var finalLostTrust = new FakeSystem(Result(CoworkServiceState.Running), Result(CoworkServiceState.Stopped),
            Result(CoworkServiceState.Stopped, verified: false));
        CoworkServiceResult finalUnverified = new CoworkServiceController(finalLostTrust).Close();
        assert(!finalUnverified.ConfirmedStopped && finalUnverified.Warnings.Count > 0,
            "Cowork final confirmation must still verify trust even when status remains stopped");

        var finalReplacement = new FakeSystem(Result(CoworkServiceState.Running), Result(CoworkServiceState.Stopped),
            Result(CoworkServiceState.Stopped, identity: "replacement-config"));
        CoworkServiceResult replacement = new CoworkServiceController(finalReplacement).Close();
        assert(!replacement.ConfirmedStopped && replacement.Warnings.Count > 0,
            "Cowork service identity is rechecked in the final stopped confirmation");

        var reappeared = new FakeSystem(Result(CoworkServiceState.Running), Absent(), Result(CoworkServiceState.Running));
        CoworkServiceResult reappearance = new CoworkServiceController(reappeared).Close();
        assert(!reappearance.ConfirmedStopped && reappearance.Warnings.Count > 0 && reappeared.StopCalls.Count == 1,
            "Cowork service that reappears after a transient absent observation is not closed");

        var disappearedButUnreadable = new FakeSystem(Result(CoworkServiceState.Running), Absent(),
            new(null, ["fixture: absence confirmation failed"]));
        CoworkServiceResult uncertainAbsence = new CoworkServiceController(disappearedButUnreadable).Close();
        assert(!uncertainAbsence.ConfirmedStopped && uncertainAbsence.Warnings.Contains("fixture: absence confirmation failed"),
            "Cowork transient service absence cannot replace the final successful absence query");
    }

    private sealed class FakeSystem(params CoworkServiceResult[] inspections) : ICoworkServiceSystem
    {
        public int InspectCalls { get; private set; }
        public List<CoworkServiceSnapshot> StopCalls { get; } = [];
        public List<TimeSpan> Delays { get; } = [];
        public List<string> Events { get; } = [];
        public Func<CoworkServiceSnapshot, CoworkServiceResult>? StopReply { get; init; }

        public CoworkServiceResult Inspect()
        {
            if (inspections.Length == 0) throw new InvalidOperationException("Fake service has no inspection script.");
            Events.Add("inspect");
            return inspections[Math.Min(InspectCalls++, inspections.Length - 1)];
        }

        public CoworkServiceResult StopVerified(CoworkServiceSnapshot expected)
        {
            Events.Add("stop");
            StopCalls.Add(expected);
            return StopReply?.Invoke(expected) ?? new(expected with { State = CoworkServiceState.StopPending }, []);
        }

        public void Delay(TimeSpan duration)
        {
            Events.Add("delay");
            Delays.Add(duration);
        }
    }
}
