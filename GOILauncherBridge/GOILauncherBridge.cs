// GOILauncherBridge - lets GOILauncher.Multiplayer (Zonakkis, rooms protocol) clients join a Zenith server
//
// Setup: put GOILauncherBridge.dll in plugins/GOILauncherBridge/, open UDP 9027, and run Zenith with --nosteam
// (GOILauncher clients can't do Steam auth). Settings are in plugins/GOILauncherBridge/config.json:
// Port (9027), ListenAddress, MaxPlayers, RoomName, ServerSenderName, UniqueLoopbackPerIp, ColorNames.
//
// How it works: the plugin listens for GOILauncher.Multiplayer clients (LiteNetLib, default UDP 9027).
// For every client that joins, it opens a real Zenith client connection to this server over loopback,
// so to Zenith (and every other plugin) that person is an ordinary player: chat, /commands, kicks,
// bans, ranks and all events work unchanged. The plugin translates packets in both directions.
//
// IPs: each real IP is given its own stable loopback address (127.x.y.z, saved in ip-map.json), and the
// bridge connection to Zenith is made from that address. So "ban <player>", ranks and IP lookups in
// Zenith work per real person. Bans on the REAL IP (banip 1.2.3.4) are also enforced for bridged players.
// Use the "goiip" command to see a bridged player's real IP. Don't delete ip-map.json.
//
// Building: lib/LiteNetLib.dll is Zonakkis' LiteNetLib fork (0.9 branch, github.com/Zonakkis/LiteNetLib,
// commit 35927de). The NuGet LiteNetLib can't be used: the fork has a newer packet format that GOILauncher
// clients rely on. ILRepack (see ILRepack.targets) merges it into GOILauncherBridge.dll after every build, so
// only that one dll is shipped. (Costura doesn't work here: Zenith calls GetTypes() on plugins before any
// plugin code runs, and the packet types need LiteNetLib at that point.)
//
// By luckycdev. Uses LiteNetLib (MIT, Ruslan Pyrch; license in lib/) and GOILauncher.Multiplayer packet
// definitions by Zonakkis (MIT, notice above the protocol section at the bottom of this file).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Net.Http;
using GOILauncher.Multiplayer.Core.Data.Constants;
using GOILauncher.Multiplayer.Core.Data.Models;
using GOILauncher.Multiplayer.Core.Data.Packets;
using GOILauncher.Multiplayer.Network;
using Lidgren.Network;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using Pyratron.Frameworks.Commands.Parser;
using ServerShared;
using ServerShared.Logging;
using ServerShared.Player;
using ServerShared.Plugins;
using UnityEngine;
using Logger = ServerShared.Logging.Logger;
using ZMsg = ServerShared.MessageType;
using NetPeer = LiteNetLib.NetPeer;
using ConnectionState = LiteNetLib.ConnectionState;
using GOILauncher.Multiplayer.Core.Extensions;
using GOILauncher.Multiplayer.Core.Utils;
using System.Security.Cryptography;

namespace GOILauncherBridge
{
    public class BridgeConfig
    {
        public bool Enabled = true;
        /// <summary>UDP port GOILauncher.Multiplayer clients connect to (their default is 9027).</summary>
        public int Port = 9027;
        /// <summary>Address the bridge listens on. 0.0.0.0 = all interfaces.</summary>
        public string ListenAddress = "0.0.0.0";
        /// <summary>Max simultaneous bridged players (Zenith's own player limit still applies).</summary>
        public int MaxPlayers = 50;
        /// <summary>Name of the room GOILauncher players see instead of the lobby.</summary>
        public string RoomName = "Zenith";
        /// <summary>Sender name GOILauncher players see for server/system messages.</summary>
        public string ServerSenderName = "[Server]";
        /// <summary>Give each real IP its own loopback address so Zenith bans/ranks work per person.</summary>
        public bool UniqueLoopbackPerIp = true;
        /// <summary>Show player names in their chat color (e.g. ChatManager rank colors) on GOILauncher clients.</summary>
        public bool ColorNames = true;
    }

    public class GOILauncherBridgePlugin : IPlugin
    {
        public string Name => "GOILauncherBridge";
        public string Version => "0.1";
        public string Author => "luckycdev";

        internal static GOILauncherBridgePlugin Instance;
        internal BridgeConfig Config;
        internal string Folder;
        private Dictionary<string, string> _ipMap = new Dictionary<string, string>();
        private readonly object _ipLock = new object();

        private NetManager _net;
        private Thread _thread;
        private volatile bool _running;
        internal readonly Dictionary<int, Session> Sessions = new Dictionary<int, Session>();
        private readonly Dictionary<ulong, Action<NetPeer, NetDataReader>> _handlers = new Dictionary<ulong, Action<NetPeer, NetDataReader>>();
        private double _nextBanCheck;

        public void Initialize()
        {
            Instance = this;
            Folder = Path.Combine("plugins", "GOILauncherBridge");
            Directory.CreateDirectory(Folder);
            LoadConfig();
            LoadIpMap();
            Logger.LogInfo($"[{Name}] Initialized!");
            _ = CheckForNewerVersionAsync();
            if (!Config.Enabled)
            {
                Logger.LogInfo($"[{Name}] Disabled in config.json.");
                return;
            }
            try
            {
                var server = GameServer.Instance;
                server?.PlayerCommands.RegisterCommandsFromAssembly(typeof(GOILauncherBridgePlugin).Assembly);
                server?.ServerCommands.RegisterCommandsFromAssembly(typeof(GOILauncherBridgePlugin).Assembly);
            }
            catch (Exception ex) { Logger.LogWarning($"[{Name}] Could not register commands: {ex.Message}"); }

            RegisterHandlers();
            var listener = new EventBasedNetListener();
            listener.ConnectionRequestEvent += OnConnectionRequest;
            listener.PeerConnectedEvent += OnPeerConnected;
            listener.PeerDisconnectedEvent += OnPeerDisconnected;
            listener.NetworkReceiveEvent += OnReceive;
            _net = new NetManager(listener) { ChannelsCount = NetworkChannels.Count, DisconnectTimeout = 10000 };
            var ip = IPAddress.Parse(Config.ListenAddress);
            if (!_net.Start(ip, IPAddress.IPv6Any, Config.Port))
            {
                Logger.LogError($"[{Name}] Could not listen on UDP port {Config.Port} (is it already in use?).");
                return;
            }
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "GOILauncherBridge" };
            _thread.Start();
            Logger.LogInfo($"[{Name}] GOILauncher.Multiplayer clients can join on UDP port {Config.Port}.");
            if (GameServer.Instance != null && GameServer.Instance.RequireSteamAuth)
                Logger.LogWarning($"[{Name}] This server requires Steam auth. GOILauncher players have no Steam ticket, so they will be refused.");
        }

        public void Shutdown()
        {
            _running = false;
            try { _thread?.Join(2000); } catch { }
            foreach (var s in Sessions.Values.ToList()) s.Close("Server shutting down");
            Sessions.Clear();
            try { _net?.Stop(); } catch { }
            SaveConfig();
            Logger.LogInfo($"[{Name}] Shutdown!");
        }

