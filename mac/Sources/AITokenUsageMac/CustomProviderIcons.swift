import AppKit
import CryptoKit
import Darwin
import ImageIO
import UniformTypeIdentifiers

enum CustomProviderIcons {
    private static let size = 96
    private static let maxImportBytes = 16 * 1024 * 1024
    private static let maxStoredBytes = 128 * 1024
    private static let maxPixels = 32_000_000
    private static let lock = NSLock()
    // Swift String equality folds canonically equivalent spellings. Keep the exact ID bytes here too.
    private static var cache: [Data: NSImage?] = [:]

    private static func iconURL(_ key: Data) throws -> URL {
        guard !key.isEmpty else { throw CocoaError(.fileWriteInvalidFileName) }
        let support = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask,
                                                 appropriateFor: nil, create: false)
        let name = SHA256.hash(data: key).map { String(format: "%02x", $0) }.joined()
        return support.appendingPathComponent("AITokenUsage/provider-icons", isDirectory: true)
            .appendingPathComponent(name + ".png")
    }

    static func load(_ id: String) -> NSImage? {
        guard !id.isEmpty else { return nil }
        let key = Data(id.utf8)
        lock.lock()
        defer { lock.unlock() }
        if let cached = cache[key] { return cached }
        let image = try? decode(iconURL(key), stored: true)
        let result = image.map { NSImage(cgImage: $0, size: NSSize(width: size, height: size)) }
        cache.updateValue(result, forKey: key)
        return result
    }

    static func importImage(_ id: String, from url: URL) throws {
        let key = Data(id.utf8)
        let destination = try iconURL(key)
        guard url.isFileURL, (url.host ?? "").isEmpty || url.host == "localhost" else {
            throw CocoaError(.fileReadUnsupportedScheme)
        }
        let source = url.resolvingSymlinksInPath()
        guard try source.resourceValues(forKeys: [.volumeIsLocalKey]).volumeIsLocal == true else {
            throw CocoaError(.fileReadUnsupportedScheme)
        }
        let image = try decode(source, stored: false)
        let data = NSMutableData()
        guard let encoder = CGImageDestinationCreateWithData(data as CFMutableData, UTType.png.identifier as CFString, 1, nil) else {
            throw CocoaError(.fileWriteUnknown)
        }
        CGImageDestinationAddImage(encoder, image, nil)
        guard CGImageDestinationFinalize(encoder) else { throw CocoaError(.fileWriteUnknown) }
        try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        lock.lock()
        defer { lock.unlock() }
        try (data as Data).write(to: destination, options: .atomic)
        cache[key] = NSImage(cgImage: image, size: NSSize(width: size, height: size))
    }

    static func remove(_ id: String) throws {
        let key = Data(id.utf8)
        let url = try iconURL(key)
        lock.lock()
        defer { lock.unlock() }
        // unlink removes only this file, even if a directory unexpectedly occupies its path.
        if unlink(url.path) != 0 && errno != ENOENT {
            throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
        }
        cache.updateValue(nil, forKey: key)
    }

    private static func decode(_ url: URL, stored: Bool) throws -> CGImage {
        let limit = stored ? maxStoredBytes : maxImportBytes
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
        guard values.isRegularFile == true, let length = values.fileSize, (1...limit).contains(length) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        guard var data = try file.read(upToCount: length + 1), data.count == length else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let ico = !stored && data.starts(with: [0, 0, 1, 0])
        if ico { data = try selectIconFrame(data) }
        guard let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              let type = CGImageSourceGetType(source) as String?,
              !stored || type == UTType.png.identifier,
              completeImage(data, type: type, ico: ico),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0, width <= maxPixels / height else {
            throw CocoaError(.fileReadCorruptFile)
        }
        if stored {
            guard width == size, height == size,
                  let image = CGImageSourceCreateImageAtIndex(source, 0,
                      [kCGImageSourceShouldCacheImmediately: true] as CFDictionary),
                  CGImageSourceGetStatusAtIndex(source, 0) == .statusComplete else {
                throw CocoaError(.fileReadCorruptFile)
            }
            return image
        }

        let options: [CFString: Any] = [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                        kCGImageSourceCreateThumbnailWithTransform: true,
                                        kCGImageSourceThumbnailMaxPixelSize: size,
                                        kCGImageSourceShouldCacheImmediately: true]
        guard let thumb = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary),
              CGImageSourceGetStatusAtIndex(source, 0) == .statusComplete,
              let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8,
                                      bytesPerRow: size * 4, space: colorSpace,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let side = CGFloat(size)
        let scale = side / CGFloat(max(thumb.width, thumb.height))
        let w = max(1, (CGFloat(thumb.width) * scale).rounded())
        let h = max(1, (CGFloat(thumb.height) * scale).rounded())
        context.clear(CGRect(x: 0, y: 0, width: side, height: side))
        context.interpolationQuality = .high
        context.draw(thumb, in: CGRect(x: floor((side - w) / 2), y: floor((side - h) / 2), width: w, height: h))
        guard let result = context.makeImage() else { throw CocoaError(.fileReadCorruptFile) }
        return result
    }

    private static func completeImage(_ data: Data, type: String, ico: Bool) -> Bool {
        // ImageIO can display partial images. Check the format's end marker or declared file size.
        func littleSize(at offset: Int) -> Int {
            let value = data.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: offset, as: UInt32.self) }
            return Int(UInt32(littleEndian: value))
        }
        switch type {
        case UTType.png.identifier:
            return data.suffix(12).elementsEqual([0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130])
        case UTType.jpeg.identifier:
            return data.suffix(2).elementsEqual([0xff, 0xd9])
        case UTType.gif.identifier:
            return data.last == 0x3b
        case UTType.bmp.identifier:
            return data.count >= 26 && data.starts(with: "BM".utf8) && littleSize(at: 2) == data.count
        case UTType.webP.identifier:
            return data.count >= 20 && data.starts(with: "RIFF".utf8) && data[8..<12].elementsEqual("WEBP".utf8) &&
                littleSize(at: 4) == data.count - 8
        case UTType.ico.identifier:
            return ico
        default:
            return false
        }
    }

    // Bound the directory before ImageIO opens it, then pass only the frame closest to the output size.
    private static func selectIconFrame(_ data: Data) throws -> Data {
        let corrupt = CocoaError(.fileReadCorruptFile)
        func word(_ offset: Int) -> Int {
            Int(data[offset]) | (Int(data[offset + 1]) << 8)
        }
        func dword(_ offset: Int, bigEndian: Bool = false) -> Int {
            let value = data.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: offset, as: UInt32.self) }
            return Int(bigEndian ? UInt32(bigEndian: value) : UInt32(littleEndian: value))
        }
        func dimension(_ offset: Int) -> Int { data[offset] == 0 ? 256 : Int(data[offset]) }

        guard data.count >= 6 else { throw corrupt }
        let count = word(4), directoryEnd = 6 + count * 16
        guard (1...64).contains(count), directoryEnd <= data.count else { throw corrupt }
        var best = 6, bestRank = Int.max, bestDepth = -1
        for i in 0..<count {
            let entry = 6 + 16 * i, length = dword(entry + 8), start = dword(entry + 12)
            guard start >= directoryEnd, length > 0, start + length <= data.count else { throw corrupt }
            let side = min(dimension(entry), dimension(entry + 1))
            let rank = side >= size ? side - size : 256 + size - side
            let depth = word(entry + 6)
            if rank < bestRank || (rank == bestRank && depth > bestDepth) {
                (best, bestRank, bestDepth) = (entry, rank, depth)
            }
        }

        let start = dword(best + 12), length = dword(best + 8)
        let payload = data[start..<(start + length)]
        let width = dimension(best), height = dimension(best + 1)
        if payload.starts(with: [137, 80, 78, 71, 13, 10, 26, 10]) {
            guard length >= 33, data[(start + 12)..<(start + 16)].elementsEqual("IHDR".utf8),
                  dword(start + 16, bigEndian: true) == width, dword(start + 20, bigEndian: true) == height,
                  payload.suffix(12).elementsEqual([0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130]) else { throw corrupt }
        } else {
            guard length >= 40, [40, 108, 124].contains(dword(start)),
                  dword(start + 4) == width, dword(start + 8) == height * 2 else { throw corrupt }
        }
        var single = Data([0, 0, 1, 0, 1, 0])
        single.append(contentsOf: data[best..<(best + 12)])
        single.append(contentsOf: [22, 0, 0, 0])
        single.append(contentsOf: payload)
        return single
    }
}
