#import <AppKit/AppKit.h>
#import <Foundation/Foundation.h>
#import <libproc.h>
#import <sys/sysctl.h>
#import <signal.h>
#import <unistd.h>

@interface CCProcess : NSObject
@property pid_t pid;
@property pid_t parent;
@property uid_t uid;
@property uint64_t seconds;
@property uint64_t microseconds;
@property(copy) NSString *path;
@property(copy) NSArray<NSString *> *arguments;
@property(copy) NSString *reason;
@property BOOL protectedFromTermination;
@end
@implementation CCProcess
@end

static BOOL CCUnder(NSString *path, NSString *directory) {
    return [path hasPrefix:[directory stringByAppendingString:@"/"]];
}

// Inspect actual argv entries, never search a whole command line for “claude”.
static void CCWarning(NSMutableArray<NSString *> *warnings, NSString *warning) {
    if (warnings && ![warnings containsObject:warning]) [warnings addObject:warning];
}

static void CCReadWarning(pid_t pid, NSString *stage, int code, NSMutableArray *warnings) {
    if (!warnings) return;
    // A normal exit or zombie during a snapshot is not a coverage failure.
    struct proc_bsdinfo check = {0};
    if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &check, sizeof(check)) == sizeof(check)) {
        if (check.pbi_status == SZOMB || check.pbi_uid != getuid()) return;
    } else if (kill(pid, 0) < 0 && errno == ESRCH) return;
    CCWarning(warnings, [NSString stringWithFormat:@"PID %d：无法读取%@（%s）；检查不完整。", pid, stage, strerror(code ?: EIO)]);
}

static NSArray<NSString *> *CCArgumentsWithWarnings(pid_t pid, NSMutableArray *warnings) {
    int query[] = {CTL_KERN, KERN_PROCARGS2, pid};
    size_t length = 0;
    if (sysctl(query, 3, NULL, &length, NULL, 0) != 0 || length <= sizeof(int)) {
        CCReadWarning(pid, @"脚本入口参数", errno, warnings); return @[];
    }
    NSMutableData *buffer = [NSMutableData dataWithLength:length];
    if (sysctl(query, 3, buffer.mutableBytes, &length, NULL, 0) != 0 || length <= sizeof(int)) {
        CCReadWarning(pid, @"脚本入口参数", errno, warnings); return @[];
    }
    const char *start = buffer.bytes, *end = start + length, *cursor = start + sizeof(int);
    int count = 0;
    memcpy(&count, start, sizeof(int));
    while (cursor < end && *cursor) cursor++;
    while (cursor < end && !*cursor) cursor++;
    NSMutableArray *result = [NSMutableArray array];
    for (int i = 0; i < count && cursor < end; i++) {
        const char *boundary = memchr(cursor, 0, (size_t)(end - cursor));
        if (!boundary) break;
        NSString *value = [[NSString alloc] initWithBytes:cursor length:(NSUInteger)(boundary - cursor) encoding:NSUTF8StringEncoding];
        [result addObject:value ?: @""];
        cursor = boundary + 1;
    }
    if (count <= 0 || result.count != (NSUInteger)count)
        CCReadWarning(pid, @"完整脚本入口参数", EIO, warnings);
    return result;
}

