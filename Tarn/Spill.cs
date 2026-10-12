using System;
using System.Collections.Generic;

namespace RavenIron.Tarn
{
    /// <summary>
    /// How high water can stand in the hollow around a point before it spills out. Pure logic (no Unity), so the
    /// harness can run it on any height function.
    ///
    /// Grid of cells <c>step</c> metres apart over a square of half-width <c>radius</c>. The spill height is the
    /// lowest possible "highest point" on any path from the centre to the edge of the square: a priority flood that
    /// always grows from the lowest reachable cell, carrying the highest height met so far. The first edge cell it
    /// reaches carries the spill height. The lake is then every cell connected to the centre that lies below the
    /// level (the spill height less a margin).
    /// </summary>
    public static class Spill
    {
        public sealed class Result
        {
            public float SpillHeight;     // where it would overflow
            public float Level;           // the water surface: SpillHeight - margin
            public float BedHeight;       // the lowest ground under the lake
            public float MinX, MinZ, MaxX, MaxZ;   // extent of the wet cells, world metres
            public int WetCells;
            public float Step;
            public float AreaM2 => WetCells * Step * Step;
            public float Depth => Level - BedHeight;
        }

        /// <param name="height">ground height (absolute world y) at world x, z</param>
        public static Result Compute(Func<float, float, float> height, float cx, float cz, float radius, float step, float margin)
        {
            int half = (int)Math.Ceiling(radius / step);
            int n = 2 * half + 1;
            var h = new float[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    h[j * n + i] = height(cx + (i - half) * step, cz + (j - half) * step);

            // the centre's lowest neighbour starts the flood, so a point placed on a small bump still finds its hollow
            int start = half * n + half;
            var cost = new float[n * n];
            for (int k = 0; k < cost.Length; k++) cost[k] = float.PositiveInfinity;
            var heap = new MinHeap();
            cost[start] = h[start];
            heap.Push(h[start], start);
            float spill = float.NaN;
            while (heap.Count > 0)
            {
                heap.Pop(out float c, out int idx);
                if (c > cost[idx]) continue;
                int x = idx % n, y = idx / n;
                if (x == 0 || y == 0 || x == n - 1 || y == n - 1) { spill = c; break; }
                Relax(x + 1, y); Relax(x - 1, y); Relax(x, y + 1); Relax(x, y - 1);

                void Relax(int ax, int ay)
                {
                    int a = ay * n + ax;
                    float nc = Math.Max(c, h[a]);
                    if (nc < cost[a]) { cost[a] = nc; heap.Push(nc, a); }
                }
            }
            if (float.IsNaN(spill)) return null;

            var r = new Result { SpillHeight = spill, Level = spill - margin, Step = step, BedHeight = float.PositiveInfinity,
                                 MinX = float.PositiveInfinity, MinZ = float.PositiveInfinity,
                                 MaxX = float.NegativeInfinity, MaxZ = float.NegativeInfinity };
            if (!(h[start] < r.Level)) { r.BedHeight = h[start]; return r; }   // no water at the centre: WetCells 0

            var seen = new bool[n * n];
            var stack = new Stack<int>();
            stack.Push(start); seen[start] = true;
            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                int x = idx % n, y = idx / n;
                r.WetCells++;
                r.BedHeight = Math.Min(r.BedHeight, h[idx]);
                float wx = cx + (x - half) * step, wz = cz + (y - half) * step;
                r.MinX = Math.Min(r.MinX, wx); r.MaxX = Math.Max(r.MaxX, wx);
                r.MinZ = Math.Min(r.MinZ, wz); r.MaxZ = Math.Max(r.MaxZ, wz);
                Visit(x + 1, y); Visit(x - 1, y); Visit(x, y + 1); Visit(x, y - 1);

                void Visit(int ax, int ay)
                {
                    if (ax < 0 || ay < 0 || ax >= n || ay >= n) return;
                    int a = ay * n + ax;
                    if (seen[a] || !(h[a] < r.Level)) return;
                    seen[a] = true; stack.Push(a);
                }
            }
            return r;
        }

        private sealed class MinHeap
        {
            private readonly List<float> _k = new List<float>();
            private readonly List<int> _v = new List<int>();
            public int Count => _k.Count;
            public void Push(float k, int v)
            {
                _k.Add(k); _v.Add(v);
                int i = _k.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_k[p] <= _k[i]) break;
                    Swap(i, p); i = p;
                }
            }
            public void Pop(out float k, out int v)
            {
                k = _k[0]; v = _v[0];
                int last = _k.Count - 1;
                _k[0] = _k[last]; _v[0] = _v[last];
                _k.RemoveAt(last); _v.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < _k.Count && _k[l] < _k[m]) m = l;
                    if (r < _k.Count && _k[r] < _k[m]) m = r;
                    if (m == i) break;
                    Swap(i, m); i = m;
                }
            }
            private void Swap(int a, int b)
            {
                float tk = _k[a]; _k[a] = _k[b]; _k[b] = tk;
                int tv = _v[a]; _v[a] = _v[b]; _v[b] = tv;
            }
        }
    }
}
