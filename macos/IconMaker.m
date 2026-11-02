#import <AppKit/AppKit.h>

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        if (argc != 3) return 1;
        NSString *folder = @(argv[1]);
        BOOL closing = strcmp(argv[2], "close") == 0;
        [[NSFileManager defaultManager] createDirectoryAtPath:folder withIntermediateDirectories:YES attributes:nil error:nil];
        for (NSNumber *sizeValue in @[@16, @32, @128, @256, @512]) {
            for (int scale = 1; scale <= 2; scale++) {
                NSInteger size = sizeValue.integerValue, pixels = size * scale;
                NSBitmapImageRep *bitmap = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:NULL pixelsWide:pixels pixelsHigh:pixels bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSCalibratedRGBColorSpace bytesPerRow:0 bitsPerPixel:0];
                [NSGraphicsContext saveGraphicsState];
                NSGraphicsContext.currentContext = [NSGraphicsContext graphicsContextWithBitmapImageRep:bitmap];
                NSAffineTransform *transform = [NSAffineTransform transform];
                [transform scaleBy:pixels / 1024.0];
                [transform concat];
                NSColor *color = closing ? [NSColor colorWithCalibratedRed:0.75 green:0.34 blue:0.24 alpha:1] : [NSColor colorWithCalibratedRed:0.17 green:0.43 blue:0.38 alpha:1];
                [color setFill];
                [[NSBezierPath bezierPathWithRoundedRect:NSMakeRect(52, 52, 920, 920) xRadius:210 yRadius:210] fill];
                NSImage *symbol = [NSImage imageWithSystemSymbolName:closing ? @"power" : @"magnifyingglass" accessibilityDescription:nil];
                NSImage *tinted = [NSImage imageWithSize:NSMakeSize(520, 520) flipped:NO drawingHandler:^BOOL(NSRect rect) {
                    [symbol drawInRect:rect];
                    [[NSColor whiteColor] setFill];
                    NSRectFillUsingOperation(rect, NSCompositingOperationSourceIn);
                    return YES;
                }];
                [tinted drawInRect:NSMakeRect(252, 252, 520, 520)];
                [NSGraphicsContext restoreGraphicsState];
                NSString *name = [NSString stringWithFormat:@"icon_%ldx%ld%@.png", (long)size, (long)size, scale == 2 ? @"@2x" : @""];
                NSData *data = [bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
                if (![data writeToFile:[folder stringByAppendingPathComponent:name] atomically:YES]) return 2;
            }
        }
    }
    return 0;
}
