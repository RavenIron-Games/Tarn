using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;

namespace RavenIron.Tarn
{
    /// <summary>
    /// Every Tarn setting follows the server (owner, 2026-10-12: "all changeable values need to server sync").
    ///
    /// The authority (dedicated server, host, single player) uses its own config. It sends every setting to a joining
    /// player (with the lakes, LakeSync.RPC_Request) and to everybody whenever one changes: a `tarn` command, a config
    /// manager, or the .cfg file edited on disk, which is noticed within a few seconds (its write time is checked from
    /// Plugin.Update, no watcher thread) and re-read with ConfigFile.Reload, whose SetSerializedValue raises each
    /// changed entry's SettingChanged (read out of libs\BepInEx.dll, 5.4).
    ///
    /// A connected player reads every setting through Get: the server's value while it has one, its own otherwise. The
    /// server's values live in memory only; the player's .cfg is never written, so their own settings are back in single
    /// player. A server older than 0.4.1 sends nothing, and the player keeps its own values (and, for the two lake-bed
    /// settings, what the lake message carries).
    ///
    /// Wire: ZPackage { int protocol, string tarnVersion, int count, count x (string section, string key, string value) },
    /// values in BepInEx's own serialized form (TomlTypeConverter, invariant culture).
    /// </summary>
    internal static class ConfigSync
    {
        private const string ConfigRpc = "com.raveniron.tarn.config";
        private const int Protocol = 1;
        private const float FileCheckEvery = 3f;

        private static ConfigFile _file;
        private static readonly List<ConfigEntryBase> _entries = new List<ConfigEntryBase>();
        private static readonly Dictionary<ConfigEntryBase, object> _server = new Dictionary<ConfigEntryBase, object>();
        private static ZRoutedRpc _registeredOn;
        private static bool _dirty;
        private static float _sinceFileCheck;
        private static DateTime _fileTime;

        internal static void Init(ConfigFile file, params ConfigEntryBase[] entries)
        {
            _file = file;
            _entries.AddRange(entries);
            file.SettingChanged += (s, e) => _dirty = true;
            _fileTime = FileTime();
        }

        /// <summary>The value in force here: the server's while connected to one that sent it, else this machine's.</summary>
        internal static T Get<T>(ConfigEntry<T> e) => Connected && _server.TryGetValue(e, out var v) ? (T)v : e.Value;

        internal static bool HasServer(ConfigEntryBase e) => Connected && _server.ContainsKey(e);

        // on a joined player's game only: never on the authority, and never with no world up (a past server's values)
        private static bool Connected => ZNet.instance != null && !LakeSync.IsAuthority;

        internal static void Tick(float dt)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            if (!ReferenceEquals(rpc, _registeredOn))
            {
                try { rpc.Register<ZPackage>(ConfigRpc, RPC_Config); }
                catch (Exception ex) { Plugin.Log.LogWarning($"config sync: register failed: {ex.Message}"); }
                _registeredOn = rpc;
                _server.Clear();
            }
            if (!LakeSync.IsAuthority) return;
            _sinceFileCheck += dt;
            if (_sinceFileCheck >= FileCheckEvery)
            {
                _sinceFileCheck = 0f;
                var t = FileTime();
                if (t != _fileTime)
                {
                    _fileTime = t;
                    ReloadFile("its file changed");
                }
            }
            if (_dirty)
            {
                _dirty = false;
                Broadcast();
            }
        }

