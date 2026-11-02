// Strictly isolated process-close integration tests. This translation unit never
// enumerates the real user process list and never asks real desktop apps to quit.
#import <AppKit/AppKit.h>
#import <Foundation/Foundation.h>
#import <libproc.h>
#import <signal.h>
#import <sys/wait.h>
#import <poll.h>
#import <unistd.h>

static int CIListPids(uint32_t type, uint32_t typeinfo, void *buffer, int buffersize);
static int CIKill(pid_t pid, int signalNumber);

@interface CINoDesktopApplications : NSObject
+ (NSArray<CINoDesktopApplications *> *)runningApplicationsWithBundleIdentifier:(NSString *)identifier;
@property(readonly) pid_t processIdentifier;
- (BOOL)terminate;
@end

#define CC_TESTING 1
#define proc_listpids CIListPids
#define kill CIKill
#define NSRunningApplication CINoDesktopApplications
#import "ClaudeControl.m"
#undef NSRunningApplication
#undef kill
#undef proc_listpids

static NSMutableDictionary<NSNumber *, CCProcess *> *CIAllowed;
static NSMutableArray<NSDictionary *> *CISignals;
static NSUInteger CIChecks = 0;
static NSUInteger CIDeniedSignals = 0;
static NSUInteger CIDesktopQueries = 0;
static BOOL CIForceStartFailure = NO;
static pid_t CILastStartedPID = -1;

@implementation CINoDesktopApplications
+ (NSArray<CINoDesktopApplications *> *)runningApplicationsWithBundleIdentifier:(NSString *)identifier {
    CIDesktopQueries++;
    return @[];
}
- (pid_t)processIdentifier { return -1; }
- (BOOL)terminate { abort(); }
@end

static void CIAssert(BOOL condition, NSString *message) {
    CIChecks++;
    if (!condition) @throw [NSException exceptionWithName:@"CloseIntegrationFailure" reason:message userInfo:nil];
}

// There is deliberately no fallback to proc_listpids: the default is an empty
// list, represented by a zero PID so the normal snapshot code can read it.
static int CIListPids(uint32_t type, uint32_t typeinfo, void *buffer, int buffersize) {
    if (type != PROC_UID_ONLY || typeinfo != getuid() || buffersize < 0) {
        errno = EINVAL;
        return -1;
    }
    NSMutableArray<NSNumber *> *live = [NSMutableArray array];
    for (CCProcess *expected in CIAllowed.allValues) {
        CCProcess *current = CCReadProcess(expected.pid, NO);
        if (CCSame(expected, current)) [live addObject:@(expected.pid)];
    }
    NSUInteger slots = MAX((NSUInteger)1, live.count);
    int bytes = (int)(slots * sizeof(pid_t));
    if (!buffer) return bytes;
    int used = MIN(bytes, buffersize - buffersize % (int)sizeof(pid_t));
    memset(buffer, 0, (size_t)used);
    for (NSUInteger i = 0; i < live.count && (i + 1) * sizeof(pid_t) <= (NSUInteger)used; i++)
        ((pid_t *)buffer)[i] = live[i].intValue;
    return used;
}

// A second independent guard denies every mutating signal outside the recorded
// fixture identity, even if an enumeration or production matching bug occurs.
static int CIKill(pid_t pid, int signalNumber) {
    if (signalNumber == 0) return kill(pid, 0); // Read-only existence probe.
    CCProcess *expected = CIAllowed[@(pid)];
    if (pid <= 1 || pid == getpid() || !expected || !CCSame(expected, CCReadProcess(pid, NO))) {
        CIDeniedSignals++;
        errno = EPERM;
        return -1;
    }
    [CISignals addObject:@{@"pid": @(pid), @"signal": @(signalNumber)}];
    return kill(pid, signalNumber);
}

@interface CIFixture : NSObject
@property(strong) NSTask *task;
@property(strong) NSPipe *output;
@property(strong) CCProcess *root;
@property(strong) CCProcess *child;
@end
@implementation CIFixture
@end
static void CICleanup(CIFixture *fixture);

