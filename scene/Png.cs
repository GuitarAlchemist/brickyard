using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

// An RGB PNG from a swapchain image's bytes, BGRA or RGBA.
internal static class Png
{
    private static readonly uint[] Table = MakeTable();

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
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
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
