using System;
using System.Collections.Generic;
using UnityEngine;

namespace RavenIron.Tarn
{
    /// <summary>
    /// The server owns the lakes; every player draws them.
    ///
    /// The AUTHORITY (a dedicated server, or whoever hosts or plays alone: ZNet.IsServer) reads
    /// BepInEx/config/Tarn/&lt;world&gt;.txt and is the only one that ever writes it. A joining client asks for the list
    /// (RequestRpc, repeated every few seconds until an answer comes, because the first ask can leave before the
    /// connection is fully up) and the server answers that peer alone; after any change (`tarn at`, `tarn remove`,
    /// `tarn reload` on the server, or forwarded to it by an admin) it sends the new list to everybody. A client keeps the
    /// list in memory only and never reads its own file while connected, so a stale local copy cannot disagree with the
    /// server. A headless server builds no water: nothing there is drawn, and nothing it simulates stands in a lake.
    ///
    /// Wire: ZPackage { int protocol, string tarnVersion, string world, int count, count x string lakeLine, bool lakeBeds, float lakeBedShore }.
    /// lakeBeds (0.4.0) is the server's Clearing.LakeBeds, appended rather than a new protocol: a 0.3.0 client stops
    /// reading before it, and a 0.4.0 client finds the package ends there when the server is older (then no bed clearing,
    /// because the server never chose it). lakeBedShore (0.4.1) follows the same way; a 0.4.0 server's is its fixed 2 m.
    /// </summary>
    internal static class LakeSync
    {
        internal const string RequestRpc = "com.raveniron.tarn.request";
        internal const string LakesRpc = "com.raveniron.tarn.lakes";
        private const int Protocol = 1;
        internal const float MaxShore = 10f;
        private const float AskEvery = 4f;
        private const int GiveUpAfter = 15;               // ~1 minute

        private static ZRoutedRpc _registeredOn;
        private static float _sinceAsk = AskEvery;
        private static int _asks;
        private static bool _answered, _gaveUp, _warnedVersion;
        private static bool _serverLakeBeds;
        private const float ShoreBefore041 = 2f;          // what 0.4.0 cleared, fixed, before it was a setting
        private static float _serverShore = ShoreBefore041;

        internal static bool IsAuthority => ZNet.instance != null && ZNet.instance.IsServer();
        internal static bool Answered => _answered;
        internal static bool GaveUp => _gaveUp;

        /// <summary>Whether lake beds are kept clear here: the authority's own setting, or what the server sent.</summary>
        internal static bool LakeBedsOn => IsAuthority ? Plugin.ClearLakeBeds.Value
            : ConfigSync.HasServer(Plugin.ClearLakeBeds) ? _answered && ConfigSync.Get(Plugin.ClearLakeBeds)
            : _answered && _serverLakeBeds;               // a 0.4.0 / 0.4.1 server: what its lake message carried

        /// <summary>How far past the water's edge lake beds are cleared: the authority's own setting, or the server's.</summary>
        internal static float LakeBedShore => IsAuthority || ConfigSync.HasServer(Plugin.LakeBedShore) ? ConfigSync.Get(Plugin.LakeBedShore) : _serverShore;