        private async Task CheckForNewerVersionAsync()
        {
            try
            {
                using var http = new HttpClient();
                var fileContent = await http.GetStringAsync("https://raw.githubusercontent.com/luckycdev/Zenith-Plugins/main/GOILauncherBridge/GOILauncherBridge.cs");

                var versionMatch = Regex.Match(
                    fileContent,
                    @"public\s+string\s+Version\s*=>\s*""([^""]+)"""
                );

                if (versionMatch.Success)
                {
                    var remoteVersionStr = versionMatch.Groups[1].Value;
                    var localVersionStr = Version;

                    if (System.Version.TryParse(remoteVersionStr, out var remoteVersion) &&
                        System.Version.TryParse(localVersionStr, out var localVersion))
                    {
                        if (remoteVersion > localVersion)
                        {
                            Logger.LogWarning(
                                $"[{Name}] A newer version is available! Installed: {localVersion}, Latest: {remoteVersion}"
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[{Name}] Error checking for new version: {ex}");
            }
        }

        // ------------------------------------------------------------------ main loop (single thread)
        private void Loop()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double nextTick = 0;
            while (_running)
            {
                try
                {
                    _net.PollEvents();
                    double now = sw.Elapsed.TotalSeconds;
                    foreach (var s in Sessions.Values.ToList()) s.Update(now);
                    if (now >= nextTick)
                    {
                        nextTick = now + 1.0 / 30.0;
                        foreach (var s in Sessions.Values.ToList()) s.Tick();
                    }
                    if (now >= _nextBanCheck)
                    {
                        _nextBanCheck = now + 2.0;
                        foreach (var s in Sessions.Values.ToList()) s.CheckBan();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[{Name}] Loop error: {ex}");
                }
                Thread.Sleep(4);
            }
        }

        // ------------------------------------------------------------------ GOILauncher side
        private void OnConnectionRequest(ConnectionRequest request)
        {
            if (Sessions.Count >= Config.MaxPlayers) { request.Reject(); return; }
            request.AcceptIfKey(RoomConstants.ConnectionKey);
        }

        private void OnPeerConnected(NetPeer peer)
        {
            var session = new Session(this, peer);
            Sessions[peer.Id] = session;
            session.SendServerHandshake();
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (Sessions.TryGetValue(peer.Id, out var s))
            {
                Sessions.Remove(peer.Id);
                s.OnClientGone();
            }
        }

        private void OnReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod method)
        {
            try
            {
                if (reader.AvailableBytes >= 8 && _handlers.TryGetValue(reader.GetULong(), out var handler))
                    handler(peer, reader);
            }
            catch (Exception ex)
            {
                Logger.LogDebug($"[{Name}] Bad packet from {peer.EndPoint}: {ex.Message}");
            }
            finally { reader.Recycle(); }
        }

        private void On<T>(Action<Session, T> handle) where T : INetSerializable, new()
        {
            _handlers[TypeHash(typeof(T))] = (peer, reader) =>
            {
                if (!Sessions.TryGetValue(peer.Id, out var s)) return;
                var packet = new T();
                packet.Deserialize(reader);
                handle(s, packet);
            };
        }

        private void RegisterHandlers()
        {
            On<C2SClientHandShakePacket>((s, p) => s.OnClientHandshake(p));
            On<C2SPlayerStatePacket>((s, p) => s.OnState(p));
            On<C2SChatMessagePacket>((s, p) => s.OnChat(p));
            On<C2SIsInGameUpdatePacket>((s, p) => s.OnInGame(p.IsInGame));
            On<C2SRoomOperationPacket>((s, p) => s.OnRoomOperation(p));
            // Skin uploads/requests and opening-animation sync are not translated; they are ignored.
        }

        internal static ulong TypeHash(Type type)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in type.ToString()) { hash ^= c; hash *= 1099511628211UL; }
            return hash;
        }

        // ------------------------------------------------------------------ IP mapping
        internal IPAddress LoopbackFor(IPAddress real)
        {
            if (!Config.UniqueLoopbackPerIp) return IPAddress.Loopback;
            string key = real.ToString();
            lock (_ipLock)
            {
                if (!_ipMap.TryGetValue(key, out var fake))
                {
                    int n = _ipMap.Count + 1;   // 127.73.0.1, 127.73.0.2, ... (65k+ addresses)
                    fake = $"127.{73 + n / 65536}.{(n / 256) % 256}.{n % 256}";
                    if (n % 256 == 0) { n++; fake = $"127.{73 + n / 65536}.{(n / 256) % 256}.{n % 256}"; }
                    _ipMap[key] = fake;
                    SaveIpMap();
                }
                return IPAddress.Parse(fake);
            }
        }

        internal string RealIpFor(IPAddress loopback)
        {
            string fake = loopback.ToString();
            lock (_ipLock) return _ipMap.FirstOrDefault(kv => kv.Value == fake).Key;
        }

        private void LoadIpMap()
        {
            try
            {
                var path = Path.Combine(Folder, "ip-map.json");
                if (File.Exists(path))
                    _ipMap = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>();
            }
            catch (Exception ex) { Logger.LogError($"[{Name}] Could not read ip-map.json: {ex.Message}"); }
        }

        private void SaveIpMap()
        {
            try { File.WriteAllText(Path.Combine(Folder, "ip-map.json"), JsonConvert.SerializeObject(_ipMap, Formatting.Indented)); }
            catch (Exception ex) { Logger.LogError($"[{Name}] Could not save ip-map.json: {ex.Message}"); }
        }

        private void LoadConfig()
        {
            var path = Path.Combine(Folder, "config.json");
            try
            {
                Config = File.Exists(path) ? JsonConvert.DeserializeObject<BridgeConfig>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex) { Logger.LogError($"[{Name}] Bad config.json ({ex.Message}); using defaults."); }
            if (Config == null) Config = new BridgeConfig();
            SaveConfig();
        }

        internal void SaveConfig()
        {
            try { File.WriteAllText(Path.Combine(Folder, "config.json"), JsonConvert.SerializeObject(Config, Formatting.Indented)); }
            catch (Exception ex) { Logger.LogError($"[{Name}] Could not save config.json: {ex.Message}"); }
        }
    }

    // ====================================================================== one bridged player
    internal sealed class Session
    {
        private const int ServerVirtualId = 2000000000;
        private const ulong MyMembership = 1, ServerMembership = 2;
        private const int ZenithIdOffset = 1000000;

        private readonly GOILauncherBridgePlugin _plugin;
        public readonly NetPeer Peer;
        public readonly IPAddress RealIp;
        public IPAddress LoopbackIp;
        private readonly int _myId;

        public string Name;
        private Platform _platform;
        private bool _inGame, _handshaken, _ready, _closing;
        private GameClientPeer _zenith;
        private int _zenithId;
        private readonly Dictionary<int, string> _zNames = new Dictionary<int, string>();   // name as chat plugins show it
        private readonly Dictionary<int, string> _zShown = new Dictionary<int, string>();   // same, with color tag for GOILauncher
        private readonly Dictionary<int, PlayerMove> _zMoves = new Dictionary<int, PlayerMove>();
        private uint _sequence;
        private PlayerMove? _pendingMove;
        private PlayerMove _lastMove = Hidden();
        private double _closeAt = -1;
        private float? _lastAngle;                                 // last body-animation angle sent (see Pose)
        private readonly List<string> _sentChats = new List<string>();   // chats this client already shows locally
        private static readonly System.Text.RegularExpressions.Regex JoinLeave =
            new System.Text.RegularExpressions.Regex(@"^.+ (joined|left) the server\.?$");

        public Session(GOILauncherBridgePlugin plugin, NetPeer peer)
        {
            _plugin = plugin;
            Peer = peer;
            RealIp = peer.EndPoint.Address.IsIPv4MappedToIPv6 ? peer.EndPoint.Address.MapToIPv4() : peer.EndPoint.Address;
            _myId = peer.Id + 1;
        }

        // ---------- GOILauncher -> bridge
        public void SendServerHandshake() => Send(new S2CServerHandShakePacket { PlayerId = _myId }, DeliveryMethod.ReliableOrdered);

        public void OnClientHandshake(C2SClientHandShakePacket p)
        {
            if (_handshaken) return;
            _handshaken = true;
            Name = (p.PlayerName ?? "").Trim();
            _platform = p.Platform;
            _inGame = p.IsInGame;
            if (Name.Length == 0 || Name.Length > SharedConstants.MaxNameLength) { Kick("Invalid name."); return; }

            var server = GameServer.Instance;
            if (server == null) { Kick("Server not ready."); return; }
            if (server.RequireSteamAuth) { Kick("This server requires Steam, which GOILauncher clients can't provide."); return; }

            LoopbackIp = _plugin.LoopbackFor(RealIp);
            if (IsBanned(out var reason)) { Kick("You are banned from this server. " + reason); return; }

            ConnectToZenith(server.Port);
        }

        public void OnState(C2SPlayerStatePacket p)
        {
            if (!_ready || p.MembershipId != MyMembership) return;
            _pendingMove = ToZenith(p.State, ref _lastAngle);
        }

