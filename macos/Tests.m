#define CC_TESTING 1
#import "ClaudeControl.m"
#import <sys/wait.h>

static NSUInteger checks = 0, failures = 0;

static void T(BOOL condition, NSString *message) {
    checks++;
    if (!condition) {
        failures++;
        fprintf(stderr, "FAIL: %s\n", message.UTF8String);
    }
}

static CCProcess *Fixture(pid_t pid, pid_t parent, NSString *path, NSArray *arguments) {
    CCProcess *process = [CCProcess new];
    process.pid = pid;
    process.parent = parent;
    process.uid = getuid();
    process.seconds = 12345;
    process.microseconds = 6789;
    process.path = path;
    process.arguments = arguments ?: @[];
    process.reason = @"";
    return process;
}

static void TestMatching(void) {
    NSString *home = NSHomeDirectory();
    NSArray *positivePaths = @[
        @"/Applications/Claude.app/Contents/MacOS/Claude",
        @"/Applications/Claude.app/Contents/Frameworks/Claude Helper (GPU).app/Contents/MacOS/Claude Helper (GPU)",
        @"/Applications/Claude.app/Contents/Helpers/claude-ssh-askpass",
        [home stringByAppendingPathComponent:@"Applications/Claude.app/Contents/MacOS/Claude"],
        [home stringByAppendingPathComponent:@"Library/Application Support/Claude/claude-code/2.1.309/hash/claude.app/Contents/MacOS/claude"],
        [home stringByAppendingPathComponent:@".local/share/claude/versions/2.1.309"],
        @"/opt/homebrew/bin/claude",
        @"/custom/bin/claude"
    ];
    for (NSString *path in positivePaths) {
        T(CCRootReason(Fixture(100001, 1, path, nil)) != nil, [@"Recognizes " stringByAppendingString:path]);
    }
    NSArray *negativePaths = @[
        @"/Applications/Claude.app.bak/Contents/MacOS/other",
        @"/Applications/Claude.app/ContentsExtra/Helper",
        @"/Applications/Claude Desktop.app/Contents/MacOS/Helper",
        @"/Applications/Codex.app/Contents/MacOS/Codex",
        @"/bin/zsh",
        @"/usr/bin/claude-helper",
        [home stringByAppendingPathComponent:@"Library/Application Support/Claude/claude-code-other/program"],
        [home stringByAppendingPathComponent:@"学习笔记/Claude相关/program"],
        [home stringByAppendingPathComponent:@".local/share/claude/versions-backup/program"]
    ];
    for (NSString *path in negativePaths) {
        T(CCRootReason(Fixture(100001, 1, path, @[@"unrelated", @"claude"])) == nil,
          [@"Rejects " stringByAppendingString:path]);
    }

    NSString *script = @"/some path/node_modules/@anthropic-ai/claude-code/cli.js";
    NSArray *positiveArguments = @[
        @[@"node", script],
        @[@"node", @"node_modules/@anthropic-ai/claude-code/cli.js"],
        @[@"node", @"--enable-source-maps", script],
        @[@"node", @"--require", @"module", script],
        @[@"node", @"--import", @"module", @"--", script],
        @[@"node", @"--require=module", script],
        @[@"node", @"--max-old-space-size=64", script],
        @[@"node", @"--title", @"claude-code", script]
    ];
    for (NSArray *arguments in positiveArguments) {
        T(CCRootReason(Fixture(100001, 1, @"/opt/homebrew/bin/node", arguments)) != nil,
          [@"Recognizes script entry: " stringByAppendingString:arguments.description]);
    }
    NSArray *negativeArguments = @[
        @[@"node", @"unrelated.js", script],
        @[@"node", @"-e", script],
        @[@"node", @"--eval", script],
        @[@"node", [@"--eval=" stringByAppendingString:script]],
        @[@"node", @"--print", script],
        @[@"node", @"-esetInterval(()=>{},1000)", script],
        @[@"node", @"-p123", script],
        @[@"node", @"--title", script, @"unrelated.js"],
        @[@"node", @"--run", script],
        @[@"node", @"--unknown-option", script, @"unrelated.js"],
        @[@"node", @"--require", script, @"unrelated.js"],
        @[@"node", @"--", @"unrelated.js", script],
        @[@"node", @"https://claude.ai"],
        @[@"node", @"other-node_modules/@anthropic-ai/claude-code/cli.js"],
        @[@"node", @"/tmp/node_modules/@anthropic-ai/claude-code/cli.js.bak"]
    ];
    for (NSArray *arguments in negativeArguments) {
        T(CCRootReason(Fixture(100001, 1, @"/opt/homebrew/bin/node", arguments)) == nil,
          [@"Rejects script-like data: " stringByAppendingString:arguments.description]);
    }
    T(CCRootReason(Fixture(100001, 1, @"/bin/zsh", @[@"zsh", script])) == nil,
      @"Non-runtime executable ignores script-like arguments");
    T(CCRootReason(Fixture(100001, 1, @"/usr/local/bin/bun", @[@"bun", script])) != nil,
      @"Recognizes Bun script entry");
}

