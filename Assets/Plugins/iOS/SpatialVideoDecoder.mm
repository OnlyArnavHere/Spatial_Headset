// The hardware H.264 decoder for laptop windows that arrive over the USB cable (StreamVideoDecoder.cs). A WebRTC
// session decodes inside libwebrtc; the stream transport (USB without a network) hands this plugin raw access units.
//
//   SLVD_Create(srgb)                  -> handle
//   SLVD_Decode(handle, data, length)  one H.264 access unit in Annex B; any thread; decodes synchronously
//   SLVD_Acquire(handle, &w, &h, &serial) -> id<MTLTexture> of the newest frame (main thread; kept alive by the plugin
//                                          for the next few frames), or NULL before the first one
//   SLVD_Destroy(handle)
//
// SPS and PPS ride in every keyframe; a change rebuilds the decompression session. Frames before the first SPS/PPS, or
// after the session failed (the app was in the background), are dropped until the next keyframe.
#import <Foundation/Foundation.h>
#import <VideoToolbox/VideoToolbox.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#import <Metal/Metal.h>
#include <pthread.h>
#include <stdint.h>
#include <string.h>

#define SLVD_HELD 4

typedef struct SLVD
{
    pthread_mutex_t decodeLock;   // the session, format and parameter sets (decode thread)
    pthread_mutex_t frameLock;    // latest frame and textures (decode callback and main thread)
    VTDecompressionSessionRef session;
    CMVideoFormatDescriptionRef format;
    uint8_t sps[256]; size_t spsLength;
    uint8_t pps[256]; size_t ppsLength;
    int needKeyframe;
    int srgb;
    CVPixelBufferRef latest;      // retained
    int64_t latestSerial;
    int64_t takenSerial;
    CVMetalTextureCacheRef cache;
    CVMetalTextureRef held[SLVD_HELD]; // textures Unity may still be sampling
    int heldNext;
    int width, height;
} SLVD;

static void OnDecoded(void* refcon, void* frameRefcon, OSStatus status, VTDecodeInfoFlags flags,
                      CVImageBufferRef image, CMTime pts, CMTime duration)
{
    SLVD* d = (SLVD*)refcon;
    if (status != noErr || image == NULL) return;
    pthread_mutex_lock(&d->frameLock);
    if (d->latest != NULL) CVPixelBufferRelease(d->latest);
    d->latest = CVPixelBufferRetain(image);
    d->latestSerial++;
    pthread_mutex_unlock(&d->frameLock);
}

static void DropSession(SLVD* d)
{
    if (d->session != NULL)
    {
        VTDecompressionSessionInvalidate(d->session);
        CFRelease(d->session);
        d->session = NULL;
    }
    if (d->format != NULL)
    {
        CFRelease(d->format);
        d->format = NULL;
    }
}

static int MakeSession(SLVD* d)
{
    DropSession(d);
    const uint8_t* sets[2] = { d->sps, d->pps };
    const size_t sizes[2] = { d->spsLength, d->ppsLength };
    OSStatus status = CMVideoFormatDescriptionCreateFromH264ParameterSets(kCFAllocatorDefault, 2, sets, sizes, 4, &d->format);
    if (status != noErr) return (int)status;

    NSDictionary* attributes = @{
        (id)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA),
        (id)kCVPixelBufferMetalCompatibilityKey: @YES,
        (id)kCVPixelBufferIOSurfacePropertiesKey: @{},
    };
    VTDecompressionOutputCallbackRecord callback = { OnDecoded, d };
    status = VTDecompressionSessionCreate(kCFAllocatorDefault, d->format, NULL, (__bridge CFDictionaryRef)attributes,
                                          &callback, &d->session);
    if (status != noErr)
    {
        DropSession(d);
        return (int)status;
    }
    VTSessionSetProperty(d->session, kVTDecompressionPropertyKey_RealTime, kCFBooleanTrue);
    return 0;
}

// Next NAL unit in Annex B: returns its start (after the start code) and length, or NULL at the end.
static const uint8_t* NextNal(const uint8_t* p, const uint8_t* end, size_t* length, const uint8_t** next)
{
    while (p + 3 <= end && !(p[0] == 0 && p[1] == 0 && p[2] == 1)) p++;
    if (p + 3 > end) return NULL;
    const uint8_t* start = p + 3;
    const uint8_t* q = start;
    while (q + 3 <= end && !(q[0] == 0 && q[1] == 0 && (q[2] == 1 || (q[2] == 0 && q + 4 <= end && q[3] == 1)))) q++;
    if (q + 3 > end) q = end;
    *length = (size_t)(q - start);
    *next = q;
    return start;
}

extern "C" void* SLVD_Create(int srgb)
{
    SLVD* d = (SLVD*)calloc(1, sizeof(SLVD));
    if (d == NULL) return NULL;
    pthread_mutex_init(&d->decodeLock, NULL);
    pthread_mutex_init(&d->frameLock, NULL);
    d->srgb = srgb;
    d->needKeyframe = 1;
    id<MTLDevice> device = MTLCreateSystemDefaultDevice(); // the one GPU, the same device Unity renders with
    if (device == nil || CVMetalTextureCacheCreate(kCFAllocatorDefault, NULL, device, NULL, &d->cache) != kCVReturnSuccess)
    {
        free(d);
        return NULL;
    }
    return d;
}

static int DecodeLocked(SLVD* d, const uint8_t* data, int length);

// 0 decoded (or dropped while waiting for a keyframe); otherwise an OSStatus. Called on a .NET worker thread, which has
// no autorelease pool of its own.
extern "C" int SLVD_Decode(void* handle, const uint8_t* data, int length)
{
    SLVD* d = (SLVD*)handle;
    if (d == NULL || data == NULL || length <= 0) return -50;
    int result;
    pthread_mutex_lock(&d->decodeLock);
    @autoreleasepool
    {
        result = DecodeLocked(d, data, length);
    }
    pthread_mutex_unlock(&d->decodeLock);
    return result;
}

