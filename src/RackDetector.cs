// RackDetector.cs — 从游戏画面里找出"弹槽那一排子弹"，读出种类与顺序。
//
// 两个模式的颜色签名（都从真实截图实测得到）：
//
//   单机（桌面视角，红光很重）        多人（枪身视角，光照更正常）
//   实弹 粉红 RGB≈(180,108,109)      实弹 亮红粉 RGB≈(198,109,100)
//   空弹 灰   RGB≈(80,72,71)         空弹 亮蓝   RGB≈(58,119,129)
//   背景 暗红棕 / 木桌               背景 黄绿/棕 木桌 + 白线
//
// 所以判据是：
//   实弹 = 够亮 且 R 明显高于 G、B
//   空弹 = 蓝色系(高饱和偏青) 或 灰色系(中等亮度+极低饱和)
//
// 定位方式：不依赖固定区域和固定角度。先用连通域找出所有候选色块，
// 再用 RANSAC 找"最像弹槽的一条直线"（子弹中心共线、间距均匀），
// 因此单机的斜排、多人的转动视角都能自动适配。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

public enum RackKind { None = 0, Live = 1, Blank = 2 }

public class RackParams
{
    // ---- 实弹：R 明显高于 G、B（比"亮度"可靠得多）----
    // 实测：木桌/黄铜带 R-G 最高只到 56，红弹 R-G 有 59~91。
    // 亮度门槛必须放低，因为多人模式的红弹只有 v≈0.47~0.50（单机是 0.71）。
    public double LiveVal = 0.40;
    public int LiveDiff = 58;

    // ---- 空弹 A：蓝色系（多人） ----
    public double BlueHueMin = 168, BlueHueMax = 246;
    public double BlueSatMin = 0.26, BlueValMin = 0.24;

    // ---- 空弹 B：灰色系（单机） ----
    public double BlankValMin = 0.22, BlankValMax = 0.52;
    public double BlankSatMax = 0.26;
    public int BlankWarmMax = 32;

    // ---- 尺寸过滤 ----
    public int MinBlobPx = 42;
    public int MaxBlobPx = 360;
    public int MinBlobArea = 180;
    public int Step = 3;

    // ---- 结构校验 ----
    public double LineTolRatio = 0.45;   // 被判为"同一条线"的最大垂距 / 色块高度
    public double GapTol = 0.62;         // 间距与中位间距的最大相对偏差
    public double MinGapRatio = 0.30;    // 间距 / 平均高度 的下限
    public double MaxGapRatio = 3.4;     // 间距 / 平均高度 的上限
    public double MaxAreaRatio = 5.0;    // 同类色块面积比上限
    public double MaxHeightRatio = 2.0;  // 同类色块高度比上限
    public double MaxCrossHeightRatio = 2.6;
    // 弹槽里子弹是紧挨着的：相邻两颗之间的空隙必须远小于子弹自身尺寸。
    // 这条用来排除"桌面图标"那类排列整齐但彼此隔很远的色块。
    public double MaxGapToSize = 0.80;
    public bool Reverse = false;
    public int MinRowCount = 2;
}

public class RackResult
{
    public List<RackKind> Kinds = new List<RackKind>();
    public List<Rectangle> Boxes = new List<Rectangle>();
    public List<Point> Centers = new List<Point>();
    public string Note = "";
    public int RawBlobs;
    public List<string> Debug = new List<string>();
}

