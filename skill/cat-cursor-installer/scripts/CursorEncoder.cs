using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;

public static class CursorEncoder
{
    static byte[] Dib(Bitmap bitmap)
    {
        int n = bitmap.Width, stride = ((n + 31) / 32) * 4;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(40); writer.Write(n); writer.Write(n * 2);
            writer.Write((ushort)1); writer.Write((ushort)32);
            for (int i = 0; i < 6; i++) writer.Write(0);
            var mask = new byte[stride * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                Color pixel = bitmap.GetPixel(x, n - y - 1);
                writer.Write(pixel.B); writer.Write(pixel.G); writer.Write(pixel.R); writer.Write(pixel.A);
                if (pixel.A == 0) mask[y * stride + x / 8] |= (byte)(128 >> (x % 8));
            }
            writer.Write(mask); return stream.ToArray();
        }
    }
    static Bitmap Resize(Bitmap source, Rectangle crop, int size)
    {
        var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        float scale = (size - 2f) / Math.Max(crop.Width, crop.Height);
        using (var graphics = Graphics.FromImage(image))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(1, 1, (int)Math.Round(crop.Width * scale), (int)Math.Round(crop.Height * scale)), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, attributes);
        }
        return image;
    }
    public static void Convert(string sourcePath, string cursorPath, int hotX, int hotY)
    {
        using (var source = new Bitmap(sourcePath))
        {
            if (hotX < 0 || hotY < 0 || hotX >= source.Width || hotY >= source.Height || source.GetPixel(hotX, hotY).A < 80)
                throw new ArgumentException("热点必须位于图片非透明轮廓内。");
            int left = source.Width, top = source.Height, right = -1, bottom = -1;
            bool transparent = false;
            for (int y = 0; y < source.Height; y++) for (int x = 0; x < source.Width; x++)
            {
                int alpha = source.GetPixel(x, y).A;
                if (alpha < 5) transparent = true;
                if (alpha >= 80) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            }
            if (!transparent || right < left) throw new ArgumentException("需要带真实透明背景的 PNG。");
            var crop = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            int[] sizes = { 32, 48, 64, 96 };
            var frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++) using (var image = Resize(source, crop, sizes[i])) frames[i] = Dib(image);
            using (var writer = new BinaryWriter(File.Create(cursorPath)))
            {
                writer.Write((ushort)0); writer.Write((ushort)2); writer.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int n = sizes[i]; float scale = (n - 2f) / Math.Max(crop.Width, crop.Height);
                    writer.Write((byte)n); writer.Write((byte)n); writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((ushort)(1 + Math.Round((hotX - left) * scale)));
                    writer.Write((ushort)(1 + Math.Round((hotY - top) * scale)));
                    writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
                }
                foreach (var frame in frames) writer.Write(frame);
            }
        }
    }
    public static void Icon(string sourcePath, string output)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        using (var source = new Bitmap(sourcePath))
        using (var writer = new BinaryWriter(File.Create(output)))
        {
            var frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++) using (var image = Resize(source, new Rectangle(0, 0, source.Width, source.Height), sizes[i])) frames[i] = Dib(image);
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte n = sizes[i] == 256 ? (byte)0 : (byte)sizes[i];
                writer.Write(n); writer.Write(n); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
    }
}