static CCProcess *CCReadProcessWithWarnings(pid_t pid, BOOL arguments, NSMutableArray *warnings) {
    struct proc_bsdinfo info = {0};
    if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, sizeof(info)) != sizeof(info)) {
        CCReadWarning(pid, @"进程身份", errno, warnings); return nil;
    }
    if (info.pbi_uid != getuid() || info.pbi_status == SZOMB) return nil;
    char executable[PROC_PIDPATHINFO_MAXSIZE] = {0};
    if (proc_pidpath(pid, executable, sizeof(executable)) <= 0)
        CCReadWarning(pid, @"可执行文件路径", errno, warnings);
    CCProcess *process = [CCProcess new];
    process.pid = pid;
    process.parent = info.pbi_ppid;
    process.uid = info.pbi_uid;
    process.seconds = info.pbi_start_tvsec;
    process.microseconds = info.pbi_start_tvusec;
    process.path = [[NSString alloc] initWithUTF8String:executable] ?: @"";
    if (!process.path.length) CCReadWarning(pid, @"可执行文件路径", EIO, warnings);
    NSString *name = process.path.lastPathComponent.lowercaseString;
    BOOL runtime = [@[@"node", @"nodejs", @"bun"] containsObject:name];
    process.arguments = arguments && runtime ? CCArgumentsWithWarnings(pid, warnings) : @[];
    process.reason = @"";
    // Metadata, path and argv are separate kernel reads. Discard a PID that was
    // replaced while they were being read instead of mixing two identities.
    struct proc_bsdinfo after = {0};
    if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &after, sizeof(after)) != sizeof(after)) {
        CCReadWarning(pid, @"复核进程身份", errno, warnings); return nil;
    }
    if (after.pbi_uid != info.pbi_uid || after.pbi_start_tvsec != info.pbi_start_tvsec ||
        after.pbi_start_tvusec != info.pbi_start_tvusec || after.pbi_status == SZOMB) return nil;
    process.parent = after.pbi_ppid;
    return process;
}

static CCProcess *CCReadProcess(pid_t pid, BOOL arguments) {
    return CCReadProcessWithWarnings(pid, arguments, nil);
}

static NSDictionary<NSNumber *, CCProcess *> *CCSnapshotWithWarnings(NSMutableArray *warnings) {
    // proc_listpids returns bytes; scope the enumeration to the current UID so
    // inaccessible system/other-user processes do not create spurious warnings.
    int size = proc_listpids(PROC_UID_ONLY, getuid(), NULL, 0);
    if (size <= 0) @throw [NSException exceptionWithName:@"ProcessScan" reason:@"无法读取当前用户的进程列表。" userInfo:nil];
    NSMutableData *buffer;
    int used = 0;
    BOOL complete = NO;
    for (NSUInteger attempt = 0; attempt < 4; attempt++) {
        size += 256 * (int)sizeof(pid_t);
        buffer = [NSMutableData dataWithLength:(NSUInteger)size];
        used = proc_listpids(PROC_UID_ONLY, getuid(), buffer.mutableBytes, size);
        if (used <= 0) @throw [NSException exceptionWithName:@"ProcessScan" reason:@"无法读取当前用户的进程列表。" userInfo:nil];
        if (used < size) { complete = YES; break; }
        size *= 2;
    }
    if (!complete) @throw [NSException exceptionWithName:@"ProcessScan" reason:@"进程列表持续变化，无法完成完整检查。" userInfo:nil];
    pid_t *pids = buffer.mutableBytes;
    NSMutableDictionary *snapshot = [NSMutableDictionary dictionary];
    for (int i = 0; i < used / (int)sizeof(pid_t); i++) {
        if (pids[i] <= 0) continue;
        CCProcess *process = CCReadProcessWithWarnings(pids[i], YES, warnings);
        if (process) snapshot[@(process.pid)] = process;
    }
    return snapshot;
}

static NSDictionary<NSNumber *, CCProcess *> *CCSnapshot(void) {
    return CCSnapshotWithWarnings(nil);
}

static BOOL CCSame(CCProcess *first, CCProcess *second) {
    return first && second && first.pid == second.pid && first.uid == second.uid &&
        first.seconds == second.seconds && first.microseconds == second.microseconds;
}

static BOOL CCClaudeScript(NSString *script) {
    NSString *entry = @"node_modules/@anthropic-ai/claude-code/cli.js";
    if ([script isEqualToString:entry] || [script hasSuffix:[@"/" stringByAppendingString:entry]]) return YES;
    // npm's bin/claude normally points to the actual Node script through a symlink.
    return script.isAbsolutePath && [[script stringByResolvingSymlinksInPath] hasSuffix:[@"/" stringByAppendingString:entry]];
}

