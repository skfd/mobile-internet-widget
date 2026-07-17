using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Win32;

namespace MobileData.App;

/// <summary>
/// Colors and background art for the Adaptive Card, chosen for the current theme.
///
/// The Widgets Board themes its own chrome (the header row) but hands third-party
/// cards the *dark* Adaptive Cards host config regardless of the system theme. In
/// light mode that makes "default" text #F8F8F8 on a #F9F9F9 card and "warning"
/// a #FCE100 yellow — both invisible. So we never use the host's theme-dependent
/// colors: text is pinned to the "dark"/"light" enum values (the only two that
/// render predictably), and the usage bar and brand wash are painted with PNG
/// data URIs we generate ourselves.
/// </summary>
internal readonly record struct CardColors(
    string Text,
    string Muted,
    string Status,
    string Message,
    string BarFill,
    string BarTrack,
    string Background)
{
    /// <param name="barStyle">accent, warning, attention, or emphasis (no data).</param>
    /// <param name="brandHex">Carrier brand color, e.g. "#E87722" for Fido.</param>
    public static CardColors For(string barStyle, string brandHex)
    {
        var light = IsLightTheme();

        // On a light card the host's accent/warning/attention are all under 2.5:1,
        // so light mode carries the status in the bar alone and keeps text black.
        var status = light
            ? "dark"
            : barStyle switch { "emphasis" => "light", _ => barStyle };

        return new CardColors(
            Text: light ? "dark" : "light",
            // For isSubtle text: the host renders "light"+subtle nearly black,
            // so dark mode uses "default"+subtle (a readable gray in the dark
            // host config; only its *non*-subtle #F8F8F8 is a light-mode trap).
            Muted: light ? "dark" : "default",
            Status: status,
            Message: light ? "dark" : "warning",
            BarFill: SolidPng(BarFillHex(barStyle, light)),
            BarTrack: SolidPng(light ? "#C9C9C9" : "#4A4A4A"),
            Background: BrandWashPng(brandHex));
    }

    private static string BarFillHex(string barStyle, bool light) => barStyle switch
    {
        "attention" => light ? "#C42B1C" : "#FF99A4",
        "warning" => light ? "#9D5D00" : "#FCE100",
        "accent" => light ? "#5B4FD6" : "#DB9EE5",
        _ => light ? "#C9C9C9" : "#4A4A4A",
    };

    private static bool IsLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
    }

    // ---- Generated card art -------------------------------------------------
    //
    // Adaptive Cards can't express a gradient, but backgroundImage takes a data
    // URI, so we encode the PNGs by hand. Everything here is a single scanline
    // the card tiles: 1px for the solid bar segments, 600px for the brand wash.

    /// <summary>Opaque 1x1 of #RRGGBB, tiled to fill a bar segment. Lets us pick the
    /// exact color the host's container styles won't give us in light mode.</summary>
    private static string SolidPng(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return PngUri($"solid:{hex}", new[] { r, g, b, (byte)255 });
    }

    /// <summary>The brand wash: the carrier color fading out left-to-right at low
    /// alpha, so it composites over whichever surface the board hands us.</summary>
    private static string BrandWashPng(string hex)
    {
        const int width = 600;
        const byte maxAlpha = 80;

        var (r, g, b) = ParseHex(hex);
        var row = new byte[width * 4];
        for (var x = 0; x < width; x++)
        {
            row[x * 4] = r;
            row[x * 4 + 1] = g;
            row[x * 4 + 2] = b;
            row[x * 4 + 3] = (byte)(maxAlpha * (1.0 - (double)x / (width - 1)));
        }
        return PngUri($"wash:{hex}", row);
    }

    private static (byte r, byte g, byte b) ParseHex(string hex) => (
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    private static readonly Dictionary<string, string> _cache = new();

    private static string PngUri(string cacheKey, byte[] rgbaRow)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            var uri = "data:image/png;base64," + Convert.ToBase64String(EncodePng(rgbaRow));
            _cache[cacheKey] = uri;
            return uri;
        }
    }

    /// <summary>Encodes a single RGBA scanline as a 1-pixel-tall PNG.</summary>
    private static byte[] EncodePng(byte[] rgbaRow)
    {
        // The scanline, prefixed with filter type 0 (none).
        var raw = new byte[1 + rgbaRow.Length];
        rgbaRow.CopyTo(raw, 1);

        // zlib header, one stored (uncompressed) deflate block, Adler-32.
        var idat = new List<byte> { 0x78, 0x01, 0x01 };
        idat.AddRange(new[] { (byte)raw.Length, (byte)(raw.Length >> 8) });          // LEN
        idat.AddRange(new[] { (byte)~raw.Length, (byte)(~raw.Length >> 8) });        // NLEN
        idat.AddRange(raw);
        idat.AddRange(BigEndian(Adler32(raw)));

        var width = rgbaRow.Length / 4;
        var ihdr = new List<byte>();
        ihdr.AddRange(BigEndian((uint)width));
        ihdr.AddRange(BigEndian(1));
        ihdr.AddRange(new byte[] { 8, 6, 0, 0, 0 });  // 8-bit, color type 6 (RGBA), no interlace

        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        png.AddRange(Chunk("IHDR", ihdr.ToArray()));
        png.AddRange(Chunk("IDAT", idat.ToArray()));
        png.AddRange(Chunk("IEND", Array.Empty<byte>()));
        return png.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var typed = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);

        var chunk = new List<byte>();
        chunk.AddRange(BigEndian((uint)data.Length));
        chunk.AddRange(typed);
        chunk.AddRange(BigEndian(Crc32(typed)));
        return chunk.ToArray();
    }

    private static byte[] BigEndian(uint value) =>
        new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static readonly uint[] _crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in data)
            c = _crcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
