import AppKit
import CoreImage
import CoreImage.CIFilterBuiltins

enum QRCode {
    /// Renders `string` as a black-on-white QR code, one pixel per module.
    /// Scale it up with `.interpolation(.none)` to keep the edges crisp.
    static func image(for string: String) -> NSImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(string.utf8)
        filter.correctionLevel = "M"

        guard let output = filter.outputImage,
              let cgImage = CIContext().createCGImage(output, from: output.extent)
        else { return nil }

        return NSImage(cgImage: cgImage, size: output.extent.size)
    }
}
