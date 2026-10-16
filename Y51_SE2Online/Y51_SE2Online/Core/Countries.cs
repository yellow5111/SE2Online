using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace Y51_SE2Online.Core;

/// <summary>Server country: a configured ISO 3166 code, or "auto" (the directory fills it in from the announcing IP).</summary>
public static class Countries
{
    public const string Auto = "auto";

    /// <summary>The code to publish: "" for auto, the upper-case code when it is a real country, null when invalid.</summary>
    public static string Normalize(string setting)
    {
        string value = setting?.Trim() ?? "";
        if (value.Length == 0 || value.Equals(Auto, StringComparison.OrdinalIgnoreCase)) return "";
        value = value.ToUpperInvariant();
        return IsCode(value) ? value : null;
    }

    public static bool IsCode(string code) => code is { Length: 2 } && char.IsAsciiLetterUpper(code[0]) && char.IsAsciiLetterUpper(code[1])
        && (code == "XK" || Name(code) != code);

    public static string Name(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        if (code == "XK") return "Kosovo";
        try { return new RegionInfo(code).EnglishName; }
        catch (ArgumentException) { return code; }
    }

    /// <summary>Decodes an 8-bit RGBA, non-interlaced PNG (the format the shipped flags use) to straight-alpha RGBA.</summary>
    public static (int Width, int Height, byte[] Rgba) DecodePng(byte[] png)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(signature)) throw new InvalidDataException("Not a PNG file.");
        int width = 0, height = 0;
        using var data = new MemoryStream();
        for (int at = 8; at + 8 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (length < 0 || at + 12 + length > png.Length) throw new InvalidDataException("Truncated PNG chunk.");
            var body = png.AsSpan(at + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                if (body[8] != 8 || body[9] != 6 || body[12] != 0) throw new InvalidDataException("Only 8-bit RGBA, non-interlaced PNGs are supported.");
                if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new InvalidDataException("PNG size out of range.");
            }
            else if (type == "IDAT") data.Write(body);
            else if (type == "IEND") break;
            at += 12 + length;
        }
        if (width == 0) throw new InvalidDataException("PNG has no header.");
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        data.Position = 0;
        using (var z = new ZLibStream(data, CompressionMode.Decompress)) z.ReadExactly(raw);
        var rgba = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[y * (stride + 1)];
            int src = y * (stride + 1) + 1, dst = y * stride, prev = dst - stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= 4 ? rgba[dst + x - 4] : 0, b = y > 0 ? rgba[prev + x] : 0, c = x >= 4 && y > 0 ? rgba[prev + x - 4] : 0;
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                int predictor = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c, _ => throw new InvalidDataException("Bad PNG filter.") };
                rgba[dst + x] = (byte)(raw[src + x] + predictor);
            }
        }
        return (width, height, rgba);
    }
}
