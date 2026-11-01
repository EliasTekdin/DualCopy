// SPDX-License-Identifier: MIT
// DualCopy clipboard bridge for SpringBoard.
#import <UIKit/UIKit.h>
#import <Foundation/Foundation.h>
#import <os/log.h>
#import <objc/runtime.h>
#import <dlfcn.h>
#import <sys/socket.h>
#import <netinet/in.h>
#import <arpa/inet.h>
#import <unistd.h>
#import <errno.h>
#import <string.h>

#ifndef APELIN_BUILD_ID
#define APELIN_BUILD_ID "development"
#endif
#ifndef APELIN_PACKAGE_ID
#define APELIN_PACKAGE_ID "local.apelin.dualcopy"
#endif

static const uint16_t kDualCopyPort = 49732;
static const NSUInteger kDualCopyHistoryLimit = 50;
static os_log_t gLog;
static dispatch_queue_t gNetworkQueue;
static NSMutableArray<NSDictionary *> *gHistory;
static int gListenFD = -1;
static int gClientFD = -1;
static NSString *gLastClipboard;
static dispatch_source_t gClipboardTimer;
static dispatch_once_t gStartOnce;
static void (*gOriginalDidFinishLaunching)(id, SEL, id);

static os_log_t DualCopyLog(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        gLog = os_log_create(APELIN_PACKAGE_ID, "tweak");
    });
    return gLog;
}

static NSString *DualCopyHistoryPath(void) {
    return @"/var/mobile/Library/Preferences/com.dualcopy.tweak-history.json";
}

static NSString *DualCopyBootstrapRoot(void) {
#ifdef APELIN_BOOTSTRAP_ROOTHIDE
    static NSString *root;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        static const char anchor = 0;
        Dl_info info;
        if (dladdr(&anchor, &info) == 0 || info.dli_fname == NULL) return;
        NSString *path = [NSString stringWithUTF8String:info.dli_fname];
        NSArray<NSString *> *parts = [path componentsSeparatedByString:@"/"];
        for (NSUInteger i = 0; i < parts.count; i++) {
            if ([parts[i] hasPrefix:@".jbroot-"]) {
                root = [[parts subarrayWithRange:NSMakeRange(0, i + 1)] componentsJoinedByString:@"/"];
                break;
            }
        }
    });
    return root;
#else
    return @"/var/jb";
#endif
}

static void DualCopyLoadHistory(void) {
    gHistory = [NSMutableArray array];
    NSData *data = [NSData dataWithContentsOfFile:DualCopyHistoryPath()];
    if (data == nil) return;
    id value = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    if (![value isKindOfClass:[NSArray class]]) return;
    for (id item in (NSArray *)value) {
        if ([item isKindOfClass:[NSDictionary class]] &&
            [item[@"id"] isKindOfClass:[NSString class]] &&
            [item[@"text"] isKindOfClass:[NSString class]]) {
            [gHistory addObject:item];
            if (gHistory.count >= kDualCopyHistoryLimit) break;
        }
    }
}

static void DualCopySaveHistory(void) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:gHistory options:NSJSONWritingSortedKeys error:nil];
    if (data != nil && ![data writeToFile:DualCopyHistoryPath() atomically:YES]) {
        os_log_error(DualCopyLog(), "Could not persist clipboard history");
    }
}

static NSDictionary *DualCopyNewest(void) {
    return gHistory.firstObject;
}

static void DualCopyTrimAndSort(void) {
    [gHistory sortUsingComparator:^NSComparisonResult(NSDictionary *a, NSDictionary *b) {
        long long at = [a[@"timestamp"] longLongValue];
        long long bt = [b[@"timestamp"] longLongValue];
        return at == bt ? NSOrderedSame : (at > bt ? NSOrderedAscending : NSOrderedDescending);
    }];
    if (gHistory.count > kDualCopyHistoryLimit) {
        [gHistory removeObjectsInRange:NSMakeRange(kDualCopyHistoryLimit, gHistory.count - kDualCopyHistoryLimit)];
    }
}

static BOOL DualCopyMergeEntry(NSDictionary *entry) {
    NSString *identifier = entry[@"id"];
    if (![identifier isKindOfClass:[NSString class]]) return NO;
    for (NSDictionary *existing in gHistory) {
        if ([existing[@"id"] isEqual:identifier]) return NO;
    }
    [gHistory addObject:entry];
    DualCopyTrimAndSort();
    return YES;
}

