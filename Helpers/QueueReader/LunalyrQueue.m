// Reads Apple Music's Up Next through MediaRemote and prints it as JSON
// lines on stdout, for Lunalyr's queue look-ahead (MusicQueueReader.swift).
//
// macOS lets only entitled processes read MediaRemote's now-playing state,
// but code loaded into /usr/bin/perl may (the mediaremote-adapter
// technique). Lunalyr runs
//
//   /usr/bin/perl -e '<loader>' <this dylib> <entry>
//
// where the loader installs `lunalyr_queue_watch` or `lunalyr_queue_once`
// as a Perl XSUB and calls it. Nothing here plays, pauses, skips or changes
// anything: it only asks for the queue.
//
// Output, one JSON object per line, flushed after each:
//   {"type":"ready","v":1}
//   {"type":"queue","v":1,"items":[{"title":…,"artist":…,"album":…,
//     "duration":…,"id":…,"genre":…,"current":true}, …]}
//   {"type":"error","code":"…"}
// A queue line is a whole snapshot: the current song and what follows it.
//
// Watch mode keeps running: it asks again when MediaRemote says the queue
// or the playing item changed, when a line arrives on stdin (Lunalyr
// writes "refresh" on each song change), and every `pollSeconds`; it
// prints a snapshot (or an error) only when it differs from the last. It exits when
// stdin closes, so it never outlives Lunalyr, even after a crash.

#import <Foundation/Foundation.h>
#include <dlfcn.h>
#include <objc/message.h>
#include <stdio.h>
#include <unistd.h>

typedef void *(*LQRequestWithRange)(long, long);
typedef void (*LQRequestForPlayer)(void *, id, dispatch_queue_t, void (^)(void *, CFErrorRef));
typedef CFTypeRef (*LQOrigin)(void);
typedef CFArrayRef (*LQCopyItems)(void *);
typedef CFStringRef (*LQGetString)(void *);
typedef double (*LQGetDouble)(void *);
typedef Boolean (*LQGetBool)(void *);
typedef CFDictionaryRef (*LQCopyInfo)(void *);
typedef void (*LQRegister)(dispatch_queue_t);

static const long LQRange = 12;
static const double LQPollSeconds = 20;
static const double LQAnswerSeconds = 6;

static struct {
    void *handle;
    LQRequestWithRange requestWithRange;
    LQRequestForPlayer requestForPlayer;
    LQOrigin localOrigin;
    LQCopyItems copyItems;
    LQGetString title, artist, album, identifier;
    LQGetDouble duration;
    LQGetBool isCurrent;
    LQCopyInfo copyInfo;
    LQRegister registerNotifications;
} LQ;

static NSString *LQLast;
static BOOL LQAsking;
static BOOL LQAskAgain;

static void LQEmit(NSDictionary *object) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:object options:0 error:nil];
    if (!data) return;
    fwrite(data.bytes, 1, data.length, stdout);
    fputc('\n', stdout);
    fflush(stdout);
}

static void LQError(NSString *code) { LQEmit(@{@"type": @"error", @"code": code}); }

static BOOL LQLoad(void) {
    if (LQ.handle) return YES;
    void *h = dlopen("/System/Library/PrivateFrameworks/MediaRemote.framework/MediaRemote", RTLD_NOW);
    if (!h) return NO;
    LQ.handle = h;
    LQ.requestWithRange = (LQRequestWithRange)dlsym(h, "MRPlaybackQueueRequestCreateDefaultWithRange");
    LQ.requestForPlayer = (LQRequestForPlayer)dlsym(h, "MRMediaRemoteRequestNowPlayingPlaybackQueueForPlayer");
    LQ.localOrigin = (LQOrigin)dlsym(h, "MRMediaRemoteGetLocalOrigin");
    LQ.copyItems = (LQCopyItems)dlsym(h, "MRPlaybackQueueCopyContentItems");
    LQ.title = (LQGetString)dlsym(h, "MRContentItemGetTitle");
    LQ.artist = (LQGetString)dlsym(h, "MRContentItemGetTrackArtistName");
    LQ.album = (LQGetString)dlsym(h, "MRContentItemGetAlbumName");
    LQ.identifier = (LQGetString)dlsym(h, "MRContentItemGetIdentifier");
    LQ.duration = (LQGetDouble)dlsym(h, "MRContentItemGetDuration");
    LQ.isCurrent = (LQGetBool)dlsym(h, "MRContentItemGetIsCurrentlyPlaying");
    LQ.copyInfo = (LQCopyInfo)dlsym(h, "MRContentItemCopyNowPlayingInfo");
    LQ.registerNotifications = (LQRegister)dlsym(h, "MRMediaRemoteRegisterForNowPlayingNotifications");
    // The request itself cannot be made without these.
    return LQ.requestWithRange && LQ.requestForPlayer && LQ.localOrigin && LQ.copyItems
        && NSClassFromString(@"MRClient") && NSClassFromString(@"MRPlayerPath");
}

