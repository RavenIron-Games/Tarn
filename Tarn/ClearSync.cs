using System;
using System.IO;
using System.IO.Compression;

namespace RavenIron.Tarn
{
    /// <summary>
    /// The server owns the clearing mask; every player who generates zones uses it (owner, 2026-10-06: "sync the clear
    /// mask to clients").
    ///
    /// Vegetation is placed by whichever machine GENERATES a zone, which in multiplayer is usually a player's client, so
    /// the mask must reach the clients. The pattern is LakeSync's: a joining client asks (RequestRpc, repeated every few
    /// seconds until an answer comes) and the authority answers that peer alone; the client keeps the mask in memory and
    /// never reads its own .clear file while connected (Clearing.Ensure).
    ///
    /// The mask is 2 MB raw but sparse, so it is deflated and sent in ChunkBytes pieces: Steam's reliable messages stop at
    /// 512 KB and other transports lower, and one ZPackage that size is not something to bet on. A server without a mask
    /// answers with a single empty message, so the client stops waiting.
    ///
    /// Zones a client generates before the mask arrives keep vanilla's vegetation: Clearing's patch logs the first one.
    ///
    /// Wire (each chunk): ZPackage { int protocol, string tarnVersion, string world, int size, float extent, int rawLen,
    /// int packedLen, int index, int count, byte[] data }. count 0 = the server has no mask.
    /// </summary>
    internal static class ClearSync
    {
        internal const string RequestRpc = "com.raveniron.tarn.clearrequest";
        internal const string ChunkRpc = "com.raveniron.tarn.clearchunk";
        private const int Protocol = 1;
        private const int ChunkBytes = 48 * 1024;
        private const float AskEvery = 4f;
        private const int GiveUpAfter = 15;               // ~1 minute

        private static ZRoutedRpc _registeredOn;
        private static float _sinceAsk = AskEvery;
        private static int _asks;
        private static bool _answered, _gaveUp;
        private static byte[][] _parts;
        private static int _partsGot;
        private static string _partsKey;

        internal static bool GaveUp => _gaveUp;

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
                    rpc.Register<ZPackage>(ChunkRpc, RPC_Chunk);
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"clear sync: register failed: {ex.Message}"); }
                _registeredOn = rpc;
                _answered = _gaveUp = false;
                _asks = 0; _sinceAsk = AskEvery;
                _parts = null; _partsGot = 0; _partsKey = null;
                Clearing.ResetClient();
            }
            if (LakeSync.IsAuthority || _answered || _gaveUp) return;
            if (ZNet.instance.GetServerPeer() == null) return;  // not connected yet
            _sinceAsk += dt;
            if (_sinceAsk < AskEvery) return;
            _sinceAsk = 0f;
            if (++_asks > GiveUpAfter)
            {
                _gaveUp = true;
                Plugin.Log.LogWarning("clear sync: the server never sent its clearing mask - is it running Tarn 0.3.0 or newer? Zones this client generates keep vanilla's vegetation.");
                return;
            }
            try { rpc.InvokeRoutedRPC(RequestRpc); }
            catch (Exception ex) { Plugin.Log.LogWarning($"clear sync: asking the server failed: {ex.Message}"); }
        }

        private static ZPackage Chunk(string world, int n, float extent, int rawLen, int packedLen, int index, int count, byte[] data)
        {
            var pkg = new ZPackage();
            pkg.Write(Protocol);
            pkg.Write(Plugin.Version);
            pkg.Write(world);
            pkg.Write(n);
            pkg.Write(extent);
            pkg.Write(rawLen);
            pkg.Write(packedLen);
            pkg.Write(index);
            pkg.Write(count);
            pkg.Write(data ?? new byte[0]);
            return pkg;
        }

        private static void RPC_Request(long sender)
        {
            if (!LakeSync.IsAuthority) return;
            try
            {
                string world = Plugin.WorldName();
                if (!Clearing.TryGetRaw(out int n, out float extent, out byte[] bits))
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(sender, ChunkRpc, Chunk(world, 0, 0f, 0, 0, 0, 0, null));
                    Plugin.Log.LogInfo($"clear sync: told peer {sender} this world has no clearing mask");
                    return;
                }
                byte[] packed;
                using (var ms = new MemoryStream())
                {
                    using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(bits, 0, bits.Length);
                    packed = ms.ToArray();
                }
                int count = (packed.Length + ChunkBytes - 1) / ChunkBytes;
                for (int i = 0; i < count; i++)
                {
                    int len = Math.Min(ChunkBytes, packed.Length - i * ChunkBytes);
                    var part = new byte[len];
                    Buffer.BlockCopy(packed, i * ChunkBytes, part, 0, len);
                    ZRoutedRpc.instance.InvokeRoutedRPC(sender, ChunkRpc, Chunk(world, n, extent, bits.Length, packed.Length, i, count, part));
                }
                Plugin.Log.LogInfo($"clear sync: sent the clearing mask to peer {sender} ({packed.Length / 1024} KB deflated from {bits.Length / 1024} KB, {count} chunk(s))");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"clear sync: answering peer {sender} failed: {ex.Message}"); }
        }

        private static void RPC_Chunk(long sender, ZPackage pkg)
        {
            if (LakeSync.IsAuthority || _answered) return;   // a host hears nothing it needs; a repeat after the answer is spare
            try
            {
                int protocol = pkg.ReadInt();
                if (protocol != Protocol)
                {
                    Plugin.Log.LogWarning($"clear sync: the server speaks clearing protocol {protocol}, this Tarn {Protocol} - update Tarn to match. No clearing.");
                    _gaveUp = true; return;
                }
                pkg.ReadString();                              // the server's Tarn version (LakeSync already warns on a mismatch)
                string world = pkg.ReadString();
                int n = pkg.ReadInt();
                float extent = pkg.ReadSingle();
                int rawLen = pkg.ReadInt(), packedLen = pkg.ReadInt(), index = pkg.ReadInt(), count = pkg.ReadInt();
                byte[] data = pkg.ReadByteArray();
                if (count == 0)
                {
                    _answered = true;
                    Clearing.SetFromServer(world, 0, 0f, null);
                    return;
                }
                string key = $"{world}|{n}|{rawLen}|{packedLen}|{count}";
                if (key != _partsKey) { _partsKey = key; _parts = new byte[count][]; _partsGot = 0; }
                if (index < 0 || index >= count || _parts[index] != null) return;
                _parts[index] = data;
                if (++_partsGot < count) return;

                var packed = new byte[packedLen];
                int at = 0;
                foreach (var p in _parts) { Buffer.BlockCopy(p, 0, packed, at, p.Length); at += p.Length; }
                if (at != packedLen) throw new InvalidDataException($"{at} of {packedLen} bytes arrived");
                var raw = new byte[rawLen];
                using (var ds = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
                {
                    int got = 0, r;
                    while (got < rawLen && (r = ds.Read(raw, got, rawLen - got)) > 0) got += r;
                    if (got != rawLen) throw new InvalidDataException($"{got} of {rawLen} bytes after inflating");
                }
                if ((long)n * n > (long)rawLen * 8) throw new InvalidDataException($"a {n} x {n} mask needs more than {rawLen} bytes");
                _answered = true;
                _parts = null;
                Clearing.SetFromServer(world, n, extent, raw);
            }
            catch (Exception ex)
            {
                _parts = null; _partsKey = null;               // the next ask starts the transfer over
                Plugin.Log.LogWarning($"clear sync: reading the server's clearing mask failed: {ex.Message}");
            }
        }
    }
}