static NSString *CCRootReason(CCProcess *process) {
    NSString *path = process.path;
    NSString *home = NSHomeDirectory();
    if (CCUnder(path, @"/Applications/Claude.app/Contents") ||
        CCUnder(path, [home stringByAppendingPathComponent:@"Applications/Claude.app/Contents"]))
        return @"Claude 桌面版";
    if (CCUnder(path, [home stringByAppendingPathComponent:@"Library/Application Support/Claude/claude-code"]) ||
        CCUnder(path, [home stringByAppendingPathComponent:@".local/share/claude/versions"]))
        return @"Claude Code";
    if ([path.lastPathComponent.lowercaseString isEqualToString:@"claude"])
        return @"Claude Code";

    NSString *name = path.lastPathComponent.lowercaseString;
    if (![@[@"node", @"nodejs", @"bun"] containsObject:name]) return nil;
    NSSet *optionsWithValue = [NSSet setWithArray:@[@"-r", @"--require", @"--import", @"--loader", @"--experimental-loader", @"--conditions", @"-C", @"--icu-data-dir", @"--openssl-config", @"--title", @"--redirect-warnings", @"--trace-event-categories", @"--trace-event-file-pattern", @"--env-file", @"--env-file-if-exists", @"--watch-path", @"--cpu-prof-dir", @"--cpu-prof-name", @"--heap-prof-dir", @"--heap-prof-name", @"--snapshot-blob", @"--report-directory", @"--report-filename", @"--inspect-port"]];
    NSSet *booleanOptions = [NSSet setWithArray:@[@"--enable-source-maps", @"--no-warnings", @"--trace-warnings", @"--trace-deprecation", @"--no-deprecation", @"--throw-deprecation", @"--pending-deprecation", @"--preserve-symlinks", @"--preserve-symlinks-main", @"--expose-gc", @"--experimental-vm-modules", @"--experimental-modules", @"--experimental-strip-types", @"--experimental-transform-types", @"--inspect", @"--inspect-brk", @"--cpu-prof", @"--heap-prof", @"--watch", @"--hot", @"--smol", @"--silent", @"--no-clear-screen"]];
    for (NSUInteger i = 1; i < process.arguments.count; i++) {
        NSString *argument = process.arguments[i];
        if ([argument hasPrefix:@"-e"] || [argument hasPrefix:@"-p"] ||
            [@[@"--eval", @"--print", @"--run", @"--test", @"--check", @"-c"] containsObject:argument] ||
            [argument hasPrefix:@"--eval="] || [argument hasPrefix:@"--print="] || [argument hasPrefix:@"--run="]) return nil;
        if ([argument isEqualToString:@"--"]) {
            return i + 1 < process.arguments.count && CCClaudeScript(process.arguments[i + 1]) ? @"Claude Code（Node）" : nil;
        }
        if ([optionsWithValue containsObject:argument]) { i++; continue; }
        if ([argument hasPrefix:@"-"]) {
            if ([booleanOptions containsObject:argument] ||
                ([argument hasPrefix:@"--"] && [argument containsString:@"="]) ||
                ([argument hasPrefix:@"-r"] && argument.length > 2)) continue;
            // An unknown option may consume the next argv entry. Guessing could
            // mistake an option value for the program being executed.
            return nil;
        }
        return CCClaudeScript(argument) ? @"Claude Code（Node）" : nil;
    }
    return nil;
}

static NSSet<NSNumber *> *CCProtectedWithWarnings(NSDictionary<NSNumber *, CCProcess *> *snapshot, NSMutableArray *warnings) {
    NSMutableSet *protected = [NSMutableSet setWithObjects:@0, @1, nil];
    pid_t cursor = getpid();
    while (cursor > 1 && ![protected containsObject:@(cursor)]) {
        [protected addObject:@(cursor)];
        CCProcess *process = snapshot[@(cursor)];
        if (process) { cursor = process.parent; continue; }
        // An ancestor can belong to another UID and therefore be absent from the
        // scoped snapshot. Read only its ancestry metadata, never its arguments.
        struct proc_bsdinfo ancestor = {0};
        if (proc_pidinfo(cursor, PROC_PIDTBSDINFO, 0, &ancestor, sizeof(ancestor)) == sizeof(ancestor)) {
            cursor = ancestor.pbi_ppid;
        } else {
            CCWarning(warnings, @"无法完整确认工具的祖先进程；为避免误关当前终端，已保护本次所有匹配进程。");
            [protected addObject:@(-1)]; // Sentinel: protect captured targets absent from this snapshot too.
            [protected addObjectsFromArray:snapshot.allKeys];
            break;
        }
    }
    return protected;
}