/// Music's player on this Mac.
static id LQMusicPath(void) {
    id origin = (__bridge id)LQ.localOrigin();
    if (!origin) return nil;
    SEL initClient = NSSelectorFromString(@"initWithBundleIdentifier:");
    Class clientClass = NSClassFromString(@"MRClient");
    if (![clientClass instancesRespondToSelector:initClient]) return nil;
    id client = ((id (*)(id, SEL, id))objc_msgSend)([clientClass alloc], initClient, @"com.apple.Music");
    Class pathClass = NSClassFromString(@"MRPlayerPath");
    SEL initPath = NSSelectorFromString(@"initWithOrigin:client:player:");
    if (!client || ![pathClass instancesRespondToSelector:initPath]) return nil;
    return ((id (*)(id, SEL, id, id, id))objc_msgSend)([pathClass alloc], initPath, origin, client, nil);
}

static NSString *LQString(CFStringRef value) {
    return value ? [(__bridge NSString *)value copy] : nil;
}

/// One item's details: the item's own accessors, else its now-playing info.
static NSDictionary *LQItem(void *item) {
    NSMutableDictionary *out = [NSMutableDictionary dictionary];
    NSDictionary *info = nil;
    if (LQ.copyInfo) {
        CFDictionaryRef copied = LQ.copyInfo(item);
        if (copied) info = CFBridgingRelease(copied);
    }
    NSString *title = LQ.title ? LQString(LQ.title(item)) : nil;
    NSString *artist = LQ.artist ? LQString(LQ.artist(item)) : nil;
    NSString *album = LQ.album ? LQString(LQ.album(item)) : nil;
    double duration = LQ.duration ? LQ.duration(item) : 0;
    if (!title) title = info[@"kMRMediaRemoteNowPlayingInfoTitle"];
    if (!artist) artist = info[@"kMRMediaRemoteNowPlayingInfoArtist"];
    if (!album) album = info[@"kMRMediaRemoteNowPlayingInfoAlbum"];
    if (!(duration > 0)) duration = [info[@"kMRMediaRemoteNowPlayingInfoDuration"] doubleValue];
    out[@"title"] = [title isKindOfClass:NSString.class] ? title : @"";
    out[@"artist"] = [artist isKindOfClass:NSString.class] ? artist : @"";
    out[@"album"] = [album isKindOfClass:NSString.class] ? album : @"";
    out[@"duration"] = @(isfinite(duration) && duration > 0 ? duration : 0);
    NSString *identifier = LQ.identifier ? LQString(LQ.identifier(item)) : nil;
    if (identifier) out[@"id"] = identifier;
    NSString *genre = info[@"kMRMediaRemoteNowPlayingInfoGenre"];
    if ([genre isKindOfClass:NSString.class] && genre.length > 0) out[@"genre"] = genre;
    out[@"current"] = @(LQ.isCurrent ? LQ.isCurrent(item) != 0 : NO);
    return out;
}

/// Asks for the queue; `done` runs once, on the main queue, with the items
/// (nil when MediaRemote did not answer or answered with an error).
static void LQAsk(void (^done)(NSArray *items, NSString *failure)) {
    id path = LQMusicPath();
    void *request = LQ.requestWithRange(0, LQRange);
    if (!path || !request) {
        done(nil, @"request");
        return;
    }
    __block BOOL answered = NO;
    void (^finish)(NSArray *, NSString *) = ^(NSArray *items, NSString *failure) {
        if (answered) return;
        answered = YES;
        done(items, failure);
    };
    LQ.requestForPlayer(request, path, dispatch_get_main_queue(), ^(void *queue, CFErrorRef error) {
        if (!queue || error) {
            finish(nil, error ? @"answer" : @"empty");
            return;
        }
        NSMutableArray *items = [NSMutableArray array];
        CFArrayRef list = LQ.copyItems(queue);
        for (CFIndex i = 0; list && i < CFArrayGetCount(list); i++) {
            [items addObject:LQItem((void *)CFArrayGetValueAtIndex(list, i))];
        }
        if (list) CFRelease(list);
        // Music does not flag its playing item here: the queue starts with
        // it, so the first item is current when none is flagged.
        BOOL flagged = NO;
        for (NSDictionary *item in items) flagged = flagged || [item[@"current"] boolValue];
        if (!flagged && items.count > 0) {
            NSMutableDictionary *first = [items[0] mutableCopy];
            first[@"current"] = @YES;
            items[0] = first;
        }
        finish(items, nil);
    });
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(LQAnswerSeconds * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
        finish(nil, @"timeout");
    });
}

