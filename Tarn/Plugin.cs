using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RavenIron.Tarn
{
    /// <summary>
    /// PROTOTYPE (2026-10-05): water at its own height. Valheim's water is a WaterVolume whose surface is simply its
    /// transform's height (WaterVolume.GetWaterSurface = transform.position.y + waves + m_surfaceOffset), and swimming,
    /// boats, floating and the underwater camera all find water through physics on the "WaterVolume" layer (trigger
    /// enter/exit, and Floating.GetLiquidLevel's overlap). Proven in-game on RavenrestTest13: a copy at 92 m made the
    /// player Wet and swim, 60 m above the sea.
    ///
    /// A lake has the shape of its basin: everything below its level joined to its deepest point (a flood fill over
    /// the generated ground, LakeBuilder). It is drawn with vanilla's own 64 m sea tiles, each with its four corner depths
    /// from the ground under it (colour, foam and wave size follow depth) and its surface mesh clipped to the basin.
    /// The water you swim in is a set of trigger boxes covering exactly the basin. Lakes are built as you come near and
    /// dropped behind you, a few milliseconds a frame.
    ///
    /// Lakes live per world in BepInEx/config/Tarn/&lt;world&gt;.txt on the SERVER (or the host / single player), one per
    /// line: written by the Ravenrest map pipeline (dem/lakes_15.py) or by `tarn here`. Players get them from the server
    /// (LakeSync); a player's own file is read only when they are the server.
    /// </summary>
    [BepInPlugin(Guid, "Tarn", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.raveniron.tarn";
        public const string Version = "0.4.0";
        internal static ManualLogSource Log;
        private static readonly CultureInfo C = CultureInfo.InvariantCulture;

        internal static ConfigEntry<float> DepthScale;
        internal static ConfigEntry<float> SoundVolume;
        internal static ConfigEntry<float> BuildRange;
        internal static ConfigEntry<float> BudgetMs;
        internal static ConfigEntry<bool> ClearLakeBeds;

        private static ZoneSystem _loadedFor;
        private static readonly List<LakeRt> Lakes = new List<LakeRt>();
        private static LakeBuilder _building;
        private static float _streamTimer;
        private static bool _loggedTemplate;

        // per water volume a lake made: the raw water depth in metres at its corners (vanilla's order: [0] minX,maxZ
        // [1] maxX,maxZ [2] maxX,minZ [3] minX,minZ). Visual tiles also feed the lake sound.
        internal sealed class Tile { public WaterVolume Volume; public float[] Depth; public Vector3 Centre; public bool Visual; }
        internal static readonly Dictionary<WaterVolume, Tile> Tiles = new Dictionary<WaterVolume, Tile>();

        internal sealed class Lake
        {
            public string Name;
            public float X, Z, Level, MinX, MinZ, MaxX, MaxZ, Depth;
            public string ToLine() => string.Join(";", Name, F(X), F(Z), F(Level), F(MinX), F(MinZ), F(MaxX), F(MaxZ), F(Depth));
            private static string F(float v) => v.ToString("0.###", C);
            public static Lake Parse(string line)
            {
                var p = line.Split(';');
                if (p.Length < 9) return null;
                float G(int i) => float.Parse(p[i], NumberStyles.Float, C);
                return new Lake { Name = p[0], X = G(1), Z = G(2), Level = G(3), MinX = G(4), MinZ = G(5), MaxX = G(6), MaxZ = G(7), Depth = G(8) };
            }
        }

        internal sealed class LakeRt
        {
            public Lake L;
            public bool Built;
            public readonly List<UnityEngine.Object> Owned = new List<UnityEngine.Object>();
            public int WetCells, VisualTiles, Boxes;
            public string Failed;
            public LakeShape Shape;         // sampled ground and flood, kept across drops (generator heights never change)
            public float DistanceTo(Vector3 p)
            {
                float dx = Mathf.Max(0f, Mathf.Max(L.MinX - p.x, p.x - L.MaxX));
                float dz = Mathf.Max(0f, Mathf.Max(L.MinZ - p.z, p.z - L.MaxZ));
                return Mathf.Sqrt(dx * dx + dz * dz);
            }
        }

        private void Awake()
        {
            Log = Logger;
            DepthScale = Config.Bind("Look", "DepthScale", 0.5f,
                new ConfigDescription("How deep the game's water shader thinks a lake is, as a share of its real depth. Vanilla's colour, foam and wave size all follow depth (full colour and waves at 10 m), so lower is calmer and greener, higher bluer and rougher. Live: `tarn depth <value>`.",
                    new AcceptableValueRange<float>(0f, 3f)));
            SoundVolume = Config.Bind("Sound", "LakeVolume", 0.35f,
                new ConfigDescription("Volume of the water sound near a lake (vanilla's ocean loop, quieter). 0 = off.", new AcceptableValueRange<float>(0f, 1f)));
            BuildRange = Config.Bind("Performance", "BuildRange", 400f,
                new ConfigDescription("Lakes within this many metres of you are built; they are dropped again 150 m further out.", new AcceptableValueRange<float>(150f, 2000f)));
            BudgetMs = Config.Bind("Performance", "BuildBudgetMs", 2f,
                new ConfigDescription("Milliseconds per frame spent building lakes.", new AcceptableValueRange<float>(0.5f, 20f)));
            ClearLakeBeds = Config.Bind("Clearing", "LakeBeds", true,
                "Keep trees, rocks, bushes and pickable debris off every lake's bed (the ground under its water, to the shore), " +
                "including lakes made with `tarn here`. Only zones generated after a lake exists are affected. In multiplayer " +
                "the SERVER's setting applies to everyone (it is sent with the lakes).");
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"Tarn {Version} (prototype) loaded");
        }

        // One Update, no coroutine: a new ZoneSystem means a new world (load its lakes); then a time-budgeted build
        // cursor and a twice-a-second streaming pass.
        private void Update()
        {
            LakeSound.Update(Time.deltaTime);
            var zs = ZoneSystem.instance;
            if (zs == null || ZNet.instance == null) { if (_loadedFor != null) Unload(); return; }
            if (zs != _loadedFor)
            {
                Unload();
                _loadedFor = zs;
                if (LakeSync.IsAuthority)
                {
                    try
                    {
                        Lakes.AddRange(LoadLakes().Select(l => new LakeRt { L = l }));
                        Log.LogInfo($"world '{WorldName()}': {Lakes.Count} lake(s) from {LakeFile()}, lake beds {(ClearLakeBeds.Value ? "kept clear" : "not cleared")}" +
                                    (ZNet.instance.IsDedicated() ? " (dedicated server: sent to players, no water built here)" : ""));
                    }
                    catch (Exception ex) { Log.LogError($"loading lakes failed: {ex}"); }
                }
                else Log.LogInfo("joined a server: waiting for its lakes");
            }
            LakeSync.Tick(Time.deltaTime);
            ClearSync.Tick(Time.deltaTime);
            ClearBeds.Tick(BudgetMs.Value);
            if (MapLakes.Dirty && _loadedFor == zs && Minimap.instance != null)
            {
                MapLakes.Dirty = false;
                try { MapLakes.Paint(Lakes.Select(r => r.L)); }
                catch (Exception ex) { Log.LogWarning($"drawing lakes on the map failed: {ex.Message}"); }
            }
            if (_building != null)
            {
                var sw = Stopwatch.StartNew();
                bool done;
                try { done = _building.Step(sw, BudgetMs.Value); }
                catch (Exception ex)
                {
                    Log.LogError($"building lake '{_building.Rt.L.Name}' failed: {ex}");
                    _building.Rt.Failed = ex.Message; Drop(_building.Rt); _building.Rt.Built = true; done = true;
                }
                if (done) _building = null;
            }
            else
            {
                try { LakeBeds.Warm(Time.deltaTime, BudgetMs.Value); }
                catch (Exception ex) { Log.LogWarning($"lake beds: shaping ahead failed: {ex.Message}"); LakeBeds.Reset(); }
            }
            _streamTimer += Time.deltaTime;
            if (_streamTimer < 0.5f) return;
            _streamTimer = 0f;
            if (!ListenerPosition(out var at)) return;      // a dedicated server draws nothing (sync comes later)
            float build = BuildRange.Value, drop = build + 150f;
            foreach (var rt in Lakes)
                if (rt.Built && rt.Failed == null && rt.DistanceTo(at) > drop && _building?.Rt != rt) Drop(rt);
            if (_building == null)
            {
                var next = Lakes.Where(r => !r.Built && r.DistanceTo(at) < build).OrderBy(r => r.DistanceTo(at)).FirstOrDefault();
                if (next != null) { _building = new LakeBuilder(next, Template()); next.Built = true; }
            }
        }

        internal static bool ListenerPosition(out Vector3 p)
        {
            if (Player.m_localPlayer != null) { p = Player.m_localPlayer.transform.position; return true; }
            var cam = Camera.main;
            if (cam != null && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) { p = cam.transform.position; return true; }
            p = default; return false;
        }

        private static void Unload()
        {
            foreach (var rt in Lakes) Drop(rt);
            Lakes.Clear();
            Tiles.Clear();
            _building = null;
            _loadedFor = null;
            LakeBeds.Reset();
        }

        internal static void Drop(LakeRt rt)
        {
            foreach (var o in rt.Owned)
            {
                if (o is GameObject go && go != null)
                    foreach (var wv in go.GetComponentsInChildren<WaterVolume>(true)) { Tiles.Remove(wv); WaterAccess.CollisionOnly.Remove(wv); }
                if (o != null) UnityEngine.Object.Destroy(o);
            }
            rt.Owned.Clear();
            rt.Built = false;
            rt.WetCells = rt.VisualTiles = rt.Boxes = 0;
        }

        internal static string WorldName() => ZNet.instance != null ? ZNet.instance.GetWorldName() : "unknown";
        internal static string LakeFile() => Path.Combine(Paths.ConfigPath, "Tarn", WorldName() + ".txt");

        private static IEnumerable<Lake> LoadLakes()
        {
            string f = LakeFile();
            if (!File.Exists(f)) yield break;
            foreach (var line in File.ReadAllLines(f))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                Lake l = null;
                try { l = Lake.Parse(line.Trim()); } catch (Exception ex) { Log.LogWarning($"bad lake line '{line}': {ex.Message}"); }
                if (l != null) yield return l;
            }
        }

        internal static void SaveLakes()
        {
            string f = LakeFile();
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            var lines = new List<string> { "# Tarn lakes: name;x;z;level;minX;minZ;maxX;maxZ;depth (world metres, absolute heights; x, z = a point in the water)" };
            lines.AddRange(Lakes.Select(l => l.L.ToLine()));
            File.WriteAllLines(f, lines.ToArray());
        }

        internal static WaterVolume Template()
        {
            var zs = ZoneSystem.instance;
            var wv = zs != null && zs.m_zonePrefab != null ? zs.m_zonePrefab.GetComponentInChildren<WaterVolume>(true) : null;
            if (wv != null && !_loggedTemplate) { _loggedTemplate = true; Log.LogInfo("zone water template:\n" + DescribeTemplate()); }
            return wv;
        }

        internal static string DescribeTemplate()
        {
            var zs = ZoneSystem.instance;
            var wv = zs != null && zs.m_zonePrefab != null ? zs.m_zonePrefab.GetComponentInChildren<WaterVolume>(true) : null;
            if (wv == null) return "no zone water template found";
            var sb = new StringBuilder();
            var root = zs.m_zonePrefab.transform;
            sb.Append($"zone prefab '{root.name}' root pos {root.position}\n");
            sb.Append($"water '{wv.name}' pos {wv.transform.position} (parent '{(wv.transform.parent ? wv.transform.parent.name : "-")}') lossy scale {wv.transform.lossyScale} layer {LayerMask.LayerToName(wv.gameObject.layer)}\n");
            sb.Append($"  surfaceOffset {wv.m_surfaceOffset} forceDepth {wv.m_forceDepth} globalWind {wv.m_useGlobalWind}\n");
            foreach (var c in wv.GetComponents<Collider>())
                sb.Append($"  collider {c.GetType().Name}" + (c is BoxCollider b ? $" size {b.size} centre {b.center} trigger {b.isTrigger}" : "") + "\n");
            if (wv.m_waterSurface != null)
            {
                var st = wv.m_waterSurface.transform;
                var mf = st.GetComponent<MeshFilter>();
                sb.Append($"surface '{st.name}' pos {st.position} (parent '{(st.parent ? st.parent.name : "-")}') lossy scale {st.lossyScale} layer {LayerMask.LayerToName(st.gameObject.layer)}" +
                          (mf && mf.sharedMesh ? $" mesh centre {mf.sharedMesh.bounds.center} size {mf.sharedMesh.bounds.size}, {mf.sharedMesh.vertexCount} verts, {mf.sharedMesh.subMeshCount} submesh(es)" : "") +
                          $"; components: {string.Join(", ", st.GetComponents<Component>().Select(c => c.GetType().Name))}\n");
                foreach (var c in st.GetComponents<Collider>())
                    sb.Append($"  surface collider {c.GetType().Name}" + (c is BoxCollider b ? $" size {b.size} centre {b.center} trigger {b.isTrigger}" : "") + "\n");
            }
            return sb.ToString();
        }

        internal static float GroundHeight(float x, float z)
        {
            // loaded terrain first (it includes the player's edits), else the generator
            if (Heightmap.GetHeight(new Vector3(x, 0f, z), out float h)) return h;
            return WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : 0f;
        }

        /// <summary>Re-reads every lake volume's depths (after `tarn depth`): the patched DetectWaterDepth, then SetupMaterial.</summary>
        internal static int Reapply()
        {
            int n = 0;
            foreach (var t in Tiles.Values.ToList())
            {
                if (t.Volume == null) continue;
                if (!WaterAccess.Detect(t.Volume) || !WaterAccess.Setup(t.Volume)) break;
                n++;
            }
            return n;
        }

        internal static List<Lake> CurrentLakes() => Lakes.Select(r => r.L).ToList();
        internal static List<LakeRt> LakeList => Lakes;

        // A new list (from the server, or re-read) keeps the shape of every lake whose line is unchanged: shaping a big lake
        // is seconds of budgeted work, and the server re-sends the whole list after every `tarn here`.
        private static Dictionary<string, LakeShape> KeptShapes()
        {
            var d = new Dictionary<string, LakeShape>();
            foreach (var rt in Lakes) if (rt.Shape != null && rt.Shape.Done) d[rt.L.ToLine()] = rt.Shape;
            LakeBeds.Reset();
            return d;
        }

        private static LakeShape KeptShape(Dictionary<string, LakeShape> kept, Lake l)
            => kept.TryGetValue(l.ToLine(), out var s) ? s : null;

        /// <summary>A client's whole list, from the server: everything rebuilt from it.</summary>
        internal static void ReplaceLakes(List<Lake> lakes, string source)
        {
            var shapes = KeptShapes();
            foreach (var rt in Lakes) Drop(rt);
            Lakes.Clear(); _building = null;
            Lakes.AddRange(lakes.Select(l => new LakeRt { L = l, Shape = KeptShape(shapes, l) }));
            _streamTimer = 1f;
            MapLakes.Dirty = true;
            Log.LogInfo($"{Lakes.Count} lake(s) from {source}");
        }

        /// <summary>Server side: save, then every player gets the new list.</summary>
        private static void Changed()
        {
            SaveLakes();
            LakeSync.Broadcast(CurrentLakes());
            MapLakes.Dirty = true;
        }

        internal static void Notify(string text)
        {
            try { if (MessageHud.instance != null) MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text); } catch { }
        }

        /// <summary>
        /// A player who is not the server sends a changing command to the server through vanilla's remote-command pipe:
        /// the server checks adminlist.txt ("You are not admin" otherwise), logs it, and runs it in its own console. The
        /// new list comes back to everyone through LakeSync.
        /// </summary>
        internal static bool Forward(string line, out string reply)
        {
            reply = null;
            if (LakeSync.IsAuthority || ZNet.instance == null) return false;
            ZNet.instance.RemoteCommand(line);
            reply = $"tarn: sent '{line}' to the server (admins only); the new lakes arrive in a moment";
            return true;
        }

        internal static string Here(Vector3 p, float radius, float margin)
        {
            if (Forward(string.Format(C, "tarn at {0:0.##} {1:0.##} {2:0.##} {3:0.###}", p.x, p.z, radius, margin), out var fwd)) return fwd;
            var r = Spill.Compute(GroundHeight, p.x, p.z, radius, 2f, margin);
            float sea = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            if (r == null) return "no spill found (unexpected)";
            if (r.Level <= sea + 0.2f) return $"this hollow drains to the sea: it spills at {r.SpillHeight:0.0} m, the sea is {sea:0.0} m";
            if (r.WetCells == 0) return $"you are not in a hollow: it would spill at {r.SpillHeight:0.0} m and the ground here is higher. Stand in the low part.";
            int k = 1;
            while (Lakes.Any(x => x.L.Name == "lake" + k)) k++;
            var l = new Lake { Name = "lake" + k, X = p.x, Z = p.z, Level = r.Level, MinX = r.MinX, MinZ = r.MinZ,
                               MaxX = r.MaxX, MaxZ = r.MaxZ, Depth = r.Depth };
            Lakes.Add(new LakeRt { L = l });
            Changed();
            _streamTimer = 1f;                  // build it on the next pass
            return $"{l.Name}: level {r.Level:0.00} m (spills at {r.SpillHeight:0.00}), deepest {r.Depth:0.0} m, " +
                   $"{r.AreaM2:0} m2 wet. Saved to {LakeFile()}" +
                   (ClearLakeBeds.Value ? ". Trees already standing here stay; zones generated from now on keep its bed clear" : "");
        }

        internal static string Remove(string name)
        {
            if (Forward("tarn remove " + name, out var fwd)) return fwd;
            int n = 0;
            for (int i = Lakes.Count - 1; i >= 0; i--)
            {
                if (name != "all" && Lakes[i].L.Name != name) continue;
                if (_building?.Rt == Lakes[i]) _building = null;
                Drop(Lakes[i]);
                Lakes.RemoveAt(i); n++;
            }
            Changed();
            return $"removed {n} lake(s)";
        }

        internal static string Reload()
        {
            if (Forward("tarn reload", out var fwd)) return fwd;
            var shapes = KeptShapes();
            foreach (var rt in Lakes) Drop(rt);
            Lakes.Clear(); _building = null;
            Lakes.AddRange(LoadLakes().Select(l => new LakeRt { L = l, Shape = KeptShape(shapes, l) }));
            _streamTimer = 1f;
            MapLakes.Dirty = true;
            LakeSync.Broadcast(CurrentLakes());
            return $"{Lakes.Count} lake(s) read from {LakeFile()}; nearby ones rebuild now";
        }

        internal static string List(int max)
        {
            if (Lakes.Count == 0) return $"no lakes in this world ({LakeFile()})";
            ListenerPosition(out var at);
            var sb = new StringBuilder($"{Lakes.Count} lake(s), {Lakes.Count(r => r.Built && r.Failed == null)} built (nearest first):\n");
            foreach (var r in Lakes.OrderBy(r => r.DistanceTo(at)).Take(max))
                sb.Append($"{r.L.Name}: ({r.L.X:0}, {r.L.Z:0}) level {r.L.Level:0.0}, {r.DistanceTo(at):0} m away, " +
                          (r.Failed != null ? "FAILED: " + r.Failed : r.Built ? (_building?.Rt == r ? "building" : $"built: {r.WetCells * 16} m2, {r.VisualTiles} tiles, {r.Boxes} boxes") : "not built") + "\n");
            return sb.ToString().TrimEnd();
        }
    }

    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    internal static class Patch_Terminal
    {
        private static void Postfix()
        {
            try
            {
                new Terminal.ConsoleCommand("tarn", "Tarn lakes: tarn here | list [n] | depth [scale] | reload | remove <name|all> | clearbeds <lake|all> [confirm] | info", args =>
                {
                    try
                    {
                        string sub = args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "help";
                        var c = CultureInfo.InvariantCulture;
                        switch (sub)
                        {
                            case "at":
                                if (args.Args.Length < 4) { args.Context.AddString("tarn at <x> <z> [radius] [margin]"); return; }
                                args.Context.AddString(Plugin.Here(new Vector3(float.Parse(args.Args[2], c), 0f, float.Parse(args.Args[3], c)),
                                    args.Args.Length > 4 ? float.Parse(args.Args[4], c) : 120f, args.Args.Length > 5 ? float.Parse(args.Args[5], c) : 0.3f));
                                return;
                            case "here":
                                if (Player.m_localPlayer == null) { args.Context.AddString("no local player"); return; }
                                float radius = args.Args.Length > 2 ? float.Parse(args.Args[2], c) : 120f;
                                float margin = args.Args.Length > 3 ? float.Parse(args.Args[3], c) : 0.3f;
                                args.Context.AddString(Plugin.Here(Player.m_localPlayer.transform.position, radius, margin));
                                return;
                            case "depth":
                                if (args.Args.Length > 2)
                                {
                                    Plugin.DepthScale.Value = float.Parse(args.Args[2], c);
                                    int n = Plugin.Reapply();
                                    args.Context.AddString($"depth scale {Plugin.DepthScale.Value:0.##} (saved), {n} lake volume(s) updated");
                                }
                                else args.Context.AddString($"depth scale {Plugin.DepthScale.Value:0.##}: the water shader sees each lake at that share of its real depth " +
                                                            "(colour, foam and waves reach full at 10 m). Try 0.2 (calm, green) to 1 (sea-like). tarn depth <value>");
                                return;
                            case "list": args.Context.AddString(Plugin.List(args.Args.Length > 2 ? int.Parse(args.Args[2], c) : 10)); return;
                            case "reload": args.Context.AddString(Plugin.Reload()); return;
                            case "remove": args.Context.AddString(Plugin.Remove(args.Args.Length > 2 ? args.Args[2] : "")); return;
                            case "info": args.Context.AddString(Plugin.DescribeTemplate()); return;
                            case "clearbeds":
                                args.Context.AddString(ClearBeds.Command(args.Args.Length > 2 ? args.Args[2] : "",
                                    args.Args.Length > 3 && args.Args[3].Equals("confirm", StringComparison.OrdinalIgnoreCase)));
                                return;
                            default:
                                args.Context.AddString("tarn here [radius=120] [margin=0.3] - fill the hollow you stand in, up to just under its spill point\n" +
                                                       "tarn list [n] - the n nearest lakes (default 10) and whether they are built\n" +
                                                       "tarn depth [scale] - how deep lakes look (colour, foam, waves); live\n" +
                                                       "tarn reload - re-read this world's lake file (on the server)\n" +
                                                       "tarn at <x> <z> [radius] [margin] - tarn here at a point (a player's tarn here sends this to the server)\n" +
                                                       "tarn clearbeds <lake|all> [confirm] - count (then remove) trees, rocks and bushes already standing on lake beds\n" +
                                                       "tarn remove <name|all> | tarn info (the zone water template)");
                                return;
                        }
                    }
                    catch (Exception ex) { args.Context.AddString("tarn: " + ex.Message); Plugin.Log.LogWarning(ex.ToString()); }
                });
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"tarn console: register failed: {ex.Message}"); }
        }
    }
}