static NSString *CIReadyLine(NSFileHandle *handle) {
    NSMutableData *bytes = [NSMutableData data];
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:5];
    while (deadline.timeIntervalSinceNow > 0 && bytes.length < 128) {
        struct pollfd descriptor = {.fd = handle.fileDescriptor, .events = POLLIN};
        int ready = poll(&descriptor, 1, 100);
        if (ready < 0 && errno == EINTR) continue;
        if (ready <= 0) continue;
        char byte = 0;
        ssize_t count = read(descriptor.fd, &byte, 1);
        if (count != 1) break;
        if (byte == '\n') return [[NSString alloc] initWithData:bytes encoding:NSUTF8StringEncoding];
        [bytes appendBytes:&byte length:1];
    }
    return nil;
}

static void CIBuildFixture(NSString *path) {
    NSString *source =
        @"#include <signal.h>\n#include <stdio.h>\n#include <stdlib.h>\n#include <string.h>\n#include <unistd.h>\n"
        "int main(int argc,char **argv) { alarm(30);\n"
        "  if(argc>1 && strcmp(argv[1],\"worker\")==0) { int fd=atoi(argv[2]); write(fd,\"R\",1); close(fd); for(;;) pause(); }\n"
        "  if(argc>1 && strcmp(argv[1],\"ignore\")==0) signal(SIGTERM,SIG_IGN);\n"
        "  int child=0;\n"
        "  if(argc>2 && strcmp(argv[1],\"tree\")==0) {\n"
        "    int ready[2]; if(pipe(ready)) return 10; signal(SIGCHLD,SIG_IGN);\n"
        "    child=fork(); if(child<0) return 11;\n"
        "    if(child==0) { close(ready[0]); char fd[32]; snprintf(fd,sizeof(fd),\"%d\",ready[1]); execl(argv[2],argv[2],\"worker\",fd,(char*)0); _exit(12); }\n"
        "    close(ready[1]); char ok=0; if(read(ready[0],&ok,1)!=1 || ok!='R') return 13; close(ready[0]);\n"
        "  }\n"
        "  printf(\"R %d\\n\",child); fflush(stdout); for(;;) pause();\n"
        "}\n";
    NSTask *compiler = [NSTask new];
    compiler.executableURL = [NSURL fileURLWithPath:@"/usr/bin/xcrun"];
    compiler.arguments = @[@"clang", @"-x", @"c", @"-", @"-o", path];
    NSPipe *input = [NSPipe pipe];
    compiler.standardInput = input;
    NSError *error = nil;
    CIAssert([compiler launchAndReturnError:&error], error.description ?: @"Fixture compiler could not start");
    [input.fileHandleForWriting writeData:[source dataUsingEncoding:NSUTF8StringEncoding]];
    [input.fileHandleForWriting closeFile];
    [compiler waitUntilExit];
    CIAssert(compiler.terminationStatus == 0, @"Harmless fixture compiles successfully");
}

static CIFixture *CIStartFixture(NSString *executable, NSArray<NSString *> *arguments) {
    CIFixture *fixture = [CIFixture new];
    fixture.task = [NSTask new];
    fixture.task.executableURL = [NSURL fileURLWithPath:executable];
    fixture.task.arguments = arguments;
    fixture.output = [NSPipe pipe];
    fixture.task.standardOutput = fixture.output;
    NSError *error = nil;
    CIAssert([fixture.task launchAndReturnError:&error], error.description ?: @"Fixture could not start");
    CILastStartedPID = fixture.task.processIdentifier;
    char canonical[PATH_MAX] = {0};
    NSString *nativeExecutable = realpath(executable.fileSystemRepresentation, canonical) ? @(canonical) : executable;
    @try {
    // Record the direct child before waiting, so failure cleanup remains scoped.
    CCProcess *lastCandidate = nil;
    for (NSUInteger attempt = 0; attempt < 100; attempt++) {
        CCProcess *candidate = CCReadProcess(fixture.task.processIdentifier, NO);
        lastCandidate = candidate;
        if ([candidate.path isEqualToString:executable] || [candidate.path isEqualToString:nativeExecutable]) {
            fixture.root = candidate;
            CIAllowed[@(candidate.pid)] = candidate;
            break;
        }
        if (!fixture.task.running) break;
        usleep(10000);
    }
    if (!fixture.root) fprintf(stderr, "Expected fixture path %s; observed %s; running=%d\n", executable.UTF8String,
                               lastCandidate.path.UTF8String ?: "<none>", fixture.task.running);
    CIAssert(fixture.root != nil, @"Fixture native process identity is recorded");
    NSString *line = CIReadyLine(fixture.output.fileHandleForReading);
    CIAssert([line hasPrefix:@"R "], @"Fixture has installed its handlers and completed child startup");
    pid_t child = [[line substringFromIndex:2] intValue];
    if (child > 0) {
        fixture.child = CCReadProcess(child, NO);
        CIAssert(fixture.child && fixture.child.parent == fixture.root.pid && CCBornNotBefore(fixture.child, fixture.root),
                 @"Worker has real parent identity and a valid creation-time relationship");
        CIAllowed[@(child)] = fixture.child;
    }
    if (CIForceStartFailure)
        @throw [NSException exceptionWithName:@"InjectedFixtureStartFailure" reason:@"Injected failure after fixture startup" userInfo:nil];
    return fixture;
    } @catch (NSException *exception) {
        // The caller cannot own this object until we return. Clean up here if
        // readiness or identity validation throws after the process has started.
        if (!fixture.root) {
            CCProcess *candidate = CCReadProcess(fixture.task.processIdentifier, NO);
            if (candidate.parent == getpid() && [candidate.path isEqualToString:nativeExecutable]) {
                fixture.root = candidate;
                CIAllowed[@(candidate.pid)] = candidate;
            }
        }
        CICleanup(fixture);
        @throw exception;
    }
}

