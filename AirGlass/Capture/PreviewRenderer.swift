import AVFoundation
import SwiftUI

/// Temporary test aid: shows captured frames in the popover.
/// Removed once frames reach the phone (milestone 4).
final class PreviewRenderer: VideoFrameConsumer {
    let displayLayer: AVSampleBufferDisplayLayer = {
        let layer = AVSampleBufferDisplayLayer()
        layer.videoGravity = .resizeAspect
        layer.backgroundColor = NSColor.black.cgColor
        return layer
    }()

    func consume(_ sampleBuffer: CMSampleBuffer) {
        Self.markDisplayImmediately(sampleBuffer)
        if displayLayer.status == .failed {
            displayLayer.flush()
        }
        displayLayer.enqueue(sampleBuffer)
    }

    /// Without a timebase the layer would otherwise wait for the buffer's
    /// presentation time, which is on the capture clock.
    private static func markDisplayImmediately(_ sampleBuffer: CMSampleBuffer) {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true),
              CFArrayGetCount(attachments) > 0
        else { return }
        let dictionary = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
        CFDictionarySetValue(
            dictionary,
            Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
            Unmanaged.passUnretained(kCFBooleanTrue).toOpaque()
        )
    }
}

struct PreviewView: NSViewRepresentable {
    let renderer: PreviewRenderer

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        view.wantsLayer = true
        renderer.displayLayer.frame = view.bounds
        renderer.displayLayer.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        view.layer?.addSublayer(renderer.displayLayer)
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}

    static func dismantleNSView(_ nsView: NSView, coordinator: ()) {
        // The layer outlives the view; detach it so the next popover can adopt it.
        nsView.layer?.sublayers?.forEach { $0.removeFromSuperlayer() }
    }
}
