using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace SE2OnlineInstaller;

internal static class Screenshot
{
    /// <summary>Renders the window to <paramref name="path"/> once the release check has had time to finish, then closes it.</summary>
    public static async Task TakeAsync(MainWindow window, string path)
    {
        await Task.Delay(4000);
        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(window);
        bitmap.Save(path);
        window.Close();
    }
}
