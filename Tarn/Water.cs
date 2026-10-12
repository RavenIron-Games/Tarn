using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RavenIron.Tarn
{
    /// <summary>
    /// WaterVolume's depth state and the two methods that use it are PRIVATE in the shipped game (the publicized
    /// assembly shows them public, which is a compile-time fiction). Reached through cached accessors, retried until
    /// they resolve; a missing member is named in an error, because then Valheim's API moved.
    /// </summary>
    internal static class WaterAccess
    {
        /// <summary>A lake's swim boxes: water you touch, never drawn. Their SetupMaterial is skipped (they share a
        /// tile's surface renderer only because UpdateMaterials needs one), so they never repaint a tile's depths.</summary>
        internal static readonly HashSet<WaterVolume> CollisionOnly = new HashSet<WaterVolume>();

        private static AccessTools.FieldRef<WaterVolume, float[]> _norm;
        private static AccessTools.FieldRef<WaterVolume, float> _one;
        private static Action<WaterVolume> _detect, _setup;
        private static bool _warned;

        private static bool Resolve()
        {
            if (_norm != null && _one != null && _detect != null && _setup != null) return true;
            try
            {
                _norm ??= AccessTools.FieldRefAccess<WaterVolume, float[]>("m_normalizedDepth");
                _one ??= AccessTools.FieldRefAccess<WaterVolume, float>("m_oneDepth");
                var d = AccessTools.Method(typeof(WaterVolume), "DetectWaterDepth");
                var s = AccessTools.Method(typeof(WaterVolume), "SetupMaterial");
                if (d != null) _detect ??= AccessTools.MethodDelegate<Action<WaterVolume>>(d);
                if (s != null) _setup ??= AccessTools.MethodDelegate<Action<WaterVolume>>(s);
            }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; Plugin.Log.LogError($"WaterVolume members did not resolve ({ex.Message}): Valheim's API moved; lakes keep flat depth"); }
                return false;
            }
            bool ok = _norm != null && _one != null && _detect != null && _setup != null;
            if (!ok && !_warned)
            {
                _warned = true;
                Plugin.Log.LogError("WaterVolume.m_normalizedDepth / m_oneDepth / DetectWaterDepth / SetupMaterial missing: Valheim's API moved");
            }
            return ok;
        }

        internal static bool Apply(WaterVolume wv, float[] metres)
        {
            if (!Resolve()) return false;
            float k = ConfigSync.Get(Plugin.DepthScale) / 10f;          // vanilla: depth / 10 m, clamped
            var a = _norm(wv);
            for (int i = 0; i < 4; i++) a[i] = Mathf.Clamp01(metres[i] * k);
            _one(wv) = -1f;                                    // per-corner, never the single-depth shortcut
            return true;
        }

        internal static bool Detect(WaterVolume wv) { if (!Resolve()) return false; _detect(wv); return true; }
        internal static bool Setup(WaterVolume wv) { if (!Resolve()) return false; _setup(wv); return true; }
    }

    /// <summary>A lake volume has no zone heightmap, so vanilla would read its forced depth (-1): put the real corner depths in.</summary>
    [HarmonyPatch(typeof(WaterVolume), "DetectWaterDepth")]
    internal static class Patch_DetectWaterDepth
    {
        private static void Postfix(WaterVolume __instance)
        {
            try
            {
                if (Plugin.Tiles.TryGetValue(__instance, out var t)) WaterAccess.Apply(__instance, t.Depth);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"lake depth: {ex.Message}"); }
        }
    }

    /// <summary>A swim box must not paint its depths onto the tile surface it borrows. Every other volume: no opinion.</summary>
    [HarmonyPatch(typeof(WaterVolume), "SetupMaterial")]
    [HarmonyPriority(Priority.Low)]
    internal static class Patch_SetupMaterial
    {
        private static bool Prefix(WaterVolume __instance) => !WaterAccess.CollisionOnly.Contains(__instance);
    }

    /// <summary>
    /// A quiet water sound near lakes: vanilla's own ocean loop (AudioMan.m_oceanAudio) through vanilla's ambient
    /// mixer, at the nearest lake tile, rising with the wind like the sea's. Cosmetic: any failure turns it off.
    /// </summary>
    internal static class LakeSound
    {
        private static AudioSource _src;
        private static float _timer;
        private static Vector3 _target;
        private static bool _near, _dead;

        internal static void Update(float dt)
        {
            if (_dead) return;
            try
            {
                var am = AudioMan.instance;
                if (am == null || Plugin.Tiles.Count == 0 || ConfigSync.Get(Plugin.SoundVolume) <= 0f)
                {
                    if (_src != null) _src.volume = Mathf.MoveTowards(_src.volume, 0f, 0.2f * dt);
                    return;
                }
                if (_src == null)
                {
                    var go = new GameObject("tarn_lake_ambient");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _src = go.AddComponent<AudioSource>();
                    _src.clip = am.m_oceanAudio;
                    _src.outputAudioMixerGroup = am.m_ambientMixer;
                    _src.loop = true; _src.spatialBlend = 0.8f; _src.minDistance = 12f; _src.maxDistance = 70f;
                    _src.rolloffMode = AudioRolloffMode.Linear; _src.dopplerLevel = 0f; _src.volume = 0f;
                    _src.Play();
                }
                _timer += dt;
                if (_timer > 1f)
                {
                    _timer = 0f;
                    Plugin.ListenerPosition(out var ear);
                    if (Camera.main != null) ear = Camera.main.transform.position;
                    float best = float.MaxValue;
                    foreach (var t in Plugin.Tiles.Values)
                    {
                        if (t.Volume == null || !t.Visual) continue;
                        var p = new Vector3(Mathf.Clamp(ear.x, t.Centre.x - 32f, t.Centre.x + 32f), t.Centre.y, Mathf.Clamp(ear.z, t.Centre.z - 32f, t.Centre.z + 32f));
                        float d = (p - ear).sqrMagnitude;
                        if (d < best) { best = d; _target = p; }
                    }
                    _near = best < 80f * 80f;
                }
                float wind = EnvMan.instance != null ? EnvMan.instance.GetWindIntensity() : 0.5f;
                float goal = _near ? ConfigSync.Get(Plugin.SoundVolume) * Mathf.Lerp(0.4f, 1f, wind) : 0f;
                _src.volume = Mathf.MoveTowards(_src.volume, goal, 0.15f * dt);
                _src.transform.position = Vector3.Lerp(_src.transform.position, _target, 0.1f);
            }
            catch (Exception ex)
            {
                _dead = true;
                Plugin.Log.LogWarning($"lake sound off: {ex.Message}");
            }
        }
    }
}