static BOOL CCIsProtected(NSSet<NSNumber *> *protected, pid_t pid) {
    return [protected containsObject:@(-1)] || [protected containsObject:@(pid)];
}

static NSSet<NSNumber *> *CCProtected(NSDictionary<NSNumber *, CCProcess *> *snapshot) {
    return CCProtectedWithWarnings(snapshot, nil);
}

static void CCExpandWithWarnings(NSDictionary<NSNumber *, CCProcess *> *snapshot,
                                 NSMutableDictionary<NSNumber *, CCProcess *> *targets,
                                 NSMutableArray *warnings) {
    NSSet *protected = CCProtectedWithWarnings(snapshot, warnings);
    for (NSNumber *pid in targets.allKeys) {
        CCProcess *previous = targets[pid], *current = snapshot[pid];
        if (!current) {
            // A transient read failure must not erase a captured child. A second
            // identity read distinguishes an exit/reuse from incomplete coverage.
            current = CCReadProcessWithWarnings(previous.pid, YES, warnings);
            if (!current) {
                if (kill(previous.pid, 0) < 0 && errno == ESRCH) [targets removeObjectForKey:pid];
                continue;
            }
        }
        if (!CCSame(previous, current)) { [targets removeObjectForKey:pid]; continue; }
        // An exec() keeps the same process identity while changing the executable.
        current.reason = previous.reason;
        current.protectedFromTermination = CCIsProtected(protected, current.pid);
        targets[pid] = current;
    }
    for (CCProcess *process in snapshot.allValues) {
        NSString *reason = CCRootReason(process);
        if (reason) {
            process.reason = reason;
            process.protectedFromTermination = CCIsProtected(protected, process.pid);
            targets[@(process.pid)] = process;
        }
    }
    BOOL changed;
    do {
        changed = NO;
        for (CCProcess *process in snapshot.allValues) {
            CCProcess *parent = targets[@(process.parent)];
            if (!parent || parent.protectedFromTermination || !CCSame(parent, snapshot[@(parent.pid)]) ||
                targets[@(process.pid)] || CCIsProtected(protected, process.pid)) continue;
            process.reason = @"Claude 子进程";
            process.protectedFromTermination = NO;
            targets[@(process.pid)] = process;
            changed = YES;
        }
    } while (changed);
}

static void CCExpand(NSDictionary<NSNumber *, CCProcess *> *snapshot,
                     NSMutableDictionary<NSNumber *, CCProcess *> *targets) {
    CCExpandWithWarnings(snapshot, targets, nil);
}

static NSArray<CCProcess *> *CCAliveWithWarnings(NSDictionary<NSNumber *, CCProcess *> *targets, NSMutableArray *warnings) {
    NSMutableArray *alive = [NSMutableArray array];
    for (CCProcess *process in targets.allValues) {
        CCProcess *current = CCReadProcessWithWarnings(process.pid, NO, warnings);
        if (CCSame(process, current)) [alive addObject:process];
    }
    return [alive sortedArrayUsingComparator:^NSComparisonResult(CCProcess *a, CCProcess *b) {
        return [@(a.pid) compare:@(b.pid)];
    }];
}

static NSArray<CCProcess *> *CCAlive(NSDictionary<NSNumber *, CCProcess *> *targets) {
    return CCAliveWithWarnings(targets, nil);
}

static void CCRefresh(NSMutableDictionary<NSNumber *, CCProcess *> *targets, NSMutableArray *warnings) {
    // Retain exited parents' captured children, and discover new children before a signal.
    CCExpandWithWarnings(CCSnapshotWithWarnings(warnings), targets, warnings);
}

