using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

/// <summary>
/// 全局文件日志。
/// 落盘：%LOCALAPPDATA%\BenziHunter\logs\app-YYYYMMDD.log（保留 7 天）
/// 每行：日期 时间 毫秒 +启动后秒数 [级别] [模块   ] 内容
/// 级别：INF 常规 / WRN 异常但可继续 / ERR 出错（带堆栈）
/// </summary>
public static class Log
{
    private static readonly object gate = new object();
    private static readonly Stopwatch since = Stopwatch.StartNew();
    private static StreamWriter writer;
    private static string filePath;
    private static string dirPath;

    public static string FilePath { get { return filePath; } }
    public static string DirPath { get { return dirPath; } }

    public static void Init()
    {
        lock (gate)
        {
            try
            {
                dirPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BenziHunter", "logs");
                Directory.CreateDirectory(dirPath);
                Prune(dirPath);
                filePath = Path.Combine(dirPath, "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                writer = new StreamWriter(
                    new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false));
                writer.AutoFlush = true;
            }
            catch { writer = null; }
        }

        Raw("INF", "BOOT", "================== 启动 ==================");
        Raw("INF", "BOOT", "时间      = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        Raw("INF", "BOOT", "OS        = " + Environment.OSVersion.VersionString);
        Raw("INF", "BOOT", "进程位数  = " + (Environment.Is64BitProcess ? "64" : "32") + "  CLR=" + Environment.Version);
        try { Raw("INF", "BOOT", "进程 PID  = " + Process.GetCurrentProcess().Id); } catch { }
        try { Raw("INF", "BOOT", "exe 路径  = " + System.Reflection.Assembly.GetEntryAssembly().Location); } catch { }
        try { Raw("INF", "BOOT", "工作目录  = " + Environment.CurrentDirectory); } catch { }
        try
        {
            StringBuilder sb = new StringBuilder();
            foreach (System.Windows.Forms.Screen s in System.Windows.Forms.Screen.AllScreens)
                sb.Append(s.DeviceName + "=" + s.Bounds + (s.Primary ? "(主) " : " "));
            Raw("INF", "BOOT", "显示器    = " + sb.ToString().Trim());
        }
        catch { }
        Raw("INF", "BOOT", "日志文件  = " + filePath);
    }

    private static void Prune(string d)
    {
        try
        {
            foreach (string f in Directory.GetFiles(d, "app-*.log"))
            {
                try { if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    public static void Info(string area, string msg) { Raw("INF", area, msg); }
    public static void Warn(string area, string msg) { Raw("WRN", area, msg); }

    public static void Err(string area, string msg, Exception ex)
    {
        string s = msg;
        if (ex != null) s += " | " + ex.GetType().Name + ": " + ex.Message;
        Raw("ERR", area, s);
        if (ex != null && !string.IsNullOrEmpty(ex.StackTrace))
        {
            foreach (string line in ex.StackTrace.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) Raw("ERR", area, "      at " + t);
            }
        }
    }

    /// <summary>只写文件、不上界面（给高频事件用）。</summary>
    private static void Raw(string lvl, string area, string msg)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
            + " +" + since.Elapsed.TotalSeconds.ToString("0.000").PadLeft(9) + "s"
            + " [" + lvl + "] [" + (area + "        ").Substring(0, 8) + "] "
            + msg;
        lock (gate)
        {
            try { if (writer != null) writer.WriteLine(line); } catch { }
        }
    }

    public static void Close()
    {
        lock (gate)
        {
            try { if (writer != null) { writer.Flush(); writer.Dispose(); writer = null; } } catch { }
        }
    }
}