        public void OnChat(C2SChatMessagePacket p)
        {
            if (!_ready || string.IsNullOrWhiteSpace(p.Content)) return;
            var text = p.Content.Trim();
            // The GOILauncher client already shows what it typed; remember it so Zenith's echo of the same
            // text isn't shown twice. Anything else Zenith sends back as this player (e.g. /shrug) is shown.
            _sentChats.Add(text.Length > SharedConstants.MaxChatLength ? text.Substring(0, SharedConstants.MaxChatLength) : text);
            if (_sentChats.Count > 20) _sentChats.RemoveAt(0);
            var msg = _zenith.CreateMessage();
            msg.Write(ZMsg.ChatMessage);
            msg.Write(text);
            _zenith.SendMessage(msg, NetDeliveryMethod.ReliableOrdered, 0);
        }

        public void OnInGame(bool inGame)
        {
            _inGame = inGame;
            if (!inGame) { _pendingMove = Hidden(); _lastAngle = null; }   // park the character out of sight while in the menu
        }

        public void OnRoomOperation(C2SRoomOperationPacket p)
        {
            var error = RoomError.None;
            if (p.Operation == RoomOperation.Create || p.Operation == RoomOperation.Join || p.Operation == RoomOperation.Update)
                error = p.RoomId == RoomConstants.LobbyId && p.Operation == RoomOperation.Join ? RoomError.None : RoomError.Forbidden;
            Send(new S2CRoomOperationResultPacket { Result = new RoomOperationResult { RequestId = p.RequestId, Operation = p.Operation, Error = error } },
                DeliveryMethod.ReliableOrdered);
            if (_ready && p.Operation == RoomOperation.Refresh) SendRoomList();
        }

        public void OnClientGone()
        {
            _closing = true;
            try { _zenith?.Disconnect("bye"); } catch { }
            try { _zenith?.Shutdown("bye"); } catch { }
            if (_zenith != null) Logger.LogInfo($"[GOILauncherBridge] {Name} ({RealIp}) disconnected.");
        }

        // ---------- bridge -> Zenith
        private void ConnectToZenith(int zenithPort)
        {
            var config = new NetPeerConfiguration(SharedConstants.AppName)
            {
                MaximumConnections = 1, ConnectionTimeout = 5, PingInterval = 1f,
                LocalAddress = LoopbackIp, Port = 0
            };
            _zenith = new GameClientPeer(config);
            _zenith.Connected += (s, a) => Logger.LogDebug($"[GOILauncherBridge] {Name} bridge connection open.");
            _zenith.Disconnected += OnZenithDisconnected;
            _zenith.DataReceived += OnZenithData;
            try { _zenith.Start(); }
            catch (Exception ex)
            {
                if (LoopbackIp.Equals(IPAddress.Loopback)) { Kick("Bridge error."); Logger.LogError($"[GOILauncherBridge] {ex.Message}"); return; }
                Logger.LogWarning($"[GOILauncherBridge] Could not bind {LoopbackIp} ({ex.Message}); falling back to 127.0.0.1. IP bans by player will affect all bridged players.");
                LoopbackIp = IPAddress.Loopback;
                config = config.Clone(); config.LocalAddress = IPAddress.Loopback;
                _zenith = new GameClientPeer(config);
                _zenith.Disconnected += OnZenithDisconnected;
                _zenith.DataReceived += OnZenithData;
                _zenith.Start();
            }
            var hail = _zenith.CreateMessage();
            hail.Write(SharedConstants.Version);
            hail.Write(Name);
            hail.Write(_lastMove);
            hail.Write(false);  // no steam auth
            hail.Write(0);      // wins
            _zenith.Connect("127.0.0.1", zenithPort, hail);
            Logger.LogInfo($"[GOILauncherBridge] {Name} joining from {RealIp} (as {LoopbackIp}), platform {_platform}.");
        }

        public void Update(double now)
        {
            if (_closeAt > 0 && now >= _closeAt) { _closeAt = -1; Peer.Disconnect(); return; }
            if (_closeAt == 0) _closeAt = now + 0.6;
            _zenith?.Update();
        }

        public void Tick()
        {
            if (!_ready || _closing) return;
            if (_pendingMove.HasValue)
            {
                _lastMove = _pendingMove.Value;
                _pendingMove = null;
                var msg = _zenith.CreateMessage();
                msg.Write(ZMsg.MoveData);
                msg.Write(_lastMove);
                _zenith.SendMessage(msg, NetDeliveryMethod.UnreliableSequenced, SharedConstants.MoveDataChannel);
            }
        }

        public void CheckBan()
        {
            if (_closing || LoopbackIp == null) return;
            if (IsBanned(out var reason)) Kick("You have been banned from this server. " + reason);
        }

