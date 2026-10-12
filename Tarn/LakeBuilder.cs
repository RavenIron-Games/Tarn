using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace RavenIron.Tarn
{
    /// <summary>
    /// Builds one lake a few milliseconds at a time (no coroutine: Plugin.Update calls Step with a budget).
    ///
    /// Steps 1 and 2 are LakeShape's, shared with LakeBeds so the ground kept clear is the ground under the water.
    /// 1. SAMPLE the generated ground on a 4 m grid over the lake's box (+12 m).
    /// 2. FLOOD from the lake's point: every cell below the level joined to it (4-connected) is water; the mask is that
    ///    grown by one cell (8-connected), so the surface reaches just under the shore and the shoreline is where the
    ///    terrain cuts it. Ground below the level that the flood cannot reach (below a sill, beyond a rim) stays dry:
    ///    that is the whole point, and why a lake is not a rectangle.
    /// 3. TILES: vanilla's sea tile at its own 64 m size, on the same 64 m grid as the zones, one per grid square the
    ///    mask touches. Its volume keeps the surface's material depths but has its collider switched off; its surface
    ///    copy gets its own mesh with every triangle outside the mask removed, and its 'Water'-layer colliders off
    ///    (they span the whole square and would stop raycasts over dry ground below the level).
    /// 4. BOXES: the mask as maximal rectangles, each a trigger box on the WaterVolume layer with a WaterVolume of its own
    ///    (that is what swimming, floating and the underwater view find). Separate objects, as vanilla's zone tiles
    ///    are, so stepping from one box to the next is vanilla's own enter/exit bookkeeping.
    /// </summary>
    internal sealed class LakeBuilder
    {
        private const float Step_ = LakeShape.Step, TileSize = 64f;
        public readonly Plugin.LakeRt Rt;
        private readonly WaterVolume _tpl;
        private readonly LakeShape _s;
        private int _phase;
        private List<Vector2Int> _tiles;
        private int _tileIdx;
        private List<RectInt> _boxes;
        private int _boxIdx;
        private MeshRenderer _anySurface;
        private int _layerVolume;

        public LakeBuilder(Plugin.LakeRt rt, WaterVolume template)
        {
            Rt = rt;
            _tpl = template;
            // a shape LakeBeds already made for this lake (same generator heights) saves the sampling
            if (rt.Shape == null) rt.Shape = new LakeShape(rt.L);
            _s = rt.Shape;
        }

        private float Level => Rt.L.Level;
        private float Cx(int i) => _s.Cx(i);
        private float Cz(int j) => _s.Cz(j);

        /// <returns>true when the lake is finished (or given up on)</returns>
        public bool Step(Stopwatch sw, float budgetMs)
        {
            if (_tpl == null) { Fail("no zone water template"); return true; }
            var wg = WorldGenerator.instance;
            if (wg == null) return false;
            while (sw.Elapsed.TotalMilliseconds < budgetMs)
            {
                switch (_phase)
                {
                    case 0:
                        if (!_s.SampleRow(wg)) _phase = 1;
                        break;
                    case 1:
                        if (!Flood()) return true;
                        _phase = 2;
                        break;
                    case 2:
                        if (_tileIdx >= _tiles.Count) { _phase = 3; break; }
                        BuildTile(_tiles[_tileIdx++]);
                        break;
                    case 3:
                        if (_anySurface == null) { Fail("no water surface was drawn"); return true; }
                        if (_boxIdx >= _boxes.Count) { Finish(); return true; }
                        BuildBox(_boxes[_boxIdx++]);
                        break;
                }
            }
            return false;
        }

        private void Fail(string why)
        {
            Rt.Failed = why;
            Plugin.Drop(Rt);
            Rt.Built = true;                // do not retry every half second
            Plugin.Log.LogWarning($"lake '{Rt.L.Name}': {why}");
        }

        private bool Flood()
        {
            if (!_s.Done) _s.Flood();
            if (_s.Failed != null) { Fail(_s.Failed); return false; }
            if (_s.TouchedEdge)
                Plugin.Log.LogWarning($"lake '{Rt.L.Name}': its water reaches the edge of its box - the basin is not closed there, so the shore is cut off at the box");
            Rt.WetCells = _s.WetCells;
            int nx = _s.Nx, nz = _s.Nz;
            // the 64 m squares (zone grid: square k spans k*64 +- 32) the mask touches
            var set = new HashSet<Vector2Int>();
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                    if (_s.Mask[j * nx + i])
                        set.Add(new Vector2Int(Mathf.FloorToInt((Cx(i) + TileSize / 2f) / TileSize), Mathf.FloorToInt((Cz(j) + TileSize / 2f) / TileSize)));
            _tiles = new List<Vector2Int>(set);
            _boxes = Rectangles(_s.Mask, nx, nz);
            _layerVolume = _tpl.gameObject.layer;
            return true;
        }

        private bool InMask(float x, float z) => _s.InMask(x, z);

        private void BuildTile(Vector2Int t)
        {
            float cx = t.x * TileSize, cz = t.y * TileSize, hs = TileSize / 2f;
            var go = Object.Instantiate(_tpl.gameObject);
            go.name = $"Tarn_{Rt.L.Name}_{t.x}_{t.y}";
            go.transform.rotation = _tpl.transform.rotation;
            go.transform.localScale = _tpl.transform.lossyScale;
            go.transform.position = new Vector3(cx, Level - _tpl.m_surfaceOffset, cz);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;   // the boxes are the water you touch
            Rt.Owned.Add(go);
            var wv = go.GetComponent<WaterVolume>();
            var st = _tpl.m_waterSurface != null ? _tpl.m_waterSurface.transform : null;
            if (st == null) { Plugin.Log.LogWarning("zone water has no surface renderer: lakes will be invisible"); return; }
            var surf = Object.Instantiate(st.gameObject);
            surf.name = go.name + "_s";
            foreach (var stray in surf.GetComponentsInChildren<WaterVolume>(true))   // never a second ocean
            {
                if (stray.gameObject == surf) Object.DestroyImmediate(stray);
                else Object.DestroyImmediate(stray.gameObject);
            }
            foreach (var c in surf.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            surf.transform.rotation = st.rotation;
            surf.transform.localScale = st.lossyScale;
            surf.transform.position = go.transform.position + (st.position - _tpl.transform.position);
            Rt.Owned.Add(surf);
            var mf = surf.GetComponent<MeshFilter>();
            var mr = surf.GetComponent<MeshRenderer>();
            if (mf != null && mf.sharedMesh != null)
            {
                var mesh = Object.Instantiate(mf.sharedMesh);
                mesh.name = surf.name;
                var verts = mesh.vertices;
                var keep = new bool[verts.Length];
                for (int v = 0; v < verts.Length; v++)
                {
                    var w = surf.transform.TransformPoint(verts[v]);
                    keep[v] = InMask(w.x, w.z);
                }
                int kept = 0;
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                {
                    var tris = mesh.GetTriangles(sm);
                    var outTris = new List<int>(tris.Length);
                    for (int k = 0; k + 2 < tris.Length; k += 3)
                        if (keep[tris[k]] || keep[tris[k + 1]] || keep[tris[k + 2]])
                        { outTris.Add(tris[k]); outTris.Add(tris[k + 1]); outTris.Add(tris[k + 2]); }
                    mesh.SetTriangles(outTris, sm);
                    kept += outTris.Count / 3;
                }
                Rt.Owned.Add(mesh);
                if (kept == 0) { Object.Destroy(surf); Object.Destroy(go); return; }   // only the margin grazed this square
                mf.sharedMesh = mesh;
            }
            wv.m_waterSurface = mr;
            wv.m_heightmap = null;          // no zone heightmap: our corner depths stand in for it (DetectWaterDepth patch)
            wv.m_forceDepth = -1f;
            wv.m_useGlobalWind = true;
            var depth = new[]
            {
                Mathf.Max(0f, Level - Plugin.GroundHeight(cx - hs, cz + hs)),
                Mathf.Max(0f, Level - Plugin.GroundHeight(cx + hs, cz + hs)),
                Mathf.Max(0f, Level - Plugin.GroundHeight(cx + hs, cz - hs)),
                Mathf.Max(0f, Level - Plugin.GroundHeight(cx - hs, cz - hs)),
            };
            Plugin.Tiles[wv] = new Plugin.Tile { Volume = wv, Depth = depth, Centre = new Vector3(cx, Level, cz), Visual = true };
            if (_anySurface == null) _anySurface = mr;
            Rt.VisualTiles++;
        }

        private void BuildBox(RectInt r)
        {
            float x0 = Cx(r.xMin) - Step_ / 2f, x1 = Cx(r.xMax - 1) + Step_ / 2f;
            float z0 = Cz(r.yMin) - Step_ / 2f, z1 = Cz(r.yMax - 1) + Step_ / 2f;
            float bed = Level, sum = 0f; int n = 0;
            for (int j = r.yMin; j < r.yMax; j++)
                for (int i = r.xMin; i < r.xMax; i++)
                {
                    float g = _s.H[j * _s.Nx + i];
                    bed = Mathf.Min(bed, g);
                    sum += Mathf.Max(0f, Level - g); n++;
                }
            float bottom = bed - 4f, top = Level + 10f;            // vanilla's box reaches 10 m above its surface too
            var go = new GameObject($"Tarn_{Rt.L.Name}_box{_boxIdx}");
            go.SetActive(false);                                    // Awake waits until the box and the surface are set
            go.layer = _layerVolume;
            go.transform.position = new Vector3((x0 + x1) / 2f, Level, (z0 + z1) / 2f);
            var bc = go.AddComponent<BoxCollider>();
            bc.isTrigger = true;
            bc.size = new Vector3(x1 - x0, top - bottom, z1 - z0);
            bc.center = new Vector3(0f, (top + bottom) / 2f - Level, 0f);
            var wv = go.AddComponent<WaterVolume>();
            wv.m_waterSurface = _anySurface;                       // UpdateMaterials needs one; SetupMaterial is skipped for boxes
            wv.m_heightmap = null;
            wv.m_forceDepth = -1f;
            wv.m_useGlobalWind = true;
            wv.m_surfaceOffset = 0f;
            float avg = n > 0 ? sum / n : 0f;
            Plugin.Tiles[wv] = new Plugin.Tile { Volume = wv, Depth = new[] { avg, avg, avg, avg }, Centre = go.transform.position, Visual = false };
            WaterAccess.CollisionOnly.Add(wv);
            Rt.Owned.Add(go);
            go.SetActive(true);
            Rt.Boxes++;
        }

        private void Finish()
        {
            Plugin.Log.LogInfo($"lake '{Rt.L.Name}' built at ({Rt.L.X:0}, {Rt.L.Z:0}): level {Level:0.00}, {Rt.WetCells * Step_ * Step_:0} m2 of water, " +
                               $"{Rt.VisualTiles} tile(s), {Rt.Boxes} box(es)");
        }

        /// <summary>The mask as rectangles: runs along each row, a run extended down while the rows below hold exactly it.</summary>
        internal static List<RectInt> Rectangles(bool[] mask, int nx, int nz)
        {
            var used = new bool[mask.Length];
            var list = new List<RectInt>();
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    if (!mask[k] || used[k]) continue;
                    int i1 = i;
                    while (i1 < nx && mask[j * nx + i1] && !used[j * nx + i1]) i1++;
                    int j1 = j + 1;
                    for (; j1 < nz; j1++)
                    {
                        bool full = true;
                        for (int a = i; a < i1 && full; a++) full = mask[j1 * nx + a] && !used[j1 * nx + a];
                        if (!full) break;
                    }
                    for (int b = j; b < j1; b++)
                        for (int a = i; a < i1; a++) used[b * nx + a] = true;
                    list.Add(new RectInt(i, j, i1 - i, j1 - j));
                }
            return list;
        }
    }
}