static BOOL DualCopyMergeEntries(NSArray *entries) {
    BOOL changed = NO;
    for (id value in entries) {
        if ([value isKindOfClass:[NSDictionary class]] &&
            [value[@"id"] isKindOfClass:[NSString class]] &&
            [value[@"text"] isKindOfClass:[NSString class]]) {
            changed |= DualCopyMergeEntry(value);
        }
    }
    if (changed) DualCopySaveHistory();
    return changed;
}

static void DualCopySendObject(NSDictionary *object) {
    if (gClientFD < 0) return;
    NSError *error = nil;
    NSData *json = [NSJSONSerialization dataWithJSONObject:object options:0 error:&error];
    if (json == nil || error != nil) return;
    NSMutableData *line = [json mutableCopy];
    [line appendBytes:"\n" length:1];
    const uint8_t *bytes = line.bytes;
    size_t remaining = line.length;
    while (remaining > 0) {
        ssize_t sent = send(gClientFD, bytes, remaining, 0);
        if (sent <= 0) {
            if (errno == EINTR) continue;
            close(gClientFD);
            gClientFD = -1;
            return;
        }
        bytes += sent;
        remaining -= (size_t)sent;
    }
}

static void DualCopySendHistory(void) {
    DualCopySendObject(@{ @"type": @"history", @"entries": [gHistory copy] });
}

static void DualCopyApplyRemoteText(NSString *text) {
    if (text.length == 0) return;
    dispatch_async(dispatch_get_main_queue(), ^{
        UIPasteboard *pasteboard = UIPasteboard.generalPasteboard;
        if (![pasteboard.string isEqualToString:text]) {
            gLastClipboard = [text copy];
            pasteboard.string = text;
            os_log(DualCopyLog(), "Applied clipboard text received from Windows");
        }
    });
}

static void DualCopyHandleObject(NSDictionary *object) {
    NSString *type = object[@"type"];
    if ([type isEqualToString:@"hello"]) {
        NSArray *entries = object[@"history"];
        NSDictionary *before = DualCopyNewest();
        BOOL changed = [entries isKindOfClass:[NSArray class]] && DualCopyMergeEntries(entries);
        DualCopySendHistory();
        NSDictionary *after = DualCopyNewest();
        if (changed && after != nil && ![after[@"id"] isEqual:before[@"id"]] &&
            [after[@"timestamp"] longLongValue] > [before[@"timestamp"] longLongValue]) {
            DualCopyApplyRemoteText(after[@"text"]);
        }
        return;
    }
    if ([type isEqualToString:@"history"]) {
        NSArray *entries = object[@"entries"];
        if ([entries isKindOfClass:[NSArray class]]) DualCopyMergeEntries(entries);
        return;
    }
    if ([type isEqualToString:@"clipboard"]) {
        NSString *text = object[@"text"];
        if (![text isKindOfClass:[NSString class]] || text.length == 0) return;
        DualCopyMergeEntry(object);
        DualCopySaveHistory();
        DualCopyApplyRemoteText(text);
    }
}

static void DualCopyReadClient(int clientFD) {
    gClientFD = clientFD;
    DualCopySendObject(@{ @"type": @"hello", @"device": @"ios", @"history": [gHistory copy] });
    NSMutableData *pending = [NSMutableData data];
    uint8_t buffer[4096];
    while (gClientFD == clientFD) {
        ssize_t count = recv(clientFD, buffer, sizeof(buffer), 0);
        if (count <= 0) break;
        [pending appendBytes:buffer length:(NSUInteger)count];
        while (true) {
            const uint8_t *bytes = pending.bytes;
            const uint8_t *newline = memchr(bytes, '\n', pending.length);
            if (newline == NULL) break;
            NSUInteger length = (NSUInteger)(newline - bytes);
            NSData *line = [pending subdataWithRange:NSMakeRange(0, length)];
            [pending replaceBytesInRange:NSMakeRange(0, length + 1) withBytes:NULL length:0];
            NSDictionary *object = [NSJSONSerialization JSONObjectWithData:line options:0 error:nil];
            if ([object isKindOfClass:[NSDictionary class]]) DualCopyHandleObject(object);
        }
    }
    if (gClientFD == clientFD) {
        close(clientFD);
        gClientFD = -1;
    }
    os_log(DualCopyLog(), "Windows clipboard peer disconnected");
}