public static class RackDetector
{
    public static RackResult Detect(Bitmap bmp, Rectangle roi, Rectangle exclude, RackParams p)
    {
        RackResult res = new RackResult();
        int x0 = 0, y0 = 0, x1 = bmp.Width, y1 = bmp.Height;
        if (roi.Width > 4 && roi.Height > 4)
        {
            x0 = Math.Max(0, roi.X); y0 = Math.Max(0, roi.Y);
            x1 = Math.Min(bmp.Width, roi.Right); y1 = Math.Min(bmp.Height, roi.Bottom);
        }
        int w = x1 - x0, h = y1 - y0;
        if (w < 16 || h < 16) { res.Note = "区域无效"; return res; }

        int step = Math.Max(2, p.Step);
        int gw = w / step, gh = h / step;
        if (gw < 4 || gh < 4) { res.Note = "区域太小"; return res; }
        byte[] grid = new byte[gw * gh];

        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* s0 = (byte*)bd.Scan0; int stride = bd.Stride;
                for (int gy = 0; gy < gh; gy++)
                {
                    int py = y0 + gy * step;
                    byte* row = s0 + (long)py * stride;
                    for (int gx = 0; gx < gw; gx++)
                    {
                        int px = x0 + gx * step;
                        if (exclude.Width > 0 && px >= exclude.Left && px < exclude.Right && py >= exclude.Top && py < exclude.Bottom) continue;
                        byte* q = row + (long)px * 4;
                        int c = Classify(q[2], q[1], q[0], p);
                        if (c != 0) grid[gy * gw + gx] = (byte)c;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(bd); }

        // ---- 连通域 ----
        List<int[]> comps = new List<int[]>();
        int[] stack = new int[gw * gh];
        bool[] seen = new bool[gw * gh];
        for (int i = 0; i < grid.Length; i++)
        {
            if (grid[i] == 0 || seen[i]) continue;
            int sp = 0; stack[sp++] = i; seen[i] = true;
            int cnt = 0, minx = int.MaxValue, maxx = -1, miny = int.MaxValue, maxy = -1;
            byte kind = grid[i];
            while (sp > 0)
            {
                int cur = stack[--sp];
                int cy = cur / gw, cx = cur % gw;
                cnt++;
                if (cx < minx) minx = cx; if (cx > maxx) maxx = cx;
                if (cy < miny) miny = cy; if (cy > maxy) maxy = cy;
                if (cx > 0) Push(grid, seen, stack, ref sp, cur - 1, kind);
                if (cx < gw - 1) Push(grid, seen, stack, ref sp, cur + 1, kind);
                if (cy > 0) Push(grid, seen, stack, ref sp, cur - gw, kind);
                if (cy < gh - 1) Push(grid, seen, stack, ref sp, cur + gw, kind);
            }
            bool note = kind == 1 || (maxx - minx + 1) * step >= 40;
            if (cnt < p.MinBlobArea) { if (note && res.Debug.Count < 40) res.Debug.Add("drop 面积小 " + cnt + " @(" + minx * step + "," + miny * step + ") " + (maxx - minx + 1) * step + "x" + (maxy - miny + 1) * step + " " + (kind == 1 ? "实" : "空")); continue; }
            int bw = (maxx - minx + 1) * step, bh = (maxy - miny + 1) * step;
            if (bw < p.MinBlobPx || bh < p.MinBlobPx) { if (note && res.Debug.Count < 40) res.Debug.Add("drop 尺寸小 " + bw + "x" + bh + " area=" + cnt + " " + (kind == 1 ? "实" : "空")); continue; }
            if (bw > p.MaxBlobPx || bh > p.MaxBlobPx) { if (res.Debug.Count < 40) res.Debug.Add("drop 尺寸大 " + bw + "x" + bh + " area=" + cnt + " " + (kind == 1 ? "实" : "空")); continue; }
            double ar = (double)bw / bh;
            if (ar < 0.22 || ar > 7.0) { if (res.Debug.Count < 40) res.Debug.Add("drop 长宽比 " + bw + "x" + bh + " " + (kind == 1 ? "实" : "空")); continue; }
            comps.Add(new int[] { x0 + minx * step, y0 + miny * step, bw, bh, cnt, kind });
        }
        res.RawBlobs = comps.Count;
        foreach (int[] c in comps)
            res.Debug.Add("blob " + (c[5] == 1 ? "实" : "空") + " (" + c[0] + "," + c[1] + ") " + c[2] + "x" + c[3] + " area=" + c[4]);
        if (comps.Count < p.MinRowCount) { res.Note = "没找到足够的子弹色块"; return res; }

        // ---- RANSAC：列出所有"像一排"的候选，按评分从高到低逐个验证 ----
        List<List<int[]>> cands = FindCandidateRows(comps, p);
        if (cands.Count == 0) { res.Note = "色块不成排（可能是界面元素）"; return res; }

        List<int[]> slots = null;
        double ux = 0, uy = 0;
        string why = "";
        foreach (List<int[]> cand in cands)
        {
            double cx2, cy2;
            FitAxis(cand, out cx2, out cy2);
            List<int[]> sorted = new List<int[]>(cand);
            sorted.Sort(delegate (int[] A, int[] B) { return Proj(A, cx2, cy2).CompareTo(Proj(B, cx2, cy2)); });

            List<int[]> s = MergeSlots(sorted, cx2, cy2, p);
            if (s.Count < p.MinRowCount) continue;
            FitAxis(s, out cx2, out cy2);
            s.Sort(delegate (int[] A, int[] B) { return Proj(A, cx2, cy2).CompareTo(Proj(B, cx2, cy2)); });
            if (CheckStructure(s, cx2, cy2, p, out why)) { slots = s; ux = cx2; uy = cy2; break; }
        }
        if (slots == null)
        {
            res.Debug.Add("all candidates rejected, last reason: " + why);
            res.Note = "这些色块不像弹槽：" + why;
            return res;
        }
        foreach (int[] c in slots)
            res.Debug.Add("slot " + (c[5] == 1 ? "实" : "空") + " (" + c[0] + "," + c[1] + ") " + c[2] + "x" + c[3] + " area=" + c[4]);

        foreach (int[] c in slots)
        {
            res.Kinds.Add(c[5] == 1 ? RackKind.Live : RackKind.Blank);
            res.Boxes.Add(new Rectangle(c[0], c[1], c[2], c[3]));
            res.Centers.Add(new Point(c[0] + c[2] / 2, c[1] + c[3] / 2));
        }
        if (p.Reverse) res.Kinds.Reverse();

        int nl = 0, nb = 0;
        foreach (RackKind k in res.Kinds) { if (k == RackKind.Live) nl++; else nb++; }
        res.Note = "实" + nl + " 空" + nb + " 共" + res.Kinds.Count + " 发";
        return res;
    }

    // ------------------------------------------------------------------
    // 找最像弹槽的一排：任取两块连一条线，看有多少块落在这条线附近
    // ------------------------------------------------------------------
    /// <summary>把落在同一格上的碎片归并（灰弹/蓝弹中间常有一条暗带，会被切成两块）。</summary>
    private static List<int[]> MergeSlots(List<int[]> sorted, double ux, double uy, RackParams p)
    {
        List<int[]> slots = new List<int[]>();
        List<double> slotT = new List<double>();
        double avgH = 0; foreach (int[] c in sorted) avgH += c[3];
        avgH /= Math.Max(1, sorted.Count);
        double mergeGap = avgH * 0.42;
        foreach (int[] c in sorted)
        {
            double t = Proj(c, ux, uy);
            if (slots.Count > 0 && (t - slotT[slots.Count - 1]) <= mergeGap)
            {
                int idx = slots.Count - 1;
                int[] o = slots[idx];
                int nx = Math.Min(o[0], c[0]), ny = Math.Min(o[1], c[1]);
                int nr = Math.Max(o[0] + o[2], c[0] + c[2]);
                int nbot = Math.Max(o[1] + o[3], c[1] + c[3]);
                if (c[4] > o[4]) o[5] = c[5];
                o[0] = nx; o[1] = ny; o[2] = nr - nx; o[3] = nbot - ny; o[4] += c[4];
                slotT[idx] = (slotT[idx] + t) / 2.0;
                continue;
            }
            slots.Add((int[])c.Clone());
            slotT.Add(t);
        }
        return slots;
    }

    private static List<List<int[]>> FindCandidateRows(List<int[]> blobs, RackParams p)
    {
        List<KeyValuePair<double, List<int[]>>> found = new List<KeyValuePair<double, List<int[]>>>();
        HashSet<string> seen = new HashSet<string>();
        int n = blobs.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double h1 = blobs[i][3], h2 = blobs[j][3];
                if (h1 <= 1 || h2 <= 1) continue;
                if (Math.Max(h1, h2) / Math.Min(h1, h2) > p.MaxHeightRatio) continue;
                double avgH = (h1 + h2) / 2.0;

                double ax = blobs[i][0] + blobs[i][2] / 2.0, ay = blobs[i][1] + blobs[i][3] / 2.0;
                double bx = blobs[j][0] + blobs[j][2] / 2.0, by = blobs[j][1] + blobs[j][3] / 2.0;
                double dx = bx - ax, dy = by - ay;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) continue;
                double ux = dx / len, uy = dy / len;
                double px = -uy, py = ux;
                double tol = Math.Max(12.0, avgH * p.LineTolRatio);

                List<int[]> inl = new List<int[]>();
                foreach (int[] c in blobs)
                {
                    if (c[3] < avgH * 0.45 || c[3] > avgH * 2.2) continue;
                    double cx = c[0] + c[2] / 2.0, cy = c[1] + c[3] / 2.0;
                    if (Math.Abs(px * (cx - ax) + py * (cy - ay)) <= tol) inl.Add(c);
                }
                if (inl.Count < 2) continue;

                List<int[]> sorted = new List<int[]>(inl);
                sorted.Sort(delegate (int[] A, int[] B) { return Proj(A, ux, uy).CompareTo(Proj(B, ux, uy)); });
                StringBuilder key = new StringBuilder();
                double mean = 0, extentSum = 0;
                List<double> gaps = new List<double>();
                for (int k = 0; k < sorted.Count; k++)
                {
                    key.Append(sorted[k][0]).Append(',').Append(sorted[k][1]).Append(';');
                    double ext = Math.Abs(sorted[k][2] * ux) + Math.Abs(sorted[k][3] * uy);
                    extentSum += ext;
                    if (k > 0)
                    {
                        double g = Proj(sorted[k], ux, uy) - Proj(sorted[k - 1], ux, uy);
                        gaps.Add(g); mean += g;
                    }
                }
                string ks = key.ToString();
                if (seen.Contains(ks)) continue;
                seen.Add(ks);

                mean /= Math.Max(1, gaps.Count);
                double avgExt = extentSum / sorted.Count;
                // 紧挨着才算弹槽：平均空隙不能超过子弹尺寸的 MaxGapToSize 倍
                if (avgExt > 1 && (mean - avgExt) > avgExt * p.MaxGapToSize) continue;

                double cv = 1.0;
                if (mean > 1)
                {
                    double sd = 0;
                    foreach (double g in gaps) sd += (g - mean) * (g - mean);
                    sd = Math.Sqrt(sd / Math.Max(1, gaps.Count));
                    cv = sd / mean;
                }
                double score = inl.Count * (1.0 - Math.Min(0.9, cv));
                found.Add(new KeyValuePair<double, List<int[]>>(score, inl));
            }
        }
        found.Sort(delegate (KeyValuePair<double, List<int[]>> A, KeyValuePair<double, List<int[]>> B)
        { return B.Key.CompareTo(A.Key); });
        List<List<int[]>> outp = new List<List<int[]>>();
        for (int i = 0; i < found.Count && i < 80; i++) outp.Add(found[i].Value);
        return outp;
    }

    private static void FitAxis(List<int[]> blobs, out double ux, out double uy)
    {
        double mx = 0, my = 0;
        foreach (int[] c in blobs) { mx += c[0] + c[2] / 2.0; my += c[1] + c[3] / 2.0; }
        mx /= blobs.Count; my /= blobs.Count;
        double sxx = 0, syy = 0, sxy = 0;
        foreach (int[] c in blobs)
        {
            double dx = (c[0] + c[2] / 2.0) - mx, dy = (c[1] + c[3] / 2.0) - my;
            sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
        }
        double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        ux = Math.Cos(theta); uy = Math.Sin(theta);
        // 统一方向：让轴向的 x 分量为正，保证"排序从小到大"对应画面里从左到右
        if (ux < 0) { ux = -ux; uy = -uy; }
    }

    private static double Proj(int[] c, double ux, double uy)
    {
        return ux * (c[0] + c[2] / 2.0) + uy * (c[1] + c[3] / 2.0);
    }

    private static bool KindUniform(List<int[]> blobs, int kind, RackParams p, out string why)
    {
        why = "";
        int minA = int.MaxValue, maxA = 0, minH = int.MaxValue, maxH = 0, n = 0;
        foreach (int[] c in blobs)
        {
            if (c[5] != kind) continue;
            n++;
            if (c[4] < minA) minA = c[4];
            if (c[4] > maxA) maxA = c[4];
            if (c[3] < minH) minH = c[3];
            if (c[3] > maxH) maxH = c[3];
        }
        if (n < 2) return true;
        if ((double)maxA / Math.Max(1, minA) > p.MaxAreaRatio) { why = "同类色块面积相差太大"; return false; }
        if ((double)maxH / Math.Max(1, minH) > p.MaxHeightRatio) { why = "同类色块高度相差太大"; return false; }
        return true;
    }

    private static bool CheckStructure(List<int[]> blobs, double ux, double uy, RackParams p, out string why)
    {
        why = "";
        if (blobs.Count < 2) { why = "少于 2 块"; return false; }

        int minA = int.MaxValue, maxA = 0, minH = int.MaxValue, maxH = 0;
        double avgH = 0;
        foreach (int[] c in blobs)
        {
            if (c[4] < minA) minA = c[4];
            if (c[4] > maxA) maxA = c[4];
            if (c[3] < minH) minH = c[3];
            if (c[3] > maxH) maxH = c[3];
            avgH += c[3];
        }
        avgH /= blobs.Count;
        if ((double)maxA / Math.Max(1, minA) > 8.0) { why = "面积相差太大"; return false; }
        if ((double)maxH / Math.Max(1, minH) > p.MaxCrossHeightRatio) { why = "高度相差太大"; return false; }
        if (!KindUniform(blobs, 1, p, out why)) return false;
        if (!KindUniform(blobs, 2, p, out why)) return false;

        // 共线
        double mx = 0, my = 0;
        foreach (int[] c in blobs) { mx += c[0] + c[2] / 2.0; my += c[1] + c[3] / 2.0; }
        mx /= blobs.Count; my /= blobs.Count;
        double px = -uy, py = ux, maxDev = 0;
        foreach (int[] c in blobs)
        {
            double d = Math.Abs(px * ((c[0] + c[2] / 2.0) - mx) + py * ((c[1] + c[3] / 2.0) - my));
            if (d > maxDev) maxDev = d;
        }
        if (maxDev > Math.Max(16.0, avgH * p.LineTolRatio)) { why = "中心不共线"; return false; }

        // 间距
        List<double> gaps = new List<double>();
        for (int i = 1; i < blobs.Count; i++)
            gaps.Add(Proj(blobs[i], ux, uy) - Proj(blobs[i - 1], ux, uy));
        foreach (double g in gaps)
        {
            if (g < avgH * p.MinGapRatio) { why = "间距过密"; return false; }
            if (g > avgH * p.MaxGapRatio) { why = "间距过疏"; return false; }
        }
        if (gaps.Count >= 2)
        {
            List<double> s = new List<double>(gaps); s.Sort();
            double med = s[s.Count / 2];
            if (med <= 0) { why = "间距异常"; return false; }
            foreach (double g in gaps)
                if (Math.Abs(g - med) > med * p.GapTol) { why = "间距不均匀"; return false; }
        }
        return true;
    }

    private static void Push(byte[] grid, bool[] seen, int[] stack, ref int sp, int idx, byte kind)
    {
        if (seen[idx] || grid[idx] != kind) return;
        seen[idx] = true; stack[sp++] = idx;
    }

    /// <summary>0=背景 1=实弹 2=空弹</summary>
    public static int Classify(int r, int g, int b, RackParams p)
    {
        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        double v = mx / 255.0;
        if (v < 0.12) return 0;
        double sat = mx == 0 ? 0 : (double)(mx - mn) / mx;

        // 实弹：够亮 + R 明显高于 G、B
        if (v >= p.LiveVal && (r - g) >= p.LiveDiff && (r - b) >= p.LiveDiff) return 1;

        // 空弹（蓝色系）
        if (sat >= p.BlueSatMin && v >= p.BlueValMin && b >= r)
        {
            double hh;
            if (mx == mn) hh = 0;
            else if (mx == r) hh = 60.0 * (((g - b) / (double)(mx - mn)) % 6);
            else if (mx == g) hh = 60.0 * ((b - r) / (double)(mx - mn) + 2);
            else hh = 60.0 * ((r - g) / (double)(mx - mn) + 4);
            if (hh < 0) hh += 360;
            if (hh >= p.BlueHueMin && hh <= p.BlueHueMax) return 2;
        }

        // 空弹（灰色系）
        if (v >= p.BlankValMin && v <= p.BlankValMax && sat <= p.BlankSatMax && (r - b) <= p.BlankWarmMax) return 2;

        return 0;
    }
}