static void TestScriptSymlinks(void) {
    char temporary[] = "/tmp/claude-control-links.XXXXXX";
    char *directory = mkdtemp(temporary);
    T(directory != NULL, @"Creates isolated npm symlink fixture directory");
    if (!directory) return;
    NSString *root = @(directory);
    NSString *package = [root stringByAppendingPathComponent:@"node_modules/@anthropic-ai/claude-code"];
    NSString *bin = [root stringByAppendingPathComponent:@"bin"];
    NSFileManager *manager = NSFileManager.defaultManager;
    [manager createDirectoryAtPath:package withIntermediateDirectories:YES attributes:nil error:NULL];
    [manager createDirectoryAtPath:bin withIntermediateDirectories:YES attributes:nil error:NULL];
    NSString *entry = [package stringByAppendingPathComponent:@"cli.js"];
    [@"// harmless test fixture\n" writeToFile:entry atomically:YES encoding:NSUTF8StringEncoding error:NULL];
    NSString *alias = [bin stringByAppendingPathComponent:@"claude"];
    BOOL linked = [manager createSymbolicLinkAtPath:alias withDestinationPath:@"../node_modules/@anthropic-ai/claude-code/cli.js" error:NULL];
    T(linked, @"Creates a relative npm executable symlink at an absolute path");
    T(CCClaudeScript(alias), @"Resolves absolute npm claude symlink to the actual script entry");
    T(CCRootReason(Fixture(100001, 1, @"/opt/homebrew/bin/node", @[@"node", alias])) != nil,
      @"Recognizes Node invoked through an absolute npm executable symlink");
    NSString *unrelated = [root stringByAppendingPathComponent:@"unrelated.js"];
    [@"// unrelated fixture\n" writeToFile:unrelated atomically:YES encoding:NSUTF8StringEncoding error:NULL];
    NSString *wrong = [bin stringByAppendingPathComponent:@"not-claude"];
    [manager createSymbolicLinkAtPath:wrong withDestinationPath:@"../unrelated.js" error:NULL];
    T(!CCClaudeScript(wrong), @"Unrelated npm symlink is rejected");
    NSString *broken = [bin stringByAppendingPathComponent:@"missing"];
    [manager createSymbolicLinkAtPath:broken withDestinationPath:@"../missing.js" error:NULL];
    T(!CCClaudeScript(broken), @"Broken unrelated symlink is rejected");
    NSString *npmBin = [root stringByAppendingPathComponent:@"node_modules/.bin"];
    [manager createDirectoryAtPath:npmBin withIntermediateDirectories:YES attributes:nil error:NULL];
    [manager createSymbolicLinkAtPath:[npmBin stringByAppendingPathComponent:@"claude"] withDestinationPath:@"../@anthropic-ai/claude-code/cli.js" error:NULL];
    CCProcess *relative = Fixture(200003, 1, @"/usr/local/bin/node", @[@"node", @"./node_modules/.bin/claude"]);
    NSMutableArray *relativeWarnings = [NSMutableArray array];
    T(CCRootReasonWithWarnings(relative, relativeWarnings) == nil && relativeWarnings.count == 1,
      @"A relative npm alias is only an incomplete-coverage clue and cannot authorize termination");
    NSString *savedDirectory = manager.currentDirectoryPath;
    BOOL changedDirectory = [manager changeCurrentDirectoryPath:root];
    relativeWarnings = [NSMutableArray array];
    T(changedDirectory && CCRootReasonWithWarnings(relative, relativeWarnings) == nil && relativeWarnings.count == 1,
      @"A valid alias in the current directory still cannot establish the process startup directory");
    [manager changeCurrentDirectoryPath:savedDirectory];
    [manager removeItemAtPath:root error:NULL];
}

