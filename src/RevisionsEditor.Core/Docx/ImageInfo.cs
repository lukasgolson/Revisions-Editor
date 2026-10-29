using System.Buffers.Binary;

namespace RevisionsEditor.Core.Docx;

/// <summary>Pixel size and resolution of a PNG, JPEG, GIF, BMP or TIFF image, read from its header.</summary>
public sealed record ImageInfo(int Width, int Height, double DpiX, double DpiY, string Extension, string ContentType)
{
    public static ImageInfo? TryRead(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var header = new byte[Math.Min(fs.Length, 512 * 1024)];
            int read = fs.Read(header, 0, header.Length);
            return TryRead(header.AsSpan(0, read));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ImageInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ReadPng(data);
        }
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return ReadJpeg(data);
        }
        if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
        {
            return new ImageInfo(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]), 96, 96, "gif", "image/gif");
        }
        if (data.Length >= 46 && data[0] == 'B' && data[1] == 'M')
        {
            int w = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
            int h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..]));
            int ppmX = BinaryPrimitives.ReadInt32LittleEndian(data[38..]);
            int ppmY = BinaryPrimitives.ReadInt32LittleEndian(data[42..]);
            return new ImageInfo(w, h, ppmX > 0 ? ppmX * 0.0254 : 96, ppmY > 0 ? ppmY * 0.0254 : 96, "bmp", "image/bmp");
        }
        if (data.Length >= 8 && ((data[0] == 'I' && data[1] == 'I') || (data[0] == 'M' && data[1] == 'M')))
        {
            return ReadTiff(data);
        }
        return null;
    }

    private static ImageInfo ReadPng(ReadOnlySpan<byte> data)
    {
        int w = BinaryPrimitives.ReadInt32BigEndian(data[16..]);
        int h = BinaryPrimitives.ReadInt32BigEndian(data[20..]);
        double dpiX = 96, dpiY = 96;
        int pos = 8;
        while (pos + 12 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data[pos..]);
            var type = data.Slice(pos + 4, 4);
            if (type.SequenceEqual("pHYs"u8) && pos + 8 + 9 <= data.Length)
            {
                uint ppuX = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 8)..]);
                uint ppuY = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 12)..]);
                if (data[pos + 16] == 1 && ppuX > 0 && ppuY > 0)
                {
                    dpiX = ppuX * 0.0254;
                    dpiY = ppuY * 0.0254;
                }
                break;
            }
            if (type.SequenceEqual("IDAT"u8) || length < 0)
            {
                break;
            }
            pos += 12 + length;
        }
        return new ImageInfo(w, h, dpiX, dpiY, "png", "image/png");
    }

    private static ImageInfo? ReadJpeg(ReadOnlySpan<byte> data)
    {
        double dpiX = 96, dpiY = 96;
        int pos = 2;
        while (pos + 9 < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                pos++;
                continue;
            }
            byte marker = data[pos + 1];
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7) || marker == 0xFF)
            {
                pos += marker == 0xFF ? 1 : 2;
                continue;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            if (marker == 0xE0 && pos + 18 < data.Length && data.Slice(pos + 4, 5).SequenceEqual("JFIF\0"u8))
            {
                byte units = data[pos + 11];
                int xd = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 12)..]);
                int yd = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 14)..]);
                if (xd > 0 && yd > 0 && units is 1 or 2)
                {
                    dpiX = units == 1 ? xd : xd * 2.54;
                    dpiY = units == 1 ? yd : yd * 2.54;
                }
            }
            bool isFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isFrame)
            {
                int h = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 5)..]);
                int w = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 7)..]);
                return new ImageInfo(w, h, dpiX, dpiY, "jpeg", "image/jpeg");
            }
            pos += 2 + length;
        }
        return null;
    }

    private static ImageInfo? ReadTiff(ReadOnlySpan<byte> data)
    {
        bool little = data[0] == 'I';

        int ifd = (int)U32(data, little, 4);
        if (ifd <= 0 || ifd + 2 > data.Length)
        {
            return null;
        }
        int count = U16(data, little, ifd);
        int w = 0, h = 0;
        for (int i = 0; i < count; i++)
        {
            int e = ifd + 2 + i * 12;
            if (e + 12 > data.Length)
            {
                break;
            }
            ushort tag = U16(data, little, e);
            ushort type = U16(data, little, e + 2);
            int value = type == 3 ? U16(data, little, e + 8) : (int)U32(data, little, e + 8);
            if (tag == 256) w = value;
            if (tag == 257) h = value;
        }
        return w > 0 && h > 0 ? new ImageInfo(w, h, 96, 96, "tiff", "image/tiff") : null;
    }

    private static uint U32(ReadOnlySpan<byte> data, bool little, int offset)
        => little ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private static ushort U16(ReadOnlySpan<byte> data, bool little, int offset)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
}
