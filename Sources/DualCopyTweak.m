// SPDX-License-Identifier: MIT
// DualCopy clipboard bridge for SpringBoard.
#import <UIKit/UIKit.h>
#import <Foundation/Foundation.h>
#import <CoreFoundation/CoreFoundation.h>
#import <os/log.h>
#import <sys/socket.h>
#import <netinet/in.h>
#import <arpa/inet.h>
#import <unistd.h>
#import <errno.h>
#import <string.h>
#import <notify.h>

#ifndef APELIN_BUILD_ID
#define APELIN_BUILD_ID "development"
#endif
#ifndef APELIN_PACKAGE_ID
#define APELIN_PACKAGE_ID "com.eliastekdin.dualcopy"
#endif

static const uint16_t kDualCopyDefaultPort = 49732;
static const NSUInteger kDualCopyDefaultHistoryLimit = 50;
static const NSUInteger kDualCopyDefaultMaxImageBytes = 4 * 1024 * 1024;
static os_log_t gLog;
static dispatch_queue_t gNetworkQueue;
static NSMutableArray<NSDictionary *> *gHistory;
static int gListenFD = -1;
static NSMutableSet<NSNumber *> *gPeerFDs;
static NSLock *gSocketLock;
static NSString *gLastClipboard;
static NSData *gLastImageData;
static NSInteger gLastPasteboardChangeCount;
static dispatch_source_t gClipboardTimer;
static dispatch_once_t gStartOnce;
static int gSettingsNotificationToken;
static BOOL gEnabled = YES;
static BOOL gSyncImages = YES;
static uint16_t gListenPort = kDualCopyDefaultPort;
static NSString *gComputerAddress;
static uint16_t gComputerPort = kDualCopyDefaultPort;
static NSUInteger gPollIntervalMilliseconds = 500;
static NSUInteger gHistoryLimit = kDualCopyDefaultHistoryLimit;
static NSUInteger gMaxImageBytes = kDualCopyDefaultMaxImageBytes;

static os_log_t DualCopyLog(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        gLog = os_log_create(APELIN_PACKAGE_ID, "tweak");
    });
    return gLog;
}

static id DualCopyPreference(NSString *key) {
    CFTypeRef value = CFPreferencesCopyAppValue((__bridge CFStringRef)key, CFSTR(APELIN_PACKAGE_ID));
    return CFBridgingRelease(value);
}

static NSUInteger DualCopyPositiveInteger(id value, NSUInteger fallback, NSUInteger minimum, NSUInteger maximum) {
    if (![value respondsToSelector:@selector(unsignedIntegerValue)]) return fallback;
    NSUInteger number = [value unsignedIntegerValue];
    return number < minimum || number > maximum ? fallback : number;
}

static void DualCopyTrimAndSort(void);