        private bool IsBanned(out string reason)
        {
            reason = null;
            try
            {
                var server = GameServer.Instance;
                if (server == null) return false;
                server.Config.PlayerData.RemoveExpiredBans();
                PlayerBan ban;
                if (server.IpBanned(RealIp, out ban) || (!LoopbackIp.Equals(IPAddress.Loopback) && server.IpBanned(LoopbackIp, out ban)))
                {
                    reason = ban.GetReasonWithExpiration();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void OnZenithDisconnected(object sender, ServerShared.DisconnectedEventArgs args)
        {
            if (_closing) return;
            string text = args.Reason switch
            {
                ServerShared.DisconnectReason.Banned => "You are banned from this server.",
                ServerShared.DisconnectReason.InvalidName => "Invalid name.",
                ServerShared.DisconnectReason.VersionNewer or ServerShared.DisconnectReason.VersionOlder => "Version mismatch with the server.",
                ServerShared.DisconnectReason.InvalidSteamSession => "This server requires Steam.",
                _ => string.IsNullOrWhiteSpace(args.ReasonString) || int.TryParse(args.ReasonString, out _) ? "Disconnected from the server." : args.ReasonString
            };
            if (!string.IsNullOrWhiteSpace(args.AdditionalInfo)) text += " " + args.AdditionalInfo;
            Kick(text);
        }

        private void OnZenithData(object sender, ServerShared.DataReceivedEventArgs args)
        {
            var m = args.Message;
            switch (args.MessageType)
            {
                case ZMsg.HandshakeResponse:
                {
                    _zenithId = m.ReadInt32();
                    m.ReadString(); m.ReadInt32(); m.ReadSingle(); m.ReadRgbaColor();
                    var names = m.ReadNamesDictionary();
                    m.ReadWinsDictionary(); m.ReadGoldnessDictionary(); m.ReadColorsDictionary();
                    var moves = m.ReadMovementDictionary();
                    foreach (var kv in names) SetName(kv.Key, kv.Value);
                    foreach (var kv in moves) _zMoves[kv.Key] = kv.Value;
                    _ready = true;
                    SendRoster();
                    break;
                }
                case ZMsg.CreatePlayer:
                {
                    int id = m.ReadInt32();
                    string name = m.ReadString();
                    var move = m.ReadPlayerMove();
                    if (id == _zenithId) break;
                    bool isNew = !_zNames.ContainsKey(id);
                    SetName(id, name);
                    _zMoves[id] = move;
                    if (isNew && _ready)
                    {
                        Send(new S2CPlayerJoinedPacket
                        {
                            Scope = new RoomPacketScope(MyMembership, MembershipOf(id)),
                            PlayerId = VirtualId(id), PlayerName = _zShown[id], Platform = Platform.PC, IsInGame = true
                        }, DeliveryMethod.ReliableOrdered);
                        SendRoomList();
                    }
                    break;
                }
                case ZMsg.RemovePlayer:
                {
                    int id = m.ReadInt32();
                    if (!_zNames.Remove(id)) break;
                    _zShown.Remove(id);
                    _zMoves.Remove(id);
                    if (_ready)
                    {
                        Send(new S2CPlayerLeftPacket { Scope = new RoomPacketScope(MyMembership, MembershipOf(id)), PlayerId = VirtualId(id) },
                            DeliveryMethod.ReliableOrdered);
                        SendRoomList();
                    }
                    break;
                }
                case ZMsg.MoveData:
                {
                    var moves = m.ReadMovementDictionary();
                    unchecked { _sequence++; }
                    foreach (var kv in moves)
                    {
                        if (kv.Key == _zenithId || !_zNames.ContainsKey(kv.Key)) continue;
                        _zMoves[kv.Key] = kv.Value;
                        if (!_ready) continue;
                        Send(new S2CPlayerStatePacket
                        {
                            Scope = new RoomPacketScope(MyMembership, MembershipOf(kv.Key)),
                            PlayerId = VirtualId(kv.Key), Sequence = _sequence, State = ToGoil(kv.Value)
                        }, DeliveryMethod.Unreliable);
                    }
                    break;
                }
                case ZMsg.ChatMessage:
                {
                    int id = m.ReadInt32();
                    string name = m.ReadString();
                    var color = m.ReadRgbaColor();
                    string text = m.ReadString();
                    if (!_ready) break;
                    if (id == _zenithId)
                    {
                        int i = _sentChats.IndexOf(text);
                        if (i >= 0) { _sentChats.RemoveRange(0, i + 1); break; }   // already shown locally
                        Chat(ServerVirtualId, ServerMembership, $"{name}: {text}", color);  // the client can't show "itself" as a remote sender
                        break;
                    }
                    if (id == 0 && JoinLeave.IsMatch(text)) break;   // GOILauncher shows its own join/leave lines
                    if (id != 0 && _zNames.TryGetValue(id, out var rosterName) && rosterName == name)
                        Chat(VirtualId(id), MembershipOf(id), text, color);
                    else
                        Chat(ServerVirtualId, ServerMembership, string.IsNullOrEmpty(name) ? text : $"{name}: {text}", color);
                    break;
                }
                // SpectateTarget, PlayerPotProperties, PlayerWins: no GOILauncher equivalent; ignored.
            }
        }

        // ---------- bridge -> GOILauncher
        private void SendRoster()
        {
            var members = new List<RoomMemberInfo>
            {
                new RoomMemberInfo(new PlayerInfo(_myId, Name, _platform, _inGame), MyMembership),
                new RoomMemberInfo(new PlayerInfo(ServerVirtualId, _plugin.Config.ServerSenderName, Platform.Unknown, false), ServerMembership),
            };
            foreach (var kv in _zShown)
                members.Add(new RoomMemberInfo(new PlayerInfo(VirtualId(kv.Key), kv.Value, Platform.PC, true), MembershipOf(kv.Key)));
            var packet = new S2CPlayerListPacket { Room = Room(members.Count) };
            packet.Members.AddRange(members);
            Send(packet, DeliveryMethod.ReliableOrdered);
            SendRoomList();
        }

        private void SendRoomList()
        {
            var packet = new S2CRoomListPacket();
            packet.Rooms.Add(Room(_zNames.Count + 2));
            Send(packet, DeliveryMethod.ReliableOrdered);
        }

        private RoomInfo Room(int count) => new RoomInfo(RoomConstants.LobbyId, _plugin.Config.RoomName, false, 0, count, null);

        private void SetName(int id, string zenithName)
        {
            var (name, color) = ChatNames.Display(id, zenithName);
            _zNames[id] = name;
            string open = _plugin.Config.ColorNames ? ColorTag(color) : null;
            _zShown[id] = open == null ? name : open + name + "</color>";
        }

        // "<color=#rrggbb>" for a non-white color, null for white.
        internal static string ColorTag(Color? color)
        {
            if (!color.HasValue) return null;
            var c = color.Value;
            int r = (int)Math.Round(Mathf.Clamp01(c.r) * 255), g = (int)Math.Round(Mathf.Clamp01(c.g) * 255), b = (int)Math.Round(Mathf.Clamp01(c.b) * 255);
            return r < 255 || g < 255 || b < 255 ? $"<color=#{r:x2}{g:x2}{b:x2}>" : null;
        }

        private void Chat(int fromId, ulong fromMembership, string text, Color? color = null)
        {
            // Zenith colors whole messages; GOILauncher chat (PC and mobile) renders Unity rich text, so the
            // color is carried as a <color> tag. Plain white is left untagged.
            string open = ColorTag(color);
            // GOILauncher chat shows one line per message, so multi-line replies (e.g. /help) are split.
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (open != null) line = open + line + "</color>";
                Send(new S2CChatMessagePacket
                {
                    Scope = new RoomPacketScope(MyMembership, fromMembership), PlayerId = fromId,
                    Content = line, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }, DeliveryMethod.ReliableOrdered);
            }
        }

        private void Kick(string reason)
        {
            if (_closing) return;
            _closing = true;
            Logger.LogInfo($"[GOILauncherBridge] {Name ?? RealIp.ToString()} removed: {reason}");
            if (_ready) Chat(ServerVirtualId, ServerMembership, reason);
            try { _zenith?.Disconnect("bye"); } catch { }
            _closeAt = 0;   // disconnect shortly, so the reason message gets delivered first
        }

        public void Close(string reason)
        {
            Kick(reason);
            try { Peer.Disconnect(); } catch { }
            try { _zenith?.Shutdown("bye"); } catch { }
        }

        private void Send<T>(T packet, DeliveryMethod method) where T : INetSerializable
        {
            if (Peer.ConnectionState != ConnectionState.Connected) return;
            Peer.Send(NetSerializablePacketWriter.Write(packet), NetworkChannels.Default, method);
        }

        public int ZenithId => _zenithId;
        public bool IsBridgedZenithPlayer => _ready;
        private static int VirtualId(int zenithId) => ZenithIdOffset + zenithId;
        private static ulong MembershipOf(int zenithId) => 1000UL + (ulong)zenithId;

        // ---------- movement conversion (same transforms on both sides: player root, Hub/Slider, Hub/Slider/Handle)
        private static PlayerMove Hidden() => new PlayerMove
        {
            Position = new Vector3(0f, -10000f, 0f), Rotation = Quaternion.identity,
            HandlePosition = new Vector3(0f, -10000f, 0f), HandleRotation = Quaternion.identity,
            SliderPosition = new Vector3(0f, -10000f, 0f), SliderRotation = Quaternion.identity,
        };

        internal static float ZAngle(Quaternion q)
        {
            // Unity's eulerAngles is a native call (unavailable here); GOI rotations are around Z only.
            float deg = 2f * (float)Math.Atan2(q.z, q.w) * 57.29578f;
            deg %= 360f;
            return deg < 0 ? deg + 360f : deg;
        }

        internal static Quaternion FromZ(float deg)
        {
            double half = deg * Math.PI / 360.0;
            return new Quaternion(0f, 0f, (float)Math.Sin(half), (float)Math.Cos(half));
        }

        private static PlayerState ToGoil(PlayerMove m) => new PlayerState
        {
            PlayerPosition = V(m.Position), PlayerRotation = ZAngle(m.Rotation),
            SliderPosition = V(m.SliderPosition), SliderRotation = ZAngle(m.SliderRotation),
            HandlePosition = V(m.HandlePosition),
            HandleRotation = new UnityQuaternion { X = m.HandleRotation.x, Y = m.HandleRotation.y, Z = m.HandleRotation.z, W = m.HandleRotation.w }
        };

        private static PlayerMove ToZenith(PlayerState s, ref float? lastAngle)
        {
            var move = new PlayerMove
            {
                Position = V(s.PlayerPosition), Rotation = FromZ(s.PlayerRotation),
                SliderPosition = V(s.SliderPosition), SliderRotation = FromZ(s.SliderRotation),
                HandlePosition = V(s.HandlePosition),
                HandleRotation = new Quaternion(s.HandleRotation.X, s.HandleRotation.Y, s.HandleRotation.Z, s.HandleRotation.W)
            };
            Pose.Fill(ref move, ref lastAngle);
            return move;
        }

        private static UnityVector3 V(Vector3 v) => new UnityVector3 { X = v.x, Y = v.y, Z = v.z };
        private static Vector3 V(UnityVector3 v) => new Vector3(v.X, v.Y, v.Z);
    }

    // ====================================================================== climber body pose
    // Regular Zenith clients send the game's body animator values (Angle, Extension); GOILauncher clients
    // only send transforms. These are computed exactly like the game's PoseControl.LateUpdate does, using
    // values from the game scene (Player prefab):
    //   hinge  = Hub -> Slider HingeJoint2D, so -jointAngle - referenceAngle = hubRot - sliderRot
    //   hammerMeshHub = Hub, so its localEulerAngles.z = hubRot - playerRot
    //   => Angle = Repeat(playerRot - sliderRot + 90, 360) / 180 - 1   (hub rotation cancels out)
    //   slider = Slider -> Handle SliderJoint2D, angle 0, anchors 0, limits -1.1 .. 0.7
    //   => translation = (handlePos - sliderPos) along the slider's x axis
    //      Extension = 0.25 * t / 0.7 (t >= 0)  or  0.25 * t / 1.1 (t < 0)
    // ====================================================================== chat name prefixes
    // Chat plugins (e.g. ChatManager) rename the sender on every message ("lucky" -> "[Mod] lucky").
    // GOILauncher clients show the player-list name on chat lines, so the list uses the same name the
    // plugins would put on that player's chat. Found by running the server's chat modifiers on a dummy line.
    internal static class ChatNames
    {
        private static readonly System.Reflection.FieldInfo ModifyField = typeof(GameServer).GetField(
            "OnChatMessageModify", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        public static (string name, Color? color) Display(int zenithId, string name)
        {
            try
            {
                var server = GameServer.Instance;
                if (server == null || ModifyField == null || !(ModifyField.GetValue(server) is Delegate del)) return (name, null);
                var player = server.Players.Values.ToList().FirstOrDefault(p => p.Id == zenithId);
                if (player == null) return (name, null);
                string n = name, msg = "x"; Color color = Color.white;
                foreach (GameServer.ChatMessageModifier mod in del.GetInvocationList())
                {
                    (n, msg, color) = mod(player, n, msg, color);
                    if (string.IsNullOrEmpty(msg)) return (name, null);
                }
                return (string.IsNullOrWhiteSpace(n) ? name : n, color);
            }
            catch { return (name, null); }
        }
    }

    internal static class Pose
    {
        private const float SliderUpper = 0.7f, SliderLower = 1.1f;
        // RightHandGripCenter depth relative to the Player root in the scene; the game moves the hammer to
        // this depth every frame (hammerMeshHub z = right grip z). Zenith clients keep the body at its scene
        // depth, so the hammer goes here too (GOILauncher reports its own engine's depth, which can differ).
        private const float HammerDepth = 0.776f;
        // Zenith clients smooth Angle with LerpAngle, so a jump across the -1/+1 wrap (hammer straight up)
        // spins the body once. Near the wrap we keep sending the value on the same side (up to this much past
        // +-1; the animator clamps it to the identical straight-up pose) so jitter there doesn't spin it.
        private const float WrapSlack = 0.12f;

        public static void Fill(ref PlayerMove m, ref float? lastAngle)
        {
            if (m.Position.y <= -9000f) { lastAngle = null; return; }   // hidden (not in game)

            float z = m.Position.z + HammerDepth;
            m.SliderPosition = new Vector3(m.SliderPosition.x, m.SliderPosition.y, z);
            m.HandlePosition = new Vector3(m.HandlePosition.x, m.HandlePosition.y, z);

            float deg = Session.ZAngle(m.Rotation) - Session.ZAngle(m.SliderRotation) + 90f;
            deg %= 360f; if (deg < 0) deg += 360f;
            float angle = deg / 180f - 1f;
            if (lastAngle.HasValue)
            {
                float best = angle;
                foreach (var c in new[] { angle + 2f, angle - 2f })
                    if (Math.Abs(c - lastAngle.Value) < Math.Abs(best - lastAngle.Value)) best = c;
                if (Math.Abs(best) <= 1f + WrapSlack) angle = best;
            }
            lastAngle = angle;
            m.AnimationAngle = angle;

            double r = Session.ZAngle(m.SliderRotation) * Math.PI / 180.0;
            float t = (float)((m.HandlePosition.x - m.SliderPosition.x) * Math.Cos(r) + (m.HandlePosition.y - m.SliderPosition.y) * Math.Sin(r));
            m.AnimationExtension = 0.25f * t / (t >= 0f ? SliderUpper : SliderLower);
        }
    }

    // ====================================================================== commands
    [Command("GOILauncher player IP", "goiip", "Show the real IP of a GOILauncher (bridged) player.")]
    [CommandPermission("goilauncherbridge.goiip", DefaultRanks.Moderator)]
    public class GoiIpCommand : BaseCommand
    {
        [CommandArgument("Player name", optional: true, defaultValue: null)]
        public string PlayerName { get; set; }

        public override void Handle(string[] args)
        {
            if (!HasAccess()) return;
            var plugin = GOILauncherBridgePlugin.Instance;
            if (plugin == null) { SendMessage("GOILauncherBridge is not running.", LogMessageType.Error); return; }
            var sessions = plugin.Sessions.Values.Where(s => s.IsBridgedZenithPlayer &&
                (string.IsNullOrEmpty(PlayerName) || s.Name.IndexOf(PlayerName, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (sessions.Count == 0) { SendMessage("No matching GOILauncher players are online.", LogMessageType.Info); return; }
            foreach (var s in sessions)
                SendMessage($"{s.Name} (id {s.ZenithId}): real IP {s.RealIp}, shown to Zenith as {s.LoopbackIp}", LogMessageType.Info);
        }
    }
}

// ======================================================================================================
// GOILauncher.Multiplayer protocol: the packets and models the bridge needs, copied from
// https://github.com/Zonakkis/GOILauncher.Multiplayer (src/Core); code unchanged, comments translated to
// English. Namespaces and type names must stay
// exactly as they are: packets are identified on the wire by a hash of their full type name.
//
// MIT License
//
// Copyright (c) 2026 Zonakkis
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
// ======================================================================================================

// ---- Data/Constants/NetworkChannels.cs
namespace GOILauncher.Multiplayer.Core.Data.Constants
{
    /// <summary>
    /// LiteNetLib channel numbers are global to the whole protocol, so they are all numbered here: if two features
    /// picked numbers separately they could collide, and a collision doesn't error - two packet kinds on one reliable channel just queue behind each other.
    /// </summary>
    public static class NetworkChannels
    {
        /// <summary>Channel for state sync and reliable control messages (rooms, roster, chat, downstream skin manifest).</summary>
        public const byte Default = 0;

        /// <summary>
        /// Channel for local skin uploads (manifest then bytes), skin requests and byte responses. The downstream manifest uses Default so the roster arrives first. A texture is hundreds of KB and needs fragmented reliable resends,
        /// so sharing a reliable channel with 60 Hz state sync would slow both down.
        /// </summary>
        public const byte Skin = 1;

        /// <summary>
        /// <c>NetManager.ChannelsCount</c> must be set to this value. It sets the length of <c>NetPeer._channels</c>
        /// (<c>ChannelsCount * 4</c>); if it's too small, sending on a higher channel goes out of range.
        /// Both ends must set it, or the receiver can't map the channel.
        /// </summary>
        public const byte Count = 2;
    }
}

// ---- Data/Constants/RoomConstants.cs
namespace GOILauncher.Multiplayer.Core.Data.Constants
{
    public static class RoomConstants
    {
        public const int LobbyId = 0;
        public const string LobbyName = "大厅"; // "Lobby" (protocol value, left as-is)
        public const int MaxNameLength = 64;
        public const int MaxPasswordLength = 128;
        // Changing a wire layout must also change this key. There is no legacy protocol adapter.
        public const string ConnectionKey = "GOILauncher.Rooms.v1";
    }
}

// ---- Data/Constants/SkinConstants.cs
namespace GOILauncher.Multiplayer.Core.Data.Constants
{
    public static class SkinConstants
    {
        /// <summary>Byte length of SHA-256. Skin textures are identified on the wire by their content hash.</summary>
        public const int HashLength = 32;

        /// <summary>Skin slot for the pot (<c>Pot/Mesh</c>).</summary>
        public const byte PotSlot = 0;

        /// <summary>Skin slot for the player body (<c>dude/Body</c>, called Diogenes by the community).</summary>
        public const byte BodySlot = 1;

        /// <summary>
        /// Every skin slot this build knows. Each slot is announced, cached and sent separately.
        /// To add a part, add it here: the server only forwards slots listed here (see <see cref="IsKnownSlot"/>),
        /// and both ends iterate this to handle "each slot of one player".
        /// </summary>
        public static readonly byte[] Slots = { PotSlot, BodySlot };

        /// <summary>
        /// Whether this build knows the slot. An unknown slot means the other side is newer; forwarding it is pointless since nobody could render it.
        /// </summary>
        public static bool IsKnownSlot(byte slot)
        {
            for (var i = 0; i < Slots.Length; i++)
            {
                if (Slots[i] == slot)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Max PNG bytes for one texture. The transport can carry far more (reliable fragmented packets go up to tens of MB);
        /// this caps the server's per-player cache, it isn't a transport limit.
        /// </summary>
        public const int MaxPayloadBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Max pixels per side. A byte limit can't stop decompression bombs - a 4 MB single-color PNG can decode to billions of pixels,
        /// so the size must be read from the PNG IHDR and rejected before <c>LoadImage</c>; checking after decoding is too late.
        /// </summary>
        public const int MaxTextureSize = 4096;
    }
}

// ---- Data/Models/OpeningState.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    /// <summary>
    /// A snapshot of the opening animation's remaining game seconds (at normal time scale).
    /// Zero is a known completed state, not an absent announcement.
    /// </summary>
    public struct OpeningState : INetSerializable
    {
        public float RemainingSeconds { get; set; }

        public bool IsValid => RemainingSeconds >= 0f
            && !float.IsNaN(RemainingSeconds) && !float.IsInfinity(RemainingSeconds);

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(RemainingSeconds);
        }

        public void Deserialize(NetDataReader reader)
        {
            RemainingSeconds = reader.GetFloat();
        }
    }
}

// ---- Data/Models/Platform.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    public enum Platform : byte
    {
        PC,
        iOS,
        Android,
        Unknown
    }
}

// ---- Data/Models/PlayerInfo.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    /// <summary>
    /// Immutable snapshot of player info: can't be changed after creation; external APIs can only read it.
    /// Updates create new instances via the WithXxx methods.
    /// </summary>
    public class PlayerInfo
    {
        private readonly int _id;
        private readonly string _name;
        private readonly Platform _platform;
        private readonly bool _isInGame;

        public int Id
        {
            get { return _id; }
        }

        public string Name
        {
            get { return _name; }
        }

        public Platform Platform
        {
            get { return _platform; }
        }

        public bool IsInGame
        {
            get { return _isInGame; }
        }

        public PlayerInfo(int id, string name, Platform platform, bool isInGame)
        {
            _id = id;
            _name = name;
            _platform = platform;
            _isInGame = isInGame;
        }

        public PlayerInfo WithIsInGame(bool isInGame)
        {
            return new PlayerInfo(_id, _name, _platform, isInGame);
        }
    }
}

// ---- Data/Models/PlayerState.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    /// <summary>
    /// PlayerRotation and SliderRotation are rotations around the Z axis only.
    /// </summary>
    public struct PlayerState : INetSerializable
    {
        public UnityVector3 PlayerPosition { get; set; }
        public float PlayerRotation { get; set; }
        public UnityVector3 SliderPosition { get; set; }
        public float SliderRotation { get; set; }
        public UnityVector3 HandlePosition { get; set; }
        public UnityQuaternion HandleRotation { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerPosition);
            writer.Put(PlayerRotation);
            writer.Put(SliderPosition);
            writer.Put(SliderRotation);
            writer.Put(HandlePosition);
            writer.Put(HandleRotation);
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerPosition = reader.Get<UnityVector3>();
            PlayerRotation = reader.GetFloat();
            SliderPosition = reader.Get<UnityVector3>();
            SliderRotation = reader.GetFloat();
            HandlePosition = reader.Get<UnityVector3>();
            HandleRotation = reader.Get<UnityQuaternion>();
        }
    }
}

// ---- Data/Models/RoomInfo.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    /// <summary>Public room metadata. Password verification material never belongs here.</summary>
    public sealed class RoomInfo : INetSerializable
    {
        public int Id { get; private set; }
        public string Name { get; private set; }
        public bool HasPassword { get; private set; }
        public int MaxPlayers { get; private set; }
        public int PlayerCount { get; private set; }
        public int? OwnerPlayerId { get; private set; }
        public bool IsLobby => Id == RoomConstants.LobbyId;
        public bool IsFull => MaxPlayers > 0 && PlayerCount >= MaxPlayers;

