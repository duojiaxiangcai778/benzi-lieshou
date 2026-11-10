using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace BenziHunter
{
    internal class TestMain
    {
        private static void Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Http.Init();
            Http.Proxy = "http://127.0.0.1:7890";

            Action<string> log = delegate(string s) { Console.WriteLine(s); };
            Action<int, int> prog0 = delegate(int d, int t2) { if (d % 20 == 0) Console.WriteLine("  img " + d + "/" + t2); };
            Func<bool> cancel0 = delegate() { return false; };
            Domains.Ensure(true, log);
            Console.WriteLine("Wnacg=" + Domains.Wnacg + "  Jm=" + Domains.Jm);

            string outDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "benzi_test");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            if (args.Length > 0 && args[0] == "--nhdl")
            {
                Hit fake = new Hit();
                fake.Site = "nhentai";
                fake.Id = args[1];
                fake.Title = "nhentai_test_" + args[1];
                bool ok2 = Nhentai.Download(fake, outDir, log, prog0, cancel0);
                Console.WriteLine("nhentai 下载: " + (ok2 ? "成功" : "失败"));
                return;
            }

            string raw = args.Length > 0 ? args[0] : "ママさんバレーでママオナホ・開幕戦と決勝戦(ai翻译)";

            List<string> queries = Rank.BuildQueries(raw);
            Console.WriteLine("原始: " + raw);
            Console.WriteLine("检索词: " + string.Join(" | ", queries.ToArray()));

            List<Hit> all = new List<Hit>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (string q in queries)
            {
                Console.WriteLine("--- 搜索: " + q);
                Add(all, seen, Wnacg.Search(q, log));
                Add(all, seen, Nhentai.Search(q, log));
                Add(all, seen, Jm.Search(q, log));
                foreach (Hit h in all)
                {
                    double s = Math.Max(Rank.Sim(q, h.Title), Rank.Sim(raw, h.Title));
                    if (h.Note != null && h.Note.Length > 0) s = Math.Max(s, Rank.Sim(q, h.Note));
                    if (s > h.Score) h.Score = s;
                }
            }

            List<Hit> kept = new List<Hit>();
            foreach (Hit h in all) if (h.Score >= 0.42) kept.Add(h);
            kept.Sort(Rank.CompareHits);

            Console.WriteLine("=== 候选（前 10） ===");
            int n = 0;
            foreach (Hit h in kept)
            {
                n++;
                if (n > 10) break;
                Console.WriteLine(string.Format("  {0}. [{1}] P{2} {3} score={4:0.00} pages={5}  {6}",
                    n, h.SiteName, h.Priority, h.Label, h.Score, h.Pages, h.Title));
            }
            if (kept.Count == 0) { Console.WriteLine("没有匹配结果"); return; }

            if (args.Length > 2 && args[2] == "nodl") { Console.WriteLine("(跳过下载)"); return; }
            Hit best = kept[0];
            Console.WriteLine("=== 下载最佳: [" + best.SiteName + "] " + best.Title);
            bool ok = false;
            Action<int, int> prog = delegate(int d, int t)
            {
                if (t > 0 && d % 20 == 0) Console.WriteLine("  进度 " + d + "/" + t);
            };
            Func<bool> cancel = delegate() { return false; };
            if (best.Site == "wnacg") ok = Wnacg.Download(best, outDir, log, prog, cancel);
            else if (best.Site == "nhentai") ok = Nhentai.Download(best, outDir, log, prog, cancel);
            else ok = Jm.Download(best, outDir, log, prog, cancel);
            Console.WriteLine("结果: " + (ok ? "成功" : "失败"));
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
        }    }
}
