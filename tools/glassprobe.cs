using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 玻璃探针：验证 DWM 亚克力在本机是否真的生效，以及"透明子控件 + 不画背景"能否透出模糊。
internal class GlassProbe : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public static int BackdropHr = 999;
    public static int MicaHr = 999;
    public static int DarkHr = 999;

    public GlassProbe()
    {
        Text = "玻璃探针";
        Width = 900; Height = 600;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        DoubleBuffered = true;

        Panel p = new Panel();
        p.Bounds = new Rectangle(40, 90, 380, 220);
        p.BackColor = Color.Transparent;   // 关键：透明子控件
        p.Paint += delegate(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
            using (GraphicsPath path = Round(r, 18))
            using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(90, 255, 255, 255), Color.FromArgb(24, 255, 255, 255), 90f))
            using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1f))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, "这张卡片是透明子控件画的", new Font("Microsoft YaHei UI", 11f), new Point(20, 20), Color.White);
        };
        Controls.Add(p);

        Load += delegate
        {
            IntPtr h = Handle;
            int on = 1;
            DarkHr = DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            int corner = 2;
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            int acrylic = 3; // DWMSBT_TRANSIENTWINDOW
            BackdropHr = DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, sizeof(int));
            int mica = 2;    // DWMSBT_MAINWINDOW
            MicaHr = DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int));

            MARGINS m = new MARGINS();
            m.Left = -1; m.Right = -1; m.Top = -1; m.Bottom = -1;
            DwmExtendFrameIntoClientArea(h, ref m);
        };
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 关键：不画背景，让 DWM 的模糊透出来
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (Font f = new Font("Microsoft YaHei UI", 10f))
        {
            TextRenderer.DrawText(e.Graphics,
                "acrylic hr=" + BackdropHr + "  mica hr=" + MicaHr + "  dark hr=" + DarkHr,
                f, new Point(40, 30), Color.White);
        }
    }

    internal static GraphicsPath Round(Rectangle r, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new GlassProbe());
    }
}