static NSUInteger CCClosableCount(NSArray<CCProcess *> *processes) {
    NSUInteger count = 0;
    for (CCProcess *process in processes) if (!process.protectedFromTermination) count++;
    return count;
}

static void CCWait(NSMutableDictionary<NSNumber *, CCProcess *> *targets, NSTimeInterval seconds, NSMutableArray *warnings) {
    NSDate *end = [NSDate dateWithTimeIntervalSinceNow:seconds];
    while (end.timeIntervalSinceNow > 0) {
        CCRefresh(targets, warnings);
        if (CCClosableCount(CCAliveWithWarnings(targets, warnings)) == 0) return;
        [NSThread sleepForTimeInterval:0.15];
    }
}

static NSUInteger CCDepth(CCProcess *process, NSDictionary<NSNumber *, CCProcess *> *targets) {
    NSMutableSet *visited = [NSMutableSet set];
    NSUInteger depth = 0;
    while (process && ![visited containsObject:@(process.pid)]) {
        [visited addObject:@(process.pid)];
        process = targets[@(process.parent)];
        if (process) depth++;
    }
    return depth;
}

static NSArray<CCProcess *> *CCSignalOrder(NSArray<CCProcess *> *alive,
                                         NSDictionary<NSNumber *, CCProcess *> *targets) {
    return [alive sortedArrayUsingComparator:^NSComparisonResult(CCProcess *a, CCProcess *b) {
        NSUInteger left = CCDepth(a, targets), right = CCDepth(b, targets);
        if (left != right) return left > right ? NSOrderedAscending : NSOrderedDescending;
        return [@(b.pid) compare:@(a.pid)];
    }];
}

static void CCSignal(NSMutableDictionary<NSNumber *, CCProcess *> *targets, int value,
                     NSMutableArray<NSString *> *errors, NSMutableArray *warnings) {
    CCRefresh(targets, warnings);
    // Children first reduces orphaning when their parent exits after a signal.
    for (CCProcess *process in CCSignalOrder(CCAliveWithWarnings(targets, warnings), targets)) {
        if (process.protectedFromTermination) continue;
        // Check PID + UID + birth time immediately before signalling (PID reuse).
        if (!CCSame(process, CCReadProcessWithWarnings(process.pid, NO, warnings))) continue;
        if (kill(process.pid, value) != 0 && errno != ESRCH) {
            [errors addObject:[NSString stringWithFormat:@"PID %d：%s", process.pid, strerror(errno)]];
        }
    }
}

static NSDictionary *CCResult(NSArray<CCProcess *> *processes, NSUInteger before, NSArray *errors, NSArray *warnings) {
    NSMutableArray *rows = [NSMutableArray array];
    for (CCProcess *process in processes) {
        [rows addObject:@{@"pid": @(process.pid), @"parent_pid": @(process.parent),
                          @"type": process.reason ?: @"Claude", @"executable": process.path ?: @"",
                          @"protected": process.protectedFromTermination ? @YES : @NO}];
    }
    return @{@"initial_count": @(before), @"remaining_count": @(processes.count),
             @"protected_count": @(processes.count - CCClosableCount(processes)),
             @"closable_count": @(CCClosableCount(processes)),
             @"scan_complete": warnings.count == 0 ? @YES : @NO,
             @"success": processes.count == 0 && errors.count == 0 && warnings.count == 0 ? @YES : @NO,
             @"scope": @"current_user", @"processes": rows, @"errors": errors ?: @[],
             @"warnings": warnings ?: @[]};
}

static NSDictionary *CCCheck(void) {
    NSMutableDictionary *targets = [NSMutableDictionary dictionary];
    NSMutableArray *warnings = [NSMutableArray array];
    CCRefresh(targets, warnings);
    NSArray *alive = CCAliveWithWarnings(targets, warnings);
    return CCResult(alive, alive.count, @[], warnings);
}

