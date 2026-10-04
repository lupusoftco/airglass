import SwiftUI

/// Black-on-white QR code on a white card (white in dark mode too, so phone
/// cameras read it reliably). 148 pt code + 10 pt padding, per the design.
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
        .frame(width: 148, height: 148)
        .padding(10)
        .background(.white, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(Color.black.opacity(0.08), lineWidth: 0.5)
        )
        .accessibilityLabel("Bağlantı QR kodu")
    }
}