        /// <summary>Called every frame from Plugin.Update while a world is up.</summary>
        internal static void Tick(float dt)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            if (!ReferenceEquals(rpc, _registeredOn))
            {
                try
                {
                    rpc.Register(RequestRpc, RPC_Request);
                    rpc.Register<ZPackage>(LakesRpc, RPC_Lakes);
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"lake sync: register failed: {ex.Message}"); }
                _registeredOn = rpc;
                _answered = _gaveUp = _warnedVersion = _serverLakeBeds = false;
                _serverShore = ShoreBefore041;
                _asks = 0; _sinceAsk = AskEvery;
            }
            if (IsAuthority || _answered || _gaveUp) return;
            if (ZNet.instance.GetServerPeer() == null) return;  // not connected yet
            _sinceAsk += dt;
            if (_sinceAsk < AskEvery) return;
            _sinceAsk = 0f;
            if (++_asks > GiveUpAfter)
            {
                _gaveUp = true;
                Plugin.Log.LogWarning("lake sync: the server never sent its lakes - is Tarn installed on the server? No lakes this session.");
                return;
            }
            try { rpc.InvokeRoutedRPC(RequestRpc); }
            catch (Exception ex) { Plugin.Log.LogWarning($"lake sync: asking the server failed: {ex.Message}"); }
        }

        private static ZPackage Pack(IEnumerable<Plugin.Lake> lakes)
        {
            var lines = new List<string>();
            foreach (var l in lakes) lines.Add(l.ToLine());
            var pkg = new ZPackage();
            pkg.Write(Protocol);
            pkg.Write(Plugin.Version);
            pkg.Write(Plugin.WorldName());
            pkg.Write(lines.Count);
            foreach (var s in lines) pkg.Write(s);
            pkg.Write(Plugin.ClearLakeBeds.Value);
            pkg.Write(Plugin.LakeBedShore.Value);
            return pkg;
        }

        /// <summary>Server: after any change, every connected player gets the new list.</summary>
        internal static void Broadcast(IEnumerable<Plugin.Lake> lakes)
        {
            var rpc = ZRoutedRpc.instance;
            if (!IsAuthority || rpc == null || ZNet.instance.GetConnectedPeers().Count == 0) return;
            try { rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, LakesRpc, Pack(lakes)); }
            catch (Exception ex) { Plugin.Log.LogWarning($"lake sync: broadcast failed: {ex.Message}"); }
        }

        private static void RPC_Request(long sender)
        {
            if (!IsAuthority) return;
            try
            {
                ConfigSync.SendTo(sender);                 // the settings first: the lake-bed ones apply to these lakes
                var lakes = Plugin.CurrentLakes();
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, LakesRpc, Pack(lakes));
                Plugin.Log.LogInfo($"lake sync: sent {lakes.Count} lake(s) to peer {sender}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"lake sync: answering peer {sender} failed: {ex.Message}"); }
        }

        private static void RPC_Lakes(long sender, ZPackage pkg)
        {
            if (IsAuthority) return;                       // a host hears its own broadcast: it already has the list
            try
            {
                int protocol = pkg.ReadInt();
                if (protocol != Protocol)
                {
                    Plugin.Log.LogWarning($"lake sync: the server speaks lake protocol {protocol}, this Tarn {Protocol} - update Tarn to match. No lakes.");
                    _gaveUp = true; return;
                }
                string version = pkg.ReadString();
                string world = pkg.ReadString();
                int n = pkg.ReadInt();
                var lakes = new List<Plugin.Lake>(n);
                for (int i = 0; i < n; i++)
                {
                    var l = Plugin.Lake.Parse(pkg.ReadString());
                    if (l != null) lakes.Add(l);
                }
                bool beds = pkg.GetPos() < pkg.Size() && pkg.ReadBool();   // absent from a server older than 0.4.0
                float shore = pkg.GetPos() < pkg.Size() ? pkg.ReadSingle() : ShoreBefore041;   // absent before 0.4.1
                if (version != Plugin.Version && !_warnedVersion)
                {
                    _warnedVersion = true;
                    Plugin.Log.LogWarning($"lake sync: the server runs Tarn {version}, this client {Plugin.Version}. It works while the protocol matches; update to match.");
                }
                bool first = !_answered;
                _serverLakeBeds = beds;
                _serverShore = Mathf.Clamp(shore, 0f, MaxShore);
                _answered = true;
                Plugin.ReplaceLakes(lakes, $"the server ({world}), lake beds {(beds ? $"kept clear to {_serverShore:0.#} m past the water" : "not cleared")}");
                if (!first) Plugin.Notify($"Tarn: the server changed its lakes ({lakes.Count})");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"lake sync: reading the server's lakes failed: {ex.Message}"); }
        }
    }
}
