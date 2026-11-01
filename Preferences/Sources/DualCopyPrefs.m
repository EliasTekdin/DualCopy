#import <Preferences/PSListController.h>

@interface DualCopyPrefsRootListController : PSListController
@end

@implementation DualCopyPrefsRootListController

- (NSArray *)specifiers {
    if (_specifiers == nil) {
        _specifiers = [self loadSpecifiersFromPlistName:@"Root" target:self];
    }
    return _specifiers;
}

@end