static NSDictionary *CCClose(void) {
    NSMutableDictionary *targets = [NSMutableDictionary dictionary];
    NSMutableArray *errors = [NSMutableArray array];
    NSMutableArray *warnings = [NSMutableArray array];
    CCRefresh(targets, warnings);
    NSArray *initial = CCAliveWithWarnings(targets, warnings);
    NSUInteger before = initial.count;
    // Only ask already-running Claude applications to quit; never launch Claude to quit it.
    for (NSRunningApplication *application in [NSRunningApplication runningApplicationsWithBundleIdentifier:@"com.anthropic.claudefordesktop"]) {
        CCProcess *captured = targets[@(application.processIdentifier)];
        if (captured && !captured.protectedFromTermination &&
            CCSame(captured, CCReadProcessWithWarnings(captured.pid, NO, warnings))) [application terminate];
    }
    if (CCClosableCount(initial)) CCWait(targets, 2.0, warnings);
    CCSignal(targets, SIGTERM, errors, warnings);
    CCWait(targets, 3.0, warnings);
    CCSignal(targets, SIGKILL, errors, warnings);
    CCWait(targets, 0.8, warnings);
    // Allow a short interval to detect an immediately respawned process.
    [NSThread sleepForTimeInterval:0.5];
    CCRefresh(targets, warnings);
    NSArray *remaining = CCAliveWithWarnings(targets, warnings);
    return CCResult(remaining, before, errors, warnings);
}

static NSString *CCDetails(NSDictionary *result) {
    NSMutableArray *lines = [NSMutableArray array];
    for (NSDictionary *row in result[@"processes"]) {
        [lines addObject:[NSString stringWithFormat:@"PID %@ · %@%@\n%@", row[@"pid"], row[@"type"],
                          [row[@"protected"] boolValue] ? @"（工具的祖先进程，已保护）" : @"", row[@"executable"]]];
    }
    [lines addObjectsFromArray:result[@"errors"] ?: @[]];
    [lines addObjectsFromArray:result[@"warnings"] ?: @[]];
    return [lines componentsJoinedByString:@"\n\n"];
}

static void CCShowResult(NSDictionary *result, BOOL closed) {
    NSUInteger count = [result[@"remaining_count"] unsignedIntegerValue];
    NSUInteger protectedCount = [result[@"protected_count"] unsignedIntegerValue];
    BOOL incomplete = ![result[@"scan_complete"] boolValue];
    BOOL failed = [result[@"errors"] count] > 0;
    NSAlert *alert = [NSAlert new];
    alert.alertStyle = count || incomplete || failed ? NSAlertStyleWarning : NSAlertStyleInformational;
    if (incomplete || failed) {
        alert.messageText = @"检查未完整完成";
        alert.informativeText = [NSString stringWithFormat:@"已识别 %lu 个 Claude 进程；部分信息读取或关闭失败，不能据此确认已全部关闭。请查看详情。", (unsigned long)count];
    } else if (protectedCount) {
        alert.messageText = @"Claude 仍有受保护进程运行";
        alert.informativeText = [NSString stringWithFormat:@"发现 %lu 个进程，其中 %lu 个是启动本工具的祖先进程。为保护当前终端，未结束这些进程，也未沿它们扩展子任务。请从 Finder 独立启动关闭工具。", (unsigned long)count, (unsigned long)protectedCount];
    } else if (closed) {
        alert.messageText = count ? @"Claude 仍有进程运行" : @"Claude 已关闭";
        alert.informativeText = count ? [NSString stringWithFormat:@"复查发现 %lu 个进程。可重试关闭，或查看下方详情。", (unsigned long)count] :
            ([result[@"initial_count"] unsignedIntegerValue] ? @"关闭完成，复查未检测到 Claude 进程。" : @"当前未检测到 Claude 进程，无需关闭。");
    } else {
        alert.messageText = count ? [NSString stringWithFormat:@"Claude 正在运行：%lu 个进程", (unsigned long)count] : @"未检测到 Claude 进程";
        alert.informativeText = count ? @"下方列出了 Claude 及其子进程。点击“一键关闭”会结束正在进行的 Claude 任务。" : @"本次检查未发现当前用户的可识别 Claude 桌面版或 Claude Code 进程。";
    }
    [alert addButtonWithTitle:@"知道了"];
    if ([result[@"closable_count"] unsignedIntegerValue]) [alert addButtonWithTitle:closed ? @"重试关闭" : @"一键关闭"];
    NSString *details = CCDetails(result);
    if (details.length) {
        NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:NSMakeRect(0, 0, 520, 180)];
        scroll.hasVerticalScroller = YES;
        scroll.borderType = NSBezelBorder;
        NSTextView *text = [[NSTextView alloc] initWithFrame:scroll.bounds];
        text.editable = NO;
        text.font = [NSFont systemFontOfSize:12];
        text.textContainerInset = NSMakeSize(8, 8);
        text.string = details;
        scroll.documentView = text;
        alert.accessoryView = scroll;
    }
    [NSApp activateIgnoringOtherApps:YES];
    if ([alert runModal] == NSAlertSecondButtonReturn) {
        // Dispatch another close after the result dialog has left its modal loop.
        [[NSNotificationCenter defaultCenter] postNotificationName:@"CCCloseRequested" object:nil];
    } else {
        [NSApp terminate:nil];
    }
}

