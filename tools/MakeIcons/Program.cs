using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Turns the logo artwork into the assets the app needs:
//   Assets/logo.png  — background removed, trimmed, square
//   Assets/app.ico   — multi-resolution icon for the exe, taskbar and tray
//
// Re-run this whenever the logo changes:
//   dotnet run --project tools/MakeIcons -- <source-image>

internal static class Program
{
    private static readonly int[] IconSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: MakeIcons <source-image> [output-assets-dir]");
            return 2;
        }

        var source = args[0];
        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"Source image not found: {source}");
            return 2;
        }

        var assetsDir = args.Length > 1
            ? args[1]
            : Path.Combine("src", "ProjectSoundboard.App", "Assets");

        Directory.CreateDirectory(assetsDir);

        using var original = new Bitmap(source);
        Console.WriteLine($"Source: {original.Width}x{original.Height}");

        using var cutOut = RemoveFlatBackground(original);
        using var trimmed = TrimAndSquare(cutOut, marginFraction: 0.03f);

        var logoPath = Path.Combine(assetsDir, "logo.png");
        trimmed.Save(logoPath, ImageFormat.Png);
        Console.WriteLine($"Wrote {logoPath} ({trimmed.Width}x{trimmed.Height})");

        var icoPath = Path.Combine(assetsDir, "app.ico");
        WriteIcon(trimmed, icoPath, IconSizes);
        Console.WriteLine($"Wrote {icoPath} ({string.Join(", ", IconSizes)} px)");

        return 0;
    }

    /// <summary>
    /// Make the flat backdrop transparent.
    ///
    /// A blanket "delete every white pixel" would punch holes straight through the
    /// microphone body and the white lettering, so instead this flood fills inwards from
    /// the border: only background that is actually connected to the edge is removed, and
    /// enclosed light areas are left alone.
    /// </summary>
    private static Bitmap RemoveFlatBackground(Bitmap source)
    {
        var width = source.Width;
        var height = source.Height;

        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var pixels = ReadPixels(source, out var stride);

        var isBackground = new bool[width * height];
        var queue = new Queue<int>();

        void Seed(int x, int y)
        {
            var index = y * width + x;
            if (isBackground[index]) return;
            if (!LooksLikeBackdrop(pixels, stride, x, y)) return;

            isBackground[index] = true;
            queue.Enqueue(index);
        }

        for (var x = 0; x < width; x++) { Seed(x, 0); Seed(x, height - 1); }
        for (var y = 0; y < height; y++) { Seed(0, y); Seed(width - 1, y); }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var cx = index % width;
            var cy = index / width;

            if (cx > 0) Seed(cx - 1, cy);
            if (cx < width - 1) Seed(cx + 1, cy);
            if (cy > 0) Seed(cx, cy - 1);
            if (cy < height - 1) Seed(cx, cy + 1);
        }

        var output = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var index = y * width + x;

                var b = pixels[offset];
                var g = pixels[offset + 1];
                var r = pixels[offset + 2];

                byte alpha;

                if (isBackground[index])
                {
                    alpha = 0;
                }
                else if (TouchesBackground(isBackground, width, height, x, y))
                {
                    // Feather the seam. The artwork was flattened onto the backdrop, so the
                    // outermost ring of kept pixels is part background — fading it by how
                    // washed out it is avoids a hard white halo at small icon sizes.
                    var lightness = Math.Min(r, Math.Min(g, b));
                    var washed = Math.Clamp((lightness - 200f) / 55f, 0f, 1f);
                    alpha = (byte)(255 * (1 - washed));
                }
                else
                {
                    alpha = 255;
                }

                output[offset] = b;
                output[offset + 1] = g;
                output[offset + 2] = r;
                output[offset + 3] = alpha;
            }
        }

        WritePixels(result, output, stride);
        return result;
    }

    /// <summary>
    /// Already transparent, or near-white and near-neutral — the flat card the logo was
    /// rendered onto. Handling existing alpha means a source that is already cut out passes
    /// through this step unharmed instead of being treated as opaque white.
    /// </summary>
    private static bool LooksLikeBackdrop(byte[] pixels, int stride, int x, int y)
    {
        var offset = y * stride + x * 4;

        if (pixels[offset + 3] <= 8) return true;

        int b = pixels[offset];
        int g = pixels[offset + 1];
        int r = pixels[offset + 2];

        var lightest = Math.Max(r, Math.Max(g, b));
        var darkest = Math.Min(r, Math.Min(g, b));

        return darkest >= 232 && lightest - darkest <= 14;
    }

    private static bool TouchesBackground(bool[] isBackground, int width, int height, int x, int y)
    {
        if (x > 0 && isBackground[y * width + x - 1]) return true;
        if (x < width - 1 && isBackground[y * width + x + 1]) return true;
        if (y > 0 && isBackground[(y - 1) * width + x]) return true;
        if (y < height - 1 && isBackground[(y + 1) * width + x]) return true;
        return false;
    }

    /// <summary>
    /// Crop away the empty margin, then pad back out to a square. Icons are square, and the
    /// original artwork has a lot of dead space that would otherwise shrink the logo to a
    /// smudge at 16 px.
    /// </summary>
    private static Bitmap TrimAndSquare(Bitmap source, float marginFraction)
    {
        var pixels = ReadPixels(source, out var stride);

        int minX = source.Width, minY = source.Height, maxX = -1, maxY = -1;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (pixels[y * stride + x * 4 + 3] <= 8) continue;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0) return (Bitmap)source.Clone(); // fully transparent — nothing to trim

        var contentWidth = maxX - minX + 1;
        var contentHeight = maxY - minY + 1;

        var side = (int)(Math.Max(contentWidth, contentHeight) * (1 + marginFraction * 2));

        var square = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(square);

        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        graphics.DrawImage(
            source,
            new Rectangle((side - contentWidth) / 2, (side - contentHeight) / 2, contentWidth, contentHeight),
            new Rectangle(minX, minY, contentWidth, contentHeight),
            GraphicsUnit.Pixel);

        return square;
    }

    /// <summary>
    /// Write a multi-resolution .ico.
    ///
    /// Every frame is an uncompressed DIB. PNG-compressed frames are the modern convention
    /// and would cut the file from roughly 370 KB to 40 KB, but <see cref="Icon"/> — which is
    /// what the WinForms tray icon uses — cannot decode them and throws or renders noise.
    /// The icon is embedded in the executable once, so correctness beats the size saving.
    /// </summary>
    private static void WriteIcon(Bitmap source, string path, int[] sizes)
    {
        var frames = new List<byte[]>();

        foreach (var size in sizes)
        {
            using var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, size, size));
            }

            frames.Add(EncodeDib(scaled));
        }

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write((short)0);            // reserved
        writer.Write((short)1);            // type: icon
        writer.Write((short)sizes.Length);

        var offset = 6 + 16 * sizes.Length;

        for (var i = 0; i < sizes.Length; i++)
        {
            // 256 is encoded as 0 in the single-byte dimension fields.
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0);         // palette size
            writer.Write((byte)0);         // reserved
            writer.Write((short)1);        // colour planes
            writer.Write((short)32);       // bits per pixel
            writer.Write(frames[i].Length);
            writer.Write(offset);

            offset += frames[i].Length;
        }

        foreach (var frame in frames) writer.Write(frame);
    }

    /// <summary>
    /// Encode a bitmap as the DIB payload an .ico entry expects: a BITMAPINFOHEADER whose
    /// height is doubled to cover the AND mask, then bottom-up BGRA rows, then the mask
    /// itself. Alpha does the real work, so the mask is left fully opaque.
    /// </summary>
    private static byte[] EncodeDib(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;

        var pixels = ReadPixels(bitmap, out var stride);

        var maskStride = (width + 31) / 32 * 4;
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);

        writer.Write(40);                       // biSize
        writer.Write(width);                    // biWidth
        writer.Write(height * 2);               // biHeight — colour data plus mask
        writer.Write((short)1);                 // biPlanes
        writer.Write((short)32);                // biBitCount
        writer.Write(0);                        // biCompression = BI_RGB
        // biSizeImage covers the colour data only. Including the mask here makes
        // System.Drawing.Icon over-read and throw when it decodes the frame.
        writer.Write(width * height * 4);
        writer.Write(0);                        // biXPelsPerMeter
        writer.Write(0);                        // biYPelsPerMeter
        writer.Write(0);                        // biClrUsed
        writer.Write(0);                        // biClrImportant

        // Colour rows, bottom-up.
        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                writer.Write(pixels[offset]);       // B
                writer.Write(pixels[offset + 1]);   // G
                writer.Write(pixels[offset + 2]);   // R
                writer.Write(pixels[offset + 3]);   // A
            }
        }

        // AND mask, bottom-up. Zero means "show the colour pixel"; the alpha channel above
        // is what actually produces transparency on every Windows version we target.
        for (var y = 0; y < height; y++)
        {
            for (var i = 0; i < maskStride; i++) writer.Write((byte)0);
        }

        writer.Flush();
        return output.ToArray();
    }

    private static byte[] ReadPixels(Bitmap bitmap, out int stride)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            stride = data.Stride;
            var bytes = new byte[data.Stride * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void WritePixels(Bitmap bitmap, byte[] bytes, int stride)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, Math.Min(bytes.Length, data.Stride * bitmap.Height));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