static void TestIdentity(void) {
    CCProcess *first = Fixture(100001, 1, @"/bin/sleep", nil);
    CCProcess *second = Fixture(100001, 1, @"/bin/sleep", nil);
    T(CCSame(first, second), @"Identical process identity matches");
    T(!CCSame(first, nil) && !CCSame(nil, second), @"Missing process never matches");
    second.pid++; T(!CCSame(first, second), @"PID mismatch rejects"); second.pid--;
    second.uid++; T(!CCSame(first, second), @"UID mismatch rejects"); second.uid--;
    second.seconds++; T(!CCSame(first, second), @"Reused PID with changed birth seconds rejects"); second.seconds--;
    second.microseconds++; T(!CCSame(first, second), @"Reused PID with changed birth microseconds rejects"); second.microseconds--;
    second.path = @"/usr/bin/python3";
    T(CCSame(first, second), @"exec preserves process identity despite an executable change");
}

static void TestDescendants(void) {
    CCProcess *root = Fixture(100001, 1, @"/Applications/Claude.app/Contents/MacOS/Claude", nil);
    CCProcess *child = Fixture(100002, root.pid, @"/bin/zsh", nil);
    CCProcess *grandchild = Fixture(100003, child.pid, @"/bin/sleep", nil);
    CCProcess *unrelated = Fixture(100004, 1, @"/bin/sleep", nil);
    NSDictionary *snapshot = @{@(root.pid):root, @(child.pid):child, @(grandchild.pid):grandchild, @(unrelated.pid):unrelated};
    NSMutableDictionary *targets = [NSMutableDictionary dictionary];
    CCExpand(snapshot, targets);
    T(targets.count == 3, @"Expands only root and its descendants");
    T(targets[@(grandchild.pid)] != nil, @"Includes multiple descendant levels");
    T(targets[@(unrelated.pid)] == nil, @"Unrelated sibling is excluded");
    T([child.reason isEqualToString:@"Claude 子进程"], @"Labels descendant provenance");

    CCProcess *reusedParent = Fixture(root.pid, 1, @"/bin/sleep", nil);
    reusedParent.seconds++;
    CCProcess *unrelatedChild = Fixture(100005, reusedParent.pid, @"/bin/sleep", nil);
    NSMutableDictionary *staleTargets = [@{@(root.pid):root} mutableCopy];
    CCExpand(@{@(reusedParent.pid):reusedParent, @(unrelatedChild.pid):unrelatedChild}, staleTargets);
    T(staleTargets[@(unrelatedChild.pid)] == nil, @"Reused parent PID cannot claim unrelated descendants");

    CCProcess *ownProcess = Fixture(getpid(), 100010, @"/tmp/tests", nil);
    CCProcess *ownParent = Fixture(100010, 1, @"/Applications/Claude.app/Contents/MacOS/Claude", nil);
    NSMutableDictionary *protectedTargets = [NSMutableDictionary dictionary];
    CCExpand(@{@(ownProcess.pid):ownProcess, @(ownParent.pid):ownParent}, protectedTargets);
    T(protectedTargets.count == 1, @"Claude ancestor remains visible instead of disappearing from the count");
    T(((CCProcess *)protectedTargets[@(ownParent.pid)]).protectedFromTermination, @"Claude ancestor is protected from termination");
    T(protectedTargets[@(ownProcess.pid)] == nil, @"Protected ancestor cannot expand to the tool itself");
    CCProcess *sibling = Fixture(100011, ownParent.pid, @"/bin/sleep", nil);
    CCExpand(@{@(ownProcess.pid):ownProcess, @(ownParent.pid):ownParent, @(sibling.pid):sibling}, protectedTargets);
    T(protectedTargets[@(sibling.pid)] == nil, @"Protected ancestor cannot expand to sibling tasks");
    T(CCClosableCount(protectedTargets.allValues) == 0, @"Protected roots are excluded from the closable count");
    NSDictionary *protectedResult = CCResult(protectedTargets.allValues, 1, @[], @[]);
    T([protectedResult[@"remaining_count"] unsignedIntegerValue] == 1 &&
      [protectedResult[@"protected_count"] unsignedIntegerValue] == 1 && ![protectedResult[@"success"] boolValue],
      @"Result cannot report success or zero while a protected Claude ancestor exists");

    CCProcess *execChild = Fixture(child.pid, root.pid, @"/usr/bin/python3", nil);
    CCProcess *newGrandchild = Fixture(100006, child.pid, @"/bin/sleep", nil);
    CCExpand(@{@(execChild.pid):execChild, @(newGrandchild.pid):newGrandchild}, targets);
    T(targets[@(newGrandchild.pid)] != nil, @"Captured child remains a descendant root after exec and parent exit");
    T([((CCProcess *)targets[@(execChild.pid)]).path isEqualToString:execChild.path], @"Captured child executable path is refreshed after exec");
    NSArray *ordered = CCSignalOrder(@[child, root, grandchild], snapshot);
    T(ordered.firstObject == grandchild && ordered.lastObject == root, @"Signal ordering follows descendant depth");
    CCProcess *wrappedChild = Fixture(5, root.pid, @"/bin/sleep", nil);
    NSDictionary *wrapped = @{@(root.pid):root, @(wrappedChild.pid):wrappedChild};
    T(CCSignalOrder(@[root, wrappedChild], wrapped).firstObject == wrappedChild,
      @"Signal ordering remains child-first when child PID is lower");
}