/// Watch mode: asks, prints the snapshot if it changed, and asks once more
/// if something changed meanwhile.
static void LQRefresh(void) {
    if (LQAsking) {
        LQAskAgain = YES;
        return;
    }
    LQAsking = YES;
    LQAsk(^(NSArray *items, NSString *failure) {
        if (items) {
            NSDictionary *line = @{@"type": @"queue", @"v": @1, @"items": items};
            NSData *data = [NSJSONSerialization dataWithJSONObject:line options:NSJSONWritingSortedKeys error:nil];
            NSString *text = data ? [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] : nil;
            if (text && ![text isEqualToString:LQLast]) {
                LQLast = text;
                LQEmit(line);
            }
        } else {
            // Said once until something changes, not on every look.
            NSString *text = [@"error:" stringByAppendingString:failure ?: @"answer"];
            if (![text isEqualToString:LQLast]) {
                LQLast = text;
                LQError(failure ?: @"answer");
            }
        }
        LQAsking = NO;
        if (LQAskAgain) {
            LQAskAgain = NO;
            LQRefresh();
        }
    });
}

/// Coalesces bursts of notifications into one request.
static void LQSoon(void) {
    static BOOL scheduled = NO;
    if (scheduled) return;
    scheduled = YES;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.4 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
        scheduled = NO;
        LQRefresh();
    });
}

static NSString *LQConstant(const char *symbol, NSString *fallback) {
    CFStringRef *value = LQ.handle ? (CFStringRef *)dlsym(LQ.handle, symbol) : NULL;
    return value && *value ? (__bridge NSString *)*value : fallback;
}

/// Perl XSUB: prints one snapshot (or an error) and returns.
void lunalyr_queue_once(void *perl, void *cv) {
    (void)perl; (void)cv;
    @autoreleasepool {
        if (!LQLoad()) {
            LQError(@"symbols");
            return;
        }
        dispatch_semaphore_t answered = dispatch_semaphore_create(0);
        __block NSArray *result = nil;
        __block NSString *failed = nil;
        // Perl's main thread is the process's: the answer comes on the main
        // queue, which the loop below services.
        LQAsk(^(NSArray *items, NSString *failure) {
            result = items;
            failed = failure;
            dispatch_semaphore_signal(answered);
        });
        NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:LQAnswerSeconds + 1];
        while (dispatch_semaphore_wait(answered, DISPATCH_TIME_NOW) != 0 && deadline.timeIntervalSinceNow > 0) {
            CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.05, true);
        }
        if (result) {
            LQEmit(@{@"type": @"queue", @"v": @1, @"items": result});
        } else {
            LQError(failed ?: @"timeout");
        }
    }
}

/// Perl XSUB: runs until stdin closes.
void lunalyr_queue_watch(void *perl, void *cv) {
    (void)perl; (void)cv;
    @autoreleasepool {
        if (!LQLoad()) {
            LQError(@"symbols");
            return;
        }
        LQEmit(@{@"type": @"ready", @"v": @1});
        if (LQ.registerNotifications) LQ.registerNotifications(dispatch_get_main_queue());
        NSArray *names = @[
            LQConstant("kMRMediaRemoteNowPlayingPlaybackQueueDidChangeNotification",
                       @"kMRMediaRemoteNowPlayingPlaybackQueueDidChangeNotification"),
            LQConstant("kMRPlaybackQueueContentItemsChangedNotification", @"kMRPlaybackQueueContentItemsChangedNotification"),
            LQConstant("kMRNowPlayingPlaybackQueueChangedNotification", @"kMRNowPlayingPlaybackQueueChangedNotification"),
            LQConstant("kMRMediaRemoteNowPlayingInfoDidChangeNotification", @"kMRMediaRemoteNowPlayingInfoDidChangeNotification"),
            LQConstant("kMRMediaRemotePlayerNowPlayingInfoDidChangeNotification",
                       @"kMRMediaRemotePlayerNowPlayingInfoDidChangeNotification"),
        ];
        for (NSString *name in names) {
            [[NSNotificationCenter defaultCenter] addObserverForName:name object:nil queue:nil
                                                          usingBlock:^(NSNotification *note) {
                (void)note;
                dispatch_async(dispatch_get_main_queue(), ^{ LQSoon(); });
            }];
        }
        // A slow look as well, in case a change is not announced.
        dispatch_source_t timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, dispatch_get_main_queue());
        dispatch_source_set_timer(timer, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(LQPollSeconds * NSEC_PER_SEC)),
                                  (uint64_t)(LQPollSeconds * NSEC_PER_SEC), NSEC_PER_SEC);
        dispatch_source_set_event_handler(timer, ^{ LQRefresh(); });
        dispatch_resume(timer);
        // Lines on stdin ask again; its end (Lunalyr quit or crashed) ends this.
        [NSThread detachNewThreadWithBlock:^{
            char buffer[256];
            while (fgets(buffer, sizeof buffer, stdin)) {
                dispatch_async(dispatch_get_main_queue(), ^{ LQSoon(); });
            }
            fflush(stdout);
            _exit(0);
        }];
        LQRefresh();
        for (;;) {
            @autoreleasepool {
                CFRunLoopRunInMode(kCFRunLoopDefaultMode, 60, false);
            }
        }
    }
}