        /// <summary>Authority: re-read the .cfg (also `tarn reload`); every changed setting reaches the players.</summary>
        internal static void ReloadFile(string why)
        {
            if (_file == null) return;
            try
            {
                var before = Snapshot();
                _file.Reload();
                _fileTime = FileTime();
                var changed = new List<string>();
                foreach (var e in _entries)
                    if (before[e] != e.GetSerializedValue()) changed.Add($"{e.Definition.Key} {before[e]} -> {e.GetSerializedValue()}");
                if (changed.Count > 0) Plugin.Log.LogInfo($"config: re-read ({why}): {string.Join(", ", changed)}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"config: re-reading {_file.ConfigFilePath} failed: {ex.Message}"); }
        }

        private static Dictionary<ConfigEntryBase, string> Snapshot()
        {
            var d = new Dictionary<ConfigEntryBase, string>();
            foreach (var e in _entries) d[e] = e.GetSerializedValue();
            return d;
        }

        private static DateTime FileTime()
        {
            try { return _file != null && File.Exists(_file.ConfigFilePath) ? File.GetLastWriteTimeUtc(_file.ConfigFilePath) : default; }
            catch { return default; }
        }

        private static ZPackage Pack()
        {
            var pkg = new ZPackage();
            pkg.Write(Protocol);
            pkg.Write(Plugin.Version);
            pkg.Write(_entries.Count);
            foreach (var e in _entries)
            {
                pkg.Write(e.Definition.Section);
                pkg.Write(e.Definition.Key);
                pkg.Write(e.GetSerializedValue());
            }
            return pkg;
        }

        /// <summary>Authority: one player's settings, sent when they ask for the lakes.</summary>
        internal static void SendTo(long peer)
        {
            if (!LakeSync.IsAuthority || ZRoutedRpc.instance == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(peer, ConfigRpc, Pack()); }
            catch (Exception ex) { Plugin.Log.LogWarning($"config sync: sending to peer {peer} failed: {ex.Message}"); }
        }

        private static void Broadcast()
        {
            if (!LakeSync.IsAuthority || ZRoutedRpc.instance == null || ZNet.instance == null || ZNet.instance.GetConnectedPeers().Count == 0) return;
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, ConfigRpc, Pack());
                Plugin.Log.LogInfo($"config sync: sent the changed settings to {ZNet.instance.GetConnectedPeers().Count} player(s)");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"config sync: broadcast failed: {ex.Message}"); }
        }

        private static void RPC_Config(long sender, ZPackage pkg)
        {
            if (LakeSync.IsAuthority) return;                  // a host hears its own broadcast: its config is the source
            try
            {
                int protocol = pkg.ReadInt();
                if (protocol != Protocol)
                {
                    Plugin.Log.LogWarning($"config sync: the server speaks config protocol {protocol}, this Tarn {Protocol} - update Tarn to match. Using this machine's settings.");
                    return;
                }
                pkg.ReadString();                              // the server's Tarn version (LakeSync warns on a mismatch)
                int n = pkg.ReadInt();
                bool first = _server.Count == 0;
                var changed = new List<string>();
                float depthBefore = Get(Plugin.DepthScale);
                for (int i = 0; i < n; i++)
                {
                    string section = pkg.ReadString(), key = pkg.ReadString(), text = pkg.ReadString();
                    var e = _entries.Find(x => x.Definition.Section == section && x.Definition.Key == key);
                    if (e == null) continue;                   // a setting this version does not have
                    object v = TomlTypeConverter.ConvertToValue(text, e.SettingType);
                    if (e.Description?.AcceptableValues != null) v = e.Description.AcceptableValues.Clamp(v);
                    bool had = _server.TryGetValue(e, out var old);
                    _server[e] = v;
                    if (!had || !Equals(old, v)) changed.Add($"{key} {TomlTypeConverter.ConvertToString(v, e.SettingType)}");
                }
                if (changed.Count > 0)
                    Plugin.Log.LogInfo($"config sync: {(first ? "the server's settings" : "the server changed")}: {string.Join(", ", changed)}");
                if (!first && changed.Count > 0) Plugin.Notify($"Tarn: the server changed {changed.Count} setting(s)");
                if (Get(Plugin.DepthScale) != depthBefore) Plugin.Reapply();
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"config sync: reading the server's settings failed: {ex.Message}"); }
        }
    }
}