static void TestCoverage(void) {
    NSMutableArray *warnings = [NSMutableArray array];
    CCWarning(warnings, @"模拟读取被拒绝");
    CCWarning(warnings, @"模拟读取被拒绝");
    T(warnings.count == 1, @"Coverage warnings are deduplicated");
    NSDictionary *partial = CCResult(@[], 0, @[], warnings);
    T(![partial[@"scan_complete"] boolValue] && ![partial[@"success"] boolValue],
      @"Zero recognized processes with a read failure is never complete or successful");
    T([CCDetails(partial) containsString:@"模拟读取被拒绝"], @"Coverage failures reach visible result details");
    NSDictionary *clean = CCResult(@[], 0, @[], @[]);
    T([clean[@"scan_complete"] boolValue] && [clean[@"success"] boolValue], @"Clean zero result is explicitly complete");
    NSDictionary *failed = CCResult(@[], 1, @[@"模拟关闭失败"], @[]);
    T(![failed[@"success"] boolValue], @"A signal error cannot report full success");
    NSMutableArray *realWarnings = [NSMutableArray array];
    CCProcess *self = CCReadProcessWithWarnings(getpid(), NO, realWarnings);
    T(self && realWarnings.count == 0, @"Own process metadata is readable without a spurious warning");
    CCReadProcessWithWarnings(999999, NO, realWarnings);
    T(realWarnings.count == 0, @"An already-exited process does not create a permission warning");
    NSMutableArray *exitWarnings = [NSMutableArray array];
    CCReadWarning(getpid(), @"模拟原始读取", ESRCH, exitWarnings);
    T(exitWarnings.count == 0,
      @"An original ESRCH remains a normal exit even when the PID is readable during diagnostic recheck");
    for (NSNumber *code in @[@(EPERM), @(EACCES), @(EIO), @(ENOENT)]) {
        NSMutableArray *failureWarnings = [NSMutableArray array];
        CCReadWarning(getpid(), @"模拟原始读取", code.intValue, failureWarnings);
        T(failureWarnings.count == 1,
          [NSString stringWithFormat:@"A live PID read error %d remains an incomplete-coverage warning", code.intValue]);
    }
    NSSet *protected = CCProtected(@{@(getpid()): self});
    T([protected containsObject:@(getpid())], @"Ancestry fallback protects self when snapshot ancestors are absent");
    CCProcess *brokenAncestor = Fixture(getpid(), 999999, @"/tmp/tool", nil);
    NSMutableArray *ancestryWarnings = [NSMutableArray array];
    NSSet *failClosed = CCProtectedWithWarnings(@{@(getpid()):brokenAncestor}, ancestryWarnings);
    T(ancestryWarnings.count == 1 && CCIsProtected(failClosed, 123456),
      @"Unknown ancestry protects even captured targets absent from the snapshot");
    CCProcess *retained = Fixture(1, 0, @"/previously-captured", nil);
    NSMutableDictionary *retainedTargets = [@{@1: retained} mutableCopy];
    CCTestUnreadablePID = 1;
    CCExpandWithWarnings(@{@(getpid()):brokenAncestor}, retainedTargets, ancestryWarnings);
    CCTestUnreadablePID = -1;
    T(retainedTargets[@1] == retained && retained.protectedFromTermination,
      @"A retained target whose identity cannot be read still receives the current protect-all decision");
}