static void DualCopyReadSettings(void) {
    id enabled = DualCopyPreference(@"enabled");
    id images = DualCopyPreference(@"syncImages");
    id port = DualCopyPreference(@"listenPort");
    id computerAddress = DualCopyPreference(@"computerAddress");
    id computerPort = DualCopyPreference(@"computerPort");
    id interval = DualCopyPreference(@"pollInterval");
    id historyLimit = DualCopyPreference(@"historyLimit");
    id maxImageBytes = DualCopyPreference(@"maxImageBytes");
    if ([enabled isKindOfClass:[NSNumber class]]) gEnabled = [enabled boolValue];
    if ([images isKindOfClass:[NSNumber class]]) gSyncImages = [images boolValue];
    if ([computerAddress isKindOfClass:[NSString class]]) gComputerAddress = [computerAddress stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    else gComputerAddress = @"";
    gListenPort = (uint16_t)DualCopyPositiveInteger(port, kDualCopyDefaultPort, 1024, 65535);
    gComputerPort = (uint16_t)DualCopyPositiveInteger(computerPort, kDualCopyDefaultPort, 1024, 65535);
    gPollIntervalMilliseconds = DualCopyPositiveInteger(interval, 500, 100, 5000);
    gHistoryLimit = DualCopyPositiveInteger(historyLimit, kDualCopyDefaultHistoryLimit, 10, 100);
    gMaxImageBytes = DualCopyPositiveInteger(maxImageBytes, kDualCopyDefaultMaxImageBytes, 256 * 1024, 16 * 1024 * 1024);
    if (gHistory != nil) DualCopyTrimAndSort();
    os_log(DualCopyLog(), "Settings loaded: enabled=%{public}s images=%{public}s port=%u computer=%{public}s computerPort=%u interval=%lu history=%lu maxImageBytes=%lu",
           gEnabled ? "yes" : "no", gSyncImages ? "yes" : "no", gListenPort,
           gComputerAddress.UTF8String ?: "", gComputerPort,
           (unsigned long)gPollIntervalMilliseconds, (unsigned long)gHistoryLimit, (unsigned long)gMaxImageBytes);
}

static NSString *DualCopyHistoryPath(void) {
    return [NSString stringWithFormat:@"/var/mobile/Library/Preferences/%s-history.json", APELIN_PACKAGE_ID];
}

static BOOL DualCopyEntryValid(NSDictionary *entry) {
    if (![entry[@"id"] isKindOfClass:[NSString class]]) return NO;
    NSString *kind = entry[@"kind"];
    if ([kind isEqualToString:@"image"]) return [entry[@"data"] isKindOfClass:[NSString class]];
    return ([kind isEqualToString:@"text"] || kind == nil) && [entry[@"text"] isKindOfClass:[NSString class]] && [entry[@"text"] length] > 0;
}

static NSDictionary *DualCopyNormaliseEntry(NSDictionary *entry) {
    if (![entry[@"kind"] isKindOfClass:[NSString class]]) {
        NSMutableDictionary *copy = [entry mutableCopy];
        copy[@"kind"] = @"text";
        return copy;
    }
    return entry;
}

static void DualCopyLoadHistory(void) {
    gHistory = [NSMutableArray array];
    NSData *data = [NSData dataWithContentsOfFile:DualCopyHistoryPath()];
    if (data == nil) return;
    id value = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    if (![value isKindOfClass:[NSArray class]]) return;
    for (id item in (NSArray *)value) {
        if (![item isKindOfClass:[NSDictionary class]]) continue;
        NSDictionary *entry = DualCopyNormaliseEntry(item);
        if (DualCopyEntryValid(entry)) [gHistory addObject:entry];
    }
    DualCopyTrimAndSort();
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
    if (gHistory.count > gHistoryLimit) {
        [gHistory removeObjectsInRange:NSMakeRange(gHistoryLimit, gHistory.count - gHistoryLimit)];
    }
}

static BOOL DualCopyMergeEntry(NSDictionary *entry) {
    NSDictionary *normalised = DualCopyNormaliseEntry(entry);
    if (!DualCopyEntryValid(normalised)) return NO;
    if ([normalised[@"kind"] isEqualToString:@"image"] && !gSyncImages) return NO;
    NSString *identifier = normalised[@"id"];
    for (NSDictionary *existing in gHistory) {
        if ([existing[@"id"] isEqual:identifier]) return NO;
    }
    [gHistory addObject:normalised];
    DualCopyTrimAndSort();
    return YES;
}

static BOOL DualCopyMergeEntries(NSArray *entries) {
    BOOL changed = NO;
    for (id value in entries) {
        if ([value isKindOfClass:[NSDictionary class]]) changed |= DualCopyMergeEntry(value);
    }
    if (changed) DualCopySaveHistory();
    return changed;
}

static BOOL DualCopySendLineLocked(NSData *line, int clientFD) {
    if (gPeerFDs == nil || ![gPeerFDs containsObject:@(clientFD)]) return NO;

    const uint8_t *bytes = line.bytes;
    size_t remaining = line.length;
    while (remaining > 0) {
        ssize_t sent = send(clientFD, bytes, remaining, 0);
        if (sent <= 0) {
            if (errno == EINTR) continue;
            [gPeerFDs removeObject:@(clientFD)];
            close(clientFD);
            return NO;
        }
        bytes += sent;
        remaining -= (size_t)sent;
    }
    return YES;
}

static NSData *DualCopyJSONLine(NSDictionary *object) {
    NSError *error = nil;
    NSData *json = [NSJSONSerialization dataWithJSONObject:object options:0 error:&error];
    if (json == nil || error != nil) return nil;
    NSMutableData *line = [json mutableCopy];
    [line appendBytes:"\n" length:1];
    return line;
}

static void DualCopyPrepareSocket(int fd) {
    int noSignal = 1;
    setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &noSignal, sizeof(noSignal));
}

