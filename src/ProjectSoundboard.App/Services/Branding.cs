using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

/// <summary>
/// Loads the application artwork.
///
/// Every member degrades to null rather than throwing when the asset is missing, so the app
/// still builds and runs with an empty Assets folder — the UI falls back to a drawn glyph.
/// Regenerate the assets with: <c>dotnet run --project tools/MakeIcons -- &lt;logo.png&gt;</c>
/// </summary>
public static class Branding
{
    private const string LogoResource = "Assets/logo.png";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Lazy<ImageSource?> LazyLogo = new(LoadLogo);
    private static readonly Lazy<byte[]?> LazyLogoBytes = new(LoadLogoBytes);

    /// <summary>The logo for in-app use, or null when the asset has not been added yet.</summary>
    public static ImageSource? Logo => LazyLogo.Value;

    public static bool HasLogo => Logo is not null;

    private static ImageSource? LoadLogo()
    {
        var bytes = LazyLogoBytes.Value;
        if (bytes is null) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn($"Logo could not be decoded: {ex.Message}");
            return null;
        }
    }

    private static byte[]? LoadLogoBytes()
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/{LogoResource}", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info is null) return null;

            using var stream = info.Stream;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (IOException)
        {
            // No logo asset in this build — expected, and handled by the callers.
            Log.Debug("No logo asset present; using the drawn fallback glyph.");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Logo resource could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Build a tray icon at the size Windows actually wants. Rendering from the PNG avoids
    /// shipping a second copy of the .ico purely so the tray can read it.
    /// Returns null when there is no logo; the caller then falls back to a stock icon.
    /// </summary>
    public static System.Drawing.Icon? CreateTrayIcon(int size)
    {
        var bytes = LazyLogoBytes.Value;
        if (bytes is null) return null;

        var handle = IntPtr.Zero;

        try
        {
            using var stream = new MemoryStream(bytes);
            using var source = new System.Drawing.Bitmap(stream);
            using var scaled = new System.Drawing.Bitmap(size, size,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (var graphics = System.Drawing.Graphics.FromImage(scaled))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, size, size));
            }

            // GetHicon hands back a handle we own; clone into a managed Icon and release it
            // immediately so the process does not leak a GDI object.
            handle = scaled.GetHicon();
            using var unowned = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)unowned.Clone();
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray icon could not be built from the logo: {ex.Message}");
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) DestroyIcon(handle);
        }
    }
}
