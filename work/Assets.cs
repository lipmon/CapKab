using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class Assets {
    public static void Make(string dir) {
        int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) {
            using (var image = DrawIcon(sizes[i]))
            using (var buffer = new MemoryStream()) {
                image.Save(buffer, ImageFormat.Png);
                images[i] = buffer.ToArray();
            }
        }
        using (var file = new BinaryWriter(File.Create(Path.Combine(dir, "capkab.ico")))) {
            file.Write((short)0);
            file.Write((short)1);
            file.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++) {
                byte dimension = sizes[i] == 256 ? (byte)0 : (byte)sizes[i];
                file.Write(dimension);
                file.Write(dimension);
                file.Write((short)0);
                file.Write((short)1);
                file.Write((short)32);
                file.Write(images[i].Length);
                file.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] image in images) file.Write(image);
        }
        using (var preview = DrawIcon(512)) preview.Save(Path.Combine(dir, "capkab-icon.png"), ImageFormat.Png);
        const int rate = 22050, samples = 2400;
        var random = new Random(39);
        using (var wave = new BinaryWriter(File.Create(Path.Combine(dir, "shutter.wav")))) {
            wave.Write(0x46464952); wave.Write(36 + samples * 2); wave.Write(0x45564157);
            wave.Write(0x20746d66); wave.Write(16); wave.Write((short)1); wave.Write((short)1);
            wave.Write(rate); wave.Write(rate * 2); wave.Write((short)2); wave.Write((short)16);
            wave.Write(0x61746164); wave.Write(samples * 2);
            double last = 0;
            for (int i = 0; i < samples; i++) {
                double t = i / (double)rate;
                double second = Math.Max(0, t - .039);
                double envelope = Math.Min(t / .0007, 1) * Math.Exp(-190 * t);
                if (t >= .039) envelope += .55 * Math.Min(second / .001, 1) * Math.Exp(-165 * second);
                last = .38 * last + .62 * (random.NextDouble() * 2 - 1);
                double tone = Math.Sin(2 * Math.PI * 570 * t) * Math.Exp(-155 * t);
                wave.Write((short)(4900 * (last * envelope + .27 * tone)));
            }
        }
    }

    static Bitmap DrawIcon(int size) {
        var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image)) {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(size / 64f, size / 64f);
            using (var square = new GraphicsPath()) {
                const float corner = 12;
                square.AddArc(2, 2, corner, corner, 180, 90);
                square.AddArc(50, 2, corner, corner, 270, 90);
                square.AddArc(50, 50, corner, corner, 0, 90);
                square.AddArc(2, 50, corner, corner, 90, 90);
                square.CloseFigure();
                using (var yellow = new SolidBrush(Color.FromArgb(255, 213, 58))) graphics.FillPath(yellow, square);
                using (var edge = new Pen(Color.FromArgb(231, 180, 33), 1)) graphics.DrawPath(edge, square);
            }

            Color ink = Color.FromArgb(35, 36, 32);
            using (var frame = new Pen(ink, 4) { LineJoin = LineJoin.Round, StartCap = LineCap.Square, EndCap = LineCap.Square }) {
                graphics.DrawLines(frame, new[] { new Point(25, 16), new Point(16, 16), new Point(16, 25) });
                graphics.DrawLines(frame, new[] { new Point(39, 16), new Point(48, 16), new Point(48, 25) });
                graphics.DrawLines(frame, new[] { new Point(16, 39), new Point(16, 48), new Point(25, 48) });
                graphics.DrawLines(frame, new[] { new Point(39, 48), new Point(48, 48), new Point(48, 39) });
                graphics.DrawEllipse(frame, 26, 26, 12, 12);
            }
        }
        return image;
    }
}
