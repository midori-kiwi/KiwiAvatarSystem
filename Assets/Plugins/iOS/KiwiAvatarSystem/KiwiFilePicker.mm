#import <UIKit/UIKit.h>
#include <stdlib.h>

extern "C" void UnitySendMessage(const char* obj, const char* method, const char* msg);
extern "C" UIViewController* UnityGetGLViewController(void);

static NSString* const KiwiImportFolderName = @"KiwiImports"; // legacy cleanup only
static const NSUInteger KiwiCopyBufferSize = 256 * 1024;
static const NSUInteger KiwiMaxFileNameLength = 120;

static UIViewController* KiwiTopViewController(UIViewController* controller)
{
    UIViewController* current = controller;
    while (current.presentedViewController != nil) {
        current = current.presentedViewController;
    }
    if ([current isKindOfClass:[UINavigationController class]]) {
        UIViewController* visible = ((UINavigationController*)current).visibleViewController;
        if (visible != nil) current = visible;
    }
    return current;
}

static NSString* KiwiImportDirectory(void)
{
    return [NSTemporaryDirectory() stringByAppendingPathComponent:KiwiImportFolderName];
}

@interface KiwiVrmDocumentPickerDelegate : NSObject<UIDocumentPickerDelegate>
@property(nonatomic, copy) NSString* gameObjectName;
@property(nonatomic, copy) NSString* successMethod;
@property(nonatomic, copy) NSString* errorMethod;
@property(nonatomic, copy) NSString* destinationDirectory;
@property(nonatomic, assign) long long maximumBytes;
@end

@implementation KiwiVrmDocumentPickerDelegate

- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls
{
    NSURL* url = urls.firstObject;
    if (url == nil) {
        [self sendError:@"No document was selected."];
        return;
    }

    NSString* fileName = url.lastPathComponent ?: @"avatar.vrm";
    if (![[fileName.pathExtension lowercaseString] isEqualToString:@"vrm"]) {
        [self sendError:@"Selected file is not a .vrm file."];
        return;
    }

    NSNumber* resourceSize = nil;
    [url getResourceValue:&resourceSize forKey:NSURLFileSizeKey error:nil];
    if (self.maximumBytes > 0 && resourceSize != nil && resourceSize.longLongValue > self.maximumBytes) {
        [self sendError:@"Selected VRM exceeds the configured runtime model size limit."];
        return;
    }

    NSString* managedDirectory = [self validatedManagedDestination:self.destinationDirectory];
    if (managedDirectory == nil) {
        [self sendError:@"Managed Models directory is outside the app sandbox or unavailable."];
        return;
    }

    BOOL scoped = [url startAccessingSecurityScopedResource];
    NSString* safeName = [self sanitizedFileName:fileName];
    long long maximumBytes = self.maximumBytes;

    __weak KiwiVrmDocumentPickerDelegate* weakSelf = self;
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        @autoreleasepool {
            KiwiVrmDocumentPickerDelegate* strongSelf = weakSelf;
            if (strongSelf == nil) {
                if (scoped) [url stopAccessingSecurityScopedResource];
                return;
            }

            NSString* destination = nil;
            NSString* errorMessage = nil;

            @try {
                NSError* dirError = nil;
                [[NSFileManager defaultManager] createDirectoryAtPath:managedDirectory
                                         withIntermediateDirectories:YES
                                                          attributes:nil
                                                               error:&dirError];
                if (dirError != nil) {
                    errorMessage = [NSString stringWithFormat:@"Unable to create Models folder: %@", dirError.localizedDescription];
                } else {
                    destination = [strongSelf uniqueDestinationInDirectory:managedDirectory fileName:safeName];
                    NSError* copyError = nil;
                    if (![strongSelf copyURL:url toPath:destination maximumBytes:maximumBytes error:&copyError]) {
                        errorMessage = [NSString stringWithFormat:@"VRM import failed: %@", copyError.localizedDescription ?: @"Unknown error"];
                    }
                }
            }
            @catch (NSException* exception) {
                errorMessage = [NSString stringWithFormat:@"VRM import failed: %@", exception.reason ?: @"Unknown exception"];
                if (destination.length > 0) {
                    [[NSFileManager defaultManager] removeItemAtPath:destination error:nil];
                }
            }
            @finally {
                if (scoped) [url stopAccessingSecurityScopedResource];
            }

            dispatch_async(dispatch_get_main_queue(), ^{
                KiwiVrmDocumentPickerDelegate* callbackSelf = weakSelf;
                if (callbackSelf == nil) return;

                if (errorMessage.length > 0) {
                    [callbackSelf sendError:errorMessage];
                } else {
                    [callbackSelf sendSuccess:destination];
                }
            });
        }
    });
}