static void TestParentBirthOrdering(void) {
    CCProcess *root = Fixture(200001, 1, @"/Applications/Claude.app/Contents/MacOS/Claude", nil);
    CCProcess *oldChild = Fixture(200002, root.pid, @"/usr/bin/python3", nil);
    root.seconds = 200;
    root.microseconds = 500;
    oldChild.seconds = 100;
    oldChild.microseconds = 999999;
    NSMutableDictionary *targets = [NSMutableDictionary dictionary];
    CCExpand(@{@(root.pid):root, @(oldChild.pid):oldChild}, targets);
    T(targets.count == 1 && targets[@(oldChild.pid)] == nil,
      @"A reused parent PID cannot adopt a child born in an earlier second");
    oldChild.seconds = root.seconds;
    oldChild.microseconds = root.microseconds - 1;
    targets = [NSMutableDictionary dictionary];
    CCExpand(@{@(root.pid):root, @(oldChild.pid):oldChild}, targets);
    T(targets.count == 1 && targets[@(oldChild.pid)] == nil,
      @"A reused parent PID cannot adopt a child born earlier within the same second");
    oldChild.microseconds = root.microseconds;
    T(CCBornNotBefore(oldChild, root), @"Equal microsecond timestamps permit a possible genuine child");
    targets = [NSMutableDictionary dictionary];
    CCExpand(@{@(root.pid):root, @(oldChild.pid):oldChild}, targets);
    T(targets.count == 2, @"A child with the same recorded birth timestamp may be expanded");
    oldChild.seconds = root.seconds + 1;
    oldChild.microseconds = 0;
    root.microseconds = 999999;
    T(CCBornNotBefore(oldChild, root), @"Seconds take precedence over microseconds across a second boundary");
    oldChild.seconds = 0;
    T(!CCBornNotBefore(oldChild, root), @"Unknown child birth time cannot authorize descendant expansion");
    oldChild.seconds = 201;
    root.seconds = 0;
    T(!CCBornNotBefore(oldChild, root), @"Unknown parent birth time cannot authorize descendant expansion");
    root.seconds = 200;
    root.microseconds = 1000000;
    T(!CCBornNotBefore(oldChild, root), @"Invalid birth microseconds cannot authorize descendant expansion");
}