        public RoomInfo() { }
        public RoomInfo(int id, string name, bool hasPassword, int maxPlayers, int playerCount, int? ownerPlayerId)
        {
            Id = id; Name = name; HasPassword = hasPassword; MaxPlayers = maxPlayers;
            PlayerCount = playerCount; OwnerPlayerId = ownerPlayerId;
        }
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Id); writer.Put(Name); writer.Put(HasPassword); writer.Put(MaxPlayers);
            writer.Put(PlayerCount); writer.Put(OwnerPlayerId.HasValue);
            if (OwnerPlayerId.HasValue) writer.Put(OwnerPlayerId.Value);
        }
        public void Deserialize(NetDataReader reader)
        {
            Id = reader.GetInt(); Name = reader.GetString(); HasPassword = reader.GetBool();
            MaxPlayers = reader.GetInt(); PlayerCount = reader.GetInt();
            OwnerPlayerId = reader.GetBool() ? (int?)reader.GetInt() : null;
        }
    }
}

// ---- Data/Models/RoomMemberInfo.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    /// <summary>A membership incarnation, not just a reusable network peer ID.</summary>
    public struct RoomMemberInfo : INetSerializable
    {
        public PlayerInfo Player { get; private set; }
        public ulong MembershipId { get; private set; }
        public RoomMemberInfo(PlayerInfo player, ulong membershipId)
        { Player = player; MembershipId = membershipId; }
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Player.Id); writer.Put(Player.Name); writer.Put((byte)Player.Platform);
            writer.Put(Player.IsInGame); writer.Put(MembershipId);
        }
        public void Deserialize(NetDataReader reader)
        {
            Player = new PlayerInfo(reader.GetInt(), reader.GetString(), (Platform)reader.GetByte(), reader.GetBool());
            MembershipId = reader.GetULong();
        }
    }

    /// <summary>Guards both ends against packets queued before either player's last room change.</summary>
    public struct RoomPacketScope : INetSerializable
    {
        public ulong RecipientMembershipId { get; set; }
        public ulong PlayerMembershipId { get; set; }
        public RoomPacketScope(ulong recipientMembershipId, ulong playerMembershipId)
        { RecipientMembershipId = recipientMembershipId; PlayerMembershipId = playerMembershipId; }
        public void Serialize(NetDataWriter writer)
        { writer.Put(RecipientMembershipId); writer.Put(PlayerMembershipId); }
        public void Deserialize(NetDataReader reader)
        { RecipientMembershipId = reader.GetULong(); PlayerMembershipId = reader.GetULong(); }
    }
}

