// Reads QR codes from a grayscale camera frame with Apple's Vision framework, for pairing with a laptop that shows its
// pairing QR (PairQrScanner.cs). Called on a .NET worker thread; synchronous.
//
//   SLQR_Detect(gray, width, height, rowBytes, output, outputLength) -> length of the first QR's text written to output
//                                                                       (UTF-8, NUL-terminated), 0 for none, -1 on error
#import <Foundation/Foundation.h>
#import <CoreGraphics/CoreGraphics.h>
#import <Vision/Vision.h>
#include <string.h>

extern "C" int SLQR_Detect(const uint8_t* gray, int width, int height, int rowBytes, char* output, int outputLength)
{
    if (gray == NULL || width <= 0 || height <= 0 || rowBytes < width || output == NULL || outputLength < 2) return -1;
    int result = 0;
    @autoreleasepool
    {
        CGColorSpaceRef space = CGColorSpaceCreateDeviceGray();
        CGDataProviderRef provider = CGDataProviderCreateWithData(NULL, gray, (size_t)rowBytes * (size_t)height, NULL);
        CGImageRef image = CGImageCreate((size_t)width, (size_t)height, 8, 8, (size_t)rowBytes, space,
                                         (CGBitmapInfo)kCGImageAlphaNone, provider, NULL, false, kCGRenderingIntentDefault);
        CGDataProviderRelease(provider);
        CGColorSpaceRelease(space);
        if (image == NULL) return -1;

        VNDetectBarcodesRequest* request = [[VNDetectBarcodesRequest alloc] init];
        request.symbologies = @[ VNBarcodeSymbologyQR ];
        VNImageRequestHandler* handler = [[VNImageRequestHandler alloc] initWithCGImage:image options:@{}];
        NSError* error = nil;
        BOOL ran = [handler performRequests:@[ request ] error:&error];
        CGImageRelease(image); // Vision is done with the pixels: performRequests is synchronous
        if (!ran) return -1;
        for (VNBarcodeObservation* found in request.results)
        {
            NSString* text = found.payloadStringValue;
            if (text.length == 0) continue;
            const char* utf8 = text.UTF8String;
            size_t length = utf8 != NULL ? strlen(utf8) : 0;
            if (length == 0 || length >= (size_t)outputLength) continue;
            memcpy(output, utf8, length);
            output[length] = 0;
            result = (int)length;
            break;
        }
    }
    return result;
}