static void TestNodeIdentificationCoverage(void) {
    NSString *entry = @"/fixture/node_modules/@anthropic-ai/claude-code/cli.js";
    NSArray<NSArray<NSString *> *> *known = @[
        @[@"node", @"--trace-uncaught", entry],
        @[@"node", @"--abort-on-uncaught-exception", entry],
        @[@"node", @"--unhandled-rejections", @"strict", entry],
        @[@"node", @"--dns-result-order", @"ipv4first", entry],
        @[@"node", @"--unhandled-rejections=strict", entry],
        @[@"node", @"--max-old-space-size=64", entry]
    ];
    for (NSArray *arguments in known) {
        NSMutableArray *warnings = [NSMutableArray array];
        CCProcess *process = Fixture(200003, 1, @"/usr/local/bin/node", arguments);
        T(CCRootReasonWithWarnings(process, warnings) != nil && warnings.count == 0,
          [@"Known Node options identify the entry without warnings: " stringByAppendingString:arguments.description]);
    }
    for (NSArray *arguments in @[@[@"node", @"--unknown", entry], @[@"node", @"--unknown-mode=expression", entry]]) {
        CCProcess *process = Fixture(200003, 1, @"/usr/local/bin/node", arguments);
        NSMutableArray *warnings = [NSMutableArray array];
        NSMutableDictionary *targets = [NSMutableDictionary dictionary];
        CCExpandWithWarnings(@{@(process.pid): process}, targets, warnings);
        NSDictionary *result = CCResult(targets.allValues, 0, @[], warnings);
        T(targets.count == 0 && warnings.count == 1 && ![result[@"scan_complete"] boolValue] && ![result[@"success"] boolValue],
          @"Unknown Node modes with an exact script clue warn instead of matching or claiming complete zero");
        CCExpandWithWarnings(@{@(process.pid): process}, targets, warnings);
        T(warnings.count == 1, @"Refresh keeps an uncertain Node warning without duplicating it");
    }
    NSArray *excluded = @[
        @[@"node", @"--unknown", @"unrelated.js"],
        @[@"node", @"--unknown-mode=expression", @"unrelated.js"],
        @[@"node", [@"--unknown-value=" stringByAppendingString:entry], @"unrelated.js"],
        @[@"node", @"--eval", entry],
        @[@"node", @"-econsole.log(1)", entry],
        @[@"node", @"--print", entry],
        @[@"node", @"--run", @"package-script", entry],
        @[@"node", @"--run=package-script", entry],
        @[@"node", @"unrelated.js", entry]
    ];
    for (NSArray *arguments in excluded) {
        NSMutableArray *warnings = [NSMutableArray array];
        CCProcess *process = Fixture(200003, 1, @"/usr/local/bin/node", arguments);
        T(CCRootReasonWithWarnings(process, warnings) == nil && warnings.count == 0,
          [@"Unrelated or explicit non-entry modes neither match nor warn: " stringByAppendingString:arguments.description]);
    }
    NSMutableArray *warnings = [NSMutableArray array];
    CCProcess *preload = Fixture(200003, 1, @"/usr/local/bin/node", @[@"node", @"--require", entry]);
    T(CCRootReasonWithWarnings(preload, warnings) == nil && warnings.count == 1,
      @"A CLI preload without a direct script is reported as uncertain instead of authorizing termination");
    CCProcess *bun = Fixture(200003, 1, @"/usr/local/bin/bun", @[@"bun", @"run", entry]);
    T(CCRootReason(bun) != nil, @"Bun run with a direct Claude script entry is recognized");
    bun.arguments = @[@"bun", @"run", @"./node_modules/@anthropic-ai/claude-code/cli.js"];
    T(CCRootReason(bun) != nil, @"Bun run recognizes an explicit ./ file entry");
    for (NSString *ambiguous in @[@"node_modules/@anthropic-ai/claude-code/cli.js", @"../node_modules/@anthropic-ai/claude-code/cli.js"]) {
        bun.arguments = @[@"bun", @"run", ambiguous];
        warnings = [NSMutableArray array];
        T(CCRootReasonWithWarnings(bun, warnings) == nil && warnings.count == 1,
          @"Ambiguous Bun package-script names only warn and cannot authorize termination");
    }
    for (NSString *title in @[@"claude", @"claude-code"]) {
        CCProcess *titled = Fixture(200003, 1, @"/usr/local/bin/node", @[title, @"", @""]);
        warnings = [NSMutableArray array];
        T(CCRootReasonWithWarnings(titled, warnings) == nil && warnings.count == 1,
          @"An overwritten Claude runtime title is only an incomplete-coverage clue");
        NSMutableDictionary *targets = [NSMutableDictionary dictionary];
        CCExpandWithWarnings(@{@(titled.pid): titled}, targets, warnings);
        NSDictionary *result = CCResult(targets.allValues, 0, @[], warnings);
        T(targets.count == 0 && ![result[@"scan_complete"] boolValue],
          @"A mutable runtime title cannot authorize termination or a complete-zero result");
    }
    warnings = [NSMutableArray array];
    CCProcess *ordinary = Fixture(200003, 1, @"/usr/local/bin/node", @[@"worker", @"", @""]);
    T(CCRootReasonWithWarnings(ordinary, warnings) == nil && warnings.count == 0,
      @"Ordinary runtime titles and empty parameters do not create global warnings");
}

