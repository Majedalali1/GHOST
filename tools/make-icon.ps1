# Renders Assets\app.ico (16â€“256 px) and Assets\icon-256.png: a neon gauge on a dark rounded square.
param([string]$OutDir = (Join-Path $PSScriptRoot "..\Assets"))
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconMaker
{
    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        p.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        p.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        p.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            bool tiny = s <= 24;
            float pad = tiny ? 0 : s * 0.04f;
            float w = s - 2 * pad;

            using (var bgPath = Rounded(pad, pad, w, w, w * 0.24f))
            using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), Color.FromArgb(0x2A, 0x1F, 0x5C), Color.FromArgb(0x08, 0x0D, 0x1C)))
            {
                g.FillPath(bg, bgPath);
                if (!tiny)
                    using (var edge = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1f, s / 128f)))
                        g.DrawPath(edge, bgPath);
            }

            float cx = s * 0.5f, cy = s * 0.55f;
            float r = s * (tiny ? 0.32f : 0.29f);
            float stroke = s * (tiny ? 0.13f : 0.085f);
            var arcRect = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);

            if (!tiny)
                using (var track = new Pen(Color.FromArgb(34, 255, 255, 255), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(track, arcRect, 135, 270);

            using (var grad = new LinearGradientBrush(new PointF(cx - r - stroke, cy), new PointF(cx + r + stroke, cy), Color.Black, Color.Black) { WrapMode = WrapMode.TileFlipX })
            {
                var blend = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0x22, 0xD3, 0xEE), Color.FromArgb(0x8B, 0x5C, 0xF6), Color.FromArgb(0xF4, 0x72, 0xB6) },
                    Positions = new[] { 0f, 0.55f, 1f },
                };
                grad.InterpolationColors = blend;
                using (var arc = new Pen(grad, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, arcRect, 135, tiny ? 270 : 215);
            }

            // Needle pointing up-right
            double a = -50 * Math.PI / 180;
            float len = r * 0.78f;
            using (var needle = new Pen(Color.White, s * (tiny ? 0.11f : 0.06f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(needle, cx, cy, cx + (float)(Math.Cos(a) * len), cy + (float)(Math.Sin(a) * len));
            float dot = s * (tiny ? 0.09f : 0.065f);
            using (var white = new SolidBrush(Color.White))
                g.FillEllipse(white, cx - dot, cy - dot, 2 * dot, 2 * dot);
        }
        return bmp;
    }

    static byte[] Dib(Bitmap b)
    {
        int s = b.Width;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++)
                {
                    var c = b.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            int maskRow = ((s + 31) / 32) * 4;
            w.Write(new byte[maskRow * s]);
            return ms.ToArray();
        }
    }

    public static void Save(string dir)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = new List<byte[]>();
        foreach (var s in sizes)
            using (var b = Render(s))
            {
                if (s >= 128)
                    using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); images.Add(ms.ToArray()); }
                else images.Add(Dib(b));
                if (s == 256) b.Save(Path.Combine(dir, "icon-256.png"), ImageFormat.Png);
            }
        using (var fs = File.Create(Path.Combine(dir, "app.ico")))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }
}
"@
New-Item -ItemType Directory -Force $OutDir | Out-Null
[IconMaker]::Save((Resolve-Path $OutDir).Path)
Get-ChildItem $OutDir | Select-Object Name, Length

