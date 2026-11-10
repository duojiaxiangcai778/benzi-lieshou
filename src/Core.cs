using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace BenziHunter
{
    internal static class Http
    {
        public const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        public static string Proxy = "http://127.0.0.1:7890";
        private static readonly Dictionary<string, CookieContainer> jars = new Dictionary<string, CookieContainer>();
        private static readonly object jarLock = new object();

        public static void Init()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.Expect100Continue = false;
            try { ServicePointManager.ServerCertificateValidationCallback = delegate { return true; }; } catch { }
        }

        public static CookieContainer Jar(string host)
        {
            lock (jarLock)
            {
                if (!jars.ContainsKey(host)) jars[host] = new CookieContainer();
                return jars[host];
            }
        }

        private static HttpWebRequest Make(string url, bool useProxy, int timeoutSec, string referer)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA;
            req.Timeout = timeoutSec * 1000;
            req.ReadWriteTimeout = timeoutSec * 1000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.CookieContainer = Jar(req.RequestUri.Host);
            req.AllowAutoRedirect = true;
            req.KeepAlive = true;
            req.Accept = "*/*";
            if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
            if (useProxy && !string.IsNullOrEmpty(Proxy)) req.Proxy = new WebProxy(Proxy, true);
            else req.Proxy = null;
            return req;
        }

        public static string GetText(string url, bool useProxy, int timeoutSec, string referer, out int code)
        {
            code = 0;
            string text = null;
            for (int attempt = 0; attempt < 3 && text == null; attempt++)
            {
                try
                {
                    HttpWebRequest req = Make(url, useProxy, timeoutSec, referer);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        code = (int)resp.StatusCode;
                        using (Stream s = resp.GetResponseStream())
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                            text = sr.ReadToEnd();
                    }
                }
                catch (WebException we)
                {
                    HttpWebResponse r = we.Response as HttpWebResponse;
                    if (r != null) { code = (int)r.StatusCode; try { r.Close(); } catch { } }
                    if (attempt < 2) Thread.Sleep(1200);
                }
                catch
                {
                    if (attempt < 2) Thread.Sleep(1200);
                }
            }
            return text;
        }

        // 自动：先直连，失败再走代理
        public static string GetAuto(string url, int timeoutSec, string referer)
        {
            int code;
            string t = GetText(url, false, timeoutSec, referer, out code);
            if (!string.IsNullOrEmpty(t) && (code == 200 || code == 304)) return t;
            t = GetText(url, true, timeoutSec, referer, out code);
            if (!string.IsNullOrEmpty(t) && (code == 200 || code == 304)) return t;
            return t;
        }

        // 返回 200 才算成功
        public static bool Probe(string url, bool useProxy, int timeoutSec, string mustContain)
        {
            int code;
            string t = GetText(url, useProxy, timeoutSec, null, out code);
            if (code != 200 || t == null) return false;
            if (!string.IsNullOrEmpty(mustContain) && t.IndexOf(mustContain, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        public static long GetSize(string url, bool useProxy, string referer)
        {
            for (int k = 0; k < 3; k++)
            {
                try
                {
                    HttpWebRequest req = Make(url, useProxy, 60, referer);
                    req.AddRange(0, 0);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        string cr = resp.Headers["Content-Range"];
                        if (!string.IsNullOrEmpty(cr))
                        {
                            Match m = Regex.Match(cr, @"/(\d+)\s*$");
                            if (m.Success) return long.Parse(m.Groups[1].Value);
                        }
                        if (resp.ContentLength > 0) return resp.ContentLength;
                    }
                }
                catch { Thread.Sleep(1500); }
            }
            return -1;
        }

        // 下载一段字节写文件（支持 Range），返回实际写入字节数
        public static long DownloadRange(string url, string path, bool useProxy, string referer, long from, long to, int timeoutSec)
        {
            try
            {
                HttpWebRequest req = Make(url, useProxy, timeoutSec, referer);
                if (to == long.MaxValue) req.AddRange(from);
                else req.AddRange(from, to);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode != 200 && (int)resp.StatusCode != 206) return -1;
                    long n = 0;
                    byte[] buf = new byte[65536];
                    using (Stream s = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    {
                        int r;
                        while ((r = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, r);
                            n += r;
                        }
                    }
                    return n;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public static bool DownloadSimple(string url, string path, bool useProxy, string referer, int timeoutSec)
        {
            long n = DownloadRange(url, path, useProxy, referer, 0, long.MaxValue, timeoutSec);
            return n > 0;
        }
    }

    // ---------------- 配置 ----------------
    internal class Config
    {
        public string SaveDir = "";
        public string Proxy = "http://127.0.0.1:7890";
        public bool AutoDownload = true;
        public bool Dark = true;
        public bool Glass = true;
        public bool Strict = false;
        public string WnacgHost = "";
        public string JmHost = "";
        public string WnacgHostTime = "";
        public string JmHostTime = "";

        public static string Path
        {
            get
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BenziHunter");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "config.json");
            }
        }

        public static Config Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    string s = File.ReadAllText(Path, Encoding.UTF8);
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    Config c = js.Deserialize<Config>(s);
                    if (c != null) return c;
                }
            }
            catch { }
            Config d = new Config();
            d.SaveDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "本子下载");
            return d;
        }

        public void Save()
        {
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                File.WriteAllText(Path, js.Serialize(this), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ---------------- 域名解析（发布页） ----------------
    internal static class Domains
    {
        public const string WnacgPublish = "https://wnacg01.link/";
        public const string JmPublish = "https://jmcomictt.site/";

        private static readonly string[] WnacgDefaults = { "www.wn10.cfd", "www.wnacg.buzz", "www.wnacg.site" };
        private static readonly string[] JmDefaults = { "comic18j-nico.site", "comic18j-ada.work", "comic18j-moco.work", "18comic.vip", "18comic.ink", "jmcomic-zzz.one" };

        public static string Wnacg = "";
        public static string Jm = "";

        public static void Ensure(bool force, Action<string> log)
        {
            if (force || string.IsNullOrEmpty(Wnacg) || !HostOk(Wnacg, "photos-index"))
                Wnacg = ResolveWnacg(log);
            if (force || string.IsNullOrEmpty(Jm) || !HostOk(Jm, "album/"))
                Jm = ResolveJm(log);
        }

        private static bool HostOk(string host, string marker)
        {
            string url = "https://" + host + "/";
            int code;
            string t = Http.GetText(url, true, 15, null, out code);
            if (t == null || code != 200) return false;
            if (!string.IsNullOrEmpty(marker)) return true;
            return t.Length > 1000;
        }

        private static string ResolveWnacg(Action<string> log)
        {
            List<string> cands = new List<string>();
            string page = Http.GetAuto(WnacgPublish, 20, null);
            if (page != null)
            {
                MatchCollection ms = Regex.Matches(page, @"href=""(https?://[^""]+)""");
                foreach (Match m in ms)
                {
                    string u = m.Groups[1].Value;
                    Match hm = Regex.Match(u, @"^https?://([^/]+)/?$");
                    if (!hm.Success) continue;
                    string h = hm.Groups[1].Value;
                    if (h.IndexOf("wnacg01", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (h.IndexOf("wnacg02", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (h.IndexOf("google", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (!cands.Contains(h)) cands.Add(h);
                }
            }
            foreach (string d in WnacgDefaults) if (!cands.Contains(d)) cands.Add(d);

            foreach (string h in cands)
            {
                string testUrl = "https://" + h + "/search/?q=elf&f=_all&s=create_time_DESC&syn=yes";
                int code;
                string t = Http.GetText(testUrl, true, 25, null, out code);
                if (t != null && code == 200 && t.IndexOf("photos-index-aid-", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (log != null) log("wnacg 域名: " + h);
                    return h;
                }
            }
            if (log != null) log("wnacg 域名解析失败，回退 " + WnacgDefaults[0]);
            return WnacgDefaults[0];
        }

        private static string ResolveJm(Action<string> log)
        {
            List<string> cands = new List<string>();
            string page = Http.GetAuto(JmPublish, 20, null);
            if (page != null)
            {
                MatchCollection ms = Regex.Matches(page, @"<span>\s*([a-z0-9\-\.]+\.[a-z]{2,6})\s*</span>", RegexOptions.IgnoreCase);
                foreach (Match m in ms)
                {
                    string h = m.Groups[1].Value.ToLower();
                    if (h.IndexOf("jm-uu") >= 0) continue;
                    if (!cands.Contains(h)) cands.Add(h);
                }
            }
            foreach (string d in JmDefaults) if (!cands.Contains(d)) cands.Add(d);

            // 已知好用的域名排前面，再试发布页给的其余候选
            List<string> ordered = new List<string>();
            foreach (string d in JmDefaults) if (!ordered.Contains(d)) ordered.Add(d);
            foreach (string d in cands) if (!ordered.Contains(d)) ordered.Add(d);

            // 先找能打开的，再验证它真的能搜（有些镜像首页 200 但搜索接口挂）
            List<string> alive = new List<string>();
            foreach (string h in ordered)
            {
                int code;
                string t = Http.GetText("https://" + h + "/", false, 12, null, out code);
                if (t == null || code != 200) t = Http.GetText("https://" + h + "/", true, 12, null, out code);
                if (t != null && code == 200 && t.IndexOf("禁漫", StringComparison.OrdinalIgnoreCase) >= 0) alive.Add(h);
            }
            foreach (string h in alive)
            {
                string test = "https://" + h + "/search/photos?search_query=elf&page=1&main_tag=0";
                int c2;
                string r = Http.GetText(test, false, 25, null, out c2);
                if (r == null || c2 != 200) r = Http.GetText(test, true, 25, null, out c2);
                if (r != null && c2 == 200 && r.IndexOf("/album/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (log != null) log("JM 域名: " + h);
                    return h;
                }
            }
            if (log != null) log("JM 域名解析失败，回退 " + JmDefaults[0]);
            return JmDefaults[0];
        }
    }

    // ---------------- 标题处理 / 匹配 / 分级 ----------------
    internal static class Rank
    {
        public const int P_GROUP = 1;     // 汉化组 / 字幕组 / 官方中文
        public const int P_CN = 2;        // 汉化（未标明组织）
        public const int P_PERSONAL = 3;  // 个人汉化 / 机翻
        public const int P_JP = 4;        // 日文原版

        private static readonly string[] JunkTags = {
            "dl版","無修正","无修正","中国翻訳","中国翻译","中文","汉化","漢化","中譯","中译","翻译","翻譯",
            "digital","chinese","english","英訳","日本語","ongoing","進行中","进行中","completed","已完成",
            "decensored","uncensored","ai翻译","ai翻譯","机翻","機翻","修正版","黑条修正","无码","無碼",
            "单行本","單行本","同人志","同人誌","合集","整合","高清","全彩","zip","rar","7z","cbr","cbz"
        };

        public static string CleanInput(string raw)
        {
            if (raw == null) return "";
            string s = raw.Trim();
            s = Regex.Replace(s, @"\.(zip|rar|7z|cbr|cbz|pdf|epub)$", "", RegexOptions.IgnoreCase);
            s = s.Trim();
            return s;
        }

        // 生成候选检索词（从完整到关键字）
        public static List<string> BuildQueries(string raw)
        {
            List<string> qs = new List<string>();
            string s = CleanInput(raw);
            if (s.Length == 0) return qs;

            // 去掉括号里的垃圾标签
            string noTag = Regex.Replace(s, @"[\[\(（【]([^\[\]\(\)（）【】]{0,40})[\]\)）】]", delegate(Match m)
            {
                string inner = m.Groups[1].Value;
                foreach (string j in JunkTags)
                    if (inner.ToLower().IndexOf(j, StringComparison.OrdinalIgnoreCase) >= 0) return " ";
                return m.Value;
            });

            // 去掉残留的方括号包裹（作者、社团）
            string flat = Regex.Replace(noTag, @"[\[\]【】\(\)（）]", " ");
            flat = Regex.Replace(flat, @"\s+", " ").Trim();
            if (flat.Length >= 2 && !qs.Contains(flat)) qs.Add(flat);

            // 取最长的日文片段
            string[] segs = Regex.Split(noTag, @"[\[\]【】（）\(\)\s]+");
            string best = "";
            foreach (string seg in segs)
            {
                string t = Regex.Replace(seg, @"[\.\-_]+", " ").Trim();
                if (t.Length < 2) continue;
                if (IsJunkSegment(t)) continue;
                int jp = CountJp(t);
                if (jp > CountJp(best) || (jp == CountJp(best) && t.Length > best.Length)) best = t;
            }
            if (best.Length >= 2 && !qs.Contains(best)) qs.Add(best);

            // 按・ 或 空白 切出前半段
            string[] parts = Regex.Split(best, @"[・·\s]+");
            if (parts.Length > 1 && parts[0].Length >= 4 && !qs.Contains(parts[0])) qs.Add(parts[0]);

            // 截断兜底
            if (flat.Length > 14 && !qs.Contains(flat.Substring(0, 14))) qs.Add(flat.Substring(0, 14));
            if (qs.Count > 4) qs.RemoveRange(4, qs.Count - 4);
            return qs;
        }

        private static bool IsJunkSegment(string t)
        {
            string lo = t.ToLower();
            foreach (string j in JunkTags)
                if (lo == j.ToLower() || lo.IndexOf(j.ToLower(), StringComparison.OrdinalIgnoreCase) == 0 && t.Length < j.Length + 3)
                    return true;
            return false;
        }

        private static int CountJp(string s)
        {
            int n = 0;
            foreach (char c in s)
            {
                if ((c >= '\u3040' && c <= '\u30ff') || (c >= '\u4e00' && c <= '\u9fff')) n++;
                else if (char.IsLetterOrDigit(c)) n++;
            }
            return n;
        }

        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder();
            s = s.ToLowerInvariant();
            foreach (char c in s)
            {
                if (c >= '！' && c <= '～') sb.Append((char)(c - 0xFEE0));
                else sb.Append(c);
            }
            string t = sb.ToString();
            t = Regex.Replace(t, @"\[[^\]]*\]", " ");
            t = Regex.Replace(t, @"\([^\)]*\)", " ");
            t = Regex.Replace(t, @"[^0-9a-z\u3040-\u30ff\u4e00-\u9fff]", "");
            return t;
        }

        public static double Sim(string a, string b)
        {
            string x = Norm(a), y = Norm(b);
            if (x.Length == 0 || y.Length == 0) return 0;
            if (x == y) return 1.0;
            if (x.Length >= 4 && y.IndexOf(x, StringComparison.Ordinal) >= 0) return 0.96;
            if (y.Length >= 4 && x.IndexOf(y, StringComparison.Ordinal) >= 0) return 0.93;
            HashSet<string> A = Bigrams(x), B = Bigrams(y);
            if (A.Count == 0 || B.Count == 0) return 0;
            int inter = 0;
            foreach (string k in A) if (B.Contains(k)) inter++;
            int uni = A.Count + B.Count - inter;
            double j = uni == 0 ? 0 : (double)inter / uni;
            // 长度差异过大打折
            double lenRatio = (double)Math.Min(x.Length, y.Length) / Math.Max(x.Length, y.Length);
            return j * (0.6 + 0.4 * lenRatio);
        }

        private static HashSet<string> Bigrams(string s)
        {
            HashSet<string> set = new HashSet<string>();
            for (int i = 0; i + 1 < s.Length; i++) set.Add(s.Substring(i, 2));
            if (s.Length == 1) set.Add(s);
            return set;
        }

        public static int Classify(string title, out string label)
        {
            string t = (title ?? "").ToLower();
            bool cn = false, grp = false, per = false;
            string[] cnMarks = { "中国翻訳", "中国翻译", "中文", "汉化", "漢化", "中譯", "中译", "翻譯", "翻译", "chinese", "中文版", "未来数位", "未來數位", "东立", "東立", "台湾", "台灣", "中文單行本" };
            string[] grpMarks = { "汉化组", "漢化組", "字幕组", "字幕組", "工作组", "工作室", "家族社", "汉化社", "漢化社", "team", "group", "扫图组", "掃圖組", "汉化組", "漫画组" };
            string[] perMarks = { "个人汉化", "個人漢化", "个人翻译", "個人翻譯", "个人重嵌", "个人制作", "personal translation", "personal scanlation", "personal work", "机翻", "機翻", "ai翻译", "ai翻譯", "自翻", "渣翻", "自己翻" };
            string[] official = { "未来数位", "未來數位", "东立", "東立", "青文", "角川", "尖端" };

            foreach (string m in cnMarks) if (t.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) { cn = true; break; }
            foreach (string m in grpMarks) if (t.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) { grp = true; break; }
            foreach (string m in perMarks) if (t.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) { per = true; break; }
            foreach (string m in official) if (t.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) { cn = true; grp = true; break; }

            if (!cn && (t.IndexOf("[]", StringComparison.Ordinal) < 0) && !grp) { label = "日文原版"; return P_JP; }
            if (cn)
            {
                if (grp) { label = "汉化组/字幕组"; return P_GROUP; }
                if (per) { label = "个人汉化"; return P_PERSONAL; }
                label = "汉化"; return P_CN;
            }
            label = "日文原版";
            return P_JP;
        }

        // 综合排序：先「非日文原版」，再「完全匹配」，再优先级，再相似度
        public static int CompareHits(Hit a, Hit b)
        {
            int la = a.Priority == P_JP ? 1 : 0;
            int lb = b.Priority == P_JP ? 1 : 0;
            if (la != lb) return la.CompareTo(lb);
            int ta = Tier(a), tb = Tier(b);
            if (ta != tb) return ta.CompareTo(tb);
            if (a.Priority != b.Priority) return a.Priority.CompareTo(b.Priority);
            if (Math.Abs(a.Score - b.Score) > 0.001) return b.Score.CompareTo(a.Score);
            int wa = SiteWeight(a.Site), wb = SiteWeight(b.Site);
            if (wa != wb) return wa.CompareTo(wb);
            return a.Title.Length.CompareTo(b.Title.Length);
        }

        private static int Tier(Hit h)
        {
            if (h.Score >= 0.985) return 0;   // 完全一致
            if (h.Score >= 0.90) return 1;    // 包含原名
            return 2;
        }

        public static int SiteWeight(string s)
        {
            if (s == "wnacg") return 0;
            if (s == "jm") return 1;
            return 2;
        }

        public static string SafeFileName(string name, int max)
        {
            if (string.IsNullOrEmpty(name)) name = "untitled";
            StringBuilder sb = new StringBuilder();
            char[] bad = System.IO.Path.GetInvalidFileNameChars();
            foreach (char c in name)
            {
                bool ok = true;
                foreach (char b in bad) if (c == b) { ok = false; break; }
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString().Trim().TrimEnd('.');
            if (s.Length > max) s = s.Substring(0, max);
            if (s.Length == 0) s = "untitled";
            return s;
        }
    }
}
