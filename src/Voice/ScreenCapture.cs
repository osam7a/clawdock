using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Clawdock
{
    // A screenshot of the display a window is on, for quick voice. It's only ever held in memory.
    static class ScreenCapture
    {
        const int LongestSide = 1568; // Claude scales anything bigger down itself
        const long JpegQuality = 85;

        // The display that window is on, as a JPEG. hide (the notch) is left out of the picture if
        // this Windows can do that. Call it on the thread that owns hide.
        public static byte[] Jpeg(IntPtr window, IntPtr hide)
        {
            bool hidden = hide != IntPtr.Zero && Native.SetWindowDisplayAffinity(hide, Native.WDA_EXCLUDEFROMCAPTURE);
            try
            {
                if (hidden) System.Threading.Thread.Sleep(60); // it takes a frame to go
                var area = Screen.FromHandle(window).Bounds;
                using (var shot = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                    return Encode(shot);
                }
            }
            finally { if (hidden) Native.SetWindowDisplayAffinity(hide, Native.WDA_NONE); }
        }

        static byte[] Encode(Bitmap shot)
        {
            var size = Fit(shot.Width, shot.Height);
            using (var scaled = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb))
            using (var mem = new MemoryStream())
            using (var options = new EncoderParameters(1))
            {
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(shot, 0, 0, size.Width, size.Height);
                }
                options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
                scaled.Save(mem, Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid), options);
                return mem.ToArray();
            }
        }

        internal static Size Fit(int width, int height)
        {
            int longest = Math.Max(width, height);
            if (longest <= LongestSide) return new Size(width, height);
            double scale = LongestSide / (double)longest;
            return new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }
    }
}
