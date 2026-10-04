import SwiftUI

struct QRCodeView: View {
    let content: String

    var body: some View {
        Group {
            if let image = QRCode.image(for: content) {
                Image(nsImage: image)
                    .interpolation(.none)
                    .resizable()
                    .scaledToFit()
            } else {
                Color.clear
            }
        }
        .padding(10)
        // Always white, even in dark mode, so phone cameras read it reliably.
        .background(.white, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .accessibilityLabel("QR kodu")
    }
}