static void CICleanup(CIFixture *fixture) {
    if (!fixture) return;
    if (fixture.child && CCSame(fixture.child, CCReadProcess(fixture.child.pid, NO))) CIKill(fixture.child.pid, SIGKILL);
    if (fixture.root && CCSame(fixture.root, CCReadProcess(fixture.root.pid, NO))) CIKill(fixture.root.pid, SIGKILL);
    if (fixture.task) [fixture.task waitUntilExit];
    [CIAllowed removeAllObjects];
}

static void CITestStartFailureCleanup(NSString *rootExecutable, NSString *workerExecutable) {
    BOOL caught = NO;
    CIForceStartFailure = YES;
    @try { (void)CIStartFixture(rootExecutable, @[@"tree", workerExecutable]); }
    @catch (NSException *exception) { caught = [exception.name isEqualToString:@"InjectedFixtureStartFailure"]; }
    @finally { CIForceStartFailure = NO; }
    CIAssert(caught, @"Injected startup failure propagated after local cleanup");
    CIAssert(CIAllowed.count == 0, @"Failed startup leaves no allowed fixture identities");
    CIAssert(CCReadProcess(CILastStartedPID, NO) == nil, @"Failed startup reaped its direct child before returning");
}

static void CITestTermTree(NSString *rootExecutable, NSString *workerExecutable) {
    CIFixture *fixture = nil;
    @try {
        fixture = CIStartFixture(rootExecutable, @[@"tree", workerExecutable]);
        CIAssert(fixture.child != nil, @"Tree fixture contains a non-Claude worker");
        [CISignals removeAllObjects];
        NSUInteger denied = CIDeniedSignals;
        NSDictionary *before = CCCheck();
        CIAssert([before[@"remaining_count"] unsignedIntegerValue] == 2, @"Real snapshot identifies the fixture root and its worker");
        NSDictionary *result = CCClose();
        if (![result[@"success"] boolValue]) {
            NSData *debug = [NSJSONSerialization dataWithJSONObject:result options:NSJSONWritingPrettyPrinted error:NULL];
            fprintf(stderr, "TERM result: %s\n", [[NSString alloc] initWithData:debug encoding:NSUTF8StringEncoding].UTF8String);
        }
        CIAssert([result[@"initial_count"] unsignedIntegerValue] == 2, @"Full close starts with both fixture processes");
        CIAssert([result[@"remaining_count"] unsignedIntegerValue] == 0 && [result[@"success"] boolValue], @"Full TERM close finishes with a complete zero-process recheck");
        CIAssert([result[@"errors"] count] == 0 && [result[@"warnings"] count] == 0, @"TERM close has no hidden errors or coverage warnings");
        [fixture.task waitUntilExit];
        CIAssert(fixture.task.terminationReason == NSTaskTerminationReasonUncaughtSignal && fixture.task.terminationStatus == SIGTERM, @"Root was stopped by TERM");
        CIAssert(CISignals.count == 2 && [CISignals[0][@"pid"] intValue] == fixture.child.pid &&
                 [CISignals[1][@"pid"] intValue] == fixture.root.pid, @"Worker is signalled before its parent");
        CIAssert([CISignals[0][@"signal"] intValue] == SIGTERM && [CISignals[1][@"signal"] intValue] == SIGTERM,
                 @"Cooperative tree needs no forced KILL");
        CIAssert(CIDeniedSignals == denied, @"Production close attempted no signal outside fixture identities");
    } @finally { CICleanup(fixture); }
}

