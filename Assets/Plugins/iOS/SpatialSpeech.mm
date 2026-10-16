// Voice typing for Spatial Launcher (VoiceTyping.cs, IosSpeechRecognizer.cs): Apple's Speech framework on the
// microphone, on the phone when the language supports it. iOS only; compiled with ARC (IosVoiceBuild.cs adds -fobjc-arc
// and links Speech.framework). C# polls: start, read partial text while listening, stop, then take the final text.
// Untested on a device so far.
#import <Foundation/Foundation.h>
#import <AVFoundation/AVFoundation.h>
#import <Speech/Speech.h>

#if !__has_feature(objc_arc)
#error SpatialSpeech.mm needs ARC (-fobjc-arc); IosVoiceBuild.cs sets it after each iOS build.
#endif

typedef NS_ENUM(int, SpatialSpeechState) { SpatialSpeechIdle = 0, SpatialSpeechListening = 1, SpatialSpeechFinishing = 2 };

static NSObject* gLock;
static SFSpeechRecognizer* gRecognizer;
static SFSpeechAudioBufferRecognitionRequest* gRequest;
static SFSpeechRecognitionTask* gTask;
static AVAudioEngine* gEngine;
static NSString* gPartial = @"";
static NSMutableArray<NSString*>* gFinals;
static NSString* gError;
static SpatialSpeechState gState = SpatialSpeechIdle;
static BOOL gOnDevice;
static AVAudioSessionCategory gPreviousCategory;
static AVAudioSessionMode gPreviousMode;
static AVAudioSessionCategoryOptions gPreviousOptions;

static void EnsureInit(void)
{
    if (gLock == nil)
    {
        gLock = [NSObject new];
        gFinals = [NSMutableArray new];
    }
}

// A copy the C# marshaller frees (it calls free() on returned strings).
static char* CopyString(NSString* text)
{
    if (text == nil) return NULL;
    const char* utf8 = [text UTF8String];
    char* copy = (char*)malloc(strlen(utf8) + 1);
    strcpy(copy, utf8);
    return copy;
}

static void StopAudio(void)
{
    if (gEngine != nil)
    {
        if (gEngine.isRunning) [gEngine stop];
        [gEngine.inputNode removeTapOnBus:0];
        gEngine = nil;
    }
}

// Back to idle: audio off, the app's audio session as it was before listening.
static void Finish(void)
{
    StopAudio();
    [gRequest endAudio];
    gRequest = nil;
    gTask = nil;
    gRecognizer = nil;
    gState = SpatialSpeechIdle;
    AVAudioSession* session = [AVAudioSession sharedInstance];
    if (gPreviousCategory != nil)
    {
        [session setCategory:gPreviousCategory mode:gPreviousMode options:gPreviousOptions error:nil];
        gPreviousCategory = nil;
    }
}

