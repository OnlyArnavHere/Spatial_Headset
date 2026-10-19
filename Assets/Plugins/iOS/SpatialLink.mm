// The headset's laptop link, native half (LaptopLink/Native/Ios*.cs): Keychain RSA identity, PBKDF2 and the Bonjour
// advertiser. Plain C calls with explicit CFRelease; no ARC dependence.
#import <Foundation/Foundation.h>
#import <Security/Security.h>
#import <CommonCrypto/CommonKeyDerivation.h>
#import <dns_sd.h>
#include <arpa/inet.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

static pthread_mutex_t g_keyLock = PTHREAD_MUTEX_INITIALIZER;
static SecKeyRef g_key = NULL;

static pthread_mutex_t g_bonjourLock = PTHREAD_MUTEX_INITIALIZER;
static DNSServiceRef g_service = NULL;
static dispatch_queue_t g_bonjourQueue = NULL;

static int ErrorCode(CFErrorRef error)
{
    int code = error != NULL ? (int)CFErrorGetCode(error) : 0;
    if (error != NULL) CFRelease(error);
    return code != 0 ? code : (int)errSecInternalError;
}

// Caller holds g_keyLock. Returns 0 with a retained key in g_key, or an OSStatus.
static int LoadOrCreateLocked(const char* tag)
{
    if (g_key != NULL) return 0;
    CFDataRef tagData = CFDataCreate(kCFAllocatorDefault, (const UInt8*)tag, (CFIndex)strlen(tag));
    if (tagData == NULL) return -108; // memFullErr

    CFMutableDictionaryRef query = CFDictionaryCreateMutable(kCFAllocatorDefault, 0, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFDictionarySetValue(query, kSecClass, kSecClassKey);
    CFDictionarySetValue(query, kSecAttrKeyType, kSecAttrKeyTypeRSA);
    CFDictionarySetValue(query, kSecAttrApplicationTag, tagData);
    CFDictionarySetValue(query, kSecReturnRef, kCFBooleanTrue);
    CFTypeRef found = NULL;
    OSStatus status = SecItemCopyMatching(query, &found);
    CFRelease(query);

    if (status == errSecSuccess && found != NULL)
    {
        g_key = (SecKeyRef)found;
        CFRelease(tagData);
        return 0;
    }
    if (status == errSecSuccess) status = errSecInternalError;
    if (status != errSecItemNotFound)
    {
        CFRelease(tagData);
        return (int)status; // never regenerate over a key that cannot be read
    }

    int bits = 2048;
    CFNumberRef bitsNumber = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &bits);
    CFMutableDictionaryRef privateAttrs = CFDictionaryCreateMutable(kCFAllocatorDefault, 0, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFDictionarySetValue(privateAttrs, kSecAttrIsPermanent, kCFBooleanTrue);
    CFDictionarySetValue(privateAttrs, kSecAttrApplicationTag, tagData);
    CFDictionarySetValue(privateAttrs, kSecAttrAccessible, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly);
    CFDictionarySetValue(privateAttrs, kSecAttrIsExtractable, kCFBooleanFalse);
    CFMutableDictionaryRef attrs = CFDictionaryCreateMutable(kCFAllocatorDefault, 0, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFDictionarySetValue(attrs, kSecAttrKeyType, kSecAttrKeyTypeRSA);
    CFDictionarySetValue(attrs, kSecAttrKeySizeInBits, bitsNumber);
    CFDictionarySetValue(attrs, kSecPrivateKeyAttrs, privateAttrs);
    CFErrorRef error = NULL;
    SecKeyRef created = SecKeyCreateRandomKey(attrs, &error);
    int result = 0;
    if (created == NULL) result = ErrorCode(error);
    else g_key = created;
    CFRelease(attrs);
    CFRelease(privateAttrs);
    CFRelease(bitsNumber);
    CFRelease(tagData);
    return result;
}

static void DNSSD_API BonjourReply(DNSServiceRef sdRef, DNSServiceFlags flags, DNSServiceErrorType errorCode,
                                   const char* name, const char* regtype, const char* domain, void* context)
{
    if (errorCode != kDNSServiceErr_NoError)
        NSLog(@"[SpatialLink] Bonjour registration failed: %d (-65570 is PolicyDenied, -65548 a name conflict)", (int)errorCode);
}

extern "C" {

int SpatialLink_LoadOrCreateKey(const char* tag, uint8_t** pub, int* len)
{
    if (tag == NULL || pub == NULL || len == NULL) return (int)errSecParam;
    *pub = NULL;
    *len = 0;
    pthread_mutex_lock(&g_keyLock);
    int status = LoadOrCreateLocked(tag);
    SecKeyRef key = status == 0 ? (SecKeyRef)CFRetain(g_key) : NULL;
    pthread_mutex_unlock(&g_keyLock);
    if (status != 0) return status;

    int result = 0;
    SecKeyRef publicKey = SecKeyCopyPublicKey(key);
    if (publicKey == NULL) result = (int)errSecInternalError;
    else
    {
        CFErrorRef error = NULL;
        CFDataRef data = SecKeyCopyExternalRepresentation(publicKey, &error);
        if (data == NULL) result = ErrorCode(error);
        else
        {
            CFIndex n = CFDataGetLength(data);
            uint8_t* buffer = (uint8_t*)malloc((size_t)n);
            if (buffer == NULL) result = -108;
            else
            {
                memcpy(buffer, CFDataGetBytePtr(data), (size_t)n);
                *pub = buffer;
                *len = (int)n;
            }
            CFRelease(data);
        }
        CFRelease(publicKey);
    }
    CFRelease(key);
    return result;
}

int SpatialLink_Sign(const uint8_t* msg, int len, uint8_t** sig, int* sigLen)
{
    if (msg == NULL || len < 0 || sig == NULL || sigLen == NULL) return (int)errSecParam;
    *sig = NULL;
    *sigLen = 0;
    pthread_mutex_lock(&g_keyLock);
    SecKeyRef key = g_key != NULL ? (SecKeyRef)CFRetain(g_key) : NULL;
    pthread_mutex_unlock(&g_keyLock);
    if (key == NULL) return (int)errSecItemNotFound;

    int result = 0;
    CFDataRef message = CFDataCreate(kCFAllocatorDefault, msg, (CFIndex)len);
    CFErrorRef error = NULL;
    CFDataRef signature = message != NULL
        ? SecKeyCreateSignature(key, kSecKeyAlgorithmRSASignatureMessagePKCS1v15SHA256, message, &error)
        : NULL;
    if (signature == NULL) result = message != NULL ? ErrorCode(error) : -108;
    else
    {
        CFIndex n = CFDataGetLength(signature);
        uint8_t* buffer = (uint8_t*)malloc((size_t)n);
        if (buffer == NULL) result = -108;
        else
        {
            memcpy(buffer, CFDataGetBytePtr(signature), (size_t)n);
            *sig = buffer;
            *sigLen = (int)n;
        }
        CFRelease(signature);
    }
    if (message != NULL) CFRelease(message);
    CFRelease(key);
    return result;
}

int SpatialLink_Pbkdf2(const uint8_t* pw, int pwLen, const uint8_t* salt, int saltLen, int iterations, uint8_t* out, int outLen)
{
    if (pw == NULL || salt == NULL || out == NULL || pwLen < 0 || saltLen < 0 || iterations <= 0 || outLen <= 0) return (int)errSecParam;
    return (int)CCKeyDerivationPBKDF(kCCPBKDF2, (const char*)pw, (size_t)pwLen, salt, (size_t)saltLen,
                                     kCCPRFHmacAlgSHA256, (unsigned)iterations, out, (size_t)outLen);
}

void SpatialLink_BonjourUnregister(void)
{
    pthread_mutex_lock(&g_bonjourLock);
    DNSServiceRef service = g_service;
    dispatch_queue_t queue = g_bonjourQueue;
    g_service = NULL;
    pthread_mutex_unlock(&g_bonjourLock);
    if (service != NULL && queue != NULL)
        dispatch_sync(queue, ^{ DNSServiceRefDeallocate(service); });
}

int SpatialLink_BonjourRegister(const char* instance, int port, int pairing)
{
    if (instance == NULL || port <= 0 || port > 65535) return (int)kDNSServiceErr_BadParam;
    SpatialLink_BonjourUnregister();

    pthread_mutex_lock(&g_bonjourLock);
    if (g_bonjourQueue == NULL) g_bonjourQueue = dispatch_queue_create("com.spatiallauncher.link.bonjour", DISPATCH_QUEUE_SERIAL);
    dispatch_queue_t queue = g_bonjourQueue;
    pthread_mutex_unlock(&g_bonjourLock);

    TXTRecordRef txt;
    char txtBuffer[64];
    TXTRecordCreate(&txt, sizeof(txtBuffer), txtBuffer);
    TXTRecordSetValue(&txt, "v", 1, "1");
    if (pairing != 0) TXTRecordSetValue(&txt, "m", 4, "pair");

    DNSServiceRef service = NULL;
    DNSServiceErrorType error = DNSServiceRegister(&service, 0, 0, instance, "_spatiallauncher._tcp", NULL, NULL,
                                                   htons((uint16_t)port), TXTRecordGetLength(&txt), TXTRecordGetBytesPtr(&txt),
                                                   BonjourReply, NULL);
    TXTRecordDeallocate(&txt);
    if (error != kDNSServiceErr_NoError || service == NULL)
    {
        NSLog(@"[SpatialLink] DNSServiceRegister failed: %d", (int)error);
        return error != kDNSServiceErr_NoError ? (int)error : (int)kDNSServiceErr_Unknown;
    }
    error = DNSServiceSetDispatchQueue(service, queue);
    if (error != kDNSServiceErr_NoError)
    {
        NSLog(@"[SpatialLink] DNSServiceSetDispatchQueue failed: %d", (int)error);
        DNSServiceRefDeallocate(service);
        return (int)error;
    }
    pthread_mutex_lock(&g_bonjourLock);
    g_service = service;
    pthread_mutex_unlock(&g_bonjourLock);
    return 0;
}

void SpatialLink_Free(void* p)
{
    free(p);
}

}