static void DualCopySendObjectToPeer(NSDictionary *object, int clientFD) {
    if (!gEnabled) return;
    NSData *line = DualCopyJSONLine(object);
    if (line == nil) return;

    [gSocketLock lock];
    DualCopySendLineLocked(line, clientFD);
    [gSocketLock unlock];
}

static void DualCopySendObject(NSDictionary *object) {
    if (!gEnabled) return;
    NSData *line = DualCopyJSONLine(object);
    if (line == nil) return;

    [gSocketLock lock];
    NSArray<NSNumber *> *peers = [gPeerFDs allObjects];
    if (peers.count == 0) {
        [gSocketLock unlock];
        os_log(DualCopyLog(), "Cannot send clipboard object: no connected Windows peer");
        return;
    }
    for (NSNumber *peer in peers) DualCopySendLineLocked(line, peer.intValue);
    [gSocketLock unlock];
}

static void DualCopySendHistory(void) {
    DualCopySendObject(@{ @"type": @"history", @"entries": [gHistory copy] });
}

static void DualCopyApplyRemoteEntry(NSDictionary *entry) {
    if (!gEnabled) return;
    NSString *kind = entry[@"kind"];
    if ([kind isEqualToString:@"image"] && gSyncImages) {
        NSData *imageData = [[NSData alloc] initWithBase64EncodedString:entry[@"data"] options:0];
        if (imageData.length == 0 || imageData.length > gMaxImageBytes) return;
        dispatch_async(dispatch_get_main_queue(), ^{
            UIImage *image = [UIImage imageWithData:imageData];
            if (image == nil) return;
            UIPasteboard *pasteboard = UIPasteboard.generalPasteboard;
            gLastImageData = imageData;
            gLastClipboard = [pasteboard.string copy];
            pasteboard.image = image;
            os_log(DualCopyLog(), "Applied an image received from Windows");
        });
        return;
    }
    NSString *text = entry[@"text"];
    if (text.length == 0) return;
    dispatch_async(dispatch_get_main_queue(), ^{
        UIPasteboard *pasteboard = UIPasteboard.generalPasteboard;
        if (![pasteboard.string isEqualToString:text]) {
            gLastClipboard = [text copy];
            gLastImageData = nil;
            pasteboard.string = text;
            os_log(DualCopyLog(), "Applied text received from Windows");
        }
    });
}

static void DualCopyHandleObject(NSDictionary *object) {
    NSString *type = object[@"type"];
    if ([type isEqualToString:@"hello"]) {
        NSArray *entries = object[@"history"];
        NSDictionary *before = DualCopyNewest();
        NSString *beforeID = before[@"id"];
        long long beforeTimestamp = [before[@"timestamp"] longLongValue];
        BOOL changed = [entries isKindOfClass:[NSArray class]] && DualCopyMergeEntries(entries);
        DualCopySendHistory();
        NSDictionary *after = DualCopyNewest();
        if (changed && after != nil && ![after[@"id"] isEqual:beforeID] &&
            [after[@"timestamp"] longLongValue] > beforeTimestamp) {
            DualCopyApplyRemoteEntry(after);
        }
        return;
    }
    if ([type isEqualToString:@"history"]) {
        NSArray *entries = object[@"entries"];
        if ([entries isKindOfClass:[NSArray class]]) DualCopyMergeEntries(entries);
        return;
    }
    if ([type isEqualToString:@"clipboard"]) {
        if (DualCopyMergeEntry(object)) {
            DualCopySaveHistory();
            DualCopyApplyRemoteEntry(object);
        }
    }
}

static BOOL DualCopyAddPeer(int clientFD) {
    [gSocketLock lock];
    if (gPeerFDs == nil) gPeerFDs = [NSMutableSet set];
    [gPeerFDs addObject:@(clientFD)];
    [gSocketLock unlock];
    return YES;
}