// ---- Data/Models/RoomOperation.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    public enum RoomOperation : byte { Refresh, Create, Join, Leave, Update }
    public enum RoomPasswordChange : byte { Keep, Set, Remove }
    public enum RoomError : byte
    {
        None, NotReady, Busy, RoomNotFound, IncorrectPassword, RoomFull,
        NotOwner, InvalidName, InvalidCapacity, InvalidPassword, Forbidden, StaleMembership
    }
    public struct RoomOperationResult : INetSerializable
    {
        public uint RequestId { get; set; }
        public RoomOperation Operation { get; set; }
        public RoomError Error { get; set; }
        public bool IsSuccess => Error == RoomError.None;
        public void Serialize(NetDataWriter writer)
        { writer.Put(RequestId); writer.Put((byte)Operation); writer.Put((byte)Error); }
        public void Deserialize(NetDataReader reader)
        { RequestId = reader.GetUInt(); Operation = (RoomOperation)reader.GetByte(); Error = (RoomError)reader.GetByte(); }
    }
}

// ---- Data/Models/SkinBlob.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    /// <summary>
    /// Wire form of one skin texture: the content-addressed <see cref="SkinHash"/> plus the PNG bytes.
    /// Uploads (C2S) and downloads (S2C) both carry this pair, so the byte wire format is defined only here.
    /// </summary>
    public struct SkinBlob : INetSerializable
    {
        private static readonly byte[] EmptyData = new byte[0];

        public SkinHash Hash { get; set; }
        public byte[] Data { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Hash);
            writer.PutBytesWithLength(Data ?? EmptyData);
        }

        public void Deserialize(NetDataReader reader)
        {
            Hash = reader.Get<SkinHash>();

            // Check the length before reading: an oversized packet shouldn't be allocated first and rejected after.
            if (reader.AvailableBytes >= sizeof(int) && reader.PeekInt() > SkinConstants.MaxPayloadBytes)
                throw new ParseException("Skin payload exceeds the size limit.");

            // Must use TryGetBytesWithLength: GetBytesWithLength BlockCopies using the length written in the packet,
            // so a forged length throws ArgumentException - that's not a ParseException,
            // PacketDispatcher doesn't catch it, and it would bubble out of PollEvents.
            byte[] data;
            if (!reader.TryGetBytesWithLength(out data))
                throw new ParseException("Skin payload is truncated.");

            Data = data;
        }
    }
}

