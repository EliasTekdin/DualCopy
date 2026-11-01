#import <Preferences/PSListController.h>
#import <CoreFoundation/CoreFoundation.h>
#import <notify.h>
#import <os/log.h>

@interface DualCopyPrefsRootListController : PSListController
@end

@implementation DualCopyPrefsRootListController

static NSString * const kDualCopyDomain = @"com.eliastekdin.dualcopy";
static NSString * const kDualCopyChangedNotification = @"com.eliastekdin.dualcopy/changed";

- (PSSpecifier *)dualCopyValueSpecifier:(NSString *)label
                                    key:(NSString *)key
                               default:(id)value
                                  cell:(PSCellType)cell {
    PSSpecifier *specifier = [PSSpecifier preferenceSpecifierNamed:label
                                                               target:self
                                                                  set:@selector(setPreferenceValue:specifier:)
                                                                  get:@selector(readPreferenceValue:)
                                                               detail:nil
                                                                 cell:cell
                                                                 edit:nil];
    [specifier setProperty:key forKey:@"key"];
    [specifier setProperty:kDualCopyDomain forKey:@"defaults"];
    [specifier setProperty:kDualCopyChangedNotification forKey:@"PostNotification"];
    if (value != nil) {
        id existing = CFBridgingRelease(CFPreferencesCopyAppValue((__bridge CFStringRef)key,
                                                                    CFSTR("com.eliastekdin.dualcopy")));
        [specifier setProperty:(existing != nil ? existing : value) forKey:@"default"];
    }
    if (cell == PSEditTextCell && key != nil &&
        ![key isEqualToString:@"computerAddress"] &&
        ![key isEqualToString:@"imageFormat"]) {
        [specifier setProperty:@"NumbersAndPunctuation" forKey:@"keyboard"];
    }
    return specifier;
}

- (PSSpecifier *)dualCopyButtonSpecifier:(NSString *)label action:(SEL)action {
    PSSpecifier *specifier = [PSSpecifier preferenceSpecifierNamed:label
                                                               target:self
                                                                  set:action
                                                                  get:nil
                                                               detail:nil
                                                                 cell:PSButtonCell
                                                                 edit:nil];
    [specifier setProperty:NSStringFromSelector(action) forKey:@"action"];
    return specifier;
}

- (void)clearHistory:(PSSpecifier *)specifier {
    (void)specifier;
    NSString *path = @"/var/mobile/Library/Preferences/com.eliastekdin.dualcopy-history.json";
    [[NSFileManager defaultManager] removeItemAtPath:path error:nil];
    notify_post(kDualCopyChangedNotification.UTF8String);
    UIAlertController *alert = [UIAlertController alertControllerWithTitle:@"History cleared"
                                                                     message:@"DualCopy clipboard history has been deleted."
                                                              preferredStyle:UIAlertControllerStyleAlert];
    [alert addAction:[UIAlertAction actionWithTitle:@"OK" style:UIAlertActionStyleDefault handler:nil]];
    [self presentViewController:alert animated:YES completion:nil];
}

- (id)readPreferenceValue:(PSSpecifier *)specifier {
    NSString *key = [specifier propertyForKey:@"key"];
    if (![key isKindOfClass:[NSString class]] || key.length == 0) return nil;

    CFPropertyListRef stored = CFPreferencesCopyAppValue((__bridge CFStringRef)key,
                                                          CFSTR("com.eliastekdin.dualcopy"));
    if (stored != NULL) return CFBridgingRelease(stored);
    return [specifier propertyForKey:@"default"];
}

- (void)setPreferenceValue:(id)value specifier:(PSSpecifier *)specifier {
    NSString *key = [specifier propertyForKey:@"key"];
    if (![key isKindOfClass:[NSString class]] || key.length == 0) return;

    CFPreferencesSetAppValue((__bridge CFStringRef)key,
                             value == nil ? NULL : (__bridge CFPropertyListRef)value,
                             CFSTR("com.eliastekdin.dualcopy"));
    CFPreferencesAppSynchronize(CFSTR("com.eliastekdin.dualcopy"));
    notify_post(kDualCopyChangedNotification.UTF8String);

    os_log_t log = os_log_create("com.eliastekdin.dualcopy", "preferences");
    os_log(log, "Stored preference %{public}@", key);
}

