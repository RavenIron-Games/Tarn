using System.Collections.Generic;
using UnityEngine;

namespace RavenIron.Tarn
{
    /// <summary>
    /// A lake's shape: the generated ground sampled every 4 m over its box (+12 m), and the flood from its point.
    /// Shared by LakeBuilder (which draws the water) and LakeBeds (which keeps vegetation off the bed), so the ground kept
    /// clear is exactly the ground under the water. Heights come from WorldGenerator, never loaded terrain, so every
    /// machine that generates a zone gets the same shape.
    ///
    /// Wet: every cell below the level joined to the point (4-connected). Mask: wet grown by one cell (8-connected), so the
    /// surface reaches just under the shore. Ground below the level that the flood cannot reach stays dry.
    /// </summary>
    internal sealed class LakeShape
    {
        internal const float Step = 4f, Edge = 12f;
        public readonly Plugin.Lake L;
        public readonly float X0, Z0;
        public readonly int Nx, Nz;
        public readonly float[] H;
        public bool[] Wet, Mask;
        public int WetCells;
        public bool TouchedEdge;
        public string Failed;           // set when the flood found no water: the point is dry
        private int _row;

        public LakeShape(Plugin.Lake l)
        {
            L = l;
            X0 = l.MinX - Edge; Z0 = l.MinZ - Edge;
            Nx = Mathf.CeilToInt((l.MaxX + Edge - X0) / Step) + 1;
            Nz = Mathf.CeilToInt((l.MaxZ + Edge - Z0) / Step) + 1;
            H = new float[Nx * Nz];
        }

        public float Cx(int i) => X0 + i * Step;
        public float Cz(int j) => Z0 + j * Step;
        public bool Sampled => _row >= Nz;
        public bool Done => Wet != null || Failed != null;

        /// <summary>Samples one more row of ground; false once every row is in.</summary>
        public bool SampleRow(WorldGenerator wg)
        {
            if (_row >= Nz) return false;
            for (int i = 0; i < Nx; i++) H[_row * Nx + i] = wg.GetHeight(Cx(i), Cz(_row));
            _row++;
            return true;
        }

        /// <summary>The whole shape at once (sampling and flood), for a caller that cannot wait a frame.</summary>
        public void Complete(WorldGenerator wg)
        {
            while (SampleRow(wg)) { }
            if (!Done) Flood();
        }

        /// <returns>false when the lake's point is dry (Failed says why)</returns>
        public bool Flood()
        {
            float level = L.Level;
            int si = Mathf.Clamp(Mathf.RoundToInt((L.X - X0) / Step), 0, Nx - 1);
            int sj = Mathf.Clamp(Mathf.RoundToInt((L.Z - Z0) / Step), 0, Nz - 1);
            if (!(H[sj * Nx + si] < level))
            {
                // the point may sit a cell off the deepest ground: take the lowest cell within 12 m
                float best = float.MaxValue; int bi = si, bj = sj;
                for (int dj = -3; dj <= 3; dj++)
                    for (int di = -3; di <= 3; di++)
                    {
                        int i = si + di, j = sj + dj;
                        if (i < 0 || j < 0 || i >= Nx || j >= Nz) continue;
                        if (H[j * Nx + i] < best) { best = H[j * Nx + i]; bi = i; bj = j; }
                    }
                si = bi; sj = bj;
                if (!(best < level)) { Failed = $"its point ({L.X:0}, {L.Z:0}) is dry: ground {best:0.0} m, level {level:0.0} m"; return false; }
            }
            var wet = new bool[Nx * Nz];
            var stack = new Stack<int>();
            stack.Push(sj * Nx + si); wet[sj * Nx + si] = true;
            int count = 0;
            bool touchedEdge = false;
            while (stack.Count > 0)
            {
                int k = stack.Pop(); count++;
                int i = k % Nx, j = k / Nx;
                if (i == 0 || j == 0 || i == Nx - 1 || j == Nz - 1) touchedEdge = true;
                Visit(i + 1, j); Visit(i - 1, j); Visit(i, j + 1); Visit(i, j - 1);
            }
            void Visit(int i, int j)
            {
                if (i < 0 || j < 0 || i >= Nx || j >= Nz) return;
                int k = j * Nx + i;
                if (wet[k] || !(H[k] < level)) return;
                wet[k] = true; stack.Push(k);
            }
            var mask = new bool[Nx * Nz];
            for (int j = 0; j < Nz; j++)
                for (int i = 0; i < Nx; i++)
                {
                    if (!wet[j * Nx + i]) continue;
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int a = i + di, b = j + dj;
                            if (a >= 0 && b >= 0 && a < Nx && b < Nz) mask[b * Nx + a] = true;
                        }
                }
            WetCells = count;
            TouchedEdge = touchedEdge;
            Mask = mask;
            Wet = wet;                  // last: Done reads it
            return true;
        }

        /// <summary>
        /// Inside the bed LakeBeds keeps clear: within pad of a wet cell's centre on both axes, strictly, as vanilla's
        /// square test is. LakeBeds' squares cover exactly the union of those per-cell squares, so this is the same ground.
        /// </summary>
        public bool InBed(float x, float z, float pad)
        {
            int i0 = Mathf.Max(0, Mathf.FloorToInt((x - pad - X0) / Step)), i1 = Mathf.Min(Nx - 1, Mathf.CeilToInt((x + pad - X0) / Step));
            int j0 = Mathf.Max(0, Mathf.FloorToInt((z - pad - Z0) / Step)), j1 = Mathf.Min(Nz - 1, Mathf.CeilToInt((z + pad - Z0) / Step));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    if (Wet[j * Nx + i] && Mathf.Abs(x - Cx(i)) < pad && Mathf.Abs(z - Cz(j)) < pad) return true;
            return false;
        }

        public bool InMask(float x, float z)
        {
            int i = Mathf.RoundToInt((x - X0) / Step), j = Mathf.RoundToInt((z - Z0) / Step);
            return i >= 0 && j >= 0 && i < Nx && j < Nz && Mask[j * Nx + i];
        }
    }
}