static void CITestKillFallback(NSString *executable) {
    CIFixture *fixture = nil;
    @try {
        fixture = CIStartFixture(executable, @[@"ignore"]);
        [CISignals removeAllObjects];
        NSUInteger denied = CIDeniedSignals;
        NSDictionary *result = CCClose();
        CIAssert([result[@"initial_count"] unsignedIntegerValue] == 1, @"Fallback close starts with only its stubborn fixture");
        CIAssert([result[@"remaining_count"] unsignedIntegerValue] == 0 && [result[@"success"] boolValue], @"KILL fallback finishes with a complete zero-process recheck");
        CIAssert([result[@"errors"] count] == 0 && [result[@"warnings"] count] == 0, @"Fallback close has no hidden errors or coverage warnings");
        [fixture.task waitUntilExit];
        CIAssert(fixture.task.terminationReason == NSTaskTerminationReasonUncaughtSignal && fixture.task.terminationStatus == SIGKILL, @"TERM-ignoring fixture requires KILL");
        CIAssert(CISignals.count == 2 && [CISignals[0][@"signal"] intValue] == SIGTERM &&
                 [CISignals[1][@"signal"] intValue] == SIGKILL, @"Full close escalates TERM to KILL in order");
        CIAssert(CIDeniedSignals == denied, @"Fallback attempted no signal outside its fixture identity");
    } @finally { CICleanup(fixture); }
}

int main(void) {
    @autoreleasepool {
        CIAllowed = [NSMutableDictionary dictionary];
        CISignals = [NSMutableArray array];
        NSString *directory = nil;
        @try {
            CIAssert(CCSnapshot().count == 0, @"Default test enumeration is empty and never falls back to real processes");
            errno = 0;
            CIAssert(CIKill(getpid(), SIGTERM) == -1 && errno == EPERM, @"Signal guard refuses a non-fixture process");
            CIAssert(CIDeniedSignals == 1 && CISignals.count == 0, @"Rejected test signal did not reach the kernel");
            char pattern[] = "/tmp/claude-close-integration.XXXXXX";
            char *created = mkdtemp(pattern);
            CIAssert(created != NULL, @"Creates isolated fixture directory");
            directory = @(created).stringByResolvingSymlinksInPath;
            NSString *rootExecutable = [directory stringByAppendingPathComponent:@"claude"];
            NSString *workerExecutable = [directory stringByAppendingPathComponent:@"fixture-worker"];
            CIBuildFixture(rootExecutable);
            CIAssert([NSFileManager.defaultManager copyItemAtPath:rootExecutable toPath:workerExecutable error:NULL], @"Creates an isolated non-Claude worker executable");
            CITestStartFailureCleanup(rootExecutable, workerExecutable);
            CITestTermTree(rootExecutable, workerExecutable);
            CITestKillFallback(rootExecutable);
            CIAssert(CIDesktopQueries == 2, @"Both full closes used the empty desktop-app stub");
            CIAssert(CCSnapshot().count == 0, @"Fixture whitelist is empty after cleanup");
            printf("%lu isolated close integration checks passed. No real user process enumeration or desktop quit was used.\n", (unsigned long)CIChecks);
        } @catch (NSException *exception) {
            fprintf(stderr, "FAIL: %s\n", exception.reason.UTF8String);
            if (directory) [NSFileManager.defaultManager removeItemAtPath:directory error:NULL];
            return 1;
        }
        if (directory) [NSFileManager.defaultManager removeItemAtPath:directory error:NULL];
    }
    return 0;
}