static void DualCopyReleasePeer(int clientFD) {
    [gSocketLock lock];
    BOOL wasPeer = [gPeerFDs containsObject:@(clientFD)];
    if (wasPeer) [gPeerFDs removeObject:@(clientFD)];
    [gSocketLock unlock];
    if (wasPeer) close(clientFD);
}

static void DualCopyReadClient(int clientFD) {
    DualCopyAddPeer(clientFD);
    DualCopySendObjectToPeer(@{ @"type": @"hello", @"device": @"ios", @"history": [gHistory copy] }, clientFD);
    NSMutableData *pending = [NSMutableData data];
    uint8_t buffer[8192];
    while (true) {
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
    DualCopyReleasePeer(clientFD);
    os_log(DualCopyLog(), "Clipboard peer disconnected");
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
    address.sin_port = htons(gListenPort);
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(gListenFD, (struct sockaddr *)&address, sizeof(address)) != 0 || listen(gListenFD, 8) != 0) {
        os_log_error(DualCopyLog(), "Could not listen for Windows clipboard peer on port %u", gListenPort);
        close(gListenFD);
        gListenFD = -1;
        return;
    }
    os_log(DualCopyLog(), "DualCopy listening for Windows on TCP port %u", gListenPort);
    while (true) {
        int client = accept(gListenFD, NULL, NULL);
        if (client < 0) {
            if (errno == EINTR) continue;
            break;
        }
        if (!gEnabled) {
            close(client);
            continue;
        }
        os_log(DualCopyLog(), "Accepted Windows clipboard peer");
        DualCopyPrepareSocket(client);
        dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{ DualCopyReadClient(client); });
    }
}

static void DualCopyConnectLoop(void) {
    while (true) {
        if (!gEnabled || gComputerAddress.length == 0) {
            sleep(2);
            continue;
        }
        [gSocketLock lock];
        BOOL connected = gPeerFDs.count > 0;
        [gSocketLock unlock];
        if (connected) {
            sleep(2);
            continue;
        }

        int client = socket(AF_INET, SOCK_STREAM, 0);
        if (client < 0) {
            sleep(2);
            continue;
        }
        DualCopyPrepareSocket(client);
        struct sockaddr_in address = {0};
        address.sin_len = sizeof(address);
        address.sin_family = AF_INET;
        address.sin_port = htons(gComputerPort);
        const char *host = gComputerAddress.UTF8String;
        if (host == NULL || inet_pton(AF_INET, host, &address.sin_addr) != 1 ||
            connect(client, (struct sockaddr *)&address, sizeof(address)) != 0) {
            os_log(DualCopyLog(), "Windows connection attempt failed for %{public}s:%u", host ?: "", gComputerPort);
            close(client);
            sleep(2);
            continue;
        }
        os_log(DualCopyLog(), "Connected to Windows at %{public}s:%u", host, gComputerPort);
        DualCopyReadClient(client);
        sleep(2);
    }
}

static void DualCopyRecordLocalText(NSString *text) {
    if (!gEnabled || text.length == 0 || [text isEqualToString:gLastClipboard]) return;
    gLastClipboard = [text copy];
    gLastImageData = nil;
    os_log(DualCopyLog(), "Detected a local text clipboard change");
    dispatch_async(gNetworkQueue, ^{
        NSDictionary *entry = @{
            @"type": @"clipboard", @"kind": @"text", @"id": [NSUUID UUID].UUIDString,
            @"text": text, @"timestamp": @((long long)(NSDate.date.timeIntervalSince1970 * 1000.0))
        };
        DualCopyMergeEntry(entry);
        DualCopySaveHistory();
        DualCopySendObject(entry);
    });
}

static NSData *DualCopyEncodedImage(UIImage *image) {
    if (image == nil) return nil;
    NSData *png = UIImagePNGRepresentation(image);
    if (png.length > 0 && png.length <= gMaxImageBytes) return png;

    NSData *jpeg = UIImageJPEGRepresentation(image, 0.82);
    if (jpeg.length > 0 && jpeg.length <= gMaxImageBytes) return jpeg;
    return nil;
}

