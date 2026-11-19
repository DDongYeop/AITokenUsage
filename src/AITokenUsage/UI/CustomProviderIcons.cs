using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AITokenUsage.UI;

public static class CustomProviderIcons
{
    private const int Size = 96;
    private const int MaxImportBytes = 16 * 1024 * 1024;
    private const int MaxStoredBytes = 128 * 1024;
    private const long MaxPixels = 32_000_000;
    private static readonly UTF8Encoding ExactUtf8 = new(false, true);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);

    private static string IconPath(string providerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        string key = Convert.ToHexStringLower(SHA256.HashData(ExactUtf8.GetBytes(providerId)));
        return Path.Combine(AppPaths.LocalDirectory, "provider-icons", key + ".png");
    }

    public static ImageSource? Load(string providerId)
    {
        if (string.IsNullOrEmpty(providerId)) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(providerId, out var image)) return image;
            try
            {
                image = Decode(IconPath(providerId), stored: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or FormatException or NotSupportedException or InvalidOperationException
                or System.Runtime.InteropServices.COMException or InvalidDataException)
            {
                image = null;
            }
            return Cache[providerId] = image;
        }
    }

    public static void Import(string providerId, string sourcePath)
    {
        string destination = IconPath(providerId);
        string source = Path.GetFullPath(sourcePath);
        if (source.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(source)!).DriveType == DriveType.Network)
            throw new IOException("Choose an image on a local drive.");

        var image = Decode(source, stored: false);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                encoder.Save(file);

            lock (Gate)
            {
                File.Move(temporary, destination, overwrite: true);
                Cache[providerId] = image;
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static void Remove(string providerId)
    {
        string path = IconPath(providerId);
        lock (Gate)
        {
            try { File.Delete(path); }
            catch (DirectoryNotFoundException) { }
            Cache[providerId] = null;
        }
    }

    private static BitmapSource Decode(string path, bool stored)
    {
        byte[] data;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            if (file.Length <= 0 || file.Length > (stored ? MaxStoredBytes : MaxImportBytes))
                throw new InvalidDataException("Image file is empty or too large.");
            data = new byte[checked((int)file.Length)];
            file.ReadExactly(data);
        }

        bool ico = !stored && data.AsSpan().StartsWith(new byte[] { 0, 0, 1, 0 });
        bool gif = !stored && (data.AsSpan().StartsWith("GIF87a"u8) || data.AsSpan().StartsWith("GIF89a"u8));
        if (ico) data = SelectIconFrame(data);
        if (gif) data = FirstGifFrame(data);
        using var stream = new MemoryStream(data, writable: false);
        const BitmapCreateOptions options = BitmapCreateOptions.IgnoreColorProfile;
        const BitmapCacheOption cache = BitmapCacheOption.OnDemand;
        BitmapDecoder decoder;
        if (ico)
            decoder = new IconBitmapDecoder(stream, options, cache);
        else if (gif)
            decoder = new GifBitmapDecoder(stream, options, cache);
        else if (!stored && data.Length >= 26 && data.AsSpan().StartsWith("BM"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(2)) == data.Length)
            decoder = new BmpBitmapDecoder(stream, options, cache);
        else if (!stored && data.Length >= 20 && data.AsSpan().StartsWith("RIFF"u8) &&
            data.AsSpan(8, 4).SequenceEqual("WEBP"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) == data.Length - 8)
            decoder = BitmapDecoder.Create(stream, options, cache); // Installed WIC WebP codec.
        else if (data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data.AsSpan().EndsWith(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }))
            decoder = new PngBitmapDecoder(stream, options, cache);
        else if (!stored && data.AsSpan().StartsWith(new byte[] { 0xff, 0xd8 }) &&
            data.AsSpan().EndsWith(new byte[] { 0xff, 0xd9 }))
            decoder = new JpegBitmapDecoder(stream, options, cache);
        else
            throw new InvalidDataException("Expected a complete PNG, JPEG, ICO, BMP, GIF, or WebP image.");

        BitmapSource bitmap = decoder.Frames[0];
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels ||
            (stored && (width != Size || height != Size)))
            throw new InvalidDataException("Unsupported icon dimensions.");

        double scale = (double)Size / Math.Max(width, height);
        int w = Math.Max(1, (int)Math.Round(width * scale));
        int h = Math.Max(1, (int)Math.Round(height * scale));
        if (width != w || height != h)
            bitmap = new TransformedBitmap(bitmap, new ScaleTransform((double)w / width, (double)h / height));

        // Detach all pixels from the decoder and discard source metadata. Empty margins stay transparent.
        bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        const int stride = Size * 4;
        byte[] pixels = new byte[Size * stride];
        int offset = (Size - bitmap.PixelHeight) / 2 * stride + (Size - bitmap.PixelWidth) / 2 * 4;
        bitmap.CopyPixels(System.Windows.Int32Rect.Empty, pixels, stride, offset);
        var result = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    // Give WIC just one frame. Validate its embedded dimensions before the codec can allocate a bitmap.
    private static byte[] SelectIconFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6) throw new InvalidDataException("Incomplete icon header.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        int directoryEnd = 6 + count * 16;
        if (count is < 1 or > 64 || directoryEnd > data.Length)
            throw new InvalidDataException("Unsupported icon directory.");

        int best = 0, bestRank = int.MaxValue, bestDepth = -1;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[(entry + 8)..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data[(entry + 12)..]);
            if (offset < directoryEnd || offset > data.Length || length == 0 || length > data.Length - offset)
                throw new InvalidDataException("Invalid icon frame bounds.");
            int side = Math.Min(IconDimension(data[entry]), IconDimension(data[entry + 1]));
            int rank = side >= Size ? side - Size : 256 + Size - side;
            int depth = BinaryPrimitives.ReadUInt16LittleEndian(data[(entry + 6)..]);
            if (rank < bestRank || (rank == bestRank && depth > bestDepth))
                (best, bestRank, bestDepth) = (entry, rank, depth);
        }

        int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(best + 12)..]);
        int bytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(best + 8)..]);
        var payload = data.Slice(start, bytes);
        int width = IconDimension(data[best]), height = IconDimension(data[best + 1]);
        bool valid = payload.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            ? payload.Length >= 33 && payload.Slice(12, 4).SequenceEqual("IHDR"u8) &&
              BinaryPrimitives.ReadUInt32BigEndian(payload[16..]) == width &&
              BinaryPrimitives.ReadUInt32BigEndian(payload[20..]) == height &&
              payload.EndsWith(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 })
            : payload.Length >= 40 && BinaryPrimitives.ReadUInt32LittleEndian(payload) is 40 or 108 or 124 &&
              BinaryPrimitives.ReadInt32LittleEndian(payload[4..]) == width &&
              BinaryPrimitives.ReadInt32LittleEndian(payload[8..]) == height * 2;
        if (!valid) throw new InvalidDataException("Invalid icon frame dimensions or format.");

        byte[] single = new byte[22 + bytes];
        data[..4].CopyTo(single);
        single[4] = 1;
        data.Slice(best, 16).CopyTo(single.AsSpan(6));
        BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(18), 22);
        payload.CopyTo(single.AsSpan(22));
        return single;
    }

    private static int IconDimension(byte value) => value == 0 ? 256 : value;

    // WPF's Frames collection can instantiate an object for every frame. End the stream after image one.
    private static byte[] FirstGifFrame(byte[] data)
    {
        if (data.Length < 14 || data[^1] != 0x3b) throw new InvalidDataException("Incomplete GIF.");
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        if (width == 0 || height == 0 || (long)width * height > MaxPixels)
            throw new InvalidDataException("Unsupported GIF dimensions.");
        static int PaletteBytes(byte flags) => (flags & 0x80) == 0 ? 0 : 3 << ((flags & 7) + 1);
        int pos = 13 + PaletteBytes(data[10]);
        while (pos < data.Length - 1)
        {
            byte marker = data[pos++];
            if (marker == 0x2c && pos + 9 < data.Length)
            {
                int w = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 4));
                int h = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 6));
                if (w == 0 || h == 0 || w + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos)) > width ||
                    h + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2)) > height) break;
                pos += 9 + PaletteBytes(data[pos + 8]) + 1; // descriptor, local palette, LZW minimum code size
            }
            else if (marker == 0x21) pos++; // extension label
            else break;
            while (pos < data.Length && data[pos] != 0) pos += 1 + data[pos];
            if (pos >= data.Length - 1) break;
            pos++;
            if (marker == 0x2c)
            {
                byte[] frame = data[..(pos + 1)];
                frame[pos] = 0x3b;
                return frame;
            }
        }
        throw new InvalidDataException("Invalid GIF frame.");
    }
}
