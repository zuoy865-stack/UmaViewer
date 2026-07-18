#import <AppKit/AppKit.h>
#import <Foundation/Foundation.h>

#include <stdlib.h>
#include <string.h>

typedef void (*AsyncCallback)(const char *path);

static char *s_result = nullptr;

static NSString *StringFromUtf8(const char *value)
{
    if (value == nullptr || value[0] == '\0')
    {
        return @"";
    }

    NSString *result = [NSString stringWithUTF8String:value];
    return result ?: @"";
}

static const char *StoreResult(NSString *value)
{
    free(s_result);

    const char *utf8 = (value ?: @"").UTF8String;
    s_result = strdup(utf8 ?: "");
    return s_result;
}

static NSArray<NSString *> *AllowedExtensions(const char *filter)
{
    NSString *filterString = StringFromUtf8(filter);
    if (filterString.length == 0)
    {
        return @[];
    }

    NSMutableOrderedSet<NSString *> *extensions = [NSMutableOrderedSet orderedSet];
    NSCharacterSet *trimSet = [NSCharacterSet whitespaceAndNewlineCharacterSet];

    for (NSString *group in [filterString componentsSeparatedByString:@"|"])
    {
        NSRange separator = [group rangeOfString:@";"];
        NSString *extensionList = separator.location == NSNotFound
            ? group
            : [group substringFromIndex:separator.location + 1];

        for (NSString *rawExtension in [extensionList componentsSeparatedByString:@","])
        {
            NSString *extension = [[rawExtension stringByTrimmingCharactersInSet:trimSet] lowercaseString];
            while ([extension hasPrefix:@"."] || [extension hasPrefix:@"*"])
            {
                extension = [extension substringFromIndex:1];
            }

            if (extension.length > 0)
            {
                [extensions addObject:extension];
            }
        }
    }

    return extensions.array;
}

static void ConfigurePanel(NSSavePanel *panel, const char *title, const char *directory)
{
    NSString *panelTitle = StringFromUtf8(title);
    if (panelTitle.length > 0)
    {
        panel.title = panelTitle;
    }

    NSString *initialDirectory = StringFromUtf8(directory);
    if (initialDirectory.length > 0)
    {
        panel.directoryURL = [NSURL fileURLWithPath:initialDirectory isDirectory:YES];
    }

    panel.canCreateDirectories = YES;
    [NSApp activateIgnoringOtherApps:YES];
}

static NSString *JoinUrls(NSArray<NSURL *> *urls)
{
    NSMutableArray<NSString *> *paths = [NSMutableArray arrayWithCapacity:urls.count];
    for (NSURL *url in urls)
    {
        if (url.path != nil)
        {
            [paths addObject:url.path];
        }
    }

    return [paths componentsJoinedByString:[NSString stringWithFormat:@"%c", 28]];
}

static NSString *RunOpenFilePanel(
    const char *title,
    const char *directory,
    const char *filter,
    bool multiselect)
{
    NSOpenPanel *panel = [NSOpenPanel openPanel];
    ConfigurePanel(panel, title, directory);
    panel.canChooseFiles = YES;
    panel.canChooseDirectories = NO;
    panel.allowsMultipleSelection = multiselect;

    NSArray<NSString *> *extensions = AllowedExtensions(filter);
    if (extensions.count > 0)
    {
        panel.allowedFileTypes = extensions;
        panel.allowsOtherFileTypes = NO;
    }

    return [panel runModal] == NSModalResponseOK ? JoinUrls(panel.URLs) : @"";
}

static NSString *RunOpenFolderPanel(
    const char *title,
    const char *directory,
    bool multiselect)
{
    NSOpenPanel *panel = [NSOpenPanel openPanel];
    ConfigurePanel(panel, title, directory);
    panel.canChooseFiles = NO;
    panel.canChooseDirectories = YES;
    panel.allowsMultipleSelection = multiselect;

    return [panel runModal] == NSModalResponseOK ? JoinUrls(panel.URLs) : @"";
}

