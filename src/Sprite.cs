using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace ClaudeBuddy
{
    internal enum EyeMode
    {
        Open,
        Closed,
        Cross,
        Happy
    }

    internal enum Prop
    {
        None,
        Laptop,
        ThoughtDots
    }

    // One animation frame, in sprite pixels.
    internal sealed class Pose
    {
        public int Dx;
        public int Dy;
        public int[] Legs = new int[] { 4, 4, 4, 4 };
        public int ArmL;
        public int ArmR;
        public EyeMode Eyes = EyeMode.Open;
        public int EyeShift;
        public int EyeDy;
        public Prop Prop = Prop.None;
        public int PropFrame;
        public Color Led = Sprite.LedIdle;
        public bool LedOn;
        public int Glow;      // 0..8 halo strength
        public int Z = -1;    // sleeping "z" phase, -1 = none
        public string Banner = "";   // short text on the forehead (usage-limit countdown)

        public void SetLegs(int length)
        {
            for (int i = 0; i < Legs.Length; i++) Legs[i] = length;
        }

        public string Key()
        {
            int[] parts = new int[]
            {
                Dx, Dy, Legs[0], Legs[1], Legs[2], Legs[3], ArmL, ArmR, (int)Eyes, EyeShift, EyeDy,
                (int)Prop, PropFrame, Led.ToArgb(), LedOn ? 1 : 0, Glow, Z
            };
            string[] text = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++) text[i] = parts[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(",", text) + "|" + Banner;
        }
    }

    // The Claude Code mascot as pixel art, drawn from rectangles so it stays crisp at any scale.
    //
    // The original is 12 x 8 blocks:
    //
    //     . . X X X X X X X X . .
    //     . . X o X X X X o X . .      o = eye
    //     X X X X X X X X X X X X
    //     X X X X X X X X X X X X
    //     . . X X X X X X X X . .
    //     . . X X X X X X X X . .
    //     . . X . X . . X . X . .
    //     . . X . X . . X . X . .
    //
    // Here every block is 2 x 2 sprite pixels so parts can move by half a block:
    // body 16 x 12, arms 4 x 4, legs 2 x 4, eyes 2 x 2, the whole bot 24 x 16.
    // The sprite grid is 30 x 33. The status lamp floats 2 px above the head and the rows
    // above it leave room to jump without the lamp's halo touching the canvas edge.
    internal static class Sprite
    {
        public const int CanvasW = 30;
        public const int CanvasH = 33;
        public const int BotX = 3;
        public const int BotY = 14;
        public const int BotW = 24;
        public const int BotH = 16;
        public const int BodyH = 12;
        const int GroundY = BotY + BotH;

        public static readonly Color Body = Color.FromArgb(203, 123, 93);
        public static readonly Color Eye = Color.FromArgb(32, 32, 31);
        public static readonly Color LedGreen = Color.FromArgb(46, 230, 107);
        public static readonly Color LedYellow = Color.FromArgb(255, 196, 0);
        public static readonly Color LedRed = Color.FromArgb(255, 59, 48);
        public static readonly Color LedIdle = Color.FromArgb(128, 134, 145);
        public static readonly Color LedOff = Color.FromArgb(72, 74, 80);
        static readonly Color Soft = Color.FromArgb(138, 160, 255);      // z's and thought dots
        static readonly Color LaptopLid = Color.FromArgb(58, 62, 70);
        static readonly Color LaptopBase = Color.FromArgb(104, 108, 118);
        static readonly Color LaptopLogo = Color.FromArgb(236, 239, 245);

        static readonly int[] LegX = new int[] { 4, 8, 14, 18 };
        static readonly int[] WaitHop = new int[] { 0, -2, -3, -4, -4, -3, -2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        static readonly int[] DoneHop = new int[] { 0, -2, -3, -2, 0, 0, -2, -3, -2, 0, 0, 0, 0, 0 };

        // The working animation rotates through three acts of this many ticks each.
        public const int WorkActTicks = 75;
        public const int WorkActs = 3;

        public static int DoneLength { get { return DoneHop.Length; } }

        public static Color LedColor(BuddyState state)
        {
            switch (state)
            {
                case BuddyState.Working:
                case BuddyState.Done:
                    return LedGreen;
                case BuddyState.Waiting:
                    return LedYellow;
                case BuddyState.Error:
                    return LedRed;
                case BuddyState.Idle:
                    return LedIdle;
                default:
                    return LedOff;
            }
        }

        // t counts animation ticks (80 ms each) since the state began.
        public static Pose GetPose(BuddyState state, int t)
        {
            if (t < 0) t = 0;
            Pose p = new Pose();
            p.Led = LedColor(state);
            switch (state)
            {
                case BuddyState.Sleep:
                {
                    bool sunk = (t % 100) >= 50;
                    p.Dy = sunk ? 1 : 0;
                    p.SetLegs(sunk ? 3 : 4);
                    p.Eyes = EyeMode.Closed;
                    p.Z = (t / 5) % 10;
                    break;
                }
                case BuddyState.Idle:
                {
                    bool tall = (t % 70) >= 35;
                    p.Dy = tall ? -1 : 0;
                    p.SetLegs(tall ? 5 : 4);
                    if (t % 55 >= 40 && t % 55 < 42) p.Eyes = EyeMode.Closed;
                    int glance = t % 240;
                    if (glance >= 180 && glance < 196) p.EyeShift = -1;
                    else if (glance >= 196 && glance < 212) p.EyeShift = 1;
                    break;
                }
                case BuddyState.Working:
                {
                    int act = (t / WorkActTicks) % WorkActs;
                    int u = t % WorkActTicks;
                    if (act == 0) WorkAtLaptop(p, u);
                    else if (act == 1) WorkRunning(p, u);
                    else WorkThinking(p, u);
                    if (t % 60 >= 44 && t % 60 < 46) p.Eyes = EyeMode.Closed;
                    p.LedOn = true;
                    p.Glow = 4 + (int)Math.Round(4.0 * Math.Sin(2.0 * Math.PI * t / 20.0));
                    break;
                }
                case BuddyState.Waiting:
                {
                    p.Dy = WaitHop[t % WaitHop.Length];
                    bool air = p.Dy < 0;
                    p.SetLegs(air ? 3 : 4);
                    p.ArmL = air ? -2 : 0;
                    p.ArmR = p.ArmL;
                    p.LedOn = (t % 8) < 5;
                    p.Glow = p.LedOn ? 8 : 0;
                    break;
                }
                case BuddyState.Error:
                {
                    int k = t % 30;
                    p.Dx = k < 8 ? ((k % 2 == 0) ? -1 : 1) : 0;
                    p.ArmL = 1;
                    p.ArmR = 1;
                    p.Eyes = EyeMode.Cross;
                    p.LedOn = true;
                    p.Glow = 3 + (int)Math.Round(5.0 * Math.Abs(Math.Sin(Math.PI * t / 15.0)));
                    break;
                }
                case BuddyState.Done:
                {
                    p.Dy = DoneHop[Math.Min(t, DoneHop.Length - 1)];
                    bool air = p.Dy < 0;
                    p.SetLegs(air ? 3 : 4);
                    p.ArmL = air ? -2 : 0;
                    p.ArmR = p.ArmL;
                    p.Eyes = EyeMode.Happy;
                    p.LedOn = true;
                    p.Glow = 8;
                    break;
                }
            }
            if (p.Glow < 0) p.Glow = 0;
            if (p.Glow > 8) p.Glow = 8;
            return p;
        }

        // Puts a countdown on the forehead. The eyes move down to make room and the bot
        // holds still so the digits can be read.
        public static void ApplyTimer(Pose p, string text)
        {
            p.Banner = text;
            p.EyeDy = 5;
            p.EyeShift = 0;
            p.Dx = 0;
            if (p.Eyes == EyeMode.Cross) p.Eyes = EyeMode.Closed; // resting until the limit lifts
        }

        // At most four characters, so it fits the 16-pixel-wide head in the 3 x 5 font:
        // "45s", "12m", "2:34", "10h", "3d".
        public static string FormatCountdown(TimeSpan left)
        {
            long seconds = (long)Math.Ceiling(left.TotalSeconds);
            if (seconds < 1) seconds = 1;
            if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + "s";
            long minutes = (seconds + 59) / 60;
            if (minutes < 60) return minutes.ToString(CultureInfo.InvariantCulture) + "m";
            long hours = minutes / 60;
            if (hours < 10) return hours.ToString(CultureInfo.InvariantCulture) + ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
            if (hours < 24) return hours.ToString(CultureInfo.InvariantCulture) + "h";
            return ((hours + 23) / 24).ToString(CultureInfo.InvariantCulture) + "d";
        }

        // The same span in words, for tooltips: "2h 34m", "12m", "under a minute".
        public static string FormatDuration(TimeSpan left)
        {
            long minutes = (long)Math.Ceiling(left.TotalMinutes);
            if (minutes <= 1) return "under a minute";
            if (minutes < 60) return minutes.ToString(CultureInfo.InvariantCulture) + "m";
            long hours = minutes / 60;
            if (hours >= 48) return ((hours + 23) / 24).ToString(CultureInfo.InvariantCulture) + " days";
            return hours.ToString(CultureInfo.InvariantCulture) + "h " + (minutes % 60).ToString(CultureInfo.InvariantCulture) + "m";
        }

        // Act 1: sitting behind a laptop, eyes on the screen, arms tapping alternately.
        static void WorkAtLaptop(Pose p, int u)
        {
            p.Dy = 2;
            p.SetLegs(2);
            p.Prop = Prop.Laptop;
            p.EyeDy = 1;
            bool leftDown = (u / 2) % 2 == 0;
            p.ArmL = leftDown ? 2 : 1;
            p.ArmR = leftDown ? 1 : 2;
            // every few seconds, a quick look up from the screen
            int look = u % 38;
            if (look >= 30 && look < 36) p.EyeDy = 0;
        }

        // Act 2: running on the spot.
        static void WorkRunning(Pose p, int u)
        {
            int frame = (u / 2) % 4;
            if (frame == 0)
            {
                p.Legs = new int[] { 4, 2, 4, 2 };
                p.ArmL = -1;
                p.ArmR = 1;
            }
            else if (frame == 2)
            {
                p.Legs = new int[] { 2, 4, 2, 4 };
                p.ArmL = 1;
                p.ArmR = -1;
            }
            else
            {
                p.Dy = -1;
                p.SetLegs(3);
            }
        }

        // Act 3: thinking. Eyes wander upwards, one arm raised, dots appear one by one.
        static void WorkThinking(Pose p, int u)
        {
            bool tall = (u % 30) >= 15;
            p.Dy = tall ? -1 : 0;
            p.SetLegs(tall ? 5 : 4);
            p.ArmR = -2;
            p.EyeDy = -1;
            p.EyeShift = (u / 19) % 2 == 0 ? 1 : -1;
            p.Prop = Prop.ThoughtDots;
            p.PropFrame = (u / 6) % 4;
        }

        // Draws one frame onto a (CanvasW*s) x (CanvasH*s) surface.
        public static void Render(Graphics g, Pose p, int s)
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.Clear(Color.Transparent);
            g.PixelOffsetMode = PixelOffsetMode.Half;

            int ox = BotX + p.Dx;
            int oy = BotY + p.Dy;

            // Ground shadow, shrinking as the bot leaves the ground.
            int lift = Math.Min(4, Math.Max(0, -p.Dy));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(70 - lift * 10, 0, 0, 0)))
            {
                g.FillEllipse(shadow, (BotX + 3 + lift) * s, (GroundY + 0.4f) * s, (18 - 2 * lift) * s, 1.8f * s);
            }

            // Status lamp halo.
            float cx = (ox + 12) * s;
            float cy = (oy - 4) * s;
            if (p.LedOn && p.Glow > 0)
            {
                float r = 6f * s;
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                    using (PathGradientBrush halo = new PathGradientBrush(path))
                    {
                        halo.CenterPoint = new PointF(cx, cy);
                        halo.CenterColor = Color.FromArgb(40 + p.Glow * 20, p.Led);
                        halo.SurroundColors = new Color[] { Color.FromArgb(0, p.Led) };
                        g.FillPath(halo, path);
                    }
                }
            }
            g.SmoothingMode = SmoothingMode.None;

            // Alpha-1 backing so the whole bot rectangle takes mouse input, not just lit pixels.
            using (SolidBrush backing = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
            {
                Fill(g, backing, s, BotX, BotY - 7, BotW, BotH + 7);
            }

            using (SolidBrush body = new SolidBrush(Body))
            {
                Fill(g, body, s, ox + 4, oy, 16, BodyH);
                Fill(g, body, s, ox, oy + 4 + p.ArmL, 4, 4);
                Fill(g, body, s, ox + 20, oy + 4 + p.ArmR, 4, 4);
                for (int i = 0; i < LegX.Length; i++)
                {
                    if (p.Legs[i] > 0) Fill(g, body, s, ox + LegX[i], oy + BodyH, 2, p.Legs[i]);
                }
            }

            using (SolidBrush eye = new SolidBrush(Eye))
            {
                DrawEye(g, eye, s, p.Eyes, ox + 6 + p.EyeShift, oy + 2 + p.EyeDy, true);
                DrawEye(g, eye, s, p.Eyes, ox + 16 + p.EyeShift, oy + 2 + p.EyeDy, false);
            }

            if (p.Banner.Length > 0)
            {
                int width = TextWidth(p.Banner);
                if (width > 0 && width <= 16)
                {
                    using (SolidBrush ink = new SolidBrush(Eye))
                    {
                        DrawText(g, ink, s, p.Banner, ox + 4 + (16 - width) / 2, oy + 1);
                    }
                }
            }

            if (p.Prop == Prop.Laptop) DrawLaptop(g, s);

            // Lamp: a 4 x 4 disc with clipped corners and a highlight.
            Color lampColor = p.LedOn ? p.Led : Blend(p.Led, Color.FromArgb(40, 40, 44), 0.55);
            using (SolidBrush lamp = new SolidBrush(lampColor))
            {
                Fill(g, lamp, s, ox + 11, oy - 6, 2, 4);
                Fill(g, lamp, s, ox + 10, oy - 5, 4, 2);
            }
            if (p.LedOn)
            {
                using (SolidBrush shine = new SolidBrush(Blend(p.Led, Color.White, 0.6)))
                {
                    Fill(g, shine, s, ox + 11, oy - 5, 1, 1);
                }
            }

            if (p.Z >= 0)
            {
                DrawZ(g, s, ox + 17, oy - 4, p.Z % 10);
                DrawZ(g, s, ox + 17, oy - 4, (p.Z + 5) % 10);
            }

            if (p.Prop == Prop.ThoughtDots)
            {
                using (SolidBrush dot = new SolidBrush(Soft))
                {
                    for (int i = 0; i < p.PropFrame && i < 3; i++)
                    {
                        Fill(g, dot, s, ox + 16 + i * 3, oy - 4 - i, 2, 2);
                    }
                }
            }
        }

        // A laptop seen from behind, standing on the ground in front of the sitting bot.
        static void DrawLaptop(Graphics g, int s)
        {
            using (SolidBrush lid = new SolidBrush(LaptopLid))
            using (SolidBrush deck = new SolidBrush(LaptopBase))
            using (SolidBrush logo = new SolidBrush(LaptopLogo))
            {
                Fill(g, lid, s, BotX + 6, GroundY - 7, 12, 6);
                Fill(g, logo, s, BotX + 11, GroundY - 5, 2, 2);
                Fill(g, deck, s, BotX + 4, GroundY - 1, 16, 1);
            }
        }

        static void DrawEye(Graphics g, Brush b, int s, EyeMode mode, int x, int y, bool left)
        {
            // Open eyes are one 2 x 2 block. Cross and happy eyes use a 3 x 3 box that grows outwards.
            int bx = left ? x - 1 : x;
            switch (mode)
            {
                case EyeMode.Open:
                    Fill(g, b, s, x, y, 2, 2);
                    break;
                case EyeMode.Closed:
                    Fill(g, b, s, x - 1, y + 1, 4, 1);
                    break;
                case EyeMode.Cross:
                    Fill(g, b, s, bx, y, 1, 1);
                    Fill(g, b, s, bx + 2, y, 1, 1);
                    Fill(g, b, s, bx + 1, y + 1, 1, 1);
                    Fill(g, b, s, bx, y + 2, 1, 1);
                    Fill(g, b, s, bx + 2, y + 2, 1, 1);
                    break;
                case EyeMode.Happy:
                    Fill(g, b, s, bx + 1, y, 1, 1);
                    Fill(g, b, s, bx, y + 1, 1, 1);
                    Fill(g, b, s, bx + 2, y + 1, 1, 1);
                    break;
            }
        }

        // 3 x 5 pixel font, just the characters a countdown needs.
        static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            { '0', new string[] { "111", "101", "101", "101", "111" } },
            { '1', new string[] { "010", "110", "010", "010", "111" } },
            { '2', new string[] { "111", "001", "111", "100", "111" } },
            { '3', new string[] { "111", "001", "111", "001", "111" } },
            { '4', new string[] { "101", "101", "111", "001", "001" } },
            { '5', new string[] { "111", "100", "111", "001", "111" } },
            { '6', new string[] { "111", "100", "111", "101", "111" } },
            { '7', new string[] { "111", "001", "001", "001", "001" } },
            { '8', new string[] { "111", "101", "111", "101", "111" } },
            { '9', new string[] { "111", "101", "111", "001", "111" } },
            { ':', new string[] { "0", "1", "0", "1", "0" } },
            { 'h', new string[] { "100", "100", "111", "101", "101" } },
            { 'm', new string[] { "101", "111", "111", "101", "101" } },
            { 's', new string[] { "011", "100", "010", "001", "110" } },
            { 'd', new string[] { "001", "001", "111", "101", "111" } }
        };

        // Width in sprite pixels, or 0 if the text has a character the font lacks.
        public static int TextWidth(string text)
        {
            int width = 0;
            foreach (char c in text)
            {
                string[] glyph;
                if (!Glyphs.TryGetValue(c, out glyph)) return 0;
                width += glyph[0].Length + 1;
            }
            return width > 0 ? width - 1 : 0;
        }

        static void DrawText(Graphics g, Brush b, int s, string text, int x, int y)
        {
            foreach (char c in text)
            {
                string[] glyph = Glyphs[c];
                for (int row = 0; row < glyph.Length; row++)
                {
                    for (int col = 0; col < glyph[row].Length; col++)
                    {
                        if (glyph[row][col] == '1') Fill(g, b, s, x + col, y + row, 1, 1);
                    }
                }
                x += glyph[0].Length + 1;
            }
        }

        static void DrawZ(Graphics g, int s, int x, int y, int k)
        {
            int px = x + k / 3;
            int py = y - k;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(235 - k * 22, Soft)))
            {
                Fill(g, b, s, px, py, 4, 1);
                Fill(g, b, s, px + 2, py + 1, 1, 1);
                Fill(g, b, s, px + 1, py + 2, 1, 1);
                Fill(g, b, s, px, py + 3, 4, 1);
            }
        }

        static void Fill(Graphics g, Brush b, int s, int x, int y, int w, int h)
        {
            g.FillRectangle(b, x * s, y * s, w * s, h * s);
        }

        static Color Blend(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // ---- icons -------------------------------------------------------------------

        // Square icon: the standing bot, optionally with a status dot in the corner.
        public static Bitmap MakeIconBitmap(int size, BuddyState state, bool withDot)
        {
            Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.Clear(Color.Transparent);
                int k = Math.Max(1, size / BotW);
                bool crisp = BotW * k <= size && BotW * k >= size * 0.8;
                if (crisp)
                {
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    DrawBot(g, k, (size - BotW * k) / 2, (size - BotH * k) / 2);
                }
                else
                {
                    const int Big = 10;
                    using (Bitmap large = new Bitmap(BotW * Big, BotH * Big, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics lg = Graphics.FromImage(large))
                        {
                            lg.Clear(Color.Transparent);
                            lg.PixelOffsetMode = PixelOffsetMode.Half;
                            DrawBot(lg, Big, 0, 0);
                        }
                        float w = size * 0.94f;
                        float h = w * BotH / BotW;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(large, (size - w) / 2f, (size - h) / 2f, w, h);
                    }
                }

                if (withDot)
                {
                    float d = size * 0.42f;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (SolidBrush rim = new SolidBrush(Color.FromArgb(230, 20, 20, 24)))
                    using (SolidBrush dot = new SolidBrush(LedColor(state)))
                    {
                        g.FillEllipse(rim, size - d - 0.5f, -0.5f, d + 1f, d + 1f);
                        g.FillEllipse(dot, size - d + 0.5f, 0.5f, d - 1f, d - 1f);
                    }
                }
            }
            return result;
        }

        static void DrawBot(Graphics g, int k, int left, int top)
        {
            using (SolidBrush body = new SolidBrush(Body))
            using (SolidBrush eye = new SolidBrush(Eye))
            {
                g.FillRectangle(body, left + 4 * k, top, 16 * k, BodyH * k);
                g.FillRectangle(body, left, top + 4 * k, 4 * k, 4 * k);
                g.FillRectangle(body, left + 20 * k, top + 4 * k, 4 * k, 4 * k);
                for (int i = 0; i < LegX.Length; i++) g.FillRectangle(body, left + LegX[i] * k, top + BodyH * k, 2 * k, 4 * k);
                g.FillRectangle(eye, left + 6 * k, top + 2 * k, 2 * k, 2 * k);
                g.FillRectangle(eye, left + 16 * k, top + 2 * k, 2 * k, 2 * k);
            }
        }

        // Writes a multi-size .ico (32-bit DIB entries, PNG for 256) for the exe icon.
        public static void WriteIco(string path)
        {
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 256 };
            byte[][] images = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                using (Bitmap bmp = MakeIconBitmap(sizes[i], BuddyState.Idle, false))
                {
                    images[i] = sizes[i] >= 256 ? PngBytes(bmp) : DibBytes(bmp);
                }
            }

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                for (int i = 0; i < sizes.Length; i++) w.Write(images[i]);
            }
        }

        static byte[] PngBytes(Bitmap bmp)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        static byte[] DibBytes(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            int maskStride = ((w + 31) / 32) * 4;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(40);
                bw.Write(w);
                bw.Write(h * 2); // XOR image + AND mask
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(0);
                bw.Write(w * h * 4 + maskStride * h);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                for (int y = h - 1; y >= 0; y--)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        bw.Write(c.B);
                        bw.Write(c.G);
                        bw.Write(c.R);
                        bw.Write(c.A);
                    }
                }
                bw.Write(new byte[maskStride * h]);
                bw.Flush();
                return ms.ToArray();
            }
        }

        // ---- contact sheet for visual checks ------------------------------------------

        public static void WriteSheet(string path, int scale)
        {
            BuddyState[] states = new BuddyState[]
            {
                BuddyState.Sleep, BuddyState.Idle, BuddyState.Working, BuddyState.Waiting, BuddyState.Error, BuddyState.Done
            };
            int[][] ticks = new int[][]
            {
                new int[] { 0, 10, 55, 70 },                                  // sleep: z drift, sunk
                new int[] { 3, 36, 40, 185 },                                 // idle: rest, tall, blink, glance
                new int[] { 0, 2, WorkActTicks, 2 * WorkActTicks + 20 },      // working: laptop x2, run, think
                new int[] { 0, 2, 4, 6 },                                     // waiting: hop, lamp on/off
                new int[] { 0, 1, 8, 15 },                                    // error: shake, glow
                new int[] { 0, 2, 5, 7 }                                      // done: hops
            };
            string[] timers = new string[] { "2:34", "45m", "12h", "30s" };          // last row: usage limit
            int cw = CanvasW * scale;
            int ch = CanvasH * scale;
            int cols = 4;
            using (Bitmap sheet = new Bitmap(cw * cols, ch * (states.Length + 1), PixelFormat.Format32bppArgb))
            {
                using (Graphics sg = Graphics.FromImage(sheet))
                using (Bitmap cell = new Bitmap(cw, ch, PixelFormat.Format32bppArgb))
                using (SolidBrush dark = new SolidBrush(Color.FromArgb(32, 33, 36)))
                using (SolidBrush light = new SolidBrush(Color.FromArgb(236, 236, 232)))
                {
                    for (int r = 0; r <= states.Length; r++)
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            Pose pose;
                            if (r < states.Length)
                            {
                                pose = GetPose(states[r], ticks[r][c]);
                            }
                            else
                            {
                                pose = GetPose(c < 3 ? BuddyState.Error : BuddyState.Idle, 8);
                                ApplyTimer(pose, timers[c]);
                            }
                            using (Graphics cg = Graphics.FromImage(cell))
                            {
                                Render(cg, pose, scale);
                            }
                            sg.FillRectangle(c < 2 ? dark : light, c * cw, r * ch, cw, ch);
                            sg.DrawImage(cell, c * cw, r * ch, cw, ch);
                        }
                    }
                }
                sheet.Save(path, ImageFormat.Png);
            }
        }
    }
}