- (void)documentPickerWasCancelled:(UIDocumentPickerViewController *)controller
{
    [self sendError:@"CANCELLED"];
}

- (NSString*)validatedManagedDestination:(NSString*)requested
{
    if (requested.length == 0) return nil;

    NSString* normalized = [requested stringByStandardizingPath];
    NSString* home = [NSHomeDirectory() stringByStandardizingPath];
    NSString* prefix = [home stringByAppendingString:@"/"];

    if ([normalized isEqualToString:home] || [normalized hasPrefix:prefix]) {
        return normalized;
    }

    return nil;
}

- (BOOL)copyURL:(NSURL*)source toPath:(NSString*)destination maximumBytes:(long long)maximumBytes error:(NSError**)error
{
    NSInputStream* input = [NSInputStream inputStreamWithURL:source];
    NSOutputStream* output = [NSOutputStream outputStreamToFileAtPath:destination append:NO];
    if (input == nil || output == nil) {
        if (error != NULL) {
            *error = [NSError errorWithDomain:@"KiwiAvatarSystem" code:1 userInfo:@{NSLocalizedDescriptionKey:@"Unable to open selected VRM."}];
        }
        return NO;
    }

    [input open];
    [output open];
    uint8_t* buffer = (uint8_t*)malloc(KiwiCopyBufferSize);
    if (buffer == NULL) {
        [input close];
        [output close];
        if (error != NULL) {
            *error = [NSError errorWithDomain:@"KiwiAvatarSystem" code:4 userInfo:@{NSLocalizedDescriptionKey:@"Unable to allocate import buffer."}];
        }
        return NO;
    }

    long long total = 0;
    BOOL success = YES;

    @try {
        while (YES) {
            NSInteger read = [input read:buffer maxLength:KiwiCopyBufferSize];
            if (read < 0) {
                success = NO;
                if (error != NULL) *error = input.streamError;
                break;
            }
            if (read == 0) break;

            total += read;
            if (maximumBytes > 0 && total > maximumBytes) {
                success = NO;
                if (error != NULL) {
                    *error = [NSError errorWithDomain:@"KiwiAvatarSystem" code:2 userInfo:@{NSLocalizedDescriptionKey:@"Selected VRM exceeds the configured runtime model size limit."}];
                }
                break;
            }

            NSInteger offset = 0;
            while (offset < read) {
                NSInteger written = [output write:buffer + offset maxLength:(NSUInteger)(read - offset)];
                if (written <= 0) {
                    success = NO;
                    if (error != NULL) *error = output.streamError;
                    break;
                }
                offset += written;
            }
            if (!success) break;
        }

        if (total <= 0 && success) {
            success = NO;
            if (error != NULL) {
                *error = [NSError errorWithDomain:@"KiwiAvatarSystem" code:3 userInfo:@{NSLocalizedDescriptionKey:@"Selected VRM is empty."}];
            }
        }
    }
    @finally {
        free(buffer);
        [input close];
        [output close];
    }

    if (!success) {
        [[NSFileManager defaultManager] removeItemAtPath:destination error:nil];
    }
    return success;
}

- (NSString*)sanitizedFileName:(NSString*)name
{
    NSCharacterSet* invalid = [NSCharacterSet characterSetWithCharactersInString:@"\\/:*?\"<>|"];
    NSArray<NSString*>* parts = [name componentsSeparatedByCharactersInSet:invalid];
    NSString* result = [[parts componentsJoinedByString:@"_"] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceAndNewlineCharacterSet]];
    if (result.length == 0) result = @"avatar.vrm";

    if (result.length > KiwiMaxFileNameLength) {
        NSString* extension = result.pathExtension;
        NSString* stem = result.stringByDeletingPathExtension;
        NSUInteger extensionLength = extension.length > 0 ? extension.length + 1 : 0;
        NSUInteger maxStem = KiwiMaxFileNameLength > extensionLength ? KiwiMaxFileNameLength - extensionLength : 1;
        if (stem.length > maxStem) stem = [stem substringToIndex:maxStem];
        result = extension.length > 0 ? [stem stringByAppendingPathExtension:extension] : stem;
    }
    return result;
}