static NSString *RunSaveFilePanel(
    const char *title,
    const char *directory,
    const char *defaultName,
    const char *filter)
{
    NSSavePanel *panel = [NSSavePanel savePanel];
    ConfigurePanel(panel, title, directory);

    NSString *name = StringFromUtf8(defaultName);
    if (name.length > 0)
    {
        panel.nameFieldStringValue = name;
    }

    NSArray<NSString *> *extensions = AllowedExtensions(filter);
    if (extensions.count > 0)
    {
        panel.allowedFileTypes = extensions;
        panel.allowsOtherFileTypes = NO;
        panel.extensionHidden = NO;
    }

    return [panel runModal] == NSModalResponseOK ? panel.URL.path : @"";
}

template <typename Function>
static NSString *RunOnMainThread(Function function)
{
    if ([NSThread isMainThread])
    {
        return function();
    }

    __block NSString *result = @"";
    dispatch_sync(dispatch_get_main_queue(), ^{
        result = function();
    });
    return result;
}

#define SFB_EXPORT extern "C" __attribute__((visibility("default")))

SFB_EXPORT const char *DialogOpenFilePanel(
    const char *title,
    const char *directory,
    const char *filter,
    bool multiselect)
{
    @autoreleasepool
    {
        NSString *result = RunOnMainThread(^NSString *{
            return RunOpenFilePanel(title, directory, filter, multiselect);
        });
        return StoreResult(result);
    }
}

SFB_EXPORT void DialogOpenFilePanelAsync(
    const char *title,
    const char *directory,
    const char *filter,
    bool multiselect,
    AsyncCallback callback)
{
    NSString *titleCopy = [StringFromUtf8(title) copy];
    NSString *directoryCopy = [StringFromUtf8(directory) copy];
    NSString *filterCopy = [StringFromUtf8(filter) copy];

    dispatch_async(dispatch_get_main_queue(), ^{
        @autoreleasepool
        {
            NSString *result = RunOpenFilePanel(
                titleCopy.UTF8String,
                directoryCopy.UTF8String,
                filterCopy.UTF8String,
                multiselect);
            if (callback != nullptr)
            {
                callback(StoreResult(result));
            }
        }
    });
}

SFB_EXPORT const char *DialogOpenFolderPanel(
    const char *title,
    const char *directory,
    bool multiselect)
{
    @autoreleasepool
    {
        NSString *result = RunOnMainThread(^NSString *{
            return RunOpenFolderPanel(title, directory, multiselect);
        });
        return StoreResult(result);
    }
}

SFB_EXPORT void DialogOpenFolderPanelAsync(
    const char *title,
    const char *directory,
    bool multiselect,
    AsyncCallback callback)
{
    NSString *titleCopy = [StringFromUtf8(title) copy];
    NSString *directoryCopy = [StringFromUtf8(directory) copy];

    dispatch_async(dispatch_get_main_queue(), ^{
        @autoreleasepool
        {
            NSString *result = RunOpenFolderPanel(
                titleCopy.UTF8String,
                directoryCopy.UTF8String,
                multiselect);
            if (callback != nullptr)
            {
                callback(StoreResult(result));
            }
        }
    });
}

SFB_EXPORT const char *DialogSaveFilePanel(
    const char *title,
    const char *directory,
    const char *defaultName,
    const char *filter)
{
    @autoreleasepool
    {
        NSString *result = RunOnMainThread(^NSString *{
            return RunSaveFilePanel(title, directory, defaultName, filter);
        });
        return StoreResult(result);
    }
}

SFB_EXPORT void DialogSaveFilePanelAsync(
    const char *title,
    const char *directory,
    const char *defaultName,
    const char *filter,
    AsyncCallback callback)
{
    NSString *titleCopy = [StringFromUtf8(title) copy];
    NSString *directoryCopy = [StringFromUtf8(directory) copy];
    NSString *defaultNameCopy = [StringFromUtf8(defaultName) copy];
    NSString *filterCopy = [StringFromUtf8(filter) copy];

    dispatch_async(dispatch_get_main_queue(), ^{
        @autoreleasepool
        {
            NSString *result = RunSaveFilePanel(
                titleCopy.UTF8String,
                directoryCopy.UTF8String,
                defaultNameCopy.UTF8String,
                filterCopy.UTF8String);
            if (callback != nullptr)
            {
                callback(StoreResult(result));
            }
        }
    });
}
