using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls;

// WPF 亚克力探针：验证「WPF 窗口 + SetWindowCompositionAttribute 亚克力」在本机是否出真模糊
internal class WpfProbe : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int WCA_ACCENT_POLICY = 19;

    public static string Result = "未执行";

    public WpfProbe()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Width = 900; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = false;

        Grid root = new Grid();

        Border card = new Border();
        card.Margin = new Thickness(40, 90, 300, 90);
        card.CornerRadius = new CornerRadius(18);
        card.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        card.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        card.BorderThickness = new Thickness(1);
        TextBlock tb = new TextBlock();
        tb.Text = "亚克力卡片";
        tb.Foreground = Brushes.White;
        tb.FontSize = 18;
        tb.Margin = new Thickness(20);
        card.Child = tb;
        root.Children.Add(card);

        TextBlock info = new TextBlock();
        info.Text = "等待…";
        info.Foreground = Brushes.White;
        info.FontSize = 14;
        info.Margin = new Thickness(40, 30, 0, 0);
        info.Name = "info";
        root.Children.Add(info);

        Content = root;

        Loaded += delegate
        {
            IntPtr h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Result = "acrylic=" + Apply(h, ACCENT_ENABLE_ACRYLICBLURBEHIND, unchecked((int)0xCC101418))
                   + " blur=" + Apply(h, ACCENT_ENABLE_BLURBEHIND, unchecked((int)0x99202020));
            info.Text = Result;
        };
    }

    private static int Apply(IntPtr hwnd, int state, int color)
    {
        AccentPolicy ap = new AccentPolicy();
        ap.AccentState = state;
        ap.AccentFlags = 2;
        ap.GradientColor = color;
        int size = Marshal.SizeOf(ap);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(ap, ptr, false);
        WindowCompositionAttributeData d = new WindowCompositionAttributeData();
        d.Attribute = WCA_ACCENT_POLICY;
        d.Data = ptr;
        d.SizeOfData = size;
        int hr = SetWindowCompositionAttribute(hwnd, ref d);
        Marshal.FreeHGlobal(ptr);
        return hr;
    }

    [STAThread]
    public static void Main()
    {
        Application app = new Application();
        app.Run(new WpfProbe());
    }
}
