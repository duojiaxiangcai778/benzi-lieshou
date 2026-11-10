using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BenziHunter
{
    internal class AppHost : Form
    {
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                          HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        // 客户区四边各让 6px 给原生缩放（WebView2 铺满会让窗口收不到 NCHITTEST）
        private const int EDGE = 6;

        private WebView2 web;
        private CoreWebView2 core;
        private Config cfg;
        private volatile bool busy = false;
        private volatile bool cancelled = false;
        private List<Hit> hits = new List<Hit>();
        private bool dark = true;
        private string saveDir = "";
        private bool autoDl = true;
        private bool strict = false;
        private bool ready = false;
        private string uiDir;
        private System.Windows.Forms.Timer bgTimer;

        public AppHost()
        {
            Log.Init();
            cfg = Config.Load();
            Http.Proxy = cfg.Proxy;
            Http.Init();

            dark = cfg.Dark;
            autoDl = cfg.AutoDownload;
            saveDir = cfg.SaveDir;
            if (string.IsNullOrEmpty(saveDir))
                saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "本子下载");
            if (!Directory.Exists(saveDir)) { try { Directory.CreateDirectory(saveDir); } catch { } }

            uiDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenziHunter", "ui");
            Directory.CreateDirectory(uiDir);

            Log.Info("CFG", "保存目录 = " + saveDir);
            Log.Info("CFG", "代理     = " + (string.IsNullOrEmpty(Http.Proxy) ? "（直连）" : Http.Proxy));
            Log.Info("CFG", "主题     = " + (dark ? "深色" : "浅色") + "  自动下载最佳=" + autoDl + "  严格匹配=" + strict);
            Log.Info("CFG", "界面目录 = " + uiDir);

            Text = "本子猎手";
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 1240;
            Height = 860;
            MinimumSize = new Size(1020, 700);
            BackColor = Color.Black;
            KeyPreview = true;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            CaptureDesktop();

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            web.AllowExternalDrop = false;
            web.CreationProperties = new CoreWebView2CreationProperties();
            web.CreationProperties.UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenziHunter", "wv2");
            Controls.Add(web);

            bgTimer = new System.Windows.Forms.Timer();
            bgTimer.Interval = 700;
            bgTimer.Tick += delegate
            {
                bgTimer.Stop();
                Log.Info("TIMER", "bgTimer 触发：窗口移动/缩放后重抓壁纸");
                CaptureDesktop();
                Send(new { t = "proxy", v = Http.Proxy });
                Send(new { t = "bg", v = bgVer });   // 底图没换就发同一个值，界面原地不动
            };

            Load += delegate
            {
                FitWeb();
                try
                {
                    int corner = 2;
                    DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
                }
                catch { }
            };
            FormClosing += delegate
            {
                Log.Info("SYS", "窗口关闭，保存配置（目录=" + saveDir + " 主题=" + (dark ? "深色" : "浅色") + "）");
                cfg.SaveDir = saveDir;
                cfg.Dark = dark;
                cfg.AutoDownload = autoDl;
                cfg.Strict = strict;
                cfg.Save();
                Log.Info("SYS", "配置已保存，退出");
                Log.Close();
            };
            Move += delegate { LogWin("Move"); bgTimer.Stop(); bgTimer.Start(); FitWeb(); };
            Resize += delegate { LogWin("Resize"); bgTimer.Stop(); bgTimer.Start(); FitWeb(); };

            web.CoreWebView2InitializationCompleted += OnWebInit;
            try { web.EnsureCoreWebView2Async(null); }
            catch (Exception ex) { MessageBox.Show("WebView2 初始化失败：" + ex.Message, "错误"); }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00040000;
                cp.Style |= 0x00020000;
                cp.Style |= 0x00010000;
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color c = dark ? Color.FromArgb(11, 14, 21) : Color.FromArgb(238, 242, 248);
            e.Graphics.Clear(c);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                int lp = m.LParam.ToInt32();
                int x = (short)(lp & 0xFFFF);
                int y = (short)((lp >> 16) & 0xFFFF);
                Point p = PointToClient(new Point(x, y));
                int g = EDGE;
                bool l = p.X <= g, r = p.X >= ClientSize.Width - g, t = p.Y <= g, b = p.Y >= ClientSize.Height - g;
                if (l && t) { m.Result = (IntPtr)HTTOPLEFT; return; }
                if (r && t) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                if (l && b) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                if (r && b) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
                if (l) { m.Result = (IntPtr)HTLEFT; return; }
                if (r) { m.Result = (IntPtr)HTRIGHT; return; }
                if (t) { m.Result = (IntPtr)HTTOP; return; }
                if (b) { m.Result = (IntPtr)HTBOTTOM; return; }
                if (p.Y <= 54 && p.X < ClientSize.Width - 200)
                {
                    m.Result = (IntPtr)HTCAPTION;
                    return;
                }
                m.Result = (IntPtr)HTCLIENT;
                return;
            }
            if (m.Msg == WM_GETMINMAXINFO)
            {
                IntPtr mon = MonitorFromWindow(Handle, 2);
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(mon, ref mi))
                {
                    MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                    mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                    mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                    mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                    Marshal.StructureToPtr(mmi, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        // 用 Win32 的真实客户区尺寸摆放 WebView2，绕开 WinForms 的尺寸缓存
        private string lastWinKey = "";
        private DateTime lastWinAt = DateTime.MinValue;

        // 窗口移动/缩放日志：同尺寸同位置 400ms 内合并，避免拖动时刷屏
        private void LogWin(string why)
        {
            string key = why + "|" + Left + "," + Top + " " + Width + "x" + Height + " client " + ClientSize.Width + "x" + ClientSize.Height;
            DateTime now = DateTime.Now;
            if (key == lastWinKey && (now - lastWinAt).TotalMilliseconds < 400) return;
            lastWinKey = key; lastWinAt = now;
            Log.Info("WIN", why + " 窗口=" + Left + "," + Top + " " + Width + "x" + Height
                + " 客户区=" + ClientSize.Width + "x" + ClientSize.Height
                + " web=" + (web == null ? "null" : web.Width + "x" + web.Height)
                + " Dpi=" + (IsHandleCreated ? DeviceDpi : 0));
        }

        private void FitWeb()
        {
            try
            {
                if (!IsHandleCreated || web == null) return;
                RECT r;
                if (!GetWindowRect(Handle, out r)) return;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return;
                web.Dock = DockStyle.None;
                web.Bounds = new Rectangle(EDGE, EDGE, Math.Max(1, w - 2 * EDGE), Math.Max(1, h - 2 * EDGE));
                web.BringToFront();
                int ww = web.Width, hh = web.Height;
                BeginInvoke((MethodInvoker)delegate { if (ready) Send(new { t = "size", w = ww, h = hh }); });
            }
            catch { }
        }

        private string lastBgKey = "";
        private volatile bool bgBusy = false;
        private int bgVer = 0;

        // 抓玻璃底图。必须异步：3840x2160 壁纸缩放到屏幕尺寸 + 存 PNG 要 800ms 上下，
        // 在主线程做就会让窗口在拖动时冻结一下（用户看到的就是「闪」）。
        private void CaptureDesktop()
        {
            if (bgBusy) { Log.Info("GLASS", "上一次抓底图还在跑，这次跳过"); return; }

            Stopwatch swc = Stopwatch.StartNew();
            int x, y, w, h;
            try
            {
                Rectangle mb = (IsHandleCreated ? Screen.FromHandle(Handle) : Screen.PrimaryScreen).Bounds;
                x = mb.X; y = mb.Y; w = mb.Width; h = mb.Height;
            }
            catch (Exception ex)
            {
                Log.Warn("GLASS", "取显示器尺寸失败，用虚拟屏：" + ex.Message);
                Rectangle vs = SystemInformation.VirtualScreen; x = vs.X; y = vs.Y; w = vs.Width; h = vs.Height;
            }
            if (w <= 0 || h <= 0) { Log.Warn("GLASS", "显示器尺寸异常 " + w + "x" + h + "，放弃"); return; }

            string wp = null;
            try
            {
                using (Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                    if (rk != null) wp = rk.GetValue("WallPaper") as string;
            }
            catch (Exception exw) { Log.Warn("GLASS", "读壁纸注册表失败：" + exw.Message); }

            string bgFile = Path.Combine(uiDir, "bg.png");
            string key = x + "," + y + " " + w + "x" + h + "|" + wp;
            if (key == lastBgKey && File.Exists(bgFile))
            {
                Log.Info("GLASS", "底图没变，跳过重抓（" + key + "）判定耗时=" + swc.ElapsedMilliseconds + "ms");
                return;
            }

            bgBusy = true;
            int bx = x, by = y, bw = w, bh = h;
            string bwp = wp, bkey = key, bfile = bgFile;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Stopwatch wsw = Stopwatch.StartNew();
                bool ok = false;
                try
                {
                    using (Bitmap bmp = new Bitmap(bw, bh))
                    {
                        if (!string.IsNullOrEmpty(bwp) && File.Exists(bwp))
                        {
                            try
                            {
                                using (Image src = Image.FromFile(bwp))
                                using (Graphics g = Graphics.FromImage(bmp))
                                {
                                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                                    double s = Math.Max((double)bw / src.Width, (double)bh / src.Height);
                                    int dw = (int)Math.Ceiling(src.Width * s), dh = (int)Math.Ceiling(src.Height * s);
                                    g.DrawImage(src, new Rectangle((bw - dw) / 2, (bh - dh) / 2, dw, dh));
                                    ok = true;
                                }
                            }
                            catch (Exception ex) { Log.Warn("GLASS", "读壁纸文件失败，回退截屏：" + ex.Message); ok = false; }
                        }
                        if (!ok)
                        {
                            using (Graphics g = Graphics.FromImage(bmp))
                                g.CopyFromScreen(bx, by, 0, 0, new Size(bw, bh));
                        }
                        // 先写临时文件再原子替换：WebView2 可能正读着 bg.png，
                        // 直接 Save 覆盖会撞「GDI+ 中发生一般性错误」
                        string tmp = bfile + ".new";
                        bmp.Save(tmp, ImageFormat.Png);
                        bool moved = false;
                        for (int attempt = 0; attempt < 6 && !moved; attempt++)
                        {
                            try
                            {
                                if (File.Exists(bfile)) File.Delete(bfile);
                                File.Move(tmp, bfile);
                                moved = true;
                            }
                            catch (Exception exm)
                            {
                                Log.Warn("GLASS", "替换 bg.png 第 " + (attempt + 1) + " 次失败：" + exm.Message);
                                Thread.Sleep(80);
                            }
                        }
                        if (!moved) Log.Err("GLASS", "bg.png 替换失败，底图可能不是最新", null);
                    }
                    lastBgKey = bkey;
                    Interlocked.Increment(ref bgVer);
                    Log.Info("GLASS", "底图已更新(后台) 来源=" + (ok ? "壁纸文件" : "屏幕截图")
                        + " 显示器=" + bx + "," + by + " " + bw + "x" + bh
                        + (ok ? " 壁纸=" + bwp : "") + " 耗时=" + wsw.ElapsedMilliseconds + "ms");
                }
                catch (Exception ex) { Log.Err("GLASS", "后台抓底图失败", ex); }
                finally { bgBusy = false; }
            });
        }
        // 从壁纸取色：底色用平均色，强调色用最鲜艳的一块
        private void SendPalette()
        {
            try
            {
                string f = Path.Combine(uiDir, "bg.png");
                if (!File.Exists(f)) return;
                int br = 0, bg2 = 0, bb = 0;
                double best = -1;
                using (Bitmap src = new Bitmap(f))
                using (Bitmap bmp = new Bitmap(src, 40, 24))
                {
                    long ar = 0, ag = 0, ab = 0; int n = 0;
                    for (int y = 0; y < bmp.Height; y++)
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            Color c = bmp.GetPixel(x, y);
                            ar += c.R; ag += c.G; ab += c.B; n++;
                            int mx = Math.Max(c.R, Math.Max(c.G, c.B));
                            int mn = Math.Min(c.R, Math.Min(c.G, c.B));
                            double sat = mx == 0 ? 0 : (double)(mx - mn) / mx;
                            double val = mx / 255.0;
                            double score = sat * 0.9 + val * 0.1;
                            if (score > best) { best = score; br = c.R; bg2 = c.G; bb = c.B; }
                        }
                    if (n > 0)
                    {
                        string baseHex = string.Format("#{0:x2}{1:x2}{2:x2}", ar / n, ag / n, ab / n);
                        string accHex = string.Format("#{0:x2}{1:x2}{2:x2}", br, bg2, bb);
                        Send(new { t = "palette", baseColor = baseHex, accent = accHex });
                    }
                }
            }
            catch { }
        }

        private void OnWebInit(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                MessageBox.Show("WebView2 启动失败：" + (e.InitializationException == null ? "未知" : e.InitializationException.Message), "错误");
                return;
            }
            core = web.CoreWebView2;
            try { Log.Info("WEB", "WebView2 就绪，运行时版本 = " + core.Environment.BrowserVersionString); }
            catch { }
            try { core.Settings.AreDefaultContextMenusEnabled = false; } catch { }
            try { core.Settings.IsStatusBarEnabled = false; } catch { }
            try { core.Settings.AreBrowserAcceleratorKeysEnabled = false; } catch { }
            try
            {
                Color bgc = dark ? Color.FromArgb(11, 14, 21) : Color.FromArgb(238, 242, 248);
                web.DefaultBackgroundColor = bgc;
                Log.Info("WEB", "DefaultBackgroundColor = " + bgc.R + "," + bgc.G + "," + bgc.B);
            }
            catch (Exception ex) { Log.Warn("WEB", "DefaultBackgroundColor 设置失败: " + ex.Message); }

            string html = LoadUi();
            if (html == null) { MessageBox.Show("UI 资源缺失", "错误"); return; }
            File.WriteAllText(Path.Combine(uiDir, "index.html"), html, new UTF8Encoding(false));
            if (!File.Exists(Path.Combine(uiDir, "bg.png"))) CaptureDesktop();

            core.WebMessageReceived += OnWebMessage;
            core.SetVirtualHostNameToFolderMapping("app.local", uiDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate("https://app.local/index.html");
            ready = true;
            core.NavigationCompleted += delegate (object s2, CoreWebView2NavigationCompletedEventArgs e2)
            {
                try
                {
                    Log.Info("WEB", "页面导航完成 success=" + e2.IsSuccess + " httpStatus=" + e2.HttpStatusCode
                        + (e2.IsSuccess ? "" : " 错误=" + e2.WebErrorStatus));
                }
                catch { }
                try
                {
                    core.ExecuteScriptAsync("JSON.stringify({w:innerWidth,h:innerHeight,dpr:devicePixelRatio,ow:outerWidth,oh:outerHeight})")
                        .ContinueWith(delegate (System.Threading.Tasks.Task<string> t)
                        {
                            try { Log.Info("WEB", "页面视口 " + t.Result + " | web=" + web.Width + "x" + web.Height + " | form=" + ClientSize.Width + "x" + ClientSize.Height + " | DeviceDpi=" + DeviceDpi); }
                            catch { }
                        });
                }
                catch { }
            };

            // 渲染进程崩溃 / 被杀 → WebView2 会自动重启，屏幕上就是「闪一下」。必须留痕
            core.ProcessFailed += delegate (object s2, CoreWebView2ProcessFailedEventArgs e3)
            {
                try
                {
                    Log.Err("WEB", "渲染进程异常 kind=" + e3.ProcessFailedKind
                        + " 退出码=" + e3.ExitCode
                        + " reason=" + e3.Reason
                        + " 进程=" + e3.ProcessDescription, null);
                }
                catch { }
                try { Log.Warn("WEB", "WebView2 自动恢复：重新加载界面"); core.Reload(); }
                catch (Exception ex) { Log.Err("WEB", "重新加载失败", ex); }
            };
        }

        private static string LoadUi()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string n in asm.GetManifestResourceNames())
                    if (n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
                    {
                        using (Stream s = asm.GetManifestResourceStream(n))
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            return sr.ReadToEnd();
                    }
                return null;
            }
            catch { return null; }
        }

        private void Send(object payload)
        {
            if (!ready || core == null) return;
            string json;
            try { json = new JavaScriptSerializer().Serialize(payload); }
            catch { return; }
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try { if (core != null) core.PostWebMessageAsJson(json); } catch { }
                    });
            }
            catch { }
        }

        private void UILog(string m) { Log.Info("UI", m); Send(new { t = "log", m = m }); }
        private void UIStatus(string m) { Send(new { t = "status", m = m }); }
        private void UIBusy(bool v) { Send(new { t = "busy", v = v }); }
        private void UIProgress(bool indet, int v, int max) { Send(new { t = "progress", indet = indet, v = v, max = max }); }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try { msg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch (Exception ex) { Log.Warn("MSG", "界面消息解析失败：" + ex.Message + " raw=" + (e.WebMessageAsJson.Length > 200 ? e.WebMessageAsJson.Substring(0, 200) : e.WebMessageAsJson)); return; }
            if (msg == null) return;
            try
            {
                string mt = msg.ContainsKey("t") ? Convert.ToString(msg["t"]) : "?";
                if (mt != "progress" && mt != "batch")
                    Log.Info("MSG", "← " + mt + (mt == "setdir" && msg.ContainsKey("v") ? " [" + Convert.ToString(msg["v"]) + "]" : ""));
            }
            catch { }
            string t = Str(msg, "t");

            if (t == "ready")
            {
                Send(new { t = "theme", dark = dark });
                Send(new { t = "proxy", v = Http.Proxy });
                Send(new { t = "dir", path = saveDir });
                Send(new { t = "opt", key = "auto", value = autoDl });
                Send(new { t = "opt", key = "strict", value = strict });
                Send(new { t = "proxy", v = Http.Proxy });
                Send(new { t = "bg", v = bgVer });   // 底图没换就发同一个值，界面原地不动
                Send(new { t = "size", w = ClientSize.Width, h = ClientSize.Height });
                SendPalette();
                Thread th = new Thread(delegate ()
                {
                    try
                    {
                        Domains.Ensure(false, UILog);
                        Send(new { t = "hosts", m = "紳士 " + Domains.Wnacg + " · 禁漫 " + Domains.Jm });
                    }
                    catch { }
                });
                th.IsBackground = true;
                th.Start();
                return;
            }
            if (t == "vp2")
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "benzi_vp2.txt"),
                        "inner=" + Str(msg, "w") + "x" + Str(msg, "h") +
                        " vh=" + Str(msg, "vh") + " appH=" + Str(msg, "appH") +
                        " scrollH=" + Str(msg, "scrollH") + " web=" + web.Width + "x" + web.Height +
                        " client=" + ClientSize.Width + "x" + ClientSize.Height);
                }
                catch { }
                return;
            }
            if (t == "proxy")
            {
                string v = Str(msg, "v").Trim();
                Http.Proxy = v;
                cfg.Proxy = v;
                cfg.Save();
                UILog("  代理已设为: " + (v.Length == 0 ? "（直连）" : v));
                return;
            }
            if (t == "refresh")
            {
                Thread th0 = new Thread(delegate ()
                {
                    UILog("  重新解析站点地址…");
                    try
                    {
                        Domains.Ensure(true, UILog);
                        Send(new { t = "hosts", m = "紳士 " + Domains.Wnacg + " · 禁漫 " + Domains.Jm });
                        UILog("  ✓ 已更新");
                    }
                    catch (Exception ex) { UILog("  刷新失败: " + ex.Message); }
                });
                th0.IsBackground = true;
                th0.Start();
                return;
            }
            if (t == "drag")
            {
                // 无边框窗口：由页面顶栏按下时发起拖动，交给系统走标准的 caption 拖拽
                ReleaseCapture();
                SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
                return;
            }
            if (t == "dblclick")
            {
                WindowState = (WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized;
                return;
            }
            if (t == "theme") { dark = Bool(msg, "dark"); return; }
            if (t == "opt")
            {
                string k = Str(msg, "key");
                bool v = Bool(msg, "value");
                if (k == "auto") autoDl = v;
                if (k == "strict") strict = v;
                cfg.AutoDownload = autoDl;
                cfg.Strict = strict;
                cfg.Save();
                return;
            }
            if (t == "win")
            {
                string act = Str(msg, "act");
                if (act == "min") WindowState = FormWindowState.Minimized;
                else if (act == "max") WindowState = (WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized;
                else if (act == "close") Close();
                return;
            }
            if (t == "paste")
            {
                string cb = "";
                try { if (Clipboard.ContainsText()) cb = Clipboard.GetText(); } catch { }
                Send(new { t = "pastev", v = cb });
                return;
            }
            if (t == "setdir")
            {
                string raw = Str(msg, "v").Trim();
                // 从资源管理器「复制路径」常带引号，粘贴进来的先剥掉
                if (raw.Length >= 2 && ((raw[0] == '"' && raw[raw.Length - 1] == '"') || (raw[0] == '\'' && raw[raw.Length - 1] == '\'')))
                    raw = raw.Substring(1, raw.Length - 2).Trim();
                try { raw = Environment.ExpandEnvironmentVariables(raw); } catch { }
                if (raw.Length == 0)
                {
                    Send(new { t = "dir", path = saveDir });
                    return;
                }
                try
                {
                    bool abs = raw.StartsWith(@"\\") ||
                               (raw.Length >= 3 && char.IsLetter(raw[0]) && raw[1] == ':' && (raw[2] == '\\' || raw[2] == '/'));
                    if (!abs) { Send(new { t = "direrr", m = "要完整路径，例如 E:\\本子下载" }); return; }
                    if (!Directory.Exists(raw)) Directory.CreateDirectory(raw);
                    saveDir = Path.GetFullPath(raw);
                    if (saveDir.Length > 3 && saveDir.EndsWith("\\")) saveDir = saveDir.TrimEnd('\\');
                    cfg.SaveDir = saveDir;
                    cfg.Save();
                    Send(new { t = "dir", path = saveDir });
                    UILog("  保存目录已改为: " + saveDir);
                }
                catch (Exception ex)
                {
                    Send(new { t = "direrr", m = "目录不可用：" + ex.Message });
                    UILog("  目录设置失败: " + ex.Message);
                }
                return;
            }
            if (t == "browse")
            {
                try
                {

                    FolderBrowserDialog d = new FolderBrowserDialog();
                    if (Directory.Exists(saveDir)) d.SelectedPath = saveDir;
                    d.Description = "选择本子保存目录";
                    DialogResult r = d.ShowDialog(this);

                    if (r == DialogResult.OK)
                    {
                        saveDir = d.SelectedPath;
                        Send(new { t = "dir", path = saveDir });
                        cfg.SaveDir = saveDir;
                        cfg.Save();
                    }
                }
                catch (Exception ex) { UILog("  打开目录选择失败: " + ex.Message); }
                return;
            }
            if (t == "search")
            {
                if (busy) { UILog("还在处理上一批，请稍等"); return; }
                string text = Str(msg, "text");
                string dir = Str(msg, "dir");
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) saveDir = dir;
                StartSearch(text, Bool(msg, "auto"));
                return;
            }
            if (t == "download")
            {
                if (busy) { UILog("正在下载，请稍等"); return; }
                int idx = (int)Num(msg, "index", -1);
                if (idx >= 0 && idx < hits.Count) StartDownload(hits[idx]);
                return;
            }
        }

        private static string Str(Dictionary<string, object> d, string k)
        {
            object v; if (!d.TryGetValue(k, out v) || v == null) return ""; return v.ToString();
        }
        private static bool Bool(Dictionary<string, object> d, string k)
        {
            object v; if (!d.TryGetValue(k, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            string s = v.ToString().ToLowerInvariant();
            return s == "true" || s == "1";
        }
        private static double Num(Dictionary<string, object> d, string k, double def)
        {
            object v; if (!d.TryGetValue(k, out v) || v == null) return def;
            double n; return double.TryParse(v.ToString(), out n) ? n : def;
        }

        private void StartSearch(string text, bool auto)
        {
            List<string> lines = new List<string>();
            foreach (string l in text.Replace("\r", "").Split('\n'))
                if (l.Trim().Length > 0) lines.Add(l.Trim());
            if (lines.Count == 0) { UILog("没有可搜索的内容"); return; }

            busy = true; cancelled = false;
            UIBusy(true);
            hits = new List<Hit>();
            UIProgress(true, 0, 0);

            Thread th = new Thread(delegate ()
            {
                try
                {
                    bool first = true;
                    foreach (string line in lines)
                    {
                        if (cancelled) break;
                        UILog("──────── " + line);
                        UIProgress(true, 0, 0);
                        List<Hit> found = SearchOne(line, auto, first);
                        first = false;
                        if (found.Count == 0) UILog("  ✗ 没找到匹配");
                    }
                }
                catch (Exception ex) { UILog("异常: " + ex.Message); }
                finally
                {
                    busy = false;
                    UIProgress(false, 0, 1);
                    UIBusy(false);
                    UIStatus("就绪");
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private List<Hit> SearchOne(string raw, bool auto, bool showList)
        {
            List<string> queries = Rank.BuildQueries(raw);
            if (queries.Count == 0) queries.Add(raw);
            double threshold = strict ? 0.60 : 0.42;

            List<Hit> all = new List<Hit>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();

            foreach (string q in queries)
            {
                if (cancelled) break;
                UIStatus("搜索: " + q);
                UILog("  检索词: " + q);
                Add(all, seen, SafeSearch(delegate { return Wnacg.Search(q, UILog); }));
                Add(all, seen, SafeSearch(delegate { return Nhentai.Search(q, UILog); }));
                Add(all, seen, SafeSearch(delegate { return Jm.Search(q, UILog); }));

                foreach (Hit h in all)
                {
                    double s = Math.Max(Rank.Sim(q, h.Title), Rank.Sim(raw, h.Title));
                    if (!string.IsNullOrEmpty(h.Note)) s = Math.Max(s, Rank.Sim(q, h.Note));
                    if (s > h.Score) h.Score = s;
                }
                bool good = false;
                foreach (Hit h in all) if (h.Score >= 0.80) { good = true; break; }
                if (good) break;
            }

            List<Hit> kept = new List<Hit>();
            foreach (Hit h in all) if (h.Score >= threshold) kept.Add(h);
            kept.Sort(Rank.CompareHits);

            hits = kept;
            if (showList) PushHits(kept);
            if (kept.Count == 0) return kept;

            Hit best = kept[0];
            UILog("  → 选中 [" + best.SiteName + "] " + best.Title);
            if (auto && !cancelled) DoDownload(best);
            return kept;
        }

        private void PushHits(List<Hit> list)
        {
            List<object> items = new List<object>();
            foreach (Hit h in list)
                items.Add(new { site = h.SiteName, label = h.Label, title = h.Title, pages = h.Pages, score = Math.Round(h.Score, 2), size = h.Size });
            Send(new { t = "hits", items = items.ToArray() });
        }

        private List<Hit> SafeSearch(Func<List<Hit>> f)
        {
            try { return f(); }
            catch (Exception ex) { UILog("  搜索异常: " + ex.Message); return new List<Hit>(); }
        }

        private static void Add(List<Hit> all, Dictionary<string, bool> seen, List<Hit> src)
        {
            foreach (Hit h in src)
            {
                string k = h.Site + "|" + h.Id;
                if (seen.ContainsKey(k)) continue;
                seen[k] = true;
                all.Add(h);
            }
        }

        private void StartDownload(Hit h)
        {
            busy = true; cancelled = false;
            UIBusy(true);
            Thread th = new Thread(delegate ()
            {
                try { DoDownload(h); }
                catch (Exception ex) { UILog("异常: " + ex.Message); }
                finally { busy = false; UIProgress(false, 0, 1); UIBusy(false); UIStatus("就绪"); }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void DoDownload(Hit h)
        {
            UILog("  开始下载: " + h.Title);
            UIStatus("下载: " + h.SiteName);
            UIProgress(true, 0, 0);
            Action<int, int> prog = delegate (int done, int total) { UIProgress(false, done, total); };
            Func<bool> cn = delegate { return cancelled; };
            bool ok = false;
            if (h.Site == "wnacg") ok = Wnacg.Download(h, saveDir, UILog, prog, cn);
            else if (h.Site == "nhentai") ok = Nhentai.Download(h, saveDir, UILog, prog, cn);
            else if (h.Site == "jm") ok = Jm.Download(h, saveDir, UILog, prog, cn);
            UILog(ok ? "  ✓ 下载完成" : "  ✗ 下载失败");
        }

        [STAThread]
        public static void Main()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "benzihunter_err.txt"), DateTime.Now + " " + e.ExceptionObject + "\r\n"); } catch { }
            };
            try { Application.Run(new AppHost()); }
            catch (Exception ex) { MessageBox.Show("启动失败: " + ex.Message, "错误"); }
        }
    }
}