static int DecodeLocked(SLVD* d, const uint8_t* data, int length)
{

    const uint8_t* end = data + length;
    const uint8_t* p = data;
    const uint8_t* next = NULL;
    size_t nalLength = 0;
    int key = 0, setsChanged = 0;
    NSMutableData* avcc = [NSMutableData dataWithCapacity:(NSUInteger)length + 64];
    for (const uint8_t* nal = NextNal(p, end, &nalLength, &next); nal != NULL; nal = NextNal(next, end, &nalLength, &next))
    {
        while (nalLength > 0 && nal[nalLength - 1] == 0) nalLength--; // trailing zero bytes belong to the next start code
        if (nalLength == 0) continue;
        int type = nal[0] & 0x1F;
        if (type == 7 || type == 8)
        {
            uint8_t* target = type == 7 ? d->sps : d->pps;
            size_t* targetLength = type == 7 ? &d->spsLength : &d->ppsLength;
            if (nalLength <= sizeof(d->sps) && (*targetLength != nalLength || memcmp(target, nal, nalLength) != 0))
            {
                memcpy(target, nal, nalLength);
                *targetLength = nalLength;
                setsChanged = 1;
            }
            continue;
        }
        if (type == 9) continue; // access unit delimiter
        if (type == 5) key = 1;
        uint32_t be = CFSwapInt32HostToBig((uint32_t)nalLength);
        [avcc appendBytes:&be length:4];
        [avcc appendBytes:nal length:nalLength];
    }

    int result = 0;
    if ((setsChanged || d->session == NULL) && d->spsLength > 0 && d->ppsLength > 0 && key)
    {
        result = MakeSession(d);
        d->needKeyframe = result != 0;
    }
    if (key && d->session != NULL) d->needKeyframe = 0;
    if (result == 0 && d->session != NULL && !d->needKeyframe && avcc.length > 0)
    {
        CMBlockBufferRef block = NULL;
        result = (int)CMBlockBufferCreateWithMemoryBlock(kCFAllocatorDefault, NULL, avcc.length, kCFAllocatorDefault, NULL,
                                                         0, avcc.length, kCMBlockBufferAssureMemoryNowFlag, &block);
        if (result == noErr) result = (int)CMBlockBufferReplaceDataBytes(avcc.bytes, block, 0, avcc.length);
        CMSampleBufferRef sample = NULL;
        const size_t sampleSize = avcc.length;
        if (result == noErr)
            result = (int)CMSampleBufferCreateReady(kCFAllocatorDefault, block, d->format, 1, 0, NULL, 1, &sampleSize, &sample);
        if (result == noErr)
        {
            result = (int)VTDecompressionSessionDecodeFrame(d->session, sample, 0, NULL, NULL);
            if (result == kVTInvalidSessionErr)
            {
                DropSession(d); // the app was in the background: start over at the next keyframe
                d->needKeyframe = 1;
            }
        }
        if (sample != NULL) CFRelease(sample);
        if (block != NULL) CFRelease(block);
    }
    return result;
}

extern "C" void* SLVD_Acquire(void* handle, int* width, int* height, int64_t* serial)
{
    SLVD* d = (SLVD*)handle;
    *width = *height = 0;
    *serial = 0;
    if (d == NULL) return NULL;
    pthread_mutex_lock(&d->frameLock);
    if (d->latest != NULL && d->latestSerial != d->takenSerial)
    {
        int w = (int)CVPixelBufferGetWidth(d->latest);
        int h = (int)CVPixelBufferGetHeight(d->latest);
        CVMetalTextureRef texture = NULL;
        MTLPixelFormat format = d->srgb ? MTLPixelFormatBGRA8Unorm_sRGB : MTLPixelFormatBGRA8Unorm;
        if (CVMetalTextureCacheCreateTextureFromImage(kCFAllocatorDefault, d->cache, d->latest, NULL, format, w, h, 0,
                                                      &texture) == kCVReturnSuccess && texture != NULL)
        {
            if (d->held[d->heldNext] != NULL) CFRelease(d->held[d->heldNext]);
            d->held[d->heldNext] = texture;
            d->heldNext = (d->heldNext + 1) % SLVD_HELD;
            d->width = w;
            d->height = h;
        }
        d->takenSerial = d->latestSerial;
        CVMetalTextureCacheFlush(d->cache, 0);
    }
    int newest = (d->heldNext + SLVD_HELD - 1) % SLVD_HELD;
    CVMetalTextureRef current = d->held[newest];
    void* result = current != NULL ? (__bridge void*)CVMetalTextureGetTexture(current) : NULL;
    *width = d->width;
    *height = d->height;
    *serial = d->takenSerial;
    pthread_mutex_unlock(&d->frameLock);
    return result;
}

extern "C" void SLVD_Destroy(void* handle)
{
    SLVD* d = (SLVD*)handle;
    if (d == NULL) return;
    pthread_mutex_lock(&d->decodeLock);
    DropSession(d);
    pthread_mutex_unlock(&d->decodeLock);
    pthread_mutex_lock(&d->frameLock);
    if (d->latest != NULL) CVPixelBufferRelease(d->latest);
    for (int i = 0; i < SLVD_HELD; i++)
        if (d->held[i] != NULL) CFRelease(d->held[i]);
    if (d->cache != NULL) CFRelease(d->cache);
    pthread_mutex_unlock(&d->frameLock);
    pthread_mutex_destroy(&d->decodeLock);
    pthread_mutex_destroy(&d->frameLock);
    free(d);
}
