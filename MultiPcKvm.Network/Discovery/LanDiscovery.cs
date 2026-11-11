using MultiPcKvm.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiPcKvm.Network.Discovery
{
    // 같은 LAN에 있는 MultiPcKvm 프로그램을 검색한다.
    //
    // 현재는 UDP 방식으로 구현한다.
    //   1) Broadcast (255.255.255.255)
    //   2) 어댑터마다 그 망의 Broadcast (예: 192.168.0.255, 169.254.255.255)
    //      → 랜선 직결(169.254.x.x)처럼 와이파이와 동시에 켜진 망 대응
    //   3) 같은 망 IP 전부에 직접 전송 (Unicast Sweep)
    //      → 학교/회사 와이파이처럼 공유기가 Broadcast 를 막는 환경 대응
    //      → ARP 가 많이 생기므로 켠 직후·네트워크 변경 직후 30초만 매번, 그 뒤로는 45초마다
    //
    // 소켓 구성:
    //   - 수신용 소켓 1개 : (0.0.0.0, 45001)
    //       모든 어댑터로 들어오는 패킷을 받는다.
    //       어느 어댑터로 들어왔는지(IP_PKTINFO)도 같이 받는다.
    //   - 어댑터별 소켓   : (내 IPv4 주소, 45001) — 어댑터 주소마다 1개
    //       2), 3) 과 대답(RESPONSE)을 이 소켓으로 보낸다.
    //       출발 주소가 그 어댑터 주소이므로 Windows 가 반드시 그 어댑터로 내보낸다.
    //
    // 왜 어댑터별 소켓이 필요한가:
    //   랜선 직결(169.254.x.x)과 다른 169.254 어댑터(Npcap Loopback,
    //   IP 없는 Hyper-V 스위치 등)가 같이 켜져 있으면, 0.0.0.0 소켓에서 보낸
    //   169.254.x.x 패킷은 Windows 가 우선순위 높은 어댑터 하나로만 내보낸다.
    //   그게 랜선이 아니면 상대를 못 찾는다.
    //
    // 받은 어댑터는 LocalRoutes 에 기억해 두고,
    // TcpTransport / UdpTransport 가 연결할 때 그 어댑터를 지정한다.
    //
    // 나중에 필요하면 이 부분을 mDNS 방식으로 교체할 수 있다.
    public class LanDiscovery
    {
        // 수신 오류가 연속으로 이 횟수 이상 나면 조금 길게 쉰다.
        private const int MaxConsecutiveReceiveErrors = 5;

        // 한 어댑터에서 직접 훑는 범위의 최대 크기 (/24 = 254개)
        private const int MinSweepPrefixLength = 24;

        // Windows: SIO_UDP_CONNRESET
        private const int SioUdpConnReset = -1744830452;

        // 이 PC를 구분하기 위한 고정 ID
        //
        // DeviceIdentity 가 %AppData%\MultiPcKvm\device-id.txt 에
        // 저장해 두고 매번 같은 값을 돌려준다.
        private readonly Guid _deviceId;

        // 수신용 소켓 (0.0.0.0, 45001)
        private UdpClient? _udpClient;

        // 검색 대기 취소용
        private CancellationTokenSource? _cts;

        // 어댑터별 소켓 (키: 어댑터 주소)
        private readonly Dictionary<uint, AdapterSocket> _adapterSockets =
            new Dictionary<uint, AdapterSocket>();

        private readonly object _adapterLock =
            new object();

        // 프로그램이 사용하는 TCP 포트
        private int _tcpPort;

        // 검색 요청 전송이 직전에 실패했는지
        // (같은 오류를 3초마다 계속 알리지 않기 위해 사용)
        private bool _lastDiscoverFailed;

        // ── 같은 망 IP 하나씩 보내기(직접 탐색) 간격 ──
        //
        // 없는 IP 로 보낼 때마다 ARP 브로드캐스트가 생겨서,
        // 3초마다 250여 개씩 보내면 와이파이에서 지연이 튈 수 있다.
        //
        //   · 브로드캐스트(1, 2)        : 지금처럼 DiscoverAsync 때마다 (WPF 는 3초마다)
        //   · 직접 탐색(3)              : 켠 직후·네트워크가 바뀐 직후 30초 동안만 매번,
        //                                 그 뒤로는 45초에 한 번
        private static readonly TimeSpan SweepBurstDuration = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(45);

        // 이 시각까지는 매번 직접 탐색한다.
        private DateTime _sweepEveryTimeUntilUtc = DateTime.MinValue;

        // 마지막 직접 탐색 시각
        private DateTime _lastSweepUtc = DateTime.MinValue;

        // 마지막으로 본 어댑터 목록 버전 (바뀌면 "네트워크가 바뀐 직후" 로 본다)
        private int _seenAdapterVersion = -1;

        // ── 최근에 대답한(또는 검색 요청을 보낸) 상대 주소 ──
        //
        // 브로드캐스트를 막는 망(학교·회사 와이파이)에서는 IP 하나씩 보내기로만 서로를 찾는다.
        // 그게 45초에 한 번이 되면 WPF 의 "12초 대답 없으면 오프라인" 때문에 목록이 깜빡인다.
        // 그래서 최근 5분 안에 대답한 주소에는 쉬는 회차에도 매번 DISCOVER 를 직접 보낸다.
        // (기기 몇 대뿐이라 패킷 몇 개이고, 있는 PC 라 ARP 도 거의 안 생긴다)
        //
        // 키: 상대 IP / 값: 마지막으로 들은 시각, 들어온 내 어댑터
        private readonly ConcurrentDictionary<uint, RecentPeer> _recentPeers =
            new ConcurrentDictionary<uint, RecentPeer>();

        private static readonly TimeSpan RecentPeerLifetime = TimeSpan.FromMinutes(5);

        private sealed record RecentPeer(
            IPAddress Address,
            DateTime LastHeardUtc,
            LocalAdapterAddress? Local);

        // 검색된 PC가 발견될 때 발생
        public event Action<DiscoveredPc>? PcDiscovered;

        /// <summary>
        /// 검색 도중 오류가 났을 때 발생한다.
        ///
        /// 인자: (어디서 난 오류인지 설명, 원래 예외)
        ///
        /// 주의:
        ///   네트워크 스레드에서 발생한다.
        ///   WPF 화면을 바꿀 때는 Dispatcher 로 넘겨야 한다.
        ///
        /// ※ StartAsync 자체의 실패는 이벤트가 아니라 예외로 던진다.
        /// </summary>
        public event Action<string, Exception>? ErrorOccurred;

        /// <summary>
        /// 이 PC의 고정 DeviceId
        /// </summary>
        public Guid DeviceId =>
            _deviceId;

        /// <summary>
        /// 검색이 동작 중인지 (소켓이 열려 있는지)
        /// </summary>
        public bool IsRunning =>
            _udpClient != null;


        // ---------------------------------------------------------
        // 어댑터별 소켓 하나
        // ---------------------------------------------------------
        private sealed class AdapterSocket
        {
            public LocalAdapterAddress Info { get; }

            public UdpClient Client { get; }

            // 이 소켓의 수신 루프만 끝낼 때 쓴다. (어댑터가 사라졌을 때)
            public CancellationTokenSource Cts { get; }

            public AdapterSocket(
                LocalAdapterAddress info,
                UdpClient client,
                CancellationToken stopToken)
            {
                Info = info;
                Client = client;
                Cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            }

            public void Close()
            {
                try { Cts.Cancel(); } catch { }
                try { Client.Close(); Client.Dispose(); } catch { }
                try { Cts.Dispose(); } catch { }
            }
        }

        // 받은 패킷 하나 + 어느 내 어댑터로 들어왔는지
        private readonly struct Received
        {
            public byte[] Buffer { get; }
            public IPEndPoint Remote { get; }
            public LocalAdapterAddress? Local { get; }

            public Received(
                byte[] buffer,
                IPEndPoint remote,
                LocalAdapterAddress? local)
            {
                Buffer = buffer;
                Remote = remote;
                Local = local;
            }
        }


        // ---------------------------------------------------------
        // 생성자
        // ---------------------------------------------------------
        public LanDiscovery()
        {
            _deviceId = DeviceIdentity.DeviceId;
        }


        // ---------------------------------------------------------
        // Discovery 시작
        // ---------------------------------------------------------

        /// <summary>
        /// LAN 검색을 시작한다.
        ///
        /// 실패하면 소켓을 정리한 뒤 예외를 그대로 던진다.
        /// (예: 45001 포트를 다른 프로그램이 사용 중, 방화벽 등)
        ///
        /// 어댑터별 소켓은 만들다 실패해도 예외를 던지지 않는다.
        /// (그 어댑터만 예전처럼 수신용 소켓으로 보낸다)
        /// </summary>
        public async Task StartAsync(int tcpPort)
        {
            if (_udpClient != null)
                return;

            _tcpPort = tcpPort;
            _cts = new CancellationTokenSource();

            // 켠 직후 30초는 직접 탐색을 매번 한다.
            _sweepEveryTimeUntilUtc = DateTime.UtcNow + SweepBurstDuration;
            _lastSweepUtc = DateTime.MinValue;

            try
            {
                UdpClient client = new UdpClient(AddressFamily.InterNetwork);

                _udpClient = client;

                ConfigureSocket(client);

                // 어느 어댑터로 들어왔는지 받기 (IP_PKTINFO)
                client.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.PacketInformation,
                    true);

                client.Client.Bind(
                    new IPEndPoint(
                        IPAddress.Any,
                        Constants.DiscoveryPort));

                // 계속 검색 요청을 받는다.
                //
                // 소켓을 인자로 넘겨서
                // Stop() 이 _udpClient 를 null 로 바꿔도
                // 루프가 NullReferenceException 으로 죽지 않게 한다.
                CancellationToken token = _cts.Token;

                _ = ReceiveLoopAsync(
                    ct => ReceiveOnListenSocketAsync(client, ct),
                    "수신용",
                    token);

                // 프로그램 시작 시 다른 PC 검색
                // (어댑터별 소켓은 DiscoverAsync 안에서 만든다)
                await DiscoverAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"LAN Discovery 시작 실패: {ex.Message}");

                Stop();

                // 호출한 쪽(WPF)이 화면에 띄울 수 있도록 다시 던진다.
                throw;
            }
        }


        // 모든 검색 소켓 공통 설정
        private static void ConfigureSocket(
            UdpClient client)
        {
            // 브로드캐스트 허용
            client.EnableBroadcast = true;

            // Windows 전용:
            // UDP Broadcast 후 상대 포트가 닫혀 있어도
            // SIO_UDP_CONNRESET 때문에 소켓이 망가지는 것을 방지
            if (OperatingSystem.IsWindows())
            {
                client.Client.IOControl(
                    (IOControlCode)SioUdpConnReset,
                    new byte[] { 0, 0, 0, 0 },
                    null);
            }

            // 수신용 (0.0.0.0, 45001) 과 어댑터별 (내IP, 45001) 이
            // 같은 포트를 나눠 쓰도록 허용
            client.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
        }


        // ---------------------------------------------------------
        // 어댑터별 소켓 맞추기
        //
        // 랜선을 꽂거나 뽑으면 어댑터 주소가 생기거나 사라진다.
        // DiscoverAsync 때마다(WPF 는 3초마다) 지금 어댑터 목록과 비교해
        // 새 주소는 소켓을 만들고, 사라진 주소는 소켓을 닫는다.
        //
        // 반환: 지금 어댑터 목록
        // ---------------------------------------------------------
        private List<LocalAdapterAddress> SyncAdapterSockets()
        {
            List<LocalAdapterAddress> adapters;

            try
            {
                adapters = LocalRoutes.GetAdapters();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"네트워크 어댑터 조회 실패: {ex.Message}");

                return new List<LocalAdapterAddress>();
            }

            CancellationTokenSource? cts = _cts;

            if (cts == null || _udpClient == null)
                return adapters;

            List<AdapterSocket> toClose =
                new List<AdapterSocket>();

            List<AdapterSocket> toStart =
                new List<AdapterSocket>();

            lock (_adapterLock)
            {
                // 사라졌거나 바뀐 주소 → 닫기
                foreach (KeyValuePair<uint, AdapterSocket> pair in _adapterSockets.ToList())
                {
                    LocalAdapterAddress? now =
                        adapters.Find(a => a.AddressValue == pair.Key);

                    if (now == null ||
                        now.InterfaceIndex != pair.Value.Info.InterfaceIndex ||
                        now.PrefixLength != pair.Value.Info.PrefixLength)
                    {
                        toClose.Add(pair.Value);
                        _adapterSockets.Remove(pair.Key);
                    }
                }

                // 새 주소 → 만들기
                foreach (LocalAdapterAddress adapter in adapters)
                {
                    if (_adapterSockets.ContainsKey(adapter.AddressValue))
                        continue;

                    UdpClient? client = null;

                    try
                    {
                        client = new UdpClient(AddressFamily.InterNetwork);

                        ConfigureSocket(client);

                        client.Client.Bind(
                            new IPEndPoint(
                                adapter.Address,
                                Constants.DiscoveryPort));

                        AdapterSocket socket =
                            new AdapterSocket(
                                adapter,
                                client,
                                cts.Token);

                        client = null;

                        _adapterSockets[adapter.AddressValue] = socket;
                        toStart.Add(socket);
                    }
                    catch (Exception ex)
                    {
                        // 이 어댑터는 예전처럼 수신용 소켓으로 보낸다.
                        Console.WriteLine(
                            $"어댑터 소켓 생성 실패 ({adapter.AdapterName} {adapter.Address}): {ex.Message}");

                        client?.Dispose();
                    }
                }
            }

            foreach (AdapterSocket socket in toClose)
            {
                Console.WriteLine(
                    $"어댑터 소켓 닫음: {socket.Info.AdapterName} {socket.Info.Address}");

                socket.Close();
            }

            // 어댑터가 생기거나 사라졌으면 (랜선 꽂기/뽑기, 와이파이 바꾸기)
            // 다시 30초 동안 직접 탐색을 매번 한다.
            if (toStart.Count > 0 || toClose.Count > 0)
                _sweepEveryTimeUntilUtc = DateTime.UtcNow + SweepBurstDuration;

            foreach (AdapterSocket socket in toStart)
            {
                Console.WriteLine(
                    $"어댑터 소켓 열림: {socket.Info.AdapterName} {socket.Info.Address}");

                AdapterSocket s = socket;

                _ = ReceiveLoopAsync(
                    ct => ReceiveOnAdapterSocketAsync(s, ct),
                    s.Info.Address.ToString(),
                    s.Cts.Token);
            }

            return adapters;
        }

        private AdapterSocket? GetAdapterSocket(
            LocalAdapterAddress? local)
        {
            if (local == null)
                return null;

            lock (_adapterLock)
            {
                return _adapterSockets.TryGetValue(
                    local.AddressValue,
                    out AdapterSocket? socket)
                    ? socket
                    : null;
            }
        }

        // 어댑터 번호 → 그 어댑터의 내 주소
        // (remote 와 같은 망인 주소를 우선)
        private LocalAdapterAddress? FindLocalByInterface(
            int interfaceIndex,
            IPAddress remote)
        {
            uint remoteValue =
                LocalRoutes.ToUInt32(remote);

            lock (_adapterLock)
            {
                LocalAdapterAddress? fallback = null;

                foreach (AdapterSocket socket in _adapterSockets.Values)
                {
                    if (socket.Info.InterfaceIndex != interfaceIndex)
                        continue;

                    if (socket.Info.Contains(remoteValue))
                        return socket.Info;

                    fallback ??= socket.Info;
                }

                return fallback;
            }
        }


        // ---------------------------------------------------------
        // LAN에 검색 요청 보내기
        // ---------------------------------------------------------
        /// <summary>
        /// 검색 요청(DISCOVER)을 보낸다.
        ///
        /// 1) 255.255.255.255 Broadcast (수신용 소켓)
        /// 2) 어댑터마다 그 망의 Broadcast 주소로 전송 (그 어댑터 소켓)
        ///    (내 IP | ~마스크. 예: 192.168.0.255, 169.254.255.255)
        /// 3) 어댑터마다 같은 망의 IP 전부에 하나씩 직접 전송 (그 어댑터 소켓)
        ///
        /// 받는 쪽은 보낸 사람 주소로 직접 RESPONSE 를 보내므로
        /// 어느 쪽으로 도착해도 검색된다.
        /// (같은 PC 가 여러 번 응답할 수 있지만 DeviceId 가 같으므로 목록은 하나)
        /// </summary>
        public async Task DiscoverAsync()
        {
            UdpClient? client = _udpClient;

            if (client == null)
                return;

            // 랜선을 꽂거나 뽑았으면 어댑터별 소켓을 다시 맞춘다.
            // (어댑터 목록은 LocalRoutes 가 기억해 두었다가 바뀔 때만 다시 읽는다)
            List<LocalAdapterAddress> adapters =
                SyncAdapterSockets();

            // Windows 가 네트워크가 바뀌었다고 알렸으면 다시 30초 동안 매번 직접 탐색
            int adapterVersion = LocalRoutes.AdapterVersion;

            if (adapterVersion != _seenAdapterVersion)
            {
                if (_seenAdapterVersion != -1)
                    _sweepEveryTimeUntilUtc = DateTime.UtcNow + SweepBurstDuration;

                _seenAdapterVersion = adapterVersion;
            }

            // 이번에 같은 망 IP 하나씩 보내기(3)를 할 차례인가
            DateTime nowUtc = DateTime.UtcNow;

            bool sweepNow =
                nowUtc < _sweepEveryTimeUntilUtc ||
                nowUtc - _lastSweepUtc >= SweepInterval;

            if (sweepNow)
                _lastSweepUtc = nowUtc;

            string message =
                $"DISCOVER|{_deviceId}|{Environment.MachineName}|{_tcpPort}";

            byte[] data =
                Encoding.UTF8.GetBytes(message);


            // -----------------------------------------------------
            // 1) Broadcast
            //
            // 막는 공유기가 많지만, 되는 환경에서는 가장 빠르다.
            // 실패해도 아래 전송은 계속한다.
            // -----------------------------------------------------

            Exception? broadcastError = null;

            try
            {
                await client.SendAsync(
                    data,
                    data.Length,
                    new IPEndPoint(
                        IPAddress.Broadcast,
                        Constants.DiscoveryPort));
            }
            catch (ObjectDisposedException)
            {
                // Stop() 도중 호출된 경우
                return;
            }
            catch (Exception ex)
            {
                broadcastError = ex;

                Console.WriteLine(
                    $"Discovery Broadcast 실패: {ex.Message}");
            }


            Exception? lastSendError = null;

            HashSet<uint> myAddresses =
                new HashSet<uint>(
                    adapters.Select(a => a.AddressValue));

            // 수신용 소켓으로 대신 보낸 망 Broadcast (중복 방지용)
            HashSet<uint> fallbackBroadcasts =
                new HashSet<uint>();

            // 직접 전송 대상 (중복 방지용)
            HashSet<uint> seenTargets =
                new HashSet<uint>();

            int subnetTotal = 0;
            int subnetSent = 0;
            int unicastTotal = 0;
            int unicastSent = 0;

            foreach (LocalAdapterAddress adapter in adapters)
            {
                // 이 어댑터 소켓. 못 만들었으면 수신용 소켓으로 보낸다.
                AdapterSocket? adapterSocket =
                    GetAdapterSocket(adapter);

                UdpClient sender =
                    adapterSocket?.Client ?? client;


                // -------------------------------------------------
                // 2) 그 망의 Broadcast
                //
                // 255.255.255.255 는 Windows 가 어댑터 하나로만 내보낸다.
                // 어댑터 소켓에서 그 망 주소로 보내면
                // 반드시 그 어댑터로 나간다.
                //
                // 169.254 어댑터가 둘이어도 소켓이 따로라
                // 둘 다 169.254.255.255 를 보낸다.
                // -------------------------------------------------

                IPAddress? subnetBroadcast =
                    adapter.SubnetBroadcast;

                bool sendBroadcast =
                    subnetBroadcast != null &&
                    !subnetBroadcast.Equals(IPAddress.Broadcast) &&
                    (adapterSocket != null ||
                     fallbackBroadcasts.Add(LocalRoutes.ToUInt32(subnetBroadcast)));

                if (sendBroadcast)
                {
                    subnetTotal++;

                    int n = await SendToAllAsync(
                        sender,
                        data,
                        new List<IPAddress> { subnetBroadcast! },
                        ex => lastSendError = ex);

                    if (n < 0 && adapterSocket == null)
                        return;     // 수신용 소켓이 닫힘 = Stop() 도중

                    if (n > 0)
                        subnetSent += n;
                }


                // -------------------------------------------------
                // 3) 같은 망 IP 전부에 직접 전송
                //    켠 직후·네트워크가 바뀐 직후 30초만 매번, 그 뒤로는 45초에 한 번
                // -------------------------------------------------

                if (!sweepNow)
                    continue;

                List<IPAddress> targets =
                    GetUnicastTargets(
                        adapter,
                        myAddresses,
                        seenTargets);

                if (targets.Count == 0)
                    continue;

                unicastTotal += targets.Count;

                int sent = await SendToAllAsync(
                    sender,
                    data,
                    targets,
                    ex => lastSendError = ex);

                if (sent < 0 && adapterSocket == null)
                    return;     // Stop() 도중

                if (sent > 0)
                    unicastSent += sent;
            }


            // -----------------------------------------------------
            // 4) 최근 5분 안에 대답한 주소에는 매번 직접 보낸다.
            //
            //    브로드캐스트가 막힌 망에서도 아는 기기는 3초마다 확인되므로
            //    목록이 오프라인↔온라인으로 깜빡이지 않는다.
            //    이번 회차에 3) 에서 이미 보낸 주소는 건너뛴다.
            // -----------------------------------------------------

            int recentTotal = 0;
            int recentSent = 0;

            foreach (KeyValuePair<uint, RecentPeer> pair in _recentPeers)
            {
                RecentPeer peer = pair.Value;

                // 오래된 주소는 잊는다.
                if (nowUtc - peer.LastHeardUtc > RecentPeerLifetime)
                {
                    _recentPeers.TryRemove(pair);
                    continue;
                }

                // 내 주소이거나 이번에 이미 보냈으면 건너뛴다.
                if (myAddresses.Contains(pair.Key) || !seenTargets.Add(pair.Key))
                    continue;

                recentTotal++;

                // 그 상대가 들어온 어댑터로 보낸다. (169.254 어댑터가 여럿일 때 대비)
                UdpClient sender =
                    GetAdapterSocket(peer.Local)?.Client ?? client;

                int n = await SendToAllAsync(
                    sender,
                    data,
                    new List<IPAddress> { peer.Address },
                    ex => lastSendError = ex);

                if (n < 0 && sender == client)
                    return;     // 수신용 소켓이 닫힘 = Stop() 도중

                if (n > 0)
                    recentSent += n;
            }

            Console.WriteLine(
                $"LAN PC 검색 요청 전송 " +
                $"(Broadcast {(broadcastError == null ? "성공" : "실패")}, " +
                $"망별 Broadcast {subnetSent}/{subnetTotal}, " +
                $"직접 {(sweepNow ? $"{unicastSent}/{unicastTotal}" : "이번엔 쉼")}, " +
                $"최근 기기 {recentSent}/{recentTotal})");


            // -----------------------------------------------------
            // 결과 판단
            //
            // 한 곳이라도 보냈으면 성공으로 본다.
            // 전부 실패했을 때만 오류를 알린다. (연속 실패는 처음 한 번만)
            // -----------------------------------------------------

            bool anySent =
                broadcastError == null ||
                subnetSent > 0 ||
                unicastSent > 0 ||
                recentSent > 0;

            if (anySent)
            {
                _lastDiscoverFailed = false;
                return;
            }

            if (!_lastDiscoverFailed)
            {
                _lastDiscoverFailed = true;

                RaiseError(
                    "LAN 검색 요청 전송 실패",
                    lastSendError
                    ?? broadcastError
                    ?? new InvalidOperationException(
                        "검색 요청을 보낼 네트워크가 없습니다."));
            }
        }


        // ---------------------------------------------------------
        // 주소 목록에 하나씩 전송
        //
        // 반환: 보낸 개수. 소켓이 닫혔으면 -1.
        // 주소 하나 실패는 흔하다 (예: 경로 없음).
        // 하나씩 알리지 않고 마지막 오류만 onError 로 넘긴다.
        // ---------------------------------------------------------
        private static async Task<int> SendToAllAsync(
            UdpClient client,
            byte[] data,
            List<IPAddress> addresses,
            Action<Exception> onError)
        {
            int sent = 0;

            foreach (IPAddress address in addresses)
            {
                try
                {
                    await client.SendAsync(
                        data,
                        data.Length,
                        new IPEndPoint(
                            address,
                            Constants.DiscoveryPort));

                    sent++;
                }
                catch (ObjectDisposedException)
                {
                    return -1;
                }
                catch (Exception ex)
                {
                    onError(ex);
                }
            }

            return sent;
        }


        // ---------------------------------------------------------
        // 직접 전송할 IP 목록 계산 (어댑터 하나)
        // ---------------------------------------------------------

        /// <summary>
        /// 어댑터 주소 + 서브넷 마스크로 같은 망의 호스트 IP 목록을 만든다.
        ///
        /// - 내 IP, 127.x, 169.254.x 제외
        ///   (169.254 는 /16 이라 /24 로 줄이면 상대의 세 번째 자리를
        ///    거의 맞히지 못한다 → 망별 Broadcast 로 찾는다)
        /// - 마스크가 /24 보다 넓으면 (예: 255.255.0.0)
        ///   내 IP 기준 /24 만 사용 (65,000개씩 보내지 않도록)
        /// - seen 에 이미 있는 주소는 다른 어댑터가 보냈으므로 제외
        /// </summary>
        private static List<IPAddress> GetUnicastTargets(
            LocalAdapterAddress adapter,
            HashSet<uint> myAddresses,
            HashSet<uint> seen)
        {
            List<IPAddress> targets =
                new List<IPAddress>();

            // 169.254.x.x 망은 직접 탐색하지 않는다.
            if (IsExcluded(adapter.AddressValue))
                return targets;

            // /31, /32 는 같은 망에 다른 PC 가 없다.
            if (adapter.PrefixLength > 30)
                return targets;

            // /24 보다 넓으면 /24 로 줄인다.
            int effectivePrefix =
                Math.Max(
                    adapter.PrefixLength,
                    MinSweepPrefixLength);

            uint mask =
                uint.MaxValue << (32 - effectivePrefix);

            uint network =
                adapter.AddressValue & mask;

            uint broadcast =
                network | ~mask;

            // network 주소와 broadcast 주소는 제외
            for (uint host = network + 1; host < broadcast; host++)
            {
                if (myAddresses.Contains(host))
                    continue;

                if (IsExcluded(host))
                    continue;

                if (!seen.Add(host))
                    continue;

                targets.Add(
                    LocalRoutes.FromUInt32(host));
            }

            return targets;
        }


        // 127.x.x.x (Loopback), 169.254.x.x (APIPA, DHCP 실패) 제외
        private static bool IsExcluded(
            uint address)
        {
            byte first = (byte)(address >> 24);
            byte second = (byte)(address >> 16);

            if (first == 127)
                return true;

            if (first == 169 && second == 254)
                return true;

            return false;
        }


        // ---------------------------------------------------------
        // 수신
        // ---------------------------------------------------------

        // 수신용 소켓: 어느 어댑터로 들어왔는지(IP_PKTINFO)도 받는다.
        private async ValueTask<Received> ReceiveOnListenSocketAsync(
            UdpClient client,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[2048];

            SocketReceiveMessageFromResult result =
                await client.Client.ReceiveMessageFromAsync(
                    buffer,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    cancellationToken);

            IPEndPoint remote =
                (IPEndPoint)result.RemoteEndPoint;

            LocalAdapterAddress? local =
                FindLocalByInterface(
                    result.PacketInformation.Interface,
                    remote.Address);

            return new Received(
                buffer.AsSpan(0, result.ReceivedBytes).ToArray(),
                remote,
                local);
        }

        // 어댑터별 소켓: 받은 어댑터 = 그 소켓의 어댑터
        private static async ValueTask<Received> ReceiveOnAdapterSocketAsync(
            AdapterSocket socket,
            CancellationToken cancellationToken)
        {
            UdpReceiveResult result =
                await socket.Client.ReceiveAsync(
                    cancellationToken);

            return new Received(
                result.Buffer,
                result.RemoteEndPoint,
                socket.Info);
        }


        // ---------------------------------------------------------
        // Discovery 메시지 수신 루프 (소켓마다 하나)
        //
        // 패킷 하나 단위로 try/catch 한다.
        // → 오류가 나도 그 패킷만 버리고 루프는 계속 돈다.
        //
        // 루프가 끝나는 경우는 Stop() 했거나
        // 어댑터가 사라져 그 소켓을 닫았을 때뿐이다.
        // ---------------------------------------------------------
        private async Task ReceiveLoopAsync(
            Func<CancellationToken, ValueTask<Received>> receive,
            string socketName,
            CancellationToken cancellationToken)
        {
            int consecutiveErrors = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                Received received;


                // -------------------------------------------------
                // 1) 수신
                // -------------------------------------------------

                try
                {
                    received =
                        await receive(cancellationToken);

                    consecutiveErrors = 0;
                }
                catch (OperationCanceledException)
                {
                    // Stop() / 어댑터 소켓 닫힘 → 정상 종료
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // 소켓을 닫음 → 정상 종료
                    break;
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    consecutiveErrors++;

                    Console.WriteLine(
                        $"Discovery 수신 오류 ({socketName}): {ex.Message}");

                    // 같은 오류가 연속으로 나면 처음 한 번만 알린다.
                    if (consecutiveErrors == 1)
                    {
                        RaiseError(
                            "LAN 검색 수신 오류",
                            ex);
                    }

                    // 바로 다시 시도하면 CPU 를 잡아먹는 바쁜 루프가 된다.
                    // 연속 오류가 많으면 조금 더 길게 쉰다.
                    int delayMs =
                        consecutiveErrors >= MaxConsecutiveReceiveErrors
                            ? 1000
                            : 50;

                    try
                    {
                        await Task.Delay(
                            delayMs,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }


                // -------------------------------------------------
                // 2) 패킷 하나 처리
                // -------------------------------------------------

                try
                {
                    await HandlePacketAsync(
                        received);
                }
                catch (ObjectDisposedException)
                {
                    // 대답하려던 소켓이 방금 닫힘. 이 패킷만 버린다.
                }
                catch (Exception ex)
                {
                    // 이 패킷만 버리고 계속 받는다.
                    Console.WriteLine(
                        $"Discovery 패킷 처리 오류: {ex.Message}");

                    RaiseError(
                        "LAN 검색 패킷 처리 오류",
                        ex);
                }
            }
        }


        // ---------------------------------------------------------
        // 받은 패킷 하나 처리
        // ---------------------------------------------------------
        private async Task HandlePacketAsync(
            Received received)
        {
            string message =
                Encoding.UTF8.GetString(
                    received.Buffer);

            string[] parts =
                message.Split('|');

            if (parts.Length < 1)
                return;


            // 다른 PC가 검색 요청을 보냈을 때
            if (parts[0] == "DISCOVER")
            {
                await HandleDiscoveryRequestAsync(
                    parts,
                    received);
            }


            // 다른 PC가 검색 응답을 보냈을 때
            else if (parts[0] == "RESPONSE")
            {
                HandleDiscoveryResponse(
                    parts,
                    received);
            }
        }


        // ---------------------------------------------------------
        // 검색 요청을 받으면 응답한다.
        //
        // 요청이 들어온 어댑터의 소켓으로 대답한다.
        // → 169.254 어댑터가 여럿이어도 대답이 들어온 어댑터로 나간다.
        // ---------------------------------------------------------
        private async Task HandleDiscoveryRequestAsync(
            string[] parts,
            Received received)
        {
            if (parts.Length < 4)
                return;

            string senderId = parts[1];


            // 내가 보낸 검색 요청이라면 무시
            if (senderId == _deviceId.ToString())
                return;


            // 이 상대는 이 어댑터로 들어온다 → TCP/UDP 도 이 어댑터로
            if (received.Local != null)
            {
                LocalRoutes.Remember(
                    received.Remote.Address,
                    received.Local);
            }


            UdpClient? sender =
                GetAdapterSocket(received.Local)?.Client
                ?? _udpClient;

            if (sender == null)
                return;

            string response =
                $"RESPONSE|{_deviceId}|{Environment.MachineName}|{_tcpPort}";

            byte[] data =
                Encoding.UTF8.GetBytes(response);

            // 전송 실패는 ReceiveLoopAsync 의
            // 패킷 단위 try/catch 에서 처리한다.
            await sender.SendAsync(
                data,
                data.Length,
                received.Remote);


            // 검색 요청을 보낸 쪽도 "발견" 으로 알린다.
            //
            // DISCOVER|ID|이름|포트 에 RESPONSE 와 같은 정보가 다 있다.
            // 그래서 나중에 켠 PC 의 요청 한 번으로 먼저 켠 PC 도 바로 상대를 찾는다.
            // (직접 탐색이 45초에 한 번이어도 기다리지 않는다)
            ReportDiscovered(parts, received, "검색 요청");
        }


        // ---------------------------------------------------------
        // 다른 PC의 응답 처리
        // ---------------------------------------------------------
        private void HandleDiscoveryResponse(
            string[] parts,
            Received received)
        {
            ReportDiscovered(parts, received, "응답");
        }


        // ---------------------------------------------------------
        // 상대 PC 를 찾았다고 알린다. (RESPONSE, DISCOVER 공용)
        //
        //   parts : [종류, ID, 이름, TCP 포트]
        //   how   : 로그용 ("응답" / "검색 요청")
        //
        // 같은 PC 가 응답·요청으로 여러 번 알려질 수 있지만
        // DeviceId 가 같으므로 WPF 목록에는 하나로 합쳐진다.
        // ---------------------------------------------------------
        private void ReportDiscovered(
            string[] parts,
            Received received,
            string how)
        {
            if (parts.Length < 4)
                return;

            string deviceIdText = parts[1];
            string computerName = parts[2];
            string portText = parts[3];


            // 혹시 내 것이면 무시
            if (deviceIdText == _deviceId.ToString())
                return;


            if (!Guid.TryParse(
                    deviceIdText,
                    out Guid deviceId))
            {
                return;
            }


            if (!int.TryParse(
                    portText,
                    out int tcpPort) ||
                tcpPort < 1 ||
                tcpPort > 65535)
            {
                return;
            }


            // 이 상대는 이 어댑터로 들어온다 → TCP/UDP 도 이 어댑터로
            if (received.Local != null)
            {
                LocalRoutes.Remember(
                    received.Remote.Address,
                    received.Local);
            }


            // 최근에 들은 주소로 기억 → 다음 검색부터 매번 직접 보낸다. (4번)
            IPAddress remoteAddress = received.Remote.Address.IsIPv4MappedToIPv6
                ? received.Remote.Address.MapToIPv4()
                : received.Remote.Address;

            if (remoteAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                _recentPeers[LocalRoutes.ToUInt32(remoteAddress)] =
                    new RecentPeer(remoteAddress, DateTime.UtcNow, received.Local);
            }


            DiscoveredPc pc = new DiscoveredPc
            {
                DeviceId = deviceId,
                ComputerName = computerName,
                IpAddress = remoteAddress.ToString(),
                TcpPort = tcpPort,
                LocalAddress = received.Local?.Address.ToString() ?? string.Empty
            };


            Console.WriteLine(
                $"PC 발견 ({how}): {pc.ComputerName} " +
                $"({pc.IpAddress}:{pc.TcpPort}, 내 어댑터 {(pc.LocalAddress == "" ? "?" : pc.LocalAddress)})");


            PcDiscovered?.Invoke(pc);
        }


        // ---------------------------------------------------------
        // 오류 알림
        //
        // 구독한 쪽 코드에서 예외가 나도
        // 수신 루프가 죽지 않도록 감싼다.
        // ---------------------------------------------------------
        private void RaiseError(
            string context,
            Exception exception)
        {
            try
            {
                ErrorOccurred?.Invoke(
                    context,
                    exception);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Discovery 오류 이벤트 처리 실패: {ex.Message}");
            }
        }


        // ---------------------------------------------------------
        // Discovery 종료
        // ---------------------------------------------------------
        public void Stop()
        {
            List<AdapterSocket> sockets;

            lock (_adapterLock)
            {
                sockets = _adapterSockets.Values.ToList();
                _adapterSockets.Clear();
            }

            try
            {
                _cts?.Cancel();

                _udpClient?.Close();

                _udpClient?.Dispose();
            }
            catch
            {
                // 종료 과정의 예외는 무시
            }
            finally
            {
                _udpClient = null;

                foreach (AdapterSocket socket in sockets)
                    socket.Close();

                _cts?.Dispose();
                _cts = null;
            }
        }
    }


    // 검색된 PC의 정보를 담는 클래스
    public class DiscoveredPc
    {
        public Guid DeviceId { get; set; }

        public string ComputerName { get; set; } =
            string.Empty;

        public string IpAddress { get; set; } =
            string.Empty;

        public int TcpPort { get; set; }

        /// <summary>
        /// 이 PC 의 대답을 받은 "내" 어댑터 주소 (모르면 빈 문자열).
        ///
        /// 169.254 어댑터가 여럿일 때 어느 어댑터로 연결해야 하는지 알려준다.
        /// TcpTransport / UdpTransport 는 LocalRoutes 로 이 정보를 자동으로 쓰므로
        /// 화면 쪽에서 따로 넘길 필요는 없다.
        /// </summary>
        public string LocalAddress { get; set; } =
            string.Empty;
    }
}
