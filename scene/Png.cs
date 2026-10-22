using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

// An RGB PNG from a swapchain image's bytes, BGRA or RGBA, and RGBA bytes from the splash's logo.
internal static class Png
{
    private static readonly uint[] Table = MakeTable();

    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    // RGBA bytes from an 8-bit RGB or RGBA PNG without interlacing, which is what BRUTAL's logo is.
    public static byte[] Load(string path, out int width, out int height)
    {
        var file = File.ReadAllBytes(path);
        if (!file.AsSpan().StartsWith(Signature))
        {
            throw new InvalidDataException($"{path} is not a PNG");
        }

        width = height = 0;
        var channels = 0;
        using var packed = new MemoryStream();
        for (var at = 8; at + 12 <= file.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
            var type = Encoding.ASCII.GetString(file, at + 4, 4);
            var data = file.AsSpan(at + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                channels = data[9] switch { 2 => 3, 6 => 4, _ => 0 };
                if (data[8] != 8 || channels == 0 || data[12] != 0)
                {
                    throw new InvalidDataException($"{path}: only 8-bit RGB or RGBA without interlacing is read");
                }
            }
            else if (type == "IDAT")
            {
                packed.Write(data);
            }
            else if (type == "IEND")
            {
                break;
            }

            at += 12 + length;
        }

        var stride = width * channels;
        var raw = new byte[height * (stride + 1)];
        packed.Position = 0;
        using (var z = new ZLibStream(packed, CompressionMode.Decompress))
        {
            z.ReadExactly(raw);
        }

        // Each row starts with its filter, which predicts a byte from the one to its left (a), above (b) and above left (c).
        var rgba = new byte[width * height * 4];
        var line = new byte[stride];
        var prior = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            raw.AsSpan((y * (stride + 1)) + 1, stride).CopyTo(line);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? line[i - channels] : 0, b = prior[i], c = i >= channels ? prior[i - channels] : 0;
                var predicted = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"{path}: row {y} has unknown filter {filter}"),
                };
                line[i] = (byte)(line[i] + predicted);
            }

            for (var x = 0; x < width; x++)
            {
                var to = ((y * width) + x) * 4;
                line.AsSpan(x * channels, 3).CopyTo(rgba.AsSpan(to));
                rgba[to + 3] = channels == 4 ? line[(x * 4) + 3] : (byte)255;
            }

            (line, prior) = (prior, line);
        }

        return rgba;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static void Save(string path, ReadOnlySpan<byte> pixels, int width, int height, bool bgra)
    {
        var stride = 1 + (width * 3);
        var raw = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var from = ((y * width) + x) * 4;
                var to = (y * stride) + 1 + (x * 3);
                raw[to] = pixels[from + (bgra ? 2 : 0)];
                raw[to + 1] = pixels[from + 1];
                raw[to + 2] = pixels[from + (bgra ? 0 : 2)];
            }
        }

        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bits per channel
        header[9] = 2; // RGB
        using var file = File.Create(path);
        file.Write(Signature);
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", packed.GetBuffer().AsSpan(0, (int)packed.Length));
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        s.Write(four);
        var name = Encoding.ASCII.GetBytes(type);
        s.Write(name);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(four, Crc(Crc(0xFFFFFFFFu, name), data) ^ 0xFFFFFFFFu);
        s.Write(four);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
