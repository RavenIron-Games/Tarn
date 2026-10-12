using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace RavenIron.Tarn
{
    /// <summary>
    /// `tarn clearbeds &lt;lake|all&gt; [confirm]`: removes the trees, rocks, bushes and pickables ALREADY standing on lake
    /// beds (owner, 2026-10-11). 0.4.0's lake-bed clearing only stops vegetation being placed in zones generated after
    /// a lake exists, so a lake made with `tarn here` on explored ground keeps what stood there.
    ///
    /// What goes: a ZDO in a generated zone whose prefab is one of vanilla's vegetation prefabs (ZoneSystem.m_vegetation,
    /// exactly what PlaceVegetation places), with no creator (player-built pieces carry one), standing on the bed as
    /// LakeBeds clears it (LakeShape.InBed). Nothing else: no stumps, logs, drops, creatures, pieces. Without `confirm`
    /// it only counts. A tree a player grew from a sapling is the wild tree's prefab and cannot be told apart.
    ///
    /// It runs on the authority, as a job: shaping lakes a few milliseconds a frame (rl003 alone is 357k samples), then
    /// one lake's bed scanned per frame, then the removal. A player's request comes by routed RPC, checked against the
    /// server's adminlist, and the answer goes back to that player: vanilla's remote-command pipe runs a command in the
    /// server's console and prints nothing back.
    /// </summary>
    internal static class ClearBeds
    {
        private const string RequestRpc = "com.raveniron.tarn.clearbeds";
        private const string ReplyRpc = "com.raveniron.tarn.clearbedsreply";

        private sealed class Job
        {
            public string Target;
            public bool Confirm;
            public long Requester;                 // 0 = this machine's console
            public List<Plugin.LakeRt> Lakes;
            public int Shaped, Scanned;
            public readonly HashSet<ZDO> Found = new HashSet<ZDO>();
            public readonly Dictionary<string, int> PerLake = new Dictionary<string, int>();
            public readonly List<string> Dry = new List<string>();
            public Dictionary<int, string> Vegetation;
            public readonly Stopwatch Started = Stopwatch.StartNew();
        }

        private static Job _job;
        private static ZRoutedRpc _registeredOn;
        private static readonly List<ZDO> _sector = new List<ZDO>();
        private static readonly SimulationDistance JustTheZone = new SimulationDistance(0, 0, classic: true);

        internal static void Tick(float budgetMs)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc != null && !ReferenceEquals(rpc, _registeredOn))
            {
                try
                {
                    rpc.Register<string, bool>(RequestRpc, RPC_Request);
                    rpc.Register<string>(ReplyRpc, RPC_Reply);
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"clearbeds: register failed: {ex.Message}"); }
                _registeredOn = rpc;
                _job = null;
            }
            if (_job == null) return;
            try { Advance(budgetMs); }
            catch (Exception ex)
            {
                var j = _job; _job = null;
                Reply(j.Requester, $"tarn clearbeds {j.Target}: failed ({ex.Message}); nothing more was removed");
                Plugin.Log.LogError($"clearbeds: {ex}");
            }
        }

        /// <summary>The console command. Returns what to print now; the result follows when the job is done.</summary>
        internal static string Command(string target, bool confirm)
        {
            if (string.IsNullOrEmpty(target))
                return "tarn clearbeds <lake|all> - count the trees, rocks, bushes and pickables standing on lake beds in explored ground\n" +
                       "tarn clearbeds <lake|all> confirm - remove them (admins only on a server; cannot be undone)";
            if (!LakeSync.IsAuthority)
            {
                var rpc = ZRoutedRpc.instance;
                if (rpc == null || ZNet.instance == null || ZNet.instance.GetServerPeer() == null) return "tarn clearbeds: not connected";
                rpc.InvokeRoutedRPC(RequestRpc, target, confirm);   // no target: the server
                return $"tarn clearbeds: sent to the server (admins only); the answer arrives here";
            }
            return Start(target, confirm, 0L);
        }

        private static string Start(string target, bool confirm, long requester)
        {
            if (_job != null) return $"tarn clearbeds: busy with '{_job.Target}', try again in a moment";
            if (WorldGenerator.instance == null || ZDOMan.instance == null || ZoneSystem.instance == null) return "tarn clearbeds: no world loaded";
            var lakes = target.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? Plugin.LakeList.ToList()
                : Plugin.LakeList.Where(r => r.L.Name.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (lakes.Count == 0) return $"tarn clearbeds: no lake named '{target}' (tarn list shows the nearest)";
            _job = new Job { Target = target, Confirm = confirm, Requester = requester, Lakes = lakes };
            int toShape = lakes.Count(r => r.Shape == null || !r.Shape.Done);
            Plugin.Log.LogInfo($"clearbeds: {(confirm ? "removing" : "counting")} on {lakes.Count} lake bed(s) for {(requester == 0 ? "the local console" : "peer " + requester)}");
            return $"tarn clearbeds {target}: {(confirm ? "removing" : "counting")} on {lakes.Count} lake bed(s)" +
                   (toShape > 0 ? $", shaping {toShape} first" : "") + "; the result follows in a moment";
        }

        private static void Advance(float budgetMs)
        {
            var j = _job;
            var wg = WorldGenerator.instance;
            if (wg == null || ZDOMan.instance == null || ZoneSystem.instance == null) { _job = null; Reply(j.Requester, $"tarn clearbeds {j.Target}: the world went away; nothing was removed"); return; }
            var sw = Stopwatch.StartNew();
            // 1. shape, within the budget
            while (j.Shaped < j.Lakes.Count)
            {
                var rt = j.Lakes[j.Shaped];
                var s = rt.Shape ?? (rt.Shape = new LakeShape(rt.L));
                while (!s.Sampled)
                {
                    if (sw.Elapsed.TotalMilliseconds >= budgetMs) return;
                    s.SampleRow(wg);
                }
                if (!s.Done) s.Flood();
                j.Shaped++;
            }
            // 2. one lake's bed a frame
            if (j.Vegetation == null) j.Vegetation = VegetationPrefabs();
            if (j.Scanned < j.Lakes.Count)
            {
                Scan(j, j.Lakes[j.Scanned++]);
                return;
            }
            // 3. the removal, and the answer
            _job = null;
            int removed = 0;
            if (j.Confirm)
            {
                long me = ZDOMan.GetSessionID();
                foreach (var z in j.Found)
                {
                    if (!z.IsValid()) continue;
                    if (!z.IsOwner()) z.SetOwner(me);   // DestroyZDO only sends for an owner
                    ZDOMan.instance.DestroyZDO(z);
                    removed++;
                }
            }
            Reply(j.Requester, Report(j, removed));
        }

        private static Dictionary<int, string> VegetationPrefabs()
        {
            var d = new Dictionary<int, string>();
            foreach (var v in ZoneSystem.instance.m_vegetation)
                if (v != null && v.m_prefab != null) d[v.m_prefab.name.GetStableHashCode()] = v.m_prefab.name;
            return d;
        }

        private static void Scan(Job j, Plugin.LakeRt rt)
        {
            var s = rt.Shape;
            if (s == null || s.Failed != null) { j.Dry.Add(rt.L.Name); return; }
            float pad = LakeBeds.Pad;
            Vector2s a = ZoneSystem.GetZone(new Vector3(s.X0 - pad, 0f, s.Z0 - pad));
            Vector2s b = ZoneSystem.GetZone(new Vector3(s.Cx(s.Nx - 1) + pad, 0f, s.Cz(s.Nz - 1) + pad));
            int n = 0;
            for (int zy = a.y; zy <= b.y; zy++)
                for (int zx = a.x; zx <= b.x; zx++)
                {
                    _sector.Clear();
                    ZDOMan.instance.FindSectorObjects(new Vector2s(zx, zy), JustTheZone, _sector);
                    foreach (var z in _sector)
                    {
                        if (z == null || !j.Vegetation.ContainsKey(z.GetPrefab())) continue;
                        if (z.GetLong(ZDOVars.s_creator) != 0L) continue;          // built by a player
                        var p = z.GetPosition();
                        if (!s.InBed(p.x, p.z, pad)) continue;
                        if (j.Found.Add(z)) n++;
                    }
                }
            if (n > 0) j.PerLake[rt.L.Name] = n;
        }

        private static string Report(Job j, int removed)
        {
            var kinds = j.Found.GroupBy(z => z.GetPrefab())
                               .Select(g => (name: j.Vegetation.TryGetValue(g.Key, out var nm) ? nm : g.Key.ToString(), count: g.Count()))
                               .OrderByDescending(k => k.count).ToList();
            var sb = new StringBuilder($"tarn clearbeds {j.Target}: ");
            if (j.Found.Count == 0) sb.Append($"nothing stands on {j.Lakes.Count - j.Dry.Count} lake bed(s) in explored ground");
            else
            {
                sb.Append(j.Confirm ? $"removed {removed} object(s)" : $"{j.Found.Count} object(s) stand on the bed(s)");
                sb.Append(": ").Append(string.Join(", ", kinds.Take(8).Select(k => $"{k.name} x{k.count}")));
                if (kinds.Count > 8) sb.Append($", and {kinds.Count - 8} more kind(s)");
                if (j.PerLake.Count > 1)
                    sb.Append("\n  by lake: ").Append(string.Join(", ", j.PerLake.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} {kv.Value}")))
                      .Append(j.PerLake.Count > 10 ? $", and {j.PerLake.Count - 10} more" : "");
                if (!j.Confirm) sb.Append($"\n  run `tarn clearbeds {j.Target} confirm` to remove them (cannot be undone)");
            }
            if (j.Dry.Count > 0) sb.Append($"\n  skipped {j.Dry.Count} lake(s) whose point is dry: {string.Join(", ", j.Dry.Take(5))}");
            sb.Append($" ({j.Started.Elapsed.TotalSeconds:0.0} s)");
            return sb.ToString();
        }

        private static void Reply(long requester, string text)
        {
            Plugin.Log.LogInfo(text.Replace("\n", " |"));
            if (requester == 0L)
            {
                if (global::Console.instance != null) global::Console.instance.Print(text);
                return;
            }
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(requester, ReplyRpc, text); }
            catch (Exception ex) { Plugin.Log.LogWarning($"clearbeds: answering peer {requester} failed: {ex.Message}"); }
        }

        private static void RPC_Request(long sender, string target, bool confirm)
        {
            if (!LakeSync.IsAuthority) return;
            string answer;
            try
            {
                var peer = ZNet.instance.GetPeer(sender);
                if (peer == null || peer.m_socket == null || !ZNet.instance.IsAdmin(peer.m_socket.GetHostName()))
                {
                    answer = "tarn clearbeds: you are not on the server's adminlist";
                    Plugin.Log.LogWarning($"clearbeds: refused peer {sender} ({(peer?.m_socket != null ? peer.m_socket.GetHostName() : "unknown")}): not an admin");
                }
                else answer = Start(target ?? "", confirm, sender);
            }
            catch (Exception ex) { answer = $"tarn clearbeds: the server failed ({ex.Message})"; Plugin.Log.LogWarning($"clearbeds: {ex}"); }
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReplyRpc, answer); } catch { }
        }

        private static void RPC_Reply(long sender, string text)
        {
            if (LakeSync.IsAuthority) return;
            Plugin.Log.LogInfo(text.Replace("\n", " |"));
            if (global::Console.instance != null) global::Console.instance.Print(text);
        }
    }
}