- (NSString*)uniqueDestinationInDirectory:(NSString*)directory fileName:(NSString*)fileName
{
    NSFileManager* fm = [NSFileManager defaultManager];
    NSString* candidate = [directory stringByAppendingPathComponent:fileName];
    if (![fm fileExistsAtPath:candidate]) return candidate;

    NSString* extension = fileName.pathExtension;
    NSString* stem = fileName.stringByDeletingPathExtension;
    for (NSInteger i = 2; i < 10000; i++) {
        NSString* numbered = [NSString stringWithFormat:@"%@_%ld", stem, (long)i];
        if (extension.length > 0) numbered = [numbered stringByAppendingPathExtension:extension];
        candidate = [directory stringByAppendingPathComponent:numbered];
        if (![fm fileExistsAtPath:candidate]) return candidate;
    }
    return [directory stringByAppendingPathComponent:[NSString stringWithFormat:@"%@_%lld.vrm", stem, (long long)(NSDate.date.timeIntervalSince1970 * 1000.0)]];
}

- (void)sendSuccess:(NSString*)path
{
    UnitySendMessage(self.gameObjectName.UTF8String, self.successMethod.UTF8String, path.UTF8String);
}

- (void)sendError:(NSString*)message
{
    UnitySendMessage(self.gameObjectName.UTF8String, self.errorMethod.UTF8String, message.UTF8String);
}

@end

static KiwiVrmDocumentPickerDelegate* gKiwiPickerDelegate = nil;

extern "C" void Kiwi_OpenVrmPicker(
    const char* gameObjectName,
    const char* successMethod,
    const char* errorMethod,
    const char* destinationDirectory,
    long long maximumBytes)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        gKiwiPickerDelegate = [KiwiVrmDocumentPickerDelegate new];
        gKiwiPickerDelegate.gameObjectName = [NSString stringWithUTF8String:gameObjectName ?: "KiwiMobileFilePicker"];
        gKiwiPickerDelegate.successMethod = [NSString stringWithUTF8String:successMethod ?: "OnNativeFilePicked"];
        gKiwiPickerDelegate.errorMethod = [NSString stringWithUTF8String:errorMethod ?: "OnNativeFilePickerError"];
        gKiwiPickerDelegate.destinationDirectory = [NSString stringWithUTF8String:destinationDirectory ?: ""];
        gKiwiPickerDelegate.maximumBytes = MAX(0LL, maximumBytes);

        UIDocumentPickerViewController* picker = [[UIDocumentPickerViewController alloc]
            initWithDocumentTypes:@[@"public.data"]
            inMode:UIDocumentPickerModeOpen];
        picker.delegate = gKiwiPickerDelegate;
        picker.modalPresentationStyle = UIModalPresentationFormSheet;

        UIViewController* controller = KiwiTopViewController(UnityGetGLViewController());
        if (controller == nil) {
            UnitySendMessage(
                gKiwiPickerDelegate.gameObjectName.UTF8String,
                gKiwiPickerDelegate.errorMethod.UTF8String,
                "Unity view controller is unavailable."
            );
            return;
        }

        [controller presentViewController:picker animated:YES completion:nil];
    });
}

extern "C" void Kiwi_DeleteImportedTemp(const char* path)
{
    if (path == NULL) return;
    NSString* target = [NSString stringWithUTF8String:path];
    if (target.length == 0) return;

    NSString* root = [KiwiImportDirectory() stringByStandardizingPath];
    NSString* normalized = [target stringByStandardizingPath];
    NSString* prefix = [root stringByAppendingString:@"/"];
    if ([normalized hasPrefix:prefix]) {
        [[NSFileManager defaultManager] removeItemAtPath:normalized error:nil];
    }
}
