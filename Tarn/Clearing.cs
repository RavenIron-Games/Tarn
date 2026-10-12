using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace RavenIron.Tarn
{
    /// <summary>
    /// Keeps trees, rocks, bushes and pickable debris off ground a map maker marked: the Ravenrest map's roads and lake
    /// beds (owner, Raventest20, 2026-10-06: "clear the debris off the roads").
    ///
    /// Better Continents' vegetation map cannot do it: it is read once per 64 m zone, at the zone's centre
    /// (ZoneSystemPatch -> ApplyVegetationMap(zoneCenterPos, ...)), so it clears whole zones or nothing, never a 9 m road.
    /// Vanilla has the right tool: ZoneSystem.PlaceVegetation skips any spot inside its clearAreas (the list locations
    /// add their m_clearArea to, a square test: centre +- radius), and that test runs AFTER the spot's random rolls, so
    /// adding areas removes only what would have stood there and moves nothing else. Grass is not vegetation: it follows
    /// the ground paint (Heightmap.IsCleared), which the map's paint map already sets on these pixels.
    ///
    /// The mask is BepInEx/config/Tarn/&lt;world&gt;.clear (written by v2/06_maps.py): "TARNCLR1", int32 size, float32 half
    /// extent in metres, then size x size bits, row 0 the north edge, numpy packbits order (high bit first). Read on the
    /// authority (dedicated server, host or single player) and sent to every joining player by ClearSync (0.3.0), because
    /// vegetation is placed by whichever machine generates a zone.
    /// Only zones generated after the file exists are affected.
    /// </summary>
    internal static class Clearing
    {
        private static string _for;
        private static byte[] _bits;
        private static int _n;
        private static float _extent;

        // A client connected to a server never reads its own .clear file: the mask is the server's (ClearSync), as the
        // lake list is, so a stale local copy cannot make one player's zones differ from another's.
        private static bool _fromServer;

        internal static string FileFor(string world) => Path.Combine(Paths.ConfigPath, "Tarn", world + ".clear");

        /// <summary>A client is waiting for the server's mask (a zone generated now keeps vanilla's vegetation).</summary>
        internal static bool WaitingForServer => !LakeSync.IsAuthority && !_fromServer && !ClearSync.GaveUp;

        internal static void ResetClient() { _fromServer = false; _for = null; _bits = null; }

        internal static void SetFromServer(string world, int n, float extent, byte[] bits)
        {
            _fromServer = true; _for = world; _n = n; _extent = extent; _bits = bits;
            if (bits == null) { Plugin.Log.LogInfo($"clear sync: the server has no clearing mask for '{world}'"); return; }
            Plugin.Log.LogInfo($"clear sync: the server's clearing mask for '{world}': {n} x {n} over +-{extent:0} m, {Km2():0.00} km2 kept free of vegetation");
        }

        /// <summary>Authority: the mask as read from the file, for ClearSync to send.</summary>
        internal static bool TryGetRaw(out int n, out float extent, out byte[] bits)
        {
            n = 0; extent = 0f; bits = null;
            if (!LakeSync.IsAuthority || !Ensure()) return false;
            n = _n; extent = _extent; bits = _bits;
            return true;
        }

        private static double Km2()
        {
            long set = 0;
            foreach (byte b in _bits) for (int k = 0; k < 8; k++) set += (b >> k) & 1;
            float mpx = 2f * _extent / _n;
            return set * mpx * mpx / 1e6;
        }

        internal static bool Ensure()
        {
            if (!LakeSync.IsAuthority) return _fromServer && _bits != null;
            string w = Plugin.WorldName();
            if (_for == w) return _bits != null;
            _for = w;
            _bits = null;
            string p = FileFor(w);
            if (!File.Exists(p))
            {
                Plugin.Log.LogInfo($"no clearing mask for world '{w}' ({p}): vegetation is placed as vanilla places it");
                return false;
            }
            try
            {
                using (var br = new BinaryReader(File.OpenRead(p)))
                {
                    if (new string(br.ReadChars(8)) != "TARNCLR1") throw new InvalidDataException("not a TARNCLR1 file");
                    _n = br.ReadInt32();
                    _extent = br.ReadSingle();
                    int len = (int)(((long)_n * _n + 7) / 8);
                    _bits = br.ReadBytes(len);
                    if (_n <= 0 || _extent <= 0f || _bits.Length != len) throw new InvalidDataException($"size {_n}, extent {_extent}, {_bits.Length} of {len} bytes");
                }
                Plugin.Log.LogInfo($"clearing mask for world '{w}': {_n} x {_n} over +-{_extent:0} m, {Km2():0.00} km2 kept free of vegetation");
            }
            catch (Exception ex)
            {
                _bits = null;
                Plugin.Log.LogError($"clearing mask {p} could not be read ({ex.Message}): vegetation is placed as vanilla places it");
            }
            return _bits != null;
        }

        // House rule 5 (publicized assemblies are compile-time only): ZoneSystem.ClearArea is a PRIVATE class in the real
        // assembly, so `new ZoneSystem.ClearArea(...)` built clean and threw MethodAccessException in Raventest21. Nothing
        // here names the type at run time: it is found by name, built by reflection, and handled as object through the
        // non-generic IList of vanilla's own list. Resolved once; the lookup is retried until it succeeds.
        private static System.Reflection.ConstructorInfo _ctor;
        private static bool _ctorMissingLogged;
        internal static object NewArea(Vector3 p, float r)
        {
            if (_ctor == null)
            {
                var t = AccessTools.Inner(typeof(ZoneSystem), "ClearArea");
                _ctor = t == null ? null : AccessTools.Constructor(t, new[] { typeof(Vector3), typeof(float) });
                if (_ctor == null)
                {
                    if (!_ctorMissingLogged) { _ctorMissingLogged = true; Plugin.Log.LogError("ZoneSystem.ClearArea(Vector3, float) not found: Valheim's API moved, nothing is cleared"); }
                    return null;
                }
            }
            return _ctor.Invoke(new object[] { p, r });
        }

        private static bool At(int r, int c)
        {
            long i = (long)r * _n + c;
            return (_bits[i >> 3] & (0x80 >> (int)(i & 7))) != 0;
        }

        /// <summary>One square clear area per marked pixel within reach of the zone (64 m square around its centre).</summary>
        internal static List<object> AreasFor(Vector3 centre)
        {
            var added = new List<object>();
            float mpx = 2f * _extent / _n, reach = 32f + mpx;
            int c0 = Mathf.Max(0, Mathf.FloorToInt((centre.x - reach + _extent) / mpx));
            int c1 = Mathf.Min(_n - 1, Mathf.FloorToInt((centre.x + reach + _extent) / mpx));
            int r0 = Mathf.Max(0, Mathf.FloorToInt((_extent - (centre.z + reach)) / mpx));
            int r1 = Mathf.Min(_n - 1, Mathf.FloorToInt((_extent - (centre.z - reach)) / mpx));
            float half = mpx * 0.5f + 0.25f;      // the pixel's own square, a hair over so neighbours leave no seam
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (At(r, c))
                    {
                        var a = NewArea(new Vector3((c + 0.5f) * mpx - _extent, 0f, _extent - (r + 0.5f) * mpx), half);
                        if (a == null) return added;
                        added.Add(a);
                    }
            return added;
        }
    }

    // House rule 1: a prefix at Priority.Low that honours __runOriginal and never skips the original; a finalizer takes
    // back exactly the areas it added, whatever happened in between. clearAreas is vanilla's List<ClearArea>, taken as
    // object (Harmony passes it as is) and used through IList, because the element type is private (see NewArea).
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation))]
    internal static class Patch_PlaceVegetation
    {
        private static bool _warned, _warnedWaiting, _provedMask, _provedBeds;

        [HarmonyPriority(Priority.Low)]
        private static void Prefix(Vector3 zoneCenterPos, object clearAreas, bool __runOriginal, ref List<object> __state)
        {
            __state = null;
            if (!__runOriginal || !(clearAreas is System.Collections.IList list)) return;
            try
            {
                // a client that joined generates zones before the server's mask and lakes arrive: say so, never silently
                bool waiting = Clearing.WaitingForServer || (!LakeSync.IsAuthority && !LakeSync.Answered && !LakeSync.GaveUp);
                if (waiting && !_warnedWaiting)
                {
                    _warnedWaiting = true;
                    Plugin.Log.LogWarning($"clearing: the zone at ({zoneCenterPos.x:0}, {zoneCenterPos.z:0}) was generated before the server's clearing mask and lakes arrived - its vegetation is vanilla's (only the first such zone is named)");
                }
                var added = Clearing.Ensure() ? Clearing.AreasFor(zoneCenterPos) : new List<object>();
                int fromMask = added.Count;
                string lake = null;
                int fromBeds = LakeSync.LakeBedsOn ? LakeBeds.AddFor(zoneCenterPos, added, out lake) : 0;
                if (added.Count == 0) return;
                __state = added;
                foreach (var a in added) list.Add(a);
                // the lines that prove the clearing ran (a silent success and a silent no-op look the same)
                if (fromMask > 0 && !_provedMask)
                {
                    _provedMask = true;
                    Plugin.Log.LogInfo($"clearing: first zone with marked ground at ({zoneCenterPos.x:0}, {zoneCenterPos.z:0}): {fromMask} clear area(s) added");
                }
                if (fromBeds > 0 && !_provedBeds)
                {
                    _provedBeds = true;
                    Plugin.Log.LogInfo($"lake beds: first zone with a lake bed at ({zoneCenterPos.x:0}, {zoneCenterPos.z:0}), lake '{lake}': {fromBeds} clear area(s) added");
                }
            }
            catch (Exception ex)
            {
                __state = null;
                if (!_warned) { _warned = true; Plugin.Log.LogError($"clearing: {ex}"); }
            }
        }

        private static void Finalizer(object clearAreas, List<object> __state)
        {
            if (__state == null || __state.Count == 0 || !(clearAreas is System.Collections.IList list)) return;
            var mine = new HashSet<object>(__state);
            for (int i = list.Count - 1; i >= 0; i--)
                if (mine.Contains(list[i])) list.RemoveAt(i);
        }
    }
}
