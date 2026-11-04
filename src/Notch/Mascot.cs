using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Clawdock
{
    // What the mascot is up to. Each one has its own little scene.
    enum MascotMood { Idle, Sleeping, Listening, Thinking, Speaking, Searching, Reading, Working, Alert, Question, Done }

    // A little pixel-art critter drawn from rectangles. It idles (gentle bob, blinks, glances
    // around), and acts out its Mood: thought clouds, a laptop to code on, a magnifying
    // glass, a book, sound waves, and so on. Scenes are drawn in the critter's own grid, where
    // the body is x 0..13, y 1..8; there's room for 4 rows above it and a few columns each side.
    class Mascot : Canvas
    {
        static readonly Brush Clay = Frozen(Color.FromRgb(0xD9, 0x77, 0x57));
        static readonly Brush ClayDark = Frozen(Color.FromRgb(0x9E, 0x4F, 0x38));
        static readonly Brush Ink = Frozen(Color.FromRgb(0x14, 0x14, 0x14));
        static readonly Brush Cloud = Frozen(Color.FromRgb(0xE6, 0xE6, 0xE6));
        static readonly Brush CloudDim = Frozen(Color.FromRgb(0x8C, 0x8C, 0x8C));
        static readonly Brush Steel = Frozen(Color.FromRgb(0x56, 0x5B, 0x63));
        static readonly Brush SteelDark = Frozen(Color.FromRgb(0x33, 0x36, 0x3B));
        static readonly Brush Glass = Frozen(Color.FromRgb(0x1C, 0x2B, 0x22));
        static readonly Brush GlassLit = Frozen(Color.FromRgb(0x9B, 0xE5, 0x64));
        static readonly Brush Spark = Frozen(Color.FromRgb(0xFF, 0xD2, 0x4A));
        static readonly Brush SparkHot = Frozen(Color.FromRgb(0xFF, 0xF6, 0xD6));
        static readonly Brush Amber = Frozen(Color.FromRgb(0xF2, 0xB1, 0x34));
        static readonly Brush Blue = Frozen(Color.FromRgb(0x6C, 0xB2, 0xF2));
        static readonly Brush Paper = Frozen(Color.FromRgb(0xF2, 0xEE, 0xE3));

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
        readonly Canvas gear = new Canvas(); // worn or held: moves with the body
        readonly Canvas air = new Canvas();  // around it: stays put
        readonly Rectangle eyeL, eyeR;
        readonly Rectangle[] legs = new Rectangle[4];
        readonly DispatcherTimer timer;
        readonly Random rnd = new Random();

        int tick, blinkAt, blinkLeft, glanceAt, glance;

        public MascotMood Mood { get; set; }

        public Mascot(double pixelSize)
        {
            px = pixelSize;
            Width = 14 * px;
            Height = 9 * px;
            ClipToBounds = false;
            Children.Add(sprite);
            Children.Add(air);

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
            sprite.Children.Add(gear);

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

        void Dot(Canvas layer, Brush fill, double x, double y, double w = 1, double h = 1)
        {
            var r = new Rectangle { Fill = fill, Width = w * px, Height = h * px };
            SetLeft(r, x * px);
            SetTop(r, y * px);
            layer.Children.Add(r);
        }

        // Rows of '#' as one shape, a pixel at a time.
        void Shape(Canvas layer, Brush fill, double x, double y, params string[] rows)
        {
            for (int row = 0; row < rows.Length; row++)
                for (int col = 0; col < rows[row].Length; col++)
                    if (rows[row][col] == '#') Dot(layer, fill, x + col, y + row);
        }

        void Step()
        {
            tick++;
            gear.Children.Clear();
            air.Children.Clear();
            var mood = Mood;

            bool hops = mood == MascotMood.Speaking || mood == MascotMood.Done;
            bool walks = mood == MascotMood.Speaking;
            bool wiggles = mood == MascotMood.Alert || mood == MascotMood.Question;

            // Body motion: slow breathing bob, or little hops.
            double y = hops ? -Math.Round(Math.Abs(Math.Sin(tick * 0.55)) * 1.5) * px
                : mood == MascotMood.Sleeping ? Math.Round(Math.Sin(tick * 0.05)) * px * 0.5
                : Math.Round(Math.Sin(tick * 0.09)) * px * 0.5;
            SetTop(sprite, y);

            bool typing = mood == MascotMood.Working && tick % 90 < 72; // now and then it stops to look at you
            double x = wiggles && tick % 12 < 4 ? (tick % 2 == 0 ? -px * 0.5 : px * 0.5) : 0;
            SetLeft(sprite, x);

            for (int i = 0; i < 4; i++)
                legs[i].Height = (walks && (tick / 2 + i) % 2 == 0 ? 1 : 2) * px;

            // Blinking.
            if (--blinkAt <= 0) { blinkLeft = 2; blinkAt = rnd.Next(30, 70); }
            bool closed = blinkLeft-- > 0;

            // Glancing left/right now and then.
            if (--glanceAt <= 0)
            {
                glance = glance == 0 ? (rnd.Next(2) == 0 ? -1 : 1) : 0;
                glanceAt = glance == 0 ? rnd.Next(40, 100) : rnd.Next(12, 25);
            }

            int dx = mood == MascotMood.Idle ? glance : 0;
            double eyeTop = 2, eyeHeight = 2;
            switch (mood)
            {
                case MascotMood.Sleeping: closed = true; DrawZzz(); break;
                case MascotMood.Listening: closed = false; DrawWaves(); break;
                case MascotMood.Thinking: eyeTop = 1.5; dx = tick / 16 % 2 == 0 ? 1 : 0; DrawClouds(); break;
                case MascotMood.Speaking: eyeTop = tick % 6 < 3 ? 1.5 : 2; DrawMouth(); break;
                case MascotMood.Searching: dx = (int)Math.Round(Math.Sin(tick * 0.14)); DrawMagnifier(); break;
                case MascotMood.Reading: eyeTop = 3; eyeHeight = 1; DrawBook(); break;
                case MascotMood.Working: dx = typing ? -1 : 0; DrawLaptop(typing); break;
                case MascotMood.Alert: closed = false; eyeTop = 1.5; DrawBang(); break;
                case MascotMood.Question: closed = false; eyeTop = 1.5; DrawQuestion(); break;
                case MascotMood.Done: closed = false; eyeTop = 2.4; eyeHeight = 0.8; DrawSparkles(); break;
            }

            foreach (var eye in new[] { eyeL, eyeR })
            {
                eye.Height = (closed ? 0.4 : eyeHeight) * px;
                SetTop(eye, (closed ? 3.2 : eyeTop) * px);
            }
            SetLeft(eyeL, (4 + dx) * px);
            SetLeft(eyeR, (9 + dx) * px);
        }

        // ---- the scenes ----------------------------------------------------

        // Thinking: two bubbles rise, then a cloud puffs up.
        void DrawClouds()
        {
            int t = tick % 40;
            if (t >= 2) Dot(air, CloudDim, 11, 0);
            if (t >= 8) Dot(air, t >= 16 ? CloudDim : Cloud, 12.5, -1);
            if (t < 14) return;
            int drift = (tick / 10) % 2;
            Shape(air, t < 18 ? CloudDim : Cloud, 8 + drift, -4,
                "..###..",
                "#######",
                ".#####.");
        }

        // Working: at a laptop in front of it, typing, with code filling up the screen.
        void DrawLaptop(bool typing)
        {
            // The lid, and the base it sits on.
            Dot(air, SteelDark, -6, 1, 6, 7);
            Dot(air, Glass, -5, 2, 4, 5);
            Dot(air, Steel, -6, 8, 9, 1);

            // Lines of code appear one at a time, then the screen scrolls on.
            int written = tick / 5;
            var colors = new[] { GlassLit, Blue, Spark, Cloud };
            for (int row = 0; row < 5; row++)
            {
                int line = written - 4 + row;
                if (line < 0) continue;
                int seed = line * 7 + 3;
                int indent = seed % 3 == 0 ? 1 : 0;
                double width = Math.Min(1 + seed % 4, 4 - indent);
                if (row == 4 && typing) width = Math.Min(width, 1 + tick % 5 % (int)width); // the line being typed
                Dot(air, colors[seed % colors.Length], -5 + indent, 2.25 + row, width, 0.5);
            }
            if (typing && tick % 8 < 4) Dot(air, SparkHot, -4.5, 6.25, 0.5, 0.5); // a cursor, blinking

            // Its arms reach over to the keys, one tapping and then the other.
            int tap = tick / 2 % 2;
            Dot(air, Clay, 0, 5, 2, 1);
            Dot(air, Clay, 0, 6, 1, typing && tap == 0 ? 2 : 1);
            Dot(air, ClayDark, 1, 6, 1, typing && tap == 1 ? 2 : 1);
        }

        // Searching: a magnifying glass sweeps across, above its head.
        void DrawMagnifier()
        {
            double gx = 5 + Math.Round(Math.Sin(tick * 0.14) * 5);
            Shape(air, Cloud, gx, -4,
                ".###.",
                "#...#",
                "#...#",
                ".###.");
            Dot(air, ClayDark, gx + 4, 0);
            Dot(air, ClayDark, gx + 5, 1);
        }

        // Reading: a book held up, its pages turning.
        void DrawBook()
        {
            Dot(gear, Paper, 2, 4, 5, 3);
            Dot(gear, Paper, 7, 4, 5, 3);
            Dot(gear, ClayDark, 6.75, 4, 0.5, 3);
            for (int line = 0; line < 2; line++)
            {
                Dot(gear, CloudDim, 3, 4.75 + line, 3, 0.4);
                Dot(gear, CloudDim, 8, 4.75 + line, 3, 0.4);
            }
            if (tick % 50 < 5) Dot(gear, Cloud, 4 + tick % 50, 3.5, 1, 3.5); // a page going over
        }

        // Listening: sound coming in from both sides.
        void DrawWaves()
        {
            int t = tick / 3 % 3;
            for (int i = 0; i <= t; i++)
            {
                double h = 4 - i;
                double top = 4.5 - h / 2;
                Dot(air, i == t ? Clay : ClayDark, -2 - i * 1.5, top, 1, h);
                Dot(air, i == t ? Clay : ClayDark, 15 + i * 1.5, top, 1, h);
            }
        }

        // Speaking: its mouth moves, and little dots come out.
        void DrawMouth()
        {
            double open = tick % 4 < 2 ? 1.2 : 0.5;
            Dot(gear, Ink, 6, 4.4, 2, open);
            int t = tick / 3 % 4;
            if (t >= 1) Dot(air, Cloud, 15, 4);
            if (t >= 2) Dot(air, Cloud, 16.5, 3);
            if (t >= 3) Dot(air, Cloud, 18, 2);
        }

        void DrawZzz()
        {
            int t = tick / 10 % 4;
            if (t >= 1) Shape(air, CloudDim, 12, -2, "###", "..#", ".#.", "###");
            if (t >= 2) Shape(air, Cloud, 15.5, -4, "####", "..#.", ".#..", "####");
        }

        void DrawBang()
        {
            if (tick % 10 < 2) return;
            Dot(air, Amber, 6.5, -4, 1, 3);
            Dot(air, Amber, 6.5, -0.5, 1, 1);
        }

        void DrawQuestion()
        {
            if (tick % 16 < 2) return;
            Shape(air, Blue, 5.5, -4,
                ".##.",
                "...#",
                "..#.");
            Dot(air, Blue, 7.5, -0.5);
        }

        // Done: twinkling stars.
        void DrawSparkles()
        {
            foreach (var star in new[] { new double[] { -2, 0, 0 }, new double[] { 15, 1, 5 }, new double[] { 12, -3, 10 } })
            {
                int t = (tick + (int)star[2]) % 15;
                double sx = star[0], sy = star[1];
                if (t < 5) Dot(air, Spark, sx, sy);
                else if (t < 10)
                {
                    Dot(air, SparkHot, sx, sy);
                    Dot(air, Spark, sx - 1, sy); Dot(air, Spark, sx + 1, sy);
                    Dot(air, Spark, sx, sy - 1); Dot(air, Spark, sx, sy + 1);
                }
            }
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