static void TestOwnedProcess(void) {
    char temporary[] = "/tmp/claude-control-tests.XXXXXX";
    char *directory = mkdtemp(temporary);
    T(directory != NULL, @"Creates isolated test directory");
    if (!directory) return;
    NSString *root = @(directory);
    NSString *executable = [root stringByAppendingPathComponent:@"claude"];
    // Build a harmless native sleeper. Copying Apple's platform-signed /bin/sleep
    // outside its system location is rejected by this Mac's code-signing policy.
    NSTask *compiler = [NSTask new];
    compiler.executableURL = [NSURL fileURLWithPath:@"/usr/bin/xcrun"];
    compiler.arguments = @[@"clang", @"-x", @"c", @"-", @"-o", executable];
    NSPipe *input = [NSPipe pipe];
    compiler.standardInput = input;
    NSError *error = nil;
    BOOL launched = [compiler launchAndReturnError:&error];
    if (launched) {
        [input.fileHandleForWriting writeData:[@"#include <unistd.h>\nint main(void) { sleep(60); return 0; }\n" dataUsingEncoding:NSUTF8StringEncoding]];
        [input.fileHandleForWriting closeFile];
        [compiler waitUntilExit];
    }
    BOOL built = launched && compiler.terminationStatus == 0;
    T(built, [@"Builds harmless test executable: " stringByAppendingString:error.description ?: @"ok"]);
    if (!built) { [NSFileManager.defaultManager removeItemAtPath:root error:NULL]; return; }
    char canonical[PATH_MAX] = {0};
    NSString *realExecutable = realpath(executable.fileSystemRepresentation, canonical) ? @(canonical) : executable;
    pid_t child = fork();
    if (child == 0) {
        execl(executable.fileSystemRepresentation, executable.fileSystemRepresentation, "60", (char *)NULL);
        _exit(127);
    }
    T(child > 0, @"Starts owned harmless test process");
    if (child < 0) { [NSFileManager.defaultManager removeItemAtPath:root error:NULL]; return; }
    CCProcess *identity = nil;
    CCProcess *lastCandidate = nil;
    for (NSUInteger i = 0; i < 100; i++) {
        CCProcess *candidate = CCReadProcess(child, NO);
        lastCandidate = candidate;
        if ([candidate.path isEqualToString:realExecutable] || [candidate.path isEqualToString:executable]) { identity = candidate; break; }
        usleep(10000);
    }
    if (!identity) fprintf(stderr, "Owned test expected %s, observed %s\n", realExecutable.UTF8String, lastCandidate.path.UTF8String ?: "<no readable process>");
    T(identity != nil, @"Reads the owned process real executable path");
    if (identity) {
        T(CCSame(identity, CCReadProcess(child, NO)), @"Owned process keeps its recorded identity");
        NSDictionary *snapshot = CCSnapshot();
        T(CCSame(identity, snapshot[@(child)]), @"Read-only system snapshot includes owned process");
        CCProcess *fake = Fixture(child, identity.parent, identity.path, nil);
        fake.seconds = identity.seconds + 1;
        fake.microseconds = identity.microseconds;
        T(!CCSame(fake, CCReadProcess(child, NO)), @"Identity guard refuses an altered birth time");
        T(CCRootReason(identity) != nil, @"Real executable detection recognizes the test CLI name");
        if (CCSame(identity, CCReadProcess(child, NO))) {
            T(kill(child, SIGTERM) == 0, @"Signals only the test child with verified identity");
        }
    } else {
        // This PID belongs to our unreaped child, so it cannot have been reused.
        kill(child, SIGTERM);
    }
    int status = 0;
    T(waitpid(child, &status, 0) == child, @"Reaps only the owned test child");
    T(WIFSIGNALED(status) && WTERMSIG(status) == SIGTERM, @"Owned test process stops with TERM");
    T(CCReadProcess(child, NO) == nil, @"Exited child is no longer reported");

    // Reuse our own locally built sleeper under a runtime name to exercise the
    // real KERN_PROCARGS2 -> CCArguments -> CCRootReason path without running Node.
    NSString *runtime = [root stringByAppendingPathComponent:@"node"];
    BOOL copiedRuntime = [NSFileManager.defaultManager copyItemAtPath:executable toPath:runtime error:NULL];
    T(copiedRuntime, @"Creates owned harmless Node-named test executable");
    if (copiedRuntime) {
        NSString *script = [root stringByAppendingPathComponent:@"node_modules/@anthropic-ai/claude-code/cli.js"];
        pid_t runtimeChild = fork();
        if (runtimeChild == 0) {
            execl(runtime.fileSystemRepresentation, runtime.fileSystemRepresentation, script.fileSystemRepresentation, (char *)NULL);
            _exit(127);
        }
        T(runtimeChild > 0, @"Starts only the owned runtime fixture");
        if (runtimeChild > 0) {
            CCProcess *runtimeIdentity = nil;
            for (NSUInteger i = 0; i < 100; i++) {
                CCProcess *candidate = CCReadProcess(runtimeChild, YES);
                if ([candidate.path.lastPathComponent isEqualToString:@"node"]) { runtimeIdentity = candidate; break; }
                usleep(10000);
            }
            T(runtimeIdentity != nil, @"Reads runtime fixture and real argv");
            T(runtimeIdentity.arguments.count == 2, @"Real argv parser returns exactly argv[0] and argv[1]");
            T([runtimeIdentity.arguments.firstObject isEqualToString:runtime], @"Real argv preserves executable argument");
            T([runtimeIdentity.arguments.lastObject isEqualToString:script], @"Real argv preserves Claude script argument");
            T([CCRootReason(runtimeIdentity) isEqualToString:@"Claude Code（Node）"], @"Real argv detection reaches Claude Node classification");
            if (runtimeIdentity && CCSame(runtimeIdentity, CCReadProcess(runtimeChild, NO))) {
                T(kill(runtimeChild, SIGTERM) == 0, @"Signals only the owned runtime fixture after identity validation");
            } else {
                // Still our unreaped child, so this PID cannot have been reused.
                kill(runtimeChild, SIGTERM);
            }
            int runtimeStatus = 0;
            T(waitpid(runtimeChild, &runtimeStatus, 0) == runtimeChild, @"Reaps owned runtime fixture");
            T(WIFSIGNALED(runtimeStatus) && WTERMSIG(runtimeStatus) == SIGTERM, @"Owned runtime fixture terminates with TERM");
        }

        NSString *package = [root stringByAppendingPathComponent:@"node_modules/@anthropic-ai/claude-code"];
        NSString *bin = [root stringByAppendingPathComponent:@"node_modules/.bin"];
        [NSFileManager.defaultManager createDirectoryAtPath:package withIntermediateDirectories:YES attributes:nil error:NULL];
        [NSFileManager.defaultManager createDirectoryAtPath:bin withIntermediateDirectories:YES attributes:nil error:NULL];
        [@"// fixture\n" writeToFile:[package stringByAppendingPathComponent:@"cli.js"] atomically:YES encoding:NSUTF8StringEncoding error:NULL];
        [NSFileManager.defaultManager createSymbolicLinkAtPath:[bin stringByAppendingPathComponent:@"claude"] withDestinationPath:@"../@anthropic-ai/claude-code/cli.js" error:NULL];
        const char *relativeEntry = "./node_modules/.bin/claude";
        pid_t relativeChild = fork();
        if (relativeChild == 0) {
            if (chdir(root.fileSystemRepresentation) != 0) _exit(126);
            execl(runtime.fileSystemRepresentation, runtime.fileSystemRepresentation, relativeEntry, (char *)NULL);
            _exit(127);
        }
        T(relativeChild > 0, @"Starts only the owned relative npm alias fixture");
        if (relativeChild > 0) {
            CCProcess *identity = nil;
            NSMutableArray *warnings = [NSMutableArray array];
            for (NSUInteger i = 0; i < 100; i++) {
                CCProcess *candidate = CCReadProcessWithWarnings(relativeChild, YES, warnings);
                if ([candidate.path.lastPathComponent isEqualToString:@"node"] &&
                    [candidate.arguments.lastObject isEqualToString:@(relativeEntry)]) { identity = candidate; break; }
                usleep(10000);
            }
            T(identity != nil, @"Reads the owned runtime fixture actual relative npm alias argument");
            T(CCRootReasonWithWarnings(identity, warnings) == nil && warnings.count == 1,
              @"A real relative npm alias only warns even when its current directory contains a valid CLI symlink");
            if (identity && CCSame(identity, CCReadProcess(relativeChild, NO))) {
                T(kill(relativeChild, SIGTERM) == 0, @"Signals only the verified owned relative-alias fixture");
            } else {
                // This remains our unreaped child; its PID cannot be reused.
                kill(relativeChild, SIGTERM);
            }
            int relativeStatus = 0;
            T(waitpid(relativeChild, &relativeStatus, 0) == relativeChild,
              @"Reaps only the owned relative-alias fixture");
            T(WIFSIGNALED(relativeStatus) && WTERMSIG(relativeStatus) == SIGTERM,
              @"Owned relative-alias fixture exits with TERM");
        }
    }
    [NSFileManager.defaultManager removeItemAtPath:root error:NULL];
}

int main(void) {
    @autoreleasepool {
        TestMatching();
        TestScriptSymlinks();
        TestIdentity();
        TestDescendants();
        TestCoverage();
        TestParentBirthOrdering();
        TestNodeIdentificationCoverage();
        TestOwnedProcess();
        printf("%lu checks, %lu failures\n", (unsigned long)checks, (unsigned long)failures);
    }
    return failures ? 1 : 0;
}