// ---- Data/Models/SkinHash.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    /// <summary>
    /// Content-addressed id of a skin texture: SHA-256 of the PNG bytes.
    /// All zeros (including <c>default(SkinHash)</c>) means "no custom texture, render the original".
    /// </summary>
    /// <remarks>
    /// A content hash instead of a version number because players may change skins on every restart, and "switch back to the previous one" and
    /// "switch to a new one" must be different things to the protocol: same hash means same bytes, so the receiver's copy is still usable.
    /// It's not used to dedupe across players - two people using the same skin is near impossible, not worth reference counting.
    /// </remarks>
    public struct SkinHash : INetSerializable, IEquatable<SkinHash>
    {
        private static readonly byte[] EmptyBytes = new byte[SkinConstants.HashLength];

        private byte[] _bytes;

        /// <summary>
        /// The 32 content bytes. Read-only use (serialize, compare, log); callers must not modify the returned array.
        /// </summary>
        public byte[] Bytes
        {
            get { return _bytes ?? EmptyBytes; }
        }

        public bool IsEmpty
        {
            get
            {
                if (_bytes == null) return true;
                for (var i = 0; i < _bytes.Length; i++)
                {
                    if (_bytes[i] != 0) return false;
                }
                return true;
            }
        }

        public static SkinHash Compute(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return default(SkinHash);

            using (var sha = new SHA256Managed())
            {
                return new SkinHash(sha.ComputeHash(payload));
            }
        }

        private SkinHash(byte[] bytes)
        {
            _bytes = bytes;
        }

        /// <summary>Whether these bytes hash exactly to this hash.</summary>
        public bool Matches(byte[] payload)
        {
            return Equals(Compute(payload));
        }

        public void Serialize(NetDataWriter writer)
        {
            // Fixed length, written raw without a length prefix: the length is a protocol constant; a variable length would just add a forgeable field.
            writer.Put(Bytes, 0, SkinConstants.HashLength);
        }

        public void Deserialize(NetDataReader reader)
        {
            if (reader.AvailableBytes < SkinConstants.HashLength)
                throw new ParseException("SkinHash is truncated.");

            var bytes = new byte[SkinConstants.HashLength];
            reader.GetBytes(bytes, SkinConstants.HashLength);
            _bytes = bytes;
        }

        public bool Equals(SkinHash other)
        {
            var left = Bytes;
            var right = other.Bytes;
            for (var i = 0; i < SkinConstants.HashLength; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is SkinHash && Equals((SkinHash)obj);
        }

        public override int GetHashCode()
        {
            // The first four bytes are enough for hashing: every bit of SHA-256 is uniform.
            var bytes = Bytes;
            return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
        }

        public override string ToString()
        {
            // Logs only need to tell two textures apart, not the full length.
            if (IsEmpty) return "vanilla";
            return BitConverter.ToString(Bytes, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}

// ---- Data/Models/SkinState.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    /// <summary>
    /// A player's current skin for one part: which slot, which texture, and how much goldness.
    /// Doesn't include the texture bytes - those are requested separately by <see cref="Hash"/>; a receiver that already has that hash doesn't need them again.
    /// </summary>
    public struct SkinState : INetSerializable
    {
        public byte Slot { get; set; }

        /// <summary>Content hash of the texture; an empty hash means this player uses the original texture.</summary>
        public SkinHash Hash { get; set; }

        /// <summary>
        /// The material's <c>_Goldness</c>: black pot 0, gold pot 1. It's separate from the texture on the same material,
        /// so the original texture (empty hash) carries it too - that's how a gold-pot player without a skin is shown.
        /// </summary>
        public float Goldness { get; set; }

        public bool HasTexture
        {
            get { return !Hash.IsEmpty; }
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Slot);
            writer.Put(Hash);
            writer.Put(Goldness);
        }

        public void Deserialize(NetDataReader reader)
        {
            Slot = reader.GetByte();
            Hash = reader.Get<SkinHash>();
            Goldness = reader.GetFloat();
        }
    }
}

// ---- Data/Models/UnityQuaternion.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    public struct UnityQuaternion : INetSerializable
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(X);
            writer.Put(Y);
            writer.Put(Z);
            writer.Put(W);
        }

        public void Deserialize(NetDataReader reader)
        {
            X = reader.GetFloat();
            Y = reader.GetFloat();
            Z = reader.GetFloat();
            W = reader.GetFloat();
        }
    }
}

// ---- Data/Models/UnityVector3.cs
namespace GOILauncher.Multiplayer.Core.Data.Models
{
    public struct UnityVector3 : INetSerializable
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(X);
            writer.Put(Y);
            writer.Put(Z);
        }

        public void Deserialize(NetDataReader reader)
        {
            X = reader.GetFloat();
            Y = reader.GetFloat();
            Z = reader.GetFloat();
        }
    }
}

// ---- Data/Packets/C2SChatMessagePacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct C2SChatMessagePacket : INetSerializable
    {
        public ulong MembershipId { get; set; }

        public string Content { get; set; }
        /// <summary>
        /// Unix timestamp in seconds
        /// </summary>
        public long Timestamp { get; set; }
        public C2SChatMessagePacket(string content)
        {
            MembershipId = 0;
            Content = content;
            Timestamp = DateTimeUtils.ToUnixTimeSeconds(DateTime.Now);
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(MembershipId);
            writer.Put(Content);
            writer.Put(Timestamp);
        }

        public void Deserialize(NetDataReader reader)
        {
            MembershipId = reader.GetULong();
            Content = reader.GetString();
            Timestamp = reader.GetLong();
        }
    }
}

// ---- Data/Packets/C2SClientHandShakePacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct C2SClientHandShakePacket : INetSerializable
    {
        public string PlayerName { get; set; }
        public Platform Platform { get; set; }
        public bool IsInGame { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerName);
            writer.Put(Platform);
            writer.Put(IsInGame);
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerName = reader.GetString();
            Platform = reader.GetPlatform();
            IsInGame = reader.GetBool();
        }
    }
}

// ---- Data/Packets/C2SIsInGameUpdatePacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct C2SIsInGameUpdatePacket : INetSerializable
    {
        public bool IsInGame { get; set; }
        public C2SIsInGameUpdatePacket(bool isInGame)
        {
            IsInGame = isInGame;
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(IsInGame);
        }
        
        public void Deserialize(NetDataReader reader)
        {
            IsInGame = reader.GetBool();
        }
    }
}

// ---- Data/Packets/C2SPlayerStatePacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct C2SPlayerStatePacket : INetSerializable
    {
        public ulong MembershipId { get; set; }
        public uint Sequence { get; set; }
        public PlayerState State { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(MembershipId);
            writer.Put(Sequence);
            writer.Put(State);
        }

        public void Deserialize(NetDataReader reader)
        {
            MembershipId = reader.GetULong();
            Sequence = reader.GetUInt();
            State = reader.Get<PlayerState>();
        }
    }
}

// ---- Data/Packets/C2SRoomOperationPacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct C2SRoomOperationPacket : INetSerializable
    {
        public uint RequestId { get; set; }
        public ulong MembershipId { get; set; }
        public RoomOperation Operation { get; set; }
        public int RoomId { get; set; }
        public string Name { get; set; }
        public int MaxPlayers { get; set; }
        public RoomPasswordChange PasswordChange { get; set; }
        public string Password { get; set; }
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(RequestId); writer.Put(MembershipId); writer.Put((byte)Operation);
            writer.Put(RoomId); writer.Put(Name ?? ""); writer.Put(MaxPlayers);
            writer.Put((byte)PasswordChange); writer.Put(Password ?? "");
        }
        public void Deserialize(NetDataReader reader)
        {
            RequestId = reader.GetUInt(); MembershipId = reader.GetULong(); Operation = (RoomOperation)reader.GetByte();
            RoomId = reader.GetInt(); Name = reader.GetString(); MaxPlayers = reader.GetInt();
            PasswordChange = (RoomPasswordChange)reader.GetByte(); Password = reader.GetString();
        }
    }
}

