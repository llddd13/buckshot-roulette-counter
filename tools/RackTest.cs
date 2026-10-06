// RackTest.cs — 用真实游戏截图回归测试 RackDetector。
// 用法: RackTest.exe <截图目录> [-roi x,y,w,h] [-axis -30] [-mask 输出目录]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

static class RackTest
{
    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        Rectangle roi = Rectangle.Empty;
        bool dbg = Array.IndexOf(args, "-debug") >= 0;
        bool onlyOne = false;
        string onlyName = null;
        string saveDir = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "-roi" && i + 1 < args.Length)
            {
                string[] v = args[++i].Split(',');
                roi = new Rectangle(int.Parse(v[0]), int.Parse(v[1]), int.Parse(v[2]), int.Parse(v[3]));
            }
            else if (args[i] == "-one" && i + 1 < args.Length) { onlyOne = true; onlyName = args[++i]; }
            else if (args[i] == "-save" && i + 1 < args.Length) { saveDir = args[++i]; }
        }
        if (saveDir != null) Directory.CreateDirectory(saveDir);
        RackParams p = new RackParams();

        List<string> files = new List<string>(Directory.GetFiles(dir, "*.jpg"));
        files.AddRange(Directory.GetFiles(dir, "*.png"));
        files.Sort();
        files.RemoveAll(delegate (string f) { return Path.GetFileName(f).StartsWith("20260930"); });

        Console.WriteLine("自动定位（RANSAC 找共线行）   ROI=" + (roi.IsEmpty ? "全屏" : roi.ToString()));
        Console.WriteLine("--------------------------------------------------------------------------");
        foreach (string f in files)
        {
            if (onlyOne && Path.GetFileName(f).IndexOf(onlyName) < 0) continue;
            using (Bitmap bmp = new Bitmap(f))
            {
                Rectangle use = roi;   // 默认全屏：算法自己定位弹槽
                RackResult r = RackDetector.Detect(bmp, use, Rectangle.Empty, p);
                StringBuilder sb = new StringBuilder();
                foreach (RackKind k in r.Kinds) sb.Append(k == RackKind.Live ? "实" : "空");
                string nm = Path.GetFileName(f);
                Console.Write((nm.Length > 14 ? nm.Substring(8, 6) : nm).PadRight(8));
                Console.Write(("[" + sb + "]").PadRight(14));
                Console.Write(("blobs=" + r.RawBlobs).PadRight(11));
                Console.Write(r.Note.PadRight(18));
                Console.Write("  中心: ");
                for (int i = 0; i < r.Centers.Count; i++)
                    Console.Write("(" + r.Centers[i].X + "," + r.Centers[i].Y + ")");
                Console.WriteLine();
                if (dbg) foreach (string s in r.Debug) Console.WriteLine("        " + s);

                if (saveDir != null)
                {
                    using (Bitmap copy = new Bitmap(bmp))
                    {
                        using (Graphics g = Graphics.FromImage(copy))
                        {
                            using (Pen pl = new Pen(Color.FromArgb(255, 0, 255, 80), 6))
                            using (Pen pb = new Pen(Color.FromArgb(255, 0, 170, 255), 6))
                            using (Font fo = new Font("Consolas", 46, FontStyle.Bold))
                            using (SolidBrush bl = new SolidBrush(Color.FromArgb(255, 0, 255, 80)))
                            using (SolidBrush bb2 = new SolidBrush(Color.FromArgb(255, 0, 170, 255)))
                            {
                                for (int i = 0; i < r.Boxes.Count; i++)
                                {
                                    bool live = r.Kinds[i] == RackKind.Live;
                                    Rectangle b = r.Boxes[i];
                                    g.DrawRectangle(live ? pl : pb, b);
                                    g.DrawString((i + 1) + (live ? "实" : "空"), fo, live ? bl : bb2, b.X, b.Y - 58);
                                }
                            }
                        }
                        copy.Save(Path.Combine(saveDir, Path.GetFileNameWithoutExtension(f) + "_ann.png"), ImageFormat.Png);
                    }
                }
            }
        }
    }
}
