using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RavenIron.Tarn
{
    /// <summary>
    /// Lakes on the map. Vanilla draws water wherever its height texture is under the sea (30 m): Minimap.GenerateWorldMap
    /// fills m_heightTexture with the generated height of every map pixel (and caches it to disk, reloaded by
    /// TryLoadMinimapTextureData), and its forest mask (m_forestMaskTexture) draws no trees under 30 m. A lake 60 m up is
    /// land to it. So once the map exists, each lake's pixels (a flood from its point over the map's own heights, inside its
    /// box) get the height of a sea as deep as the lake is (shading follows depth) and no forest. Drawing only: nothing in
    /// the world reads these textures.
    /// </summary>
    internal static class MapLakes
    {
        internal static bool Dirty;
        private static AccessTools.FieldRef<Minimap, Texture2D> _height, _mask;
        private static bool _warned;
        private const float Sea = 30f;

        internal static void Paint(IEnumerable<Plugin.Lake> lakes)
        {
            var mm = Minimap.instance;
            if (mm == null) return;
            try
            {
                _height ??= AccessTools.FieldRefAccess<Minimap, Texture2D>("m_heightTexture");
                _mask ??= AccessTools.FieldRefAccess<Minimap, Texture2D>("m_forestMaskTexture");
            }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; Plugin.Log.LogError($"Minimap.m_heightTexture / m_forestMaskTexture did not resolve ({ex.Message}): Valheim's API moved; lakes stay off the map"); }
                return;
            }
            var ht = _height(mm); var mt = _mask(mm);
            if (ht == null || mt == null) return;
            int size = mm.m_textureSize; float px = mm.m_pixelSize; int half = size / 2;
            var h = ht.GetPixels();
            var m = mt.GetPixels();
            if (h.Length != size * size || m.Length != size * size) { Plugin.Log.LogWarning("map textures are not the size the map says: lakes stay off the map"); return; }
            int Col(float wx) => Mathf.FloorToInt((wx - px / 2f) / px + half + 0.5f);
            int painted = 0, lakesShown = 0;
            var stack = new Stack<int>();
            foreach (var l in lakes)
            {
                int j0 = Mathf.Max(0, Col(l.MinX) - 2), j1 = Mathf.Min(size - 1, Col(l.MaxX) + 2);
                int i0 = Mathf.Max(0, Col(l.MinZ) - 2), i1 = Mathf.Min(size - 1, Col(l.MaxZ) + 2);
                int sj = Mathf.Clamp(Col(l.X), j0, j1), si = Mathf.Clamp(Col(l.Z), i0, i1);
                // the point may sit a pixel off the low ground at this coarse scale
                float best = float.MaxValue;
                for (int di = -2; di <= 2; di++)
                    for (int dj = -2; dj <= 2; dj++)
                    {
                        int i = si + di, j = sj + dj;
                        if (i < i0 || i > i1 || j < j0 || j > j1) continue;
                        float v = h[i * size + j].r;
                        if (v < best) { best = v; si = i; sj = j; }
                    }
                if (!(best < l.Level)) continue;            // smaller than a map pixel
                var seen = new HashSet<int>();
                stack.Push(si * size + sj); seen.Add(si * size + sj);
                int n = 0;
                while (stack.Count > 0)
                {
                    int k = stack.Pop();
                    int i = k / size, j = k % size;
                    float depth = Mathf.Max(1f, l.Level - h[k].r);
                    h[k].r = Sea - depth;
                    m[k] = new Color(0f, 0f, 0f, 0f);
                    n++;
                    Visit(i + 1, j); Visit(i - 1, j); Visit(i, j + 1); Visit(i, j - 1);
                    void Visit(int a, int b)
                    {
                        if (a < i0 || a > i1 || b < j0 || b > j1) return;
                        int q = a * size + b;
                        if (seen.Contains(q) || !(h[q].r < l.Level) || h[q].r < Sea) return;
                        seen.Add(q); stack.Push(q);
                    }
                }
                painted += n; lakesShown++;
            }
            ht.SetPixels(h); ht.Apply();
            mt.SetPixels(m); mt.Apply();
            Plugin.Log.LogInfo($"map: {lakesShown} lake(s) drawn as water, {painted} pixel(s) of {px:0} m");
        }
    }

    [HarmonyPatch(typeof(Minimap), "GenerateWorldMap")]
    internal static class Patch_MapGenerated
    {
        private static void Postfix() => MapLakes.Dirty = true;
    }

    [HarmonyPatch(typeof(Minimap), "TryLoadMinimapTextureData")]
    internal static class Patch_MapLoaded
    {
        private static void Postfix(bool __result) { if (__result) MapLakes.Dirty = true; }
    }
}