// ---- Data/Packets/S2CChatMessagePacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct S2CChatMessagePacket : INetSerializable
    {
        public RoomPacketScope Scope { get; set; }
        public int PlayerId { get; set; }
        public string Content { get; set; }
        /// <summary>
        /// Unix timestamp in seconds
        /// </summary>
        public long Timestamp { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Scope);
            writer.Put(PlayerId);
            writer.Put(Content);
            writer.Put(Timestamp);
        }

        public void Deserialize(NetDataReader reader)
        {
            Scope = reader.Get<RoomPacketScope>();
            PlayerId = reader.GetInt();
            Content = reader.GetString();
            Timestamp = reader.GetLong();
        }
    }
}

// ---- Data/Packets/S2CPlayerJoinedPacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct S2CPlayerJoinedPacket : INetSerializable
    {
        public RoomPacketScope Scope { get; set; }
        public int PlayerId { get; set; }
        public string PlayerName { get; set; }
        public Platform Platform { get; set; }
        public bool IsInGame { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Scope);
            writer.Put(PlayerId);
            writer.Put(PlayerName);
            writer.Put(Platform);   
            writer.Put(IsInGame);
        }

        public void Deserialize(NetDataReader reader)
        {
            Scope = reader.Get<RoomPacketScope>();
            PlayerId = reader.GetInt();
            PlayerName = reader.GetString();
            Platform = reader.GetPlatform();
            IsInGame = reader.GetBool();
        }
    }
}

// ---- Data/Packets/S2CPlayerLeftPacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct S2CPlayerLeftPacket : INetSerializable
    {
        public RoomPacketScope Scope { get; set; }
        public int PlayerId { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Scope);
            writer.Put(PlayerId);
        }

        public void Deserialize(NetDataReader reader)
        {
            Scope = reader.Get<RoomPacketScope>();
            PlayerId = reader.GetInt();
        }
    }
}

// ---- Data/Packets/S2CPlayerListPacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    /// <summary>Authoritative room-entry snapshot, including the local member.</summary>
    public sealed class S2CPlayerListPacket : INetSerializable
    {
        public RoomInfo Room { get; set; }
        public List<RoomMemberInfo> Members { get; set; } = new List<RoomMemberInfo>();
        public void Serialize(NetDataWriter writer)
        { writer.Put(Room); writer.Put(Members.Count); foreach (var member in Members) writer.Put(member); }
        public void Deserialize(NetDataReader reader)
        {
            Room = reader.Get<RoomInfo>(); Members.Clear();
            int count = reader.GetInt();
            if (count < 0 || count > reader.AvailableBytes) throw new ParseException("Invalid member count.");
            for (int i = 0; i < count; i++) Members.Add(reader.Get<RoomMemberInfo>());
        }
    }
}

// ---- Data/Packets/S2CPlayerStatePacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct S2CPlayerStatePacket : INetSerializable
    {
        public RoomPacketScope Scope { get; set; }
        public int PlayerId { get; set; }
        public uint Sequence { get; set; }
        public PlayerState State { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Scope);
            writer.Put(PlayerId);
            writer.Put(Sequence);
            writer.Put(State);
        }

        public void Deserialize(NetDataReader reader)
        {
            Scope = reader.Get<RoomPacketScope>();
            PlayerId = reader.GetInt();
            Sequence = reader.GetUInt();
            State = reader.Get<PlayerState>();
        }
    }
}

// ---- Data/Packets/S2CRoomListPacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public sealed class S2CRoomListPacket : INetSerializable
    {
        public List<RoomInfo> Rooms { get; set; } = new List<RoomInfo>();
        public void Serialize(NetDataWriter writer)
        { writer.Put(Rooms.Count); foreach (var room in Rooms) writer.Put(room); }
        public void Deserialize(NetDataReader reader)
        {
            Rooms.Clear();
            int count = reader.GetInt();
            if (count < 0 || count > reader.AvailableBytes) throw new ParseException("Invalid room count.");
            for (int i = 0; i < count; i++) Rooms.Add(reader.Get<RoomInfo>());
        }
    }
}

// ---- Data/Packets/S2CRoomOperationResultPacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct S2CRoomOperationResultPacket : INetSerializable
    {
        public RoomOperationResult Result { get; set; }
        public void Serialize(NetDataWriter writer) { writer.Put(Result); }
        public void Deserialize(NetDataReader reader) { Result = reader.Get<RoomOperationResult>(); }
    }
}

// ---- Data/Packets/S2CServerHandShakePacket.cs
namespace GOILauncher.Multiplayer.Core.Data.Packets
{
    public struct S2CServerHandShakePacket : INetSerializable
    {
        public int PlayerId { get; set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerId);
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerId = reader.GetInt();
        }
    }
}

// ---- Extensions/NetDataReaderExtensions.cs
namespace GOILauncher.Multiplayer.Core.Extensions
{
    public static class NetDataReaderExtensions
    {

        public static Platform GetPlatform(this NetDataReader reader)
        {
            return (Platform)reader.GetByte();
        }
    }
}

// ---- Extensions/NetDataWriterExtensions.cs
namespace GOILauncher.Multiplayer.Core.Extensions
{
    public static class NetDataWriterExtensions
    {
        public static void Put(this NetDataWriter writer, Platform platform)
        {
            writer.Put((byte)platform);
        }
    }
}

// ---- Network/NetSerializablePacketWriter.cs
namespace GOILauncher.Multiplayer.Network
{
    internal static class NetSerializablePacketWriter
    {
        public static byte[] Write(INetSerializable packet)
        {
            if (packet == null)
                throw new ArgumentNullException("packet");

            var writer = new NetDataWriter();
            writer.Put(GetHash(packet.GetType()));
            packet.Serialize(writer);
            return writer.CopyData();
        }

        private static ulong GetHash(Type type)
        {
            ulong hash = 14695981039346656037UL;
            string typeName = type.ToString();
            for (int i = 0; i < typeName.Length; i++)
            {
                hash ^= typeName[i];
                hash *= 1099511628211UL;
            }

            return hash;
        }
    }
}

// ---- Utils/DateTimeUtils.cs
namespace GOILauncher.Multiplayer.Core.Utils
{
    public static class DateTimeUtils
    {
        public static DateTime FromUnixTimeSeconds(long seconds)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(seconds).ToLocalTime();
        }

        public static long ToUnixTimeSeconds(this DateTime dateTime)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(dateTime.ToUniversalTime() - epoch).TotalSeconds;
        }
    }
}

// ---- Utils/PngHeader.cs
namespace GOILauncher.Multiplayer.Core.Utils
{
    /// <summary>
    /// Reads only the PNG header. It exists to know the size before handing bytes to <c>Texture2D.LoadImage</c> -
    /// checking after decoding is too late, the memory is already used.
    /// </summary>
    public static class PngHeader
    {
        // Layout: 8-byte signature + 4-byte chunk length + "IHDR" + 4-byte width + 4-byte height.
        private const int WidthOffset = 16;
        private const int HeightOffset = 20;
        private const int MinLength = 24;

        private static readonly byte[] Signature =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
        };

        public static bool TryReadSize(byte[] data, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (data == null || data.Length < MinLength) return false;

            for (var i = 0; i < Signature.Length; i++)
            {
                if (data[i] != Signature[i]) return false;
            }

            // The spec requires IHDR to be the first chunk, so its position is fixed; no need to walk the chunk list.
            if (data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R')
                return false;

            width = ReadBigEndianInt32(data, WidthOffset);
            height = ReadBigEndianInt32(data, HeightOffset);
            return width > 0 && height > 0;
        }

        private static int ReadBigEndianInt32(byte[] data, int offset)
        {
            // PNG is big-endian. A size with the top bit set is already invalid per the PNG spec; it comes out negative here
            // and is rejected by the > 0 check above.
            return (data[offset] << 24)
                   | (data[offset + 1] << 16)
                   | (data[offset + 2] << 8)
                   | data[offset + 3];
        }
    }
}