@interface CCDelegate : NSObject <NSApplicationDelegate>
@property BOOL close;
@property(strong) NSWindow *progress;
@end
@implementation CCDelegate
- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    [[NSNotificationCenter defaultCenter] addObserver:self selector:@selector(closeRequested:) name:@"CCCloseRequested" object:nil];
    [self run];
}
- (void)closeRequested:(NSNotification *)notification { self.close = YES; [self run]; }
- (void)run {
    self.progress = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 380, 110) styleMask:NSWindowStyleMaskTitled backing:NSBackingStoreBuffered defer:NO];
    self.progress.title = @"Claude 一键工具";
    NSTextField *label = [NSTextField labelWithString:self.close ? @"正在关闭 Claude 并检查残留进程…" : @"正在检查 Claude 进程…"];
    label.frame = NSMakeRect(24, 58, 340, 24);
    [self.progress.contentView addSubview:label];
    NSProgressIndicator *spinner = [[NSProgressIndicator alloc] initWithFrame:NSMakeRect(24, 24, 330, 14)];
    spinner.style = NSProgressIndicatorStyleBar;
    spinner.indeterminate = YES;
    [spinner startAnimation:nil];
    [self.progress.contentView addSubview:spinner];
    [self.progress center];
    [self.progress makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
    BOOL close = self.close;
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        @autoreleasepool {
            NSDictionary *result;
            @try { result = close ? CCClose() : CCCheck(); }
            @catch (NSException *exception) {
                dispatch_async(dispatch_get_main_queue(), ^{
                    [self.progress orderOut:nil];
                    NSAlert *alert = [NSAlert new];
                    alert.messageText = @"无法完成进程检查";
                    alert.informativeText = exception.reason ?: @"请稍后重试。";
                    [alert runModal];
                    [NSApp terminate:nil];
                });
                return;
            }
            dispatch_async(dispatch_get_main_queue(), ^{ [self.progress orderOut:nil]; CCShowResult(result, close); });
        }
    });
}
@end

#ifndef CC_TESTING
int main(int argc, const char *argv[]) {
    @autoreleasepool {
        NSArray *arguments = NSProcessInfo.processInfo.arguments;
        if ([arguments containsObject:@"--status"]) {
            @try {
                NSDictionary *result = CCCheck();
                NSData *data = [NSJSONSerialization dataWithJSONObject:result options:NSJSONWritingPrettyPrinted error:nil];
                puts([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding].UTF8String);
                return [result[@"scan_complete"] boolValue] ? 0 : 2;
            } @catch (NSException *exception) { fprintf(stderr, "%s\n", exception.reason.UTF8String); return 1; }
        }
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
        CCDelegate *delegate = [CCDelegate new];
        delegate.close = ![NSBundle.mainBundle.bundleIdentifier hasSuffix:@".status"];
        NSApp.delegate = delegate;
        [NSApp run];
    }
    return 0;
}
#endif
