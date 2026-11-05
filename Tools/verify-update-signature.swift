// Checks a Sparkle EdDSA (Ed25519) update signature with CryptoKit.
//
// Run with:  xcrun swift Tools/verify-update-signature.swift <archive> <signature> <public-key>
//
// <signature> is the appcast's sparkle:edSignature and <public-key> is the
// SUPublicEDKey embedded in the app, both base64. Exits 0 when the archive
// verifies, 1 when it does not, and 2 for unusable input. Only public data is
// involved: no private key is read, accepted or needed.
//
// Scripts/verify-appcast.py runs this before a release is published, so an
// update signed with the wrong key fails here rather than on installed copies.

import CryptoKit
import Foundation

func finish(_ status: Int32, _ message: String) -> Never {
    let output = status == 0 ? FileHandle.standardOutput : FileHandle.standardError
    output.write(Data((message + "\n").utf8))
    exit(status)
}

let arguments = CommandLine.arguments
guard arguments.count == 4 else {
    finish(2, "usage: xcrun swift Tools/verify-update-signature.swift <archive> <signature> <public-key>")
}
guard let archive = FileManager.default.contents(atPath: arguments[1]) else {
    finish(2, "Cannot read the update archive at \(arguments[1]).")
}
guard let signature = Data(base64Encoded: arguments[2]), signature.count == 64 else {
    finish(2, "The signature must be 64 bytes of base64.")
}
guard let keyData = Data(base64Encoded: arguments[3]), keyData.count == 32,
      let publicKey = try? Curve25519.Signing.PublicKey(rawRepresentation: keyData) else {
    finish(2, "The public key must be 32 bytes of base64.")
}
guard publicKey.isValidSignature(signature, for: archive) else {
    finish(1, "The update signature does not match this archive and the app's public key.")
}
finish(0, "Update signature verified against the app's public key.")