static void DualCopyAcceptLoop(void) {
    gListenFD = socket(AF_INET, SOCK_STREAM, 0);
    if (gListenFD < 0) {
        os_log_error(DualCopyLog(), "Could not create clipboard socket");
        return;
    }
    int reuse = 1;
    setsockopt(gListenFD, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
    struct sockaddr_in address = {0};
    address.sin_len = sizeof(address);
    address.sin_family = AF_INET;
    address.sin_port = htons(kDualCopyPort);
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(gListenFD, (struct sockaddr *)&address, sizeof(address)) != 0 || listen(gListenFD, 1) != 0) {
        os_log_error(DualCopyLog(), "Could not listen for Windows clipboard peer on port %u", kDualCopyPort);
        close(gListenFD);
        gListenFD = -1;
        return;
    }
    os_log(DualCopyLog(), "DualCopy listening for Windows on TCP port %u", kDualCopyPort);
    while (true) {
        int client = accept(gListenFD, NULL, NULL);
        if (client < 0) {
            if (errno == EINTR) continue;
            break;
        }
        DualCopyReadClient(client);
    }
}

static void DualCopyRecordLocalClipboard(NSString *text) {
    if (text.length == 0 || [text isEqualToString:gLastClipboard]) return;
    gLastClipboard = [text copy];
    dispatch_async(gNetworkQueue, ^{
        NSString *identifier = [NSUUID UUID].UUIDString;
        NSDictionary *entry = @{
            @"type": @"clipboard",
            @"id": identifier,
            @"text": text,
            @"timestamp": @((long long)(NSDate.date.timeIntervalSince1970 * 1000.0))
        };
        DualCopyMergeEntry(entry);
        DualCopySaveHistory();
        DualCopySendObject(entry);
        os_log(DualCopyLog(), "Published a local clipboard entry to Windows");
    });
}

static void DualCopyStartClipboardPolling(void) {
    if (gClipboardTimer != nil) return;
    UIPasteboard *pasteboard = UIPasteboard.generalPasteboard;
    gLastClipboard = [pasteboard.string copy];
    gClipboardTimer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, dispatch_get_main_queue());
    dispatch_source_set_timer(gClipboardTimer, dispatch_time(DISPATCH_TIME_NOW, 500 * NSEC_PER_MSEC), 500 * NSEC_PER_MSEC, 100 * NSEC_PER_MSEC);
    dispatch_source_set_event_handler(gClipboardTimer, ^{
        NSString *text = UIPasteboard.generalPasteboard.string;
        if (text != nil && ![text isEqualToString:gLastClipboard]) DualCopyRecordLocalClipboard(text);
    });
    dispatch_resume(gClipboardTimer);
}

static void DualCopyStart(void) {
    dispatch_once(&gStartOnce, ^{
        gNetworkQueue = dispatch_queue_create("com.dualcopy.network", DISPATCH_QUEUE_SERIAL);
        dispatch_async(gNetworkQueue, ^{
            DualCopyLoadHistory();
            dispatch_async(dispatch_get_main_queue(), ^{ DualCopyStartClipboardPolling(); });
            DualCopyAcceptLoop();
        });
    });
}

static void DualCopyHookedDidFinishLaunching(id self, SEL selector, id application) {
    if (gOriginalDidFinishLaunching != NULL) gOriginalDidFinishLaunching(self, selector, application);
    DualCopyStart();
}

static void DualCopyInstallLaunchHook(void) {
    Class springBoard = objc_getClass("SpringBoard");
    if (springBoard == Nil) return;
    IMP hookSymbol = (IMP)dlsym(RTLD_DEFAULT, "MSHookMessageEx");
    void *substrateHandle = NULL;
    if (hookSymbol == NULL) {
        NSString *root = DualCopyBootstrapRoot();
        if (root.length > 0) {
            NSString *path = [root stringByAppendingPathComponent:@"usr/lib/libsubstrate.dylib"];
            substrateHandle = dlopen(path.fileSystemRepresentation, RTLD_NOW | RTLD_GLOBAL);
            if (substrateHandle != NULL) hookSymbol = (IMP)dlsym(substrateHandle, "MSHookMessageEx");
        }
    }
    if (hookSymbol == NULL) {
        os_log_error(DualCopyLog(), "MSHookMessageEx is unavailable; clipboard bridge is disabled");
        return;
    }
    typedef void (*HookFunction)(Class, SEL, IMP, IMP *);
    ((HookFunction)hookSymbol)(springBoard, @selector(applicationDidFinishLaunching:), (IMP)DualCopyHookedDidFinishLaunching, (IMP *)&gOriginalDidFinishLaunching);
}

__attribute__((constructor)) static void DualCopyInitialize(void) {
    os_log(DualCopyLog(), "ApelinLoaded %{public}s in %{public}s", APELIN_BUILD_ID, getprogname());
    DualCopyInstallLaunchHook();
}
