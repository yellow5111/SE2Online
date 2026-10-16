using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Y51_SE2Online.Core;

namespace Y51_SE2Online.SyncEngine;

/// <summary>Country flags shipped in Content/Textures/GUI/Icons/Countries, for the server and session lists.</summary>
internal static class CountryFlags
{
    // SE2's Avalonia backend cannot decode image files (LoadBitmap only takes its own resource handles),
    // but it does upload writeable bitmaps, so the PNG is decoded here.
    private static readonly Dictionary<string, WriteableBitmap> Cache = new();
    private static bool _warned;

    public static IImage Get(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 2 || PluginPaths.ContentDir is not { } content) return null;
        code = code.ToLowerInvariant();
        if (Cache.TryGetValue(code, out var cached)) return cached;
        WriteableBitmap bitmap = null;
        string file = Path.Combine(content, "Textures", "GUI", "Icons", "Countries", code + ".png");
        try
        {
            if (File.Exists(file))
            {
                var (width, height, rgba) = Countries.DecodePng(File.ReadAllBytes(file));
                bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
                // SE2's RuntimeBitmapsImpl reports RowBytes as width * 32 (bits) but reads the buffer back tightly packed,
                // as R, G, B, A whatever the format, into a straight-alpha PNG: so packed, unpremultiplied RGBA.
                using var frame = bitmap.Lock();
                Marshal.Copy(rgba, 0, frame.Address, rgba.Length);
            }
        }
        catch (Exception ex) when (!_warned)
        {
            _warned = true;
            GameLog.Instance.Warn($"country flags: {file} could not be shown ({ex.GetType().Name}: {ex.Message})");
        }
        catch (Exception) { }
        Cache[code] = bitmap;
        return bitmap;
    }

    /// <summary>A flag sized for a list row or the details panel, or an empty spacer for an unknown country.</summary>
    public static Control Icon(string code, double height = 26)
    {
        var image = new Image { Source = Get(code), Height = height, Width = height * 1.5, Stretch = Stretch.Uniform, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(code)) ToolTip.SetTip(image, Countries.Name(code));
        return image;
    }
}
