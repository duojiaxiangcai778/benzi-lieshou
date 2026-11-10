using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace BenziHunter
{
    internal class Hit
    {
        public string Site = "";
        public string Id = "";
        public string Title = "";
        public string Label = "";
        public int Priority = 4;
        public double Score = 0;
        public int Pages = 0;
        public long Size = 0;
        public string Url = "";
        public string Note = "";

        public string SiteName
        {
            get
            {
                if (Site == "wnacg") return "紳士漫畫";
                if (Site == "nhentai") return "nhentai";
                if (Site == "jm") return "禁漫天堂";
                return Site;
            }
        }
    }

    // ============ 紳士漫畫 ============
    internal static class Wnacg
    {
        public static List<Hit> Search(string query, Action<string> log)
        {
            List<Hit> hits = new List<Hit>();
            string baseUrl = "https://" + Domains.Wnacg;
            string url = baseUrl + "/search/?q=" + Uri.EscapeDataString(query) + "&f=_all&s=create_time_DESC&syn=yes";
            int code;
            string html = Http.GetText(url, true, 30, null, out code);
            if (html == null || code != 200)
            {
                if (log != null) log("  [wnacg] 搜索失败 code=" + code);
                return hits;
            }
            MatchCollection ms = Regex.Matches(html, @"href=""/photos-index-aid-(\d+)\.html""[^>]*?title=""([^""]*)""");
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (Match m in ms)
            {
                string aid = m.Groups[1].Value;
                if (seen.ContainsKey(aid)) continue;
                seen[aid] = true;
                string title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (title.Length == 0) continue;
                Hit h = new Hit();
                h.Site = "wnacg";
                h.Id = aid;
                h.Title = title;
                h.Url = baseUrl + "/photos-index-aid-" + aid + ".html";
                string label;
                h.Priority = Rank.Classify(title, out label);
                h.Label = label;
                hits.Add(h);
            }
            if (log != null) log("  [wnacg] 命中 " + hits.Count + " 条");
            return hits;
        }

        public static bool PrepareDownload(string aid, out string fileName, out string directUrl, Action<string> log)
        {
            fileName = null; directUrl = null;
            string url = "https://" + Domains.Wnacg + "/download-index-aid-" + aid + ".html";
            int code;
            string html = null;
            for (int i = 0; i < 6 && html == null; i++)
            {
                html = Http.GetText(url, true, 30, null, out code);
                if (html != null && html.Length > 5000) break;
                html = null;
                Thread.Sleep(1500);
            }
            if (html == null) { if (log != null) log("    下载页打开失败"); return false; }

            Match fm = Regex.Match(html, @"FILE_NAME:\s*""((?:[^""\\]|\\.)*)""");
            if (fm.Success) fileName = Regex.Unescape(fm.Groups[1].Value);

            Match lm = Regex.Match(html, @"href=""(?:https?:)?//([^/""]+)/(down/[^""?]*\.zip)");
            if (!lm.Success) { if (log != null) log("    未找到直链"); return false; }
            directUrl = "https://" + lm.Groups[1].Value + "/" + lm.Groups[2].Value;
            return true;
        }

        public static bool Download(Hit hit, string dir, Action<string> log, Action<int, int> prog, Func<bool> cancelled)
        {
            string fileName, url;
            if (!PrepareDownload(hit.Id, out fileName, out url, log)) return false;
            if (string.IsNullOrEmpty(fileName)) fileName = Rank.SafeFileName(hit.Title, 120) + ".zip";
            if (!fileName.ToLower().EndsWith(".zip")) fileName += ".zip";

            long size = Http.GetSize(url, false, null);
            if (size <= 0) { if (log != null) log("    取不到文件大小"); return false; }
            hit.Size = size;
            if (log != null) log(string.Format("    {0}  ({1:N1} MB)  {2}", fileName, size / 1048576.0, url));

            string safe = Rank.SafeFileName(fileName, 140);
            if (!safe.ToLower().EndsWith(".zip")) safe += ".zip";
            string target = Path.Combine(dir, safe);
            if (File.Exists(target) && new FileInfo(target).Length == size)
            {
                if (log != null) log("    已存在，跳过");
                return true;
            }

            string tmpPart = target + ".part";
            if (!Chunker.Run(url, tmpPart, size, "w" + hit.Id, log, prog, cancelled)) return false;

            if (new FileInfo(tmpPart).Length != size)
            {
                if (log != null) log("    大小不符，放弃");
                try { File.Delete(tmpPart); } catch { }
                return false;
            }
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmpPart, target);
            }
            catch (Exception ex) { if (log != null) log("    重命名失败: " + ex.Message); return false; }
            if (log != null) log("    完成: " + target);
            return true;
        }
    }

    // ============ 分片下载器 ============
    internal static class Chunker
    {
        public const int CHUNK = 8 * 1024 * 1024;
        public const int WORKERS = 2;
        public const int MAXTRY = 5;

        public static bool Run(string url, string partPath, long total, string tag, Action<string> log, Action<int, int> prog, Func<bool> cancelled)
        {
            int n = (int)((total + CHUNK - 1) / CHUNK);
            string cacheDir = Path.Combine(Path.GetTempPath(), "benzi_chunks", tag);
            Directory.CreateDirectory(cacheDir);

            List<int> pending = new List<int>();
            for (int i = 0; i < n; i++)
            {
                string cf = Path.Combine(cacheDir, "c" + i + ".bin");
                long exp = ExpectLength(total, i, n);
                if (File.Exists(cf) && new FileInfo(cf).Length == exp) continue;
                pending.Add(i);
            }
            if (prog != null) prog(n - pending.Count, n);

            int round = 0;
            while (pending.Count > 0)
            {
                if (cancelled != null && cancelled()) { if (log != null) log("    已取消"); return false; }
                round++;
                List<int> batch = new List<int>();
                for (int k = 0; k < WORKERS && k < pending.Count; k++) batch.Add(pending[k]);

                List<Thread> ths = new List<Thread>();
                foreach (int idx in batch)
                {
                    int myIdx = idx;
                    Thread t = new Thread(delegate()
                    {
                        long s = (long)myIdx * CHUNK;
                        long e = Math.Min(s + CHUNK - 1, total - 1);
                        string cf = Path.Combine(cacheDir, "c" + myIdx + ".bin");
                        long already = 0;
                        if (File.Exists(cf)) already = new FileInfo(cf).Length;
                        if (already > 4096 && already < (e - s + 1))
                        {
                            string rest = cf + ".tmp";
                            long got = Http.DownloadRange(url, rest, false, null, s + already, e, 900);
                            if (got > 0)
                            {
                                using (FileStream fs = new FileStream(cf, FileMode.Append, FileAccess.Write))
                                using (FileStream src = File.OpenRead(rest)) src.CopyTo(fs, 65536);
                            }
                            try { File.Delete(rest); } catch { }
                        }
                        else
                        {
                            try { if (File.Exists(cf)) File.Delete(cf); } catch { }
                            Http.DownloadRange(url, cf, false, null, s, e, 900);
                        }
                    });
                    t.IsBackground = true;
                    t.Start();
                    ths.Add(t);
                }
                foreach (Thread t in ths) t.Join(900000);

                foreach (int idx in batch)
                {
                    long exp = ExpectLength(total, idx, n);
                    string cf = Path.Combine(cacheDir, "c" + idx + ".bin");
                    bool ok = File.Exists(cf) && new FileInfo(cf).Length == exp;
                    if (ok) { pending.Remove(idx); continue; }
                    if (File.Exists(cf) && new FileInfo(cf).Length < 4096) { try { File.Delete(cf); } catch { } }
                    // 计数
                    int tries = 0;
                    string mark = Path.Combine(cacheDir, "t" + idx);
                    if (File.Exists(mark)) int.TryParse(File.ReadAllText(mark), out tries);
                    tries++;
                    File.WriteAllText(mark, tries.ToString());
                    if (log != null && tries >= 2) log("    分片 " + idx + " 重试 " + tries);
                    if (tries >= MAXTRY)
                    {
                        pending.Remove(idx);
                        if (log != null) log("    分片 " + idx + " 放弃");
                    }
                }
                if (prog != null) prog(n - pending.Count, n);
                if (pending.Count > 0 && round > 1) Thread.Sleep(300);
            }

            for (int i = 0; i < n; i++)
            {
                long exp = ExpectLength(total, i, n);
                string cf = Path.Combine(cacheDir, "c" + i + ".bin");
                if (!File.Exists(cf) || new FileInfo(cf).Length != exp)
                {
                    if (log != null) log("    缺片: " + i);
                    return false;
                }
            }

            if (log != null) log("    合并中...");
            using (FileStream fs = new FileStream(partPath, FileMode.Create, FileAccess.Write))
            {
                for (int i = 0; i < n; i++)
                {
                    string cf = Path.Combine(cacheDir, "c" + i + ".bin");
                    using (FileStream src = File.OpenRead(cf)) src.CopyTo(fs, 1048576);
                    try { File.Delete(cf); } catch { }
                }
            }
            try { Directory.Delete(cacheDir, true); } catch { }
            return true;
        }

        private static long ExpectLength(long total, int i, int n)
        {
            long s = (long)i * CHUNK;
            long e = Math.Min(s + CHUNK - 1, total - 1);
            return e - s + 1;
        }
    }

    // ============ nhentai ============
    internal static class Nhentai
    {
        public const string BASE = "https://nhentai.net";

        public static List<Hit> Search(string query, Action<string> log)
        {
            List<Hit> hits = new List<Hit>();
            string url = BASE + "/api/v2/search?query=" + Uri.EscapeDataString(query);
            int code;
            string json = Http.GetText(url, true, 30, null, out code);
            if (json == null || code != 200)
            {
                if (log != null) log("  [nhentai] 搜索失败 code=" + code);
                return hits;
            }
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 8 * 1024 * 1024;
            Dictionary<string, object> root = null;
            try { root = js.Deserialize<Dictionary<string, object>>(json); }
            catch (Exception ex) { if (log != null) log("  [nhentai] JSON 解析失败: " + ex.Message); return hits; }

            if (root == null) { if (log != null) log("  [nhentai] 返回 null"); return hits; }
            object resObj;
            if (!root.TryGetValue("result", out resObj))
            {
                if (log != null) log("  [nhentai] 没有 result 字段: " + (json.Length > 200 ? json.Substring(0, 200) : json));
                return hits;
            }
            System.Collections.IEnumerable seq = resObj as System.Collections.IEnumerable;
            if (seq == null || resObj is string)
            {
                if (log != null) log("  [nhentai] result 不是数组, 类型=" + resObj.GetType().Name);
                return hits;
            }
            List<object> results = new List<object>();
            foreach (object o in seq) results.Add(o);

            foreach (object o in results)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                string jp = GetS(d, "japanese_title");
                string en = GetS(d, "english_title");
                string title = (jp != null && jp.Length > 0) ? jp : en;
                if (title == null || title.Length == 0) continue;
                Hit h = new Hit();
                h.Site = "nhentai";
                h.Id = GetS(d, "id");
                h.Title = title;
                h.Pages = GetI(d, "num_pages");
                h.Url = BASE + "/g/" + h.Id + "/";
                string label;
                h.Priority = Rank.Classify(title + " " + en, out label);
                h.Label = label;
                h.Note = en;
                hits.Add(h);
            }
            if (log != null) log("  [nhentai] 命中 " + hits.Count + " 条");
            return hits;
        }

        private static string GetS(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return "";
            return v.ToString();
        }
        private static int GetI(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return 0;
            int n;
            if (int.TryParse(v.ToString(), out n)) return n;
            return 0;
        }

        public static bool Download(Hit hit, string dir, Action<string> log, Action<int, int> prog, Func<bool> cancelled)
        {
            int code;
            string json = Http.GetText(BASE + "/api/v2/galleries/" + hit.Id, true, 40, null, out code);
            if (json == null || code != 200) { if (log != null) log("    详情页失败 code=" + code); return false; }
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 8 * 1024 * 1024;
            Dictionary<string, object> root = js.Deserialize<Dictionary<string, object>>(json);
            object pagesObj;
            if (!root.TryGetValue("pages", out pagesObj)) { if (log != null) log("    没有 pages"); return false; }
            System.Collections.IEnumerable pageSeq = pagesObj as System.Collections.IEnumerable;
            if (pageSeq == null || pagesObj is string) { if (log != null) log("    pages 类型异常: " + (pagesObj == null ? "null" : pagesObj.GetType().Name)); return false; }
            List<object> pages = new List<object>();
            foreach (object o in pageSeq) pages.Add(o);
            if (pages.Count == 0) { if (log != null) log("    没有页面"); return false; }

            string imgServer = "https://i1.nhentai.net";
            string cdn = Http.GetText(BASE + "/api/v2/cdn", true, 20, null, out code);
            if (cdn != null && code == 200)
            {
                Match m = Regex.Match(cdn, @"""(https://i\d\.nhentai\.net)""");
                if (m.Success) imgServer = m.Groups[1].Value;
            }

            string work = Path.Combine(Path.GetTempPath(), "benzi_nh_" + hit.Id);
            if (Directory.Exists(work)) { try { Directory.Delete(work, true); } catch { } }
            Directory.CreateDirectory(work);

            List<string[]> jobs = new List<string[]>();
            foreach (object po in pages)
            {
                Dictionary<string, object> pd = po as Dictionary<string, object>;
                if (pd == null) continue;
                string path = GetS(pd, "path");
                string num = GetS(pd, "number");
                if (path.Length == 0) continue;
                string ext = Path.GetExtension(path);
                if (ext.Length == 0) ext = ".jpg";
                int nn; int.TryParse(num, out nn);
                string fn = Path.Combine(work, string.Format("{0:D3}{1}", nn, ext));
                jobs.Add(new string[] { imgServer + "/" + path, fn });
            }
            if (log != null) log("    开始下载图片 共 " + jobs.Count + " 页（4 路并发）");
            int okc = Fetcher.Run(jobs, true, BASE + "/", 4, log, prog, cancelled);
            if (log != null) log("    抓取完成 " + okc + "/" + jobs.Count);

            string safe = Rank.SafeFileName(hit.Title, 140) + ".zip";
            string target = Path.Combine(dir, safe);
            ZipHelper.Pack(work, target, log);
            try { Directory.Delete(work, true); } catch { }
            if (log != null) log("    完成: " + target);
            return true;
        }
    }

    // ============ 禁漫天堂 ============
    internal static class Jm
    {
        public static List<Hit> Search(string query, Action<string> log)
        {
            List<Hit> hits = new List<Hit>();
            string baseUrl = "https://" + Domains.Jm;
            string url = baseUrl + "/search/photos?search_query=" + Uri.EscapeDataString(query) + "&page=1&main_tag=0";
            int code;
            string html = Http.GetText(url, false, 30, null, out code);
            if (html == null || code != 200) html = Http.GetText(url, true, 30, null, out code);
            if (html == null || code != 200)
            {
                if (log != null) log("  [jm] 搜索失败 code=" + code);
                return hits;
            }
            MatchCollection ms = Regex.Matches(html, @"(?s)<div class=""thumb-overlay"">\s*<a href=""/album/(\d+)/[^""]*""[^>]*>\s*<img[^>]*?title=""([^""]*)""");
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (Match m in ms)
            {
                string aid = m.Groups[1].Value;
                if (seen.ContainsKey(aid)) continue;
                seen[aid] = true;
                string title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (title.Length == 0) continue;
                Hit h = new Hit();
                h.Site = "jm";
                h.Id = aid;
                h.Title = title;
                h.Url = baseUrl + "/album/" + aid + "/";
                string label;
                h.Priority = Rank.Classify(title, out label);
                h.Label = label;
                hits.Add(h);
            }
            if (log != null) log("  [jm] 命中 " + hits.Count + " 条");
            return hits;
        }

        public static bool Download(Hit hit, string dir, Action<string> log, Action<int, int> prog, Func<bool> cancelled)
        {
            string baseUrl = "https://" + Domains.Jm;
            int code;
            string album = Http.GetText(baseUrl + "/album/" + hit.Id + "/", false, 30, null, out code);
            if (album == null || code != 200) { if (log != null) log("    相册页失败"); return false; }

            List<string> viewers = new List<string>();
            MatchCollection photo = Regex.Matches(album, @"/photo/(\d+)");
            if (photo.Count > 0)
            {
                foreach (Match m in photo) if (!viewers.Contains(m.Groups[1].Value)) viewers.Add(m.Groups[1].Value);
            }
            else
            {
                MatchCollection chap = Regex.Matches(album, @"/chapter/(\d+)");
                foreach (Match m in chap) if (!viewers.Contains(m.Groups[1].Value)) viewers.Add(m.Groups[1].Value);
            }
            if (viewers.Count == 0) { if (log != null) log("    找不到阅读入口"); return false; }
            if (log != null) log("    共 " + viewers.Count + " 个章节");

            string work = Path.Combine(Path.GetTempPath(), "benzi_jm_" + hit.Id);
            if (Directory.Exists(work)) { try { Directory.Delete(work, true); } catch { } }
            Directory.CreateDirectory(work);

            int fileNo = 0;
            foreach (string vid in viewers)
            {
                if (cancelled != null && cancelled()) { if (log != null) log("    已取消"); return false; }
                string vurl = baseUrl + "/photo/" + vid;
                string vhtml = Http.GetText(vurl, false, 30, baseUrl + "/", out code);
                if (vhtml == null || code != 200) { if (log != null) log("    阅读页失败 " + vid); continue; }

                Match am = Regex.Match(vhtml, @"var\s+aid\s*=\s*(\d+)");
                string aid = am.Success ? am.Groups[1].Value : vid;
                Match pm = Regex.Match(vhtml, @"var\s+page_arr\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
                if (!pm.Success) { if (log != null) log("    解析页列表失败"); continue; }
                List<string> files = new List<string>();
                foreach (Match m in Regex.Matches(pm.Groups[1].Value, @"""([^""]+)""")) files.Add(m.Groups[1].Value);
                if (files.Count == 0) continue;

                string cdn = "";
                Match cm = Regex.Match(vhtml, @"data-original=""(https://cdn[^/]+)/media/photos/");
                if (!cm.Success) cm = Regex.Match(vhtml, @"(https://cdn[^""']+/media/photos/)");
                if (cm.Success) cdn = cm.Groups[1].Value.TrimEnd('/');
                if (cdn.Length == 0) cdn = "https://cdn-msp3." + Domains.Jm;

                List<string[]> jjobs = new List<string[]>();
                foreach (string f in files)
                {
                    fileNo++;
                    string imgUrl = cdn + "/media/photos/" + aid + "/" + f;
                    string fn = Path.Combine(work, string.Format("{0:D4}{1}", fileNo, Path.GetExtension(f)));
                    jjobs.Add(new string[] { imgUrl, fn });
                }
                int jok = Fetcher.Run(jjobs, false, baseUrl + "/", 4, log, prog, cancelled);
                if (log != null) log("    本章 " + jok + "/" + jjobs.Count + " 张");
            }

            string safe = Rank.SafeFileName(hit.Title, 140) + ".zip";
            string target = Path.Combine(dir, safe);
            ZipHelper.Pack(work, target, log);
            try { Directory.Delete(work, true); } catch { }
            if (log != null) log("    完成: " + target);
            return true;
        }
    }

    // 多线程抓图：nhentai / 禁漫都是逐张图，串行太慢
    internal static class Fetcher
    {
        public static int Run(List<string[]> jobs, bool useProxy, string referer, int workers, Action<string> log, Action<int, int> prog, Func<bool> cancelled)
        {
            int total = jobs.Count, done = 0, ok = 0, next = 0;
            object gate = new object();
            List<Thread> ths = new List<Thread>();
            for (int w = 0; w < workers; w++)
            {
                Thread t = new Thread(delegate()
                {
                    while (true)
                    {
                        if (cancelled != null && cancelled()) return;
                        int i;
                        lock (gate) { if (next >= total) return; i = next++; }
                        string url = jobs[i][0], dest = jobs[i][1];
                        bool got = false;
                        for (int k = 0; k < 3 && !got; k++)
                        {
                            got = Http.DownloadSimple(url, dest, useProxy, referer, 120);
                            if (!got) Thread.Sleep(400);
                        }
                        lock (gate)
                        {
                            done++;
                            if (got) ok++;
                            if (prog != null) prog(done, total);
                            if (!got && log != null) log("    图片失败: " + Path.GetFileName(dest));
                        }
                    }
                });
                t.IsBackground = true;
                t.Start();
                ths.Add(t);
            }
            foreach (Thread t in ths) t.Join();
            return ok;
        }
    }

    internal static class ZipHelper
    {
        public static bool Pack(string srcDir, string zipPath, Action<string> log)
        {
            try
            {
                string[] files = Directory.GetFiles(srcDir);
                Array.Sort(files, StringComparer.Ordinal);
                if (files.Length == 0) { if (log != null) log("    没有图片可打包"); return false; }
                if (File.Exists(zipPath)) File.Delete(zipPath);
                using (FileStream fs = new FileStream(zipPath, FileMode.Create))
                using (ZipArchive za = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (string f in files)
                    {
                        ZipArchiveEntry e = za.CreateEntry(Path.GetFileName(f), CompressionLevel.NoCompression);
                        using (Stream es = e.Open())
                        using (FileStream src = File.OpenRead(f)) src.CopyTo(es, 1048576);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("    打包失败: " + ex.Message);
                return false;
            }
        }
    }
}
