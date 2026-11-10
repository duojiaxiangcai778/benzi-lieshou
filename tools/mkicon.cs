using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// 把一张方图打成多尺寸 ICO（32bpp BMP 帧 + AND 掩码），给 csc /win32icon 用
internal class MkIcon
{
    private static void Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("用法: mkicon <src.png> <out.ico>"); return; }
        string src = args[0], dst = args[1];
        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        using (Bitmap bmp = new Bitmap(src))
        {
            MemoryStream[] frames = new MemoryStream[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
                frames[i] = MakeFrame(bmp, sizes[i]);

            using (FileStream fs = new FileStream(dst, FileMode.Create))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
                    w.Write(dim);
                    w.Write(dim);
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write((int)frames[i].Length);
                    w.Write(offset);
                    offset += (int)frames[i].Length;
                }
                for (int i = 0; i < sizes.Length; i++)
                    w.Write(frames[i].ToArray());
            }
        }
        Console.WriteLine("OK " + dst + "  " + new FileInfo(dst).Length + " bytes");
    }

    private static MemoryStream MakeFrame(Bitmap src, int s)
    {
        using (Bitmap b = new Bitmap(s, s, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(src, 0, 0, s, s);
            }
            BitmapData bd = b.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] raw = new byte[bd.Stride * s];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, raw, 0, raw.Length);
            b.UnlockBits(bd);

            int maskRow = (int)Math.Ceiling(s / 8.0);
            while (maskRow % 4 != 0) maskRow++;
            int maskBytes = maskRow * s;

            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(40);
            w.Write(s);
            w.Write(s * 2);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(0);
            w.Write(s * s * 4 + maskBytes);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                w.Write(raw, y * bd.Stride, s * 4);
            byte[] zero = new byte[maskBytes];
            w.Write(zero, 0, zero.Length);
            w.Flush();
            return ms;
        }
    }
}
