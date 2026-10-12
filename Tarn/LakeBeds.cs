using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace RavenIron.Tarn
{
    /// <summary>
    /// Keeps vegetation off every lake's bed, from the lake list itself (0.4.0). A public user asked how to make a .clear
    /// file for lakes placed with `tarn here`; the answer is that they should not need one: the flood that shapes the water
    /// (LakeShape) already knows the bed.
    ///
    /// Works through the same PlaceVegetation patch and vanilla clear areas as the .clear mask (Clearing). For each lake
    /// whose box reaches the zone, the wet cells within reach become maximal rectangles (LakeBuilder.Rectangles), each
    /// widened by Shore past its cells, and each rectangle becomes a few of vanilla's square areas. A zone inside a lake
    /// gets one area; a shore zone a few dozen to a few hundred, where one per cell would be thousands, and vanilla tests
    /// every candidate spot against every area in a linear scan.
    ///
    /// The shape is made from WorldGenerator heights, so the server (which generates ghost zones around players) and
    /// every client (which generates the zones around itself) clear the same ground. A lake the builder has already
    /// shaped is reused; otherwise it is shaped on the spot, once per session, and a slow one is logged.
    ///
    /// Only zones generated after a lake exists are affected: `tarn here` on ground already explored leaves the trees
    /// that stand there.
    /// </summary>
    internal static class LakeBeds
    {
        private const float Reach = 32f;      // vanilla places a zone's vegetation within its own 64 m square
        // Each wet cell is a 4 m sample; its own square is +-2 m, and the shoreline lies somewhere between it and the
        // next (dry) sample. Shore (config Clearing.LakeBedShore, the server's value in multiplayer) is how much further
        // the cleared ground reaches: the default 2 m reaches that next sample, as 0.4.0 did; 0 stops halfway to it.
        private const float SlowMs = 20f;
        internal static float Pad => LakeShape.Step / 2f + LakeSync.LakeBedShore;   // each wet cell is cleared this far from its centre, both axes
        // Lakes within this of a player are shaped ahead of time. Vanilla generates zones up to a few zones out (the
        // server's ghost zones reach furthest), so this is comfortably beyond the generation front.
        private const float WarmRange = 640f;
        private const float PickEvery = 0.5f;

        private static Plugin.LakeRt _warming;
        private static float _sincePick = PickEvery;
        private static readonly List<Vector3> _refs = new List<Vector3>();

        /// <summary>
        /// Shapes the nearest unshaped lake near any player, a row at a time within the budget, so zone generation finds
        /// it ready. The big Ravenrest lakes are 300 000+ samples, a visible hitch if shaped all at once on the spot.
        /// Called from Plugin.Update while no lake is being built (the builder shapes as it goes).
        /// </summary>
        internal static void Warm(float dt, float budgetMs)
        {
            if (!LakeSync.LakeBedsOn) { _warming = null; return; }
            var wg = WorldGenerator.instance;
            if (wg == null) return;
            _sincePick += dt;
            if (_warming == null || _sincePick >= PickEvery)
            {
                _sincePick = 0f;
                _warming = Pick();
            }
            if (_warming == null) return;
            var s = _warming.Shape ?? (_warming.Shape = new LakeShape(_warming.L));
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < budgetMs && s.SampleRow(wg)) { }
            if (s.Sampled && !s.Done) s.Flood();
            if (s.Done) _warming = null;
        }

        private static Plugin.LakeRt Pick()
        {
            _refs.Clear();
            if (Player.m_localPlayer != null) _refs.Add(Player.m_localPlayer.transform.position);
            if (ZNet.instance != null && ZNet.instance.IsServer())
                foreach (var peer in ZNet.instance.GetPeers()) _refs.Add(peer.GetRefPos());
            if (_refs.Count == 0) return null;
            Plugin.LakeRt best = null;
            float bestD = WarmRange;
            foreach (var rt in Plugin.LakeList)
            {
                if (rt.Shape != null && rt.Shape.Done) continue;
                foreach (var p in _refs)
                {
                    float d = rt.DistanceTo(p);
                    if (d < bestD) { bestD = d; best = rt; }
                }
            }
            return best;
        }

        internal static void Reset() { _warming = null; _sincePick = PickEvery; }

        /// <summary>Adds the clear areas for every lake bed within reach of the zone; returns how many.</summary>
        internal static int AddFor(Vector3 c, List<object> into, out string firstLake)
        {
            firstLake = null;
            var wg = WorldGenerator.instance;
            if (wg == null) return 0;
            int added = 0;
            float pad = Pad;
            foreach (var rt in Plugin.LakeList)
            {
                var l = rt.L;
                float m = LakeShape.Edge + pad;
                if (c.x + Reach < l.MinX - m || c.x - Reach > l.MaxX + m || c.z + Reach < l.MinZ - m || c.z - Reach > l.MaxZ + m) continue;
                var s = rt.Shape ?? (rt.Shape = new LakeShape(l));
                if (!s.Done)
                {
                    var sw = Stopwatch.StartNew();
                    s.Complete(wg);
                    if (sw.Elapsed.TotalMilliseconds > SlowMs)
                        Plugin.Log.LogInfo($"lake beds: shaped lake '{l.Name}' for zone generation in {sw.Elapsed.TotalMilliseconds:0} ms ({s.Nx} x {s.Nz} samples)");
                }
                if (s.Failed != null) continue;
                int i0 = Mathf.Max(0, Mathf.CeilToInt((c.x - Reach - pad - s.X0) / LakeShape.Step));
                int i1 = Mathf.Min(s.Nx - 1, Mathf.FloorToInt((c.x + Reach + pad - s.X0) / LakeShape.Step));
                int j0 = Mathf.Max(0, Mathf.CeilToInt((c.z - Reach - pad - s.Z0) / LakeShape.Step));
                int j1 = Mathf.Min(s.Nz - 1, Mathf.FloorToInt((c.z + Reach + pad - s.Z0) / LakeShape.Step));
                if (i0 > i1 || j0 > j1) continue;
                int w = i1 - i0 + 1, h = j1 - j0 + 1;
                var sub = new bool[w * h];
                bool any = false;
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                        any |= sub[j * w + i] = s.Wet[(j0 + j) * s.Nx + i0 + i];
                if (!any) continue;
                int before = added;
                foreach (var r in LakeBuilder.Rectangles(sub, w, h))
                {
                    int n = Squares(s.Cx(i0 + r.xMin) - pad, s.Cx(i0 + r.xMax - 1) + pad,
                                    s.Cz(j0 + r.yMin) - pad, s.Cz(j0 + r.yMax - 1) + pad, into);
                    if (n < 0) return added;            // the clear-area type is missing: Clearing has said so
                    added += n;
                }
                if (added > before && firstLake == null) firstLake = l.Name;
            }
            return added;
        }

        /// <summary>A rectangle as squares along its long side, the last one flush with the far end.</summary>
        private static int Squares(float xa, float xb, float za, float zb, List<object> into)
        {
            float w = xb - xa, h = zb - za, side = Mathf.Min(w, h), half = side / 2f;
            bool alongX = w >= h;
            float a0 = alongX ? xa : za, a1 = alongX ? xb : zb, mid = alongX ? (za + zb) / 2f : (xa + xb) / 2f;
            int n = 0;
            for (float a = a0 + half; ; a += side)
            {
                float at = Mathf.Min(a, a1 - half);
                var area = Clearing.NewArea(alongX ? new Vector3(at, 0f, mid) : new Vector3(mid, 0f, at), half);
                if (area == null) return -1;
                into.Add(area); n++;
                if (at + half >= a1 - 0.01f) break;
            }
            return n;
        }
    }
}
