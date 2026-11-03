using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClaudeNotch
{
    // A little pixel-art critter drawn from rectangles. It idles (gentle bob,
    // blinks, glances around) and, while Talking is on, hops and walks in place.
    class Mascot : Canvas
    {
        static readonly Brush Clay = Frozen(Color.FromRgb(0xD9, 0x77, 0x57));
        static readonly Brush Ink = Frozen(Color.FromRgb(0x14, 0x14, 0x14));

        // 14 x 6 body; legs and eyes are drawn separately so they can move.
        static readonly string[] Body =
        {
            "..##########..",
            "..##########..",
            "##############",
            "##############",
            "..##########..",
            "..##########..",
        };
        static readonly int[] LegColumns = { 2, 4, 9, 11 };

        readonly double px;
        readonly Canvas sprite = new Canvas();
        readonly Rectangle eyeL, eyeR;
        readonly Rectangle[] legs = new Rectangle[4];
        readonly DispatcherTimer timer;
        readonly Random rnd = new Random();

        int tick, blinkAt, blinkLeft, glanceAt, glance;

        public bool Talking { get; set; }
        public bool Alert { get; set; }

        public Mascot(double pixelSize)
        {
            px = pixelSize;
            Width = 14 * px;
            Height = 9 * px;
            ClipToBounds = false;
            Children.Add(sprite);

            for (int row = 0; row < Body.Length; row++)
            {
                string line = Body[row];
                int start = -1;
                for (int col = 0; col <= line.Length; col++)
                {
                    bool filled = col < line.Length && line[col] == '#';
                    if (filled && start < 0) start = col;
                    if (!filled && start >= 0)
                    {
                        AddRect(Clay, start, row + 1, col - start, 1);
                        start = -1;
                    }
                }
            }
            for (int i = 0; i < 4; i++) legs[i] = AddRect(Clay, LegColumns[i], 7, 1, 2);
            eyeL = AddRect(Ink, 4, 2, 1, 2);
            eyeR = AddRect(Ink, 9, 2, 1, 2);

            blinkAt = rnd.Next(25, 60);
            glanceAt = rnd.Next(40, 90);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (s, e) => Step();
            timer.Start();
            Loaded += (s, e) => timer.Start();
            Unloaded += (s, e) => timer.Stop(); // e.g. the settings window closed
        }

        Rectangle AddRect(Brush fill, double x, double y, double w, double h)
        {
            var r = new Rectangle { Fill = fill, Width = w * px, Height = h * px };
            SetLeft(r, x * px);
            SetTop(r, y * px);
            sprite.Children.Add(r);
            return r;
        }

        void Step()
        {
            tick++;

            // Body motion: slow breathing bob when idle, little hops when talking.
            double y = Talking
                ? -Math.Round(Math.Abs(Math.Sin(tick * 0.55)) * 1.5) * px
                : Math.Round(Math.Sin(tick * 0.09)) * px * 0.5;
            SetTop(sprite, y);

            // Alert: a quick side-to-side wiggle in bursts, like it's waving you over.
            SetLeft(sprite, Alert && tick % 12 < 4 ? (tick % 2 == 0 ? -px * 0.5 : px * 0.5) : 0);

            // Legs: alternate pairs while talking, all down when idle.
            for (int i = 0; i < 4; i++)
            {
                bool lifted = Talking && ((tick / 2 + i) % 2 == 0);
                legs[i].Height = (lifted ? 1 : 2) * px;
            }

            // Blinking.
            if (--blinkAt <= 0) { blinkLeft = 2; blinkAt = rnd.Next(30, 70); }
            bool closed = blinkLeft-- > 0;

            // Glancing left/right now and then (or bouncing eyes while talking).
            if (--glanceAt <= 0)
            {
                glance = glance == 0 ? (rnd.Next(2) == 0 ? -1 : 1) : 0;
                glanceAt = glance == 0 ? rnd.Next(40, 100) : rnd.Next(12, 25);
            }
            int dx = Talking || Alert ? 0 : glance;
            double eyeTop = Alert ? 1.5 : Talking && tick % 6 < 3 ? 1.5 : 2;
            if (Alert) closed = false; // wide awake

            foreach (var eye in new[] { eyeL, eyeR })
            {
                eye.Height = (closed ? 0.4 : 2) * px;
                SetTop(eye, (closed ? 3.2 : eyeTop) * px);
            }
            SetLeft(eyeL, (4 + dx) * px);
            SetLeft(eyeR, (9 + dx) * px);
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