- (NSArray *)specifiers {
    if (_specifiers == nil) {
        os_log_t log = os_log_create("com.eliastekdin.dualcopy", "preferences");
        os_log(log, "DualCopy preference controller building specifiers");

        NSMutableArray *specifiers = [NSMutableArray array];
        PSSpecifier *group = [PSSpecifier groupSpecifierWithName:@"Connection"];
        [group setProperty:@"This iPhone listens on the local port below and connects to Windows using the Windows address and port. The same port, 49732, may be used on both devices."
                    forKey:@"footerText"];
        [specifiers addObject:group];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Enabled"
                                                       key:@"enabled"
                                                  default:@YES
                                                     cell:PSSwitchCell]];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Listen port (on this iPhone)"
                                                       key:@"listenPort"
                                                  default:@"49732"
                                                     cell:PSEditTextCell]];
        PSSpecifier *address = [self dualCopyValueSpecifier:@"Windows computer IP address"
                                                         key:@"computerAddress"
                                                    default:@""
                                                       cell:PSEditTextCell];
        [address setProperty:@"Required: the Windows computer address for automatic connection" forKey:@"placeholder"];
        [specifiers addObject:address];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Windows listening port"
                                                       key:@"computerPort"
                                                  default:@"49732"
                                                     cell:PSEditTextCell]];

        group = [PSSpecifier groupSpecifierWithName:@"Performance"];
        [group setProperty:@"Shorter polling detects changes faster but uses more battery."
                    forKey:@"footerText"];
        [specifiers addObject:group];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Polling interval (ms)"
                                                       key:@"pollInterval"
                                                  default:@"500"
                                                     cell:PSEditTextCell]];
        [specifiers addObject:[self dualCopyValueSpecifier:@"History limit"
                                                       key:@"historyLimit"
                                                  default:@"50"
                                                     cell:PSEditTextCell]];

        group = [PSSpecifier groupSpecifierWithName:@"History"];
        [group setProperty:@"DualCopy stores recent entries for synchronisation. The Windows companion provides the full history viewer." forKey:@"footerText"];
        [specifiers addObject:group];
        [specifiers addObject:[self dualCopyButtonSpecifier:@"Clear stored clipboard history" action:@selector(clearHistory:)]];

        group = [PSSpecifier groupSpecifierWithName:@"Files"];
        [group setProperty:@"Transfers data-backed files such as DNG, PDF, TXT, ZIP, and DEB when the source app exposes them through the pasteboard." forKey:@"footerText"];
        [specifiers addObject:group];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Sync files"
                                                       key:@"syncFiles"
                                                  default:@YES
                                                     cell:PSSwitchCell]];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Maximum file size (bytes)"
                                                       key:@"maxFileBytes"
                                                  default:@"52428800"
                                                     cell:PSEditTextCell]];

        group = [PSSpecifier groupSpecifierWithName:@"Images"];
        [group setProperty:@"Images are sent as PNG, JPEG, or the smallest compatible format. Raw camera data such as DNG is converted when iOS can decode it; otherwise it remains a file." forKey:@"footerText"];
        [specifiers addObject:group];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Sync images"
                                                       key:@"syncImages"
                                                  default:@YES
                                                     cell:PSSwitchCell]];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Convert raw images when possible"
                                                       key:@"convertRawImages"
                                                  default:@YES
                                                     cell:PSSwitchCell]];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Image format (auto, png, jpeg)"
                                                       key:@"imageFormat"
                                                  default:@"auto"
                                                     cell:PSEditTextCell]];
        [specifiers addObject:[self dualCopyValueSpecifier:@"JPEG quality (0.40-1.00)"
                                                       key:@"jpegQuality"
                                                  default:@"0.82"
                                                     cell:PSEditTextCell]];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Maximum image size (bytes)"
                                                       key:@"maxImageBytes"
                                                  default:@"4194304"
                                                     cell:PSEditTextCell]];
        _specifiers = specifiers;
    }
    return _specifiers;
}

@end