extern "C" {

// SFSpeechRecognizerAuthorizationStatus: 0 not asked yet, 1 denied, 2 restricted, 3 authorized.
int SpatialSpeech_AuthorizationStatus(void)
{
    return (int)[SFSpeechRecognizer authorizationStatus];
}

// 0 not asked yet, 1 denied, 3 granted.
int SpatialSpeech_MicrophonePermission(void)
{
    AVAudioSessionRecordPermission permission = [[AVAudioSession sharedInstance] recordPermission];
    if (permission == AVAudioSessionRecordPermissionGranted) return 3;
    if (permission == AVAudioSessionRecordPermissionDenied) return 1;
    return 0;
}

// Shows iOS's two permission prompts (speech recognition, microphone) if they haven't been answered yet.
void SpatialSpeech_RequestAuthorization(void)
{
    [SFSpeechRecognizer requestAuthorization:^(SFSpeechRecognizerAuthorizationStatus status) {}];
    [[AVAudioSession sharedInstance] requestRecordPermission:^(BOOL granted) {}];
}

bool SpatialSpeech_SupportsOnDevice(const char* locale)
{
    SFSpeechRecognizer* recognizer = [[SFSpeechRecognizer alloc] initWithLocale:[NSLocale localeWithLocaleIdentifier:[NSString stringWithUTF8String:locale]]];
    return recognizer != nil && recognizer.supportsOnDeviceRecognition;
}

// Starts listening in a language (BCP 47, e.g. "en-US"). On the phone when preferOnDevice and the language allows it.
bool SpatialSpeech_Start(const char* locale, bool preferOnDevice)
{
    EnsureInit();
    @synchronized (gLock)
    {
        if (gState != SpatialSpeechIdle) return false;
        gError = nil;
        if ([SFSpeechRecognizer authorizationStatus] != SFSpeechRecognizerAuthorizationStatusAuthorized)
        {
            gError = @"Speech recognition isn't allowed (Settings > Privacy > Speech Recognition)";
            return false;
        }
        if ([[AVAudioSession sharedInstance] recordPermission] != AVAudioSessionRecordPermissionGranted)
        {
            gError = @"The microphone isn't allowed (Settings > Privacy > Microphone)";
            return false;
        }
        gRecognizer = [[SFSpeechRecognizer alloc] initWithLocale:[NSLocale localeWithLocaleIdentifier:[NSString stringWithUTF8String:locale]]];
        if (gRecognizer == nil || !gRecognizer.isAvailable)
        {
            gError = @"Speech recognition isn't available for this language right now";
            gRecognizer = nil;
            return false;
        }

        // Record alongside the app's own audio (video windows keep playing), then put the session back afterwards.
        AVAudioSession* session = [AVAudioSession sharedInstance];
        gPreviousCategory = session.category;
        gPreviousMode = session.mode;
        gPreviousOptions = session.categoryOptions;
        NSError* error = nil;
        if (![session setCategory:AVAudioSessionCategoryPlayAndRecord mode:AVAudioSessionModeMeasurement
                          options:AVAudioSessionCategoryOptionMixWithOthers | AVAudioSessionCategoryOptionDefaultToSpeaker |
                                  AVAudioSessionCategoryOptionAllowBluetooth
                            error:&error]
            || ![session setActive:YES error:&error])
        {
            gError = [NSString stringWithFormat:@"Couldn't use the microphone: %@", error.localizedDescription];
            Finish();
            return false;
        }

        gRequest = [SFSpeechAudioBufferRecognitionRequest new];
        gRequest.shouldReportPartialResults = YES;
        gOnDevice = preferOnDevice && gRecognizer.supportsOnDeviceRecognition;
        gRequest.requiresOnDeviceRecognition = gOnDevice;

        gEngine = [AVAudioEngine new];
        AVAudioInputNode* input = gEngine.inputNode;
        AVAudioFormat* format = [input outputFormatForBus:0];
        SFSpeechAudioBufferRecognitionRequest* request = gRequest;
        [input installTapOnBus:0 bufferSize:1024 format:format block:^(AVAudioPCMBuffer* buffer, AVAudioTime* when) {
            [request appendAudioPCMBuffer:buffer];
        }];
        [gEngine prepare];
        if (![gEngine startAndReturnError:&error])
        {
            gError = [NSString stringWithFormat:@"Couldn't start the microphone: %@", error.localizedDescription];
            Finish();
            return false;
        }

        gPartial = @"";
        gState = SpatialSpeechListening;
        gTask = [gRecognizer recognitionTaskWithRequest:gRequest resultHandler:^(SFSpeechRecognitionResult* result, NSError* taskError) {
            @synchronized (gLock)
            {
                if (result != nil)
                {
                    NSString* text = result.bestTranscription.formattedString;
                    if (result.isFinal)
                    {
                        if (text.length > 0) [gFinals addObject:text];
                        gPartial = @"";
                        Finish();
                        return;
                    }
                    gPartial = text;
                }
                if (taskError != nil && gState != SpatialSpeechIdle)
                {
                    // "No speech detected" and cancellations end up here too; keep what was heard so far.
                    if (gPartial.length > 0) [gFinals addObject:gPartial];
                    gPartial = @"";
                    gError = taskError.localizedDescription;
                    Finish();
                }
            }
        }];
        return true;
    }
}

// Stops listening; the final text follows shortly (SpatialSpeech_TakeFinal).
void SpatialSpeech_Stop(void)
{
    EnsureInit();
    @synchronized (gLock)
    {
        if (gState != SpatialSpeechListening) return;
        gState = SpatialSpeechFinishing;
        StopAudio();
        [gRequest endAudio];
    }
}

// Stops without waiting for a final result.
void SpatialSpeech_Cancel(void)
{
    EnsureInit();
    @synchronized (gLock)
    {
        if (gState == SpatialSpeechIdle) return;
        [gTask cancel];
        gPartial = @"";
        Finish();
    }
}

// 0 idle, 1 listening, 2 finishing (waiting for the final text).
int SpatialSpeech_State(void)
{
    return (int)gState;
}

bool SpatialSpeech_IsOnDevice(void)
{
    return gOnDevice;
}

char* SpatialSpeech_CopyPartial(void)
{
    EnsureInit();
    @synchronized (gLock) { return CopyString(gPartial); }
}

// The oldest final text not taken yet, or NULL.
char* SpatialSpeech_TakeFinal(void)
{
    EnsureInit();
    @synchronized (gLock)
    {
        if (gFinals.count == 0) return NULL;
        NSString* text = gFinals.firstObject;
        [gFinals removeObjectAtIndex:0];
        return CopyString(text);
    }
}

// The last error not taken yet, or NULL.
char* SpatialSpeech_TakeError(void)
{
    EnsureInit();
    @synchronized (gLock)
    {
        NSString* error = gError;
        gError = nil;
        return CopyString(error);
    }
}

}