static void DualCopyRecordLocalImage(NSData *imageData) {
    if (!gEnabled || !gSyncImages || imageData.length == 0 || imageData.length > gMaxImageBytes || [imageData isEqual:gLastImageData]) return;
    gLastImageData = [imageData copy];
    gLastClipboard = nil;
    os_log(DualCopyLog(), "Detected a local image clipboard change (%lu bytes)", (unsigned long)imageData.length);
    NSString *encoded = [imageData base64EncodedStringWithOptions:0];
    dispatch_async(gNetworkQueue, ^{
        NSDictionary *entry = @{
            @"type": @"clipboard", @"kind": @"image", @"id": [NSUUID UUID].UUIDString,
            @"data": encoded, @"timestamp": @((long long)(NSDate.date.timeIntervalSince1970 * 1000.0))
        };
        DualCopyMergeEntry(entry);
        DualCopySaveHistory();
        DualCopySendObject(entry);
        os_log(DualCopyLog(), "Published an image clipboard entry to Windows");
    });
}

static void DualCopyUpdateClipboardTimer(void) {
    if (gClipboardTimer == nil) return;
    dispatch_source_set_timer(gClipboardTimer,
                              dispatch_time(DISPATCH_TIME_NOW, (int64_t)gPollIntervalMilliseconds * NSEC_PER_MSEC),
                              (uint64_t)gPollIntervalMilliseconds * NSEC_PER_MSEC,
                              100 * NSEC_PER_MSEC);
}

static void DualCopyStartClipboardPolling(void) {
    if (gClipboardTimer != nil) {
        DualCopyUpdateClipboardTimer();
        return;
    }
    UIPasteboard *pasteboard = UIPasteboard.generalPasteboard;
    gLastPasteboardChangeCount = pasteboard.changeCount;
    gLastClipboard = [pasteboard.string copy];
    UIImage *currentImage = gSyncImages ? pasteboard.image : nil;
    gLastImageData = currentImage ? DualCopyEncodedImage(currentImage) : nil;
    gClipboardTimer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, dispatch_get_main_queue());
    dispatch_source_set_event_handler(gClipboardTimer, ^{
        if (!gEnabled) return;
        UIPasteboard *current = UIPasteboard.generalPasteboard;
        NSInteger changeCount = current.changeCount;
        gLastPasteboardChangeCount = changeCount;

        if (gSyncImages && current.image != nil) {
            NSData *data = DualCopyEncodedImage(current.image);
            if (data.length > 0 && ![data isEqual:gLastImageData]) {
                DualCopyRecordLocalImage(data);
            } else if (data.length == 0 && ![data isEqual:gLastImageData]) {
                os_log(DualCopyLog(), "Skipped an image clipboard change because it exceeds the configured image limit");
            }
            return;
        }

        NSString *text = current.string;
        if (text.length > 0 && ![text isEqualToString:gLastClipboard]) {
            DualCopyRecordLocalText(text);
        }
    });
    dispatch_resume(gClipboardTimer);
    DualCopyUpdateClipboardTimer();
}

static void DualCopySettingsChanged(int token) {
    (void)token;
    DualCopyReadSettings();
    dispatch_async(dispatch_get_main_queue(), ^{ DualCopyUpdateClipboardTimer(); });
    os_log(DualCopyLog(), "Settings changed; port changes take effect after respring");
}

static void DualCopyStart(void) {
    dispatch_once(&gStartOnce, ^{
        gNetworkQueue = dispatch_queue_create("com.eliastekdin.dualcopy.network", DISPATCH_QUEUE_SERIAL);
        gSocketLock = [NSLock new];
        gPeerFDs = [NSMutableSet set];
        dispatch_async(gNetworkQueue, ^{
            DualCopyReadSettings();
            notify_register_dispatch("com.eliastekdin.dualcopy/changed", &gSettingsNotificationToken, gNetworkQueue, ^(int token) {
                DualCopySettingsChanged(token);
            });
            DualCopyLoadHistory();
            dispatch_async(dispatch_get_main_queue(), ^{ DualCopyStartClipboardPolling(); });
            dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{ DualCopyAcceptLoop(); });
            dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{ DualCopyConnectLoop(); });
        });
    });
}

__attribute__((constructor)) static void DualCopyInitialize(void) {
    os_log(DualCopyLog(), "ApelinLoaded %{public}s in %{public}s", APELIN_BUILD_ID, getprogname());
    dispatch_async(dispatch_get_main_queue(), ^{
        DualCopyStart();
    });
}
