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
                                                                  get:@selector(readPreferenceValue:specifier:)
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
    if (cell == PSEditTextCell && (key == nil || ![key isEqualToString:@"computerAddress"])) {
        [specifier setProperty:@"NumbersAndPunctuation" forKey:@"keyboard"];
    }
    return specifier;
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
        PSSpecifier *group = [PSSpecifier groupSpecifierWithName:@"DualCopy"];
        [group setProperty:@"Connect the iPhone and Windows computer over a trusted local network."
                    forKey:@"footerText"];
        [specifiers addObject:group];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Enabled"
                                                       key:@"enabled"
                                                  default:@YES
                                                     cell:PSSwitchCell]];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Listen port"
                                                       key:@"listenPort"
                                                  default:@"49732"
                                                     cell:PSEditTextCell]];
        PSSpecifier *address = [self dualCopyValueSpecifier:@"Windows computer IP address"
                                                         key:@"computerAddress"
                                                    default:@""
                                                       cell:PSEditTextCell];
        [address setProperty:@"Optional, for automatic outbound connection" forKey:@"placeholder"];
        [specifiers addObject:address];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Windows computer port"
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

        group = [PSSpecifier groupSpecifierWithName:@"Images"];
        [group setProperty:@"Images are sent as PNG over the same local connection."
                    forKey:@"footerText"];
        [specifiers addObject:group];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Sync images"
                                                       key:@"syncImages"
                                                  default:@YES
                                                     cell:PSSwitchCell]];
        [specifiers addObject:[self dualCopyValueSpecifier:@"Maximum image size (bytes)"
                                                       key:@"maxImageBytes"
                                                  default:@"4194304"
                                                     cell:PSEditTextCell]];
        _specifiers = specifiers;
    }
    return _specifiers;
}

@end
