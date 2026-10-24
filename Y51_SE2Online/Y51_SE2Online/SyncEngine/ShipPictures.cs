using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Y51_SE2Online.Core;
using Y51_SE2Online.Sync;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// Pictures for the spawn cards: a respawn ship's first screenshot (*.jpg) in Content\Ships\&lt;Preview&gt;\screenshots, else
/// that folder's icon.png; a destination's planet or moon in Content\Textures\GUI\Planets\&lt;Body&gt;.jpg. Each is read and
/// decoded once, off the UI thread, then kept; a missing picture is remembered too.
/// </summary>
internal static class ShipPictures
{
    private sealed class Entry
    {
        public bool Done;
        public WriteableBitmap Bitmap;
        public readonly List<Action<WriteableBitmap>> Waiters = new();
    }

    // UI thread only.
    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UI thread: calls <paramref name="ready"/> with the picture (null when there is none), at once when it is
    /// already loaded, else once it is.</summary>
    public static void Get(string preview, Action<WriteableBitmap> ready) => Get("ship:", preview, ShipFile, ready);

    /// <summary>UI thread: a planet or moon picture, as <see cref="Get"/>.</summary>
    public static void GetBody(string body, Action<WriteableBitmap> ready) => Get("body:", body, BodyFile, ready);

    private static void Get(string kind, string name, Func<string, string> file, Action<WriteableBitmap> ready)
    {
        string safe = RespawnShips.SafePreview(name);
        if (safe.Length == 0) { ready(null); return; }
        string key = kind + safe;
        if (!Cache.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            Cache[key] = entry;
            System.Threading.Tasks.Task.Run(() => Decode(file(safe))).ContinueWith(t => Dispatcher.UIThread.Post(() => Finish(safe, entry, t)), System.Threading.Tasks.TaskScheduler.Default);
        }
        if (entry.Done) ready(entry.Bitmap);
        else entry.Waiters.Add(ready);
    }

    private static void Finish(string name, Entry entry, System.Threading.Tasks.Task<(int Width, int Height, byte[] Rgba)?> decoded)
    {
        try
        {
            if (decoded.IsCompletedSuccessfully && decoded.Result is { } image) entry.Bitmap = Bitmap(image.Width, image.Height, image.Rgba);
            else if (decoded.Exception != null) GameLog.Instance.WarnOnce("ship-picture-" + name, $"spawn: the picture of {name} could not be read ({decoded.Exception.GetBaseException().Message})");
        }
        catch (Exception ex) { GameLog.Instance.WarnOnce("ship-picture-" + name, $"spawn: the picture of {name} could not be shown ({ex.GetType().Name}: {ex.Message})"); }
        entry.Done = true;
        foreach (var waiter in entry.Waiters)
        {
            try { waiter(entry.Bitmap); }
            catch (Exception ex) { GameLog.Instance.WarnOnce("ship-picture-show", "spawn: showing a ship picture failed: " + ex.Message); }
        }
        entry.Waiters.Clear();
    }

    // Worker thread. Only SafePreview'd names reach these, and the file must stay inside its folder under Content.
    private static string ShipFile(string name)
    {
        string folder = Inside(Path.Combine("Ships", name), "Ships");
        if (folder == null || !Directory.Exists(folder)) return null;
        string shots = Path.Combine(folder, "screenshots");
        return (Directory.Exists(shots) ? Directory.EnumerateFiles(shots, "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null)
            ?? Path.Combine(folder, "icon.png");
    }

    private static string BodyFile(string name) => Inside(Path.Combine("Textures", "GUI", "Planets", name + ".jpg"), Path.Combine("Textures", "GUI", "Planets"));

    private static string Inside(string relative, string root)
    {
        if (PluginPaths.ContentDir is not { } content) return null;
        string bound = Path.GetFullPath(Path.Combine(content, root));
        string full = Path.GetFullPath(Path.Combine(content, relative));
        return full.StartsWith(bound + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static (int Width, int Height, byte[] Rgba)? Decode(string file)
    {
        if (file == null || !File.Exists(file)) return null;
        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(File.ReadAllBytes(file));
        // The preview panel is at most about 1000 px wide even at 4K; a full 1080p picture would keep 8 MB per ship.
        if (image.Width > 1280) SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(image, x => SixLabors.ImageSharp.Processing.ResizeExtensions.Resize(x, 1280, 0));
        var rgba = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(rgba);
        return (image.Width, image.Height, rgba);
    }

    // SE2's Avalonia backend only shows bitmaps written at runtime, read back as tightly packed straight-alpha RGBA (see CountryFlags).
    private static WriteableBitmap Bitmap(int width, int height, byte[] rgba)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var frame = bitmap.Lock();
        Marshal.Copy(rgba, 0, frame.Address, rgba.Length);
        return bitmap;
    }
}
