using MultiPcKvm.Common;
using MultiPcKvm.Network.Configuration;
using MultiPcKvm.Network.Discovery;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MultiPcKvm.Network.Transport
{
    /// <summary>
    /// ITransport의 TCP 구현체.
    ///
    /// TCP:
    /// - KeyDown / KeyUp / MouseButton
    /// - Ping / Pong
    /// - ScreenInfo / PairRequest / PairResult / Unpair
    ///
    /// 1:1 연결 규칙:
    ///     연결은 항상 하나만 유지한다.
    ///     양쪽이 동시에 연결을 걸어 연결이 2개 생기면
    ///     양쪽이 "같은 연결"을 고르도록 규칙을 정해 두었다.
    ///     (IsPreferred 참고)
    ///
    /// Disconnect()
    ///     현재 연결만 종료한다.
    ///
    /// StopListening()
    ///     서버 Listen만 종료한다.
    /// </summary>
    public class TcpTransport : ITransport
    {
        private readonly NetworkConfig _config;

        // 서버 Listener
        private TcpListener? _listener;

        // Listen 전용 취소 토큰
        private CancellationTokenSource? _listenCts;

        // 현재 연결 정보
        private ConnectionContext? _connection;

        // _connection 을 바꾸는 코드(연결 등록/교체)를 한 번에 하나만 실행
        private readonly object _connectionLock =
            new object();

        // 여러 송신이 동시에 수행되지 않도록 보호
        private readonly SemaphoreSlim _sendLock =
            new SemaphoreSlim(1, 1);

        /// <summary>
        /// TCP 연결 하나에 필요한 객체를 묶는다.
        ///
        /// 이렇게 하면 재연결 과정에서
        /// 이전 연결의 CancellationTokenSource가
        /// 새 연결에 의해 덮어써지는 문제를 막을 수 있다.
        /// </summary>
        private sealed class ConnectionContext : IDisposable
        {
            public TcpClient Client { get; }

            public NetworkStream Stream { get; }

            public CancellationTokenSource Cts { get; }

            // 내가 ConnectAsync 로 건 연결이면 true
            // 상대가 걸어와서 Accept 한 연결이면 false
            public bool IsOutbound { get; }

            // 이 연결에서 "서버(Accept 한 쪽)"의 주소
            //
            // 한 연결의 양 끝에서 보면 같은 값이다.
            //   - 내가 건 연결   → RemoteEndPoint (상대 = 서버)
            //   - 내가 받은 연결 → LocalEndPoint  (나 = 서버)
            //
            // 연결이 두 개 생겼을 때
            // 양쪽이 똑같은 기준으로 하나를 고르는 데 쓴다.
            public IPEndPoint? ServerEndPoint { get; }

            // 이 연결의 상대 PC IP (방향과 무관하게 항상 RemoteEndPoint)
            //
            // 두 연결이 "같은 상대와 동시에 건 줄"인지 확인하는 데 쓴다.
            public IPAddress? PeerAddress { get; }

            public ConnectionContext(
                TcpClient client,
                NetworkStream stream,
                bool isOutbound)
            {
                Client = client;
                Stream = stream;
                IsOutbound = isOutbound;
                Cts = new CancellationTokenSource();

                EndPoint? endPoint =
                    isOutbound
                        ? client.Client.RemoteEndPoint
                        : client.Client.LocalEndPoint;

                ServerEndPoint =
                    Normalize(endPoint as IPEndPoint);

                PeerAddress =
                    Normalize(client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            }

            // IPv4-mapped IPv6(::ffff:1.2.3.4) → IPv4 로 통일
            private static IPEndPoint? Normalize(
                IPEndPoint? endPoint)
            {
                if (endPoint == null)
                    return null;

                if (endPoint.Address.IsIPv4MappedToIPv6)
                {
                    return new IPEndPoint(
                        endPoint.Address.MapToIPv4(),
                        endPoint.Port);
                }

                return endPoint;
            }

            public void Dispose()
            {
                try
                {
                    Cts.Cancel();
                }
                catch
                {
                }

                try
                {
                    Stream.Close();
                    Stream.Dispose();
                }
                catch
                {
                }

                try
                {
                    Client.Close();
                    Client.Dispose();
                }
                catch
                {
                }

                try
                {
                    Cts.Dispose();
                }
                catch
                {
                }
            }
        }


        public event Action<InputMessage>? MessageReceived;

        /// <summary>
        /// 새 TCP 연결이 현재 연결로 등록됐을 때. (내가 건 줄이든 상대가 걸어온 줄이든)
        /// 인자는 상대의 실제 IP.
        ///
        /// PeerConnection 이 이걸 보고 UDP 상대 주소를 다시 넣는다.
        /// (끊길 때 Disconnect 가 UDP 주소를 지우므로, 다시 붙으면 다시 넣어야 마우스 이동이 간다)
        ///
        /// 네트워크 스레드에서 불린다. 오래 걸리는 일을 하면 안 된다.
        /// </summary>
        public event Action<IPAddress?>? Connected;


        public bool IsConnected =>
            _connection?.Client.Connected == true;

        /// <summary>
        /// 지금 연결된 TCP 상대의 실제 IP. 연결이 없으면 null.
        ///
        /// 내가 건 줄이든 상대가 걸어온 줄이든 상대 쪽 주소다.
        /// (검색에 잡힌 주소와 다를 수 있다. 예: 상대가 랜선·와이파이 주소를 둘 다 가진 경우)
        /// </summary>
        public IPAddress? RemoteAddress
        {
            get
            {
                ConnectionContext? context = _connection;

                return context != null && context.Client.Connected
                    ? context.PeerAddress
                    : null;
            }
        }


        // =========================================================
        // 생성자
        // =========================================================

        public TcpTransport()
            : this(new NetworkConfig())
        {
        }

        public TcpTransport(NetworkConfig config)
        {
            _config =
                config
                ?? throw new ArgumentNullException(
                    nameof(config));
        }


        // =========================================================
        // TCP 서버 시작
        // =========================================================

        public void StartListening(int port)
        {
            if (_listener != null)
                return;

            try
            {
                _listenCts =
                    new CancellationTokenSource();

                TcpListener listener =
                    new TcpListener(
                        IPAddress.Any,
                        port);

                _listener = listener;

                listener.Start();

                Console.WriteLine(
                    $"TCP Listen 시작: {port}");

                _ = AcceptClientAsync(
                    listener,
                    _listenCts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"TCP Listen 시작 실패: {ex.Message}");

                StopListening();
            }
        }


        // =========================================================
        // TCP 클라이언트 접속
        // =========================================================

        /// <summary>
        /// 상대 PC에 TCP 연결을 건다.
        ///
        /// 이미 연결돼 있으면 끊지 않고 그대로 true 를 반환한다.
        /// (상대가 먼저 걸어온 연결도 "연결됨"으로 본다)
        /// </summary>
        public async Task<bool> ConnectAsync(
            string ip,
            int port)
        {
            // -----------------------------------------------------
            // 1) 이미 연결돼 있으면 그대로 사용
            //
            // 예전에는 여기서 무조건 Disconnect() 를 불러서
            // 상대가 먼저 걸어온 연결을 내가 끊어버렸다.
            // -----------------------------------------------------

            if (IsConnected)
            {
                Console.WriteLine(
                    "TCP 이미 연결됨 → 기존 연결 사용");

                return true;
            }


            // -----------------------------------------------------
            // 2) 남아 있는 "죽은 연결"만 정리
            //
            // Connected == false 인데 아직 _connection 에 남아 있는 경우.
            // 살아 있는 연결은 건드리지 않는다.
            // -----------------------------------------------------

            ConnectionContext? stale = null;

            lock (_connectionLock)
            {
                if (_connection != null &&
                    !_connection.Client.Connected)
                {
                    stale = _connection;
                    _connection = null;
                }
            }

            stale?.Dispose();


            // -----------------------------------------------------
            // 3) 새 연결 시도
            // -----------------------------------------------------

            TcpClient? client = null;

            try
            {
                // -------------------------------------------------
                // 나갈 어댑터 지정
                //
                // 상대와 같은 망인 내 어댑터가 2개 이상일 때만
                // (예: 랜선 169.254.x.x + Npcap Loopback 169.254.x.x)
                // 검색 때 상대가 들어온 어댑터 주소로 Bind 한다.
                // 그 외에는 지금처럼 Windows 가 고른다.
                // -------------------------------------------------

                IPAddress.TryParse(ip, out IPAddress? remoteAddress);

                IPAddress? localAddress =
                    LocalRoutes.ResolveLocalAddress(remoteAddress);

                if (localAddress != null)
                {
                    client = new TcpClient(
                        new IPEndPoint(localAddress, 0));

                    Console.WriteLine(
                        $"TCP 연결 어댑터 지정: {localAddress} → {ip}");
                }
                else
                {
                    client = new TcpClient();
                }

                client.NoDelay = true;


                using CancellationTokenSource timeoutCts =
                    new CancellationTokenSource(
                        _config.ConnectTimeout);

                await client.ConnectAsync(
                    ip,
                    port,
                    timeoutCts.Token);

                NetworkStream stream =
                    client.GetStream();

                ConnectionContext context =
                    new ConnectionContext(
                        client,
                        stream,
                        isOutbound: true);

                // 소유권을 ConnectionContext로 넘겼다.
                client = null;


                // -------------------------------------------------
                // 4) 등록
                //
                // 연결을 거는 동안 상대도 나에게 연결을 걸어와서
                // 이미 등록됐을 수 있다. (동시 연결)
                // 이 경우 양쪽이 같은 연결 하나를 남기도록
                // TryRegisterConnection 이 정리한다.
                // -------------------------------------------------

                if (TryRegisterConnection(context))
                {
                    Console.WriteLine(
                        $"TCP 연결 성공: {ip}:{port}");

                    _ = ReceiveLoopAsync(context);

                    return true;
                }

                // 내 연결은 버려졌지만
                // 상대가 걸어온 연결이 살아 있으면 연결된 상태다.
                Console.WriteLine(
                    "TCP 동시 연결 감지 → 상대가 건 연결을 사용");

                return IsConnected;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine(
                    $"TCP 연결 시간 초과: {ip}:{port}");

                // 시간 초과 사이에 상대가 걸어왔을 수도 있다.
                return IsConnected;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"TCP 연결 실패: {ex.Message}");

                // 동시 연결 중 상대 쪽 규칙에 따라
                // 내 연결이 거절된 경우도 여기로 올 수 있다.
                return IsConnected;
            }
            finally
            {
                client?.Dispose();
            }
        }


        // =========================================================
        // 연결 등록 (1:1 유지 + 동시 연결 정리)
        // =========================================================

        /// <summary>
        /// 새 연결을 현재 연결로 등록할지 결정한다.
        ///
        /// true  : candidate 가 현재 연결이 됨 (호출한 쪽이 ReceiveLoop 시작)
        /// false : candidate 는 버려짐 (이미 Dispose 됨)
        ///
        /// 규칙:
        ///   1. 현재 연결이 없거나 죽어 있으면 → candidate 등록
        ///   2. 같은 상대 PC와 방향이 다른 연결이 동시에 생김
        ///      → IsPreferred 로 하나 선택
        ///      (양쪽 PC가 같은 규칙으로 같은 연결을 고른다)
        ///   3. 그 외 → 기존 유지, candidate 버림
        ///      - 같은 방향의 두 번째 연결
        ///      - 다른 PC 가 걸어온 연결
        ///        (예: A 가 B 를 조종 중인데 C 가 A 에 연결
        ///         → C 의 연결이 A–B 연결을 밀어내면 안 된다)
        /// </summary>
        private bool TryRegisterConnection(
            ConnectionContext candidate)
        {
            ConnectionContext? toDispose;
            bool registered;

            lock (_connectionLock)
            {
                ConnectionContext? current =
                    _connection;

                if (current == null ||
                    !current.Client.Connected)
                {
                    _connection = candidate;
                    toDispose = current;
                    registered = true;
                }
                else if (current.IsOutbound != candidate.IsOutbound &&
                         IsSamePeer(current, candidate) &&
                         IsPreferred(candidate, current))
                {
                    _connection = candidate;
                    toDispose = current;
                    registered = true;
                }
                else
                {
                    toDispose = candidate;
                    registered = false;
                }
            }

            // Dispose 는 lock 밖에서 한다.
            // (ReceiveLoop 의 finally 가 CompareExchange 로 정리)
            toDispose?.Dispose();

            // 새 연결이 등록됐음을 알린다. (lock 밖에서, ReceiveLoop 시작 전에)
            if (registered)
            {
                try
                {
                    Connected?.Invoke(candidate.PeerAddress);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"TCP 연결 알림 처리 오류: {ex.Message}");
                }
            }

            return registered;
        }


        /// <summary>
        /// 두 연결의 상대 PC IP 가 같은지 확인한다.
        ///
        /// IsPreferred 는 "같은 두 PC 가 동시에 건 줄" 중 하나를 고르는 규칙이다.
        /// 상대가 다르면 동시 연결이 아니라 제3자의 연결이므로
        /// 비교하지 않고 새 연결을 거부해야 한다.
        ///
        /// IP 를 모르면 다른 PC 로 본다. (기존 연결 유지 쪽이 안전)
        /// </summary>
        private static bool IsSamePeer(
            ConnectionContext a,
            ConnectionContext b)
        {
            return a.PeerAddress != null &&
                   b.PeerAddress != null &&
                   a.PeerAddress.Equals(b.PeerAddress);
        }


        /// <summary>
        /// 동시 연결로 연결이 두 개 생겼을 때 a 를 남길지 결정한다.
        ///
        /// 기준: "서버 쪽 주소(IP, 포트)"가 더 작은 연결을 남긴다.
        ///
        /// 예) A = 192.168.0.10, B = 192.168.0.20
        ///     A→B 연결의 서버 주소 = B(0.20)
        ///     B→A 연결의 서버 주소 = A(0.10)  ← 더 작음 → 이것을 남김
        ///
        /// 한 연결의 서버 주소는 양 끝 어디서 봐도 같으므로
        /// A와 B가 각자 판단해도 항상 같은 연결을 고른다.
        ///
        /// 같은 PC 테스트(127.0.0.1, 포트 45000/45002)에서는
        /// IP가 같으므로 포트로 비교한다.
        /// </summary>
        private static bool IsPreferred(
            ConnectionContext a,
            ConnectionContext b)
        {
            IPEndPoint? ea = a.ServerEndPoint;
            IPEndPoint? eb = b.ServerEndPoint;

            // 주소를 모르면 기존 연결을 유지한다.
            if (ea == null || eb == null)
                return false;

            int compare =
                CompareBytes(
                    ea.Address.GetAddressBytes(),
                    eb.Address.GetAddressBytes());

            if (compare == 0)
            {
                compare =
                    ea.Port.CompareTo(
                        eb.Port);
            }

            return compare < 0;
        }


        private static int CompareBytes(
            byte[] x,
            byte[] y)
        {
            if (x.Length != y.Length)
                return x.Length.CompareTo(y.Length);

            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] != y[i])
                    return x[i].CompareTo(y[i]);
            }

            return 0;
        }


        // =========================================================
        // 서버 접속 처리
        // =========================================================

        private async Task AcceptClientAsync(
            TcpListener listener,
            CancellationToken cancellationToken)
        {
            try
            {
                while (
                    !cancellationToken.IsCancellationRequested)
                {
                    TcpClient client =
                        await listener.AcceptTcpClientAsync(
                            cancellationToken);

                    ConnectionContext context;

                    // 연결 하나를 준비하다 실패해도
                    // (예: Accept 직후 상대가 바로 끊음)
                    // Accept 루프 전체가 죽지 않도록 이 연결만 버린다.
                    try
                    {
                        client.NoDelay = true;

                        NetworkStream stream =
                            client.GetStream();

                        context =
                            new ConnectionContext(
                                client,
                                stream,
                                isOutbound: false);
                    }
                    catch (Exception ex) when (
                        ex is SocketException ||
                        ex is IOException ||
                        ex is InvalidOperationException)
                    {
                        Console.WriteLine(
                            $"TCP 수신 연결 준비 실패: {ex.Message}");

                        client.Dispose();

                        continue;
                    }

                    // 현재 프로젝트는 1:1 이다.
                    //
                    // - 연결이 없으면 → 등록
                    // - 내가 건 연결과 동시에 들어온 경우 → 규칙으로 하나 선택
                    // - 그 외 두 번째 연결 → 거부
                    if (!TryRegisterConnection(context))
                    {
                        Console.WriteLine(
                            "TCP 중복 연결 → 새 연결 거부");

                        continue;
                    }

                    Console.WriteLine(
                        "TCP 클라이언트 연결 성공");

                    _ = ReceiveLoopAsync(context);
                }
            }
            catch (OperationCanceledException)
            {
                // 정상적인 Listen 종료
            }
            catch (ObjectDisposedException)
            {
                // Listener 종료 과정
            }
            catch (SocketException)
            {
                // Listener 종료 과정
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"TCP 서버 오류: {ex.Message}");
            }
        }


        // =========================================================
        // 메시지 전송
        // =========================================================

        public async Task SendAsync(

            InputMessage message)
        {
            ConnectionContext? context =
                _connection;

            if (context == null)
            {
                throw new InvalidOperationException(
                    "TCP 연결이 되어 있지 않습니다.");
            }

            byte[] data =
                message.ToBytes();

            await _sendLock.WaitAsync();

            try
            {
                await context.Stream.WriteAsync(
                    data,
                    0,
                    data.Length);
            }
            finally
            {
                _sendLock.Release();
            }
        }


        // =========================================================
        // 메시지 수신
        // =========================================================

        private async Task ReceiveLoopAsync(
            ConnectionContext context)
        {
            byte[] buffer =
                new byte[InputMessage.Size];

            try
            {
                while (
                    !context.Cts.IsCancellationRequested)
                {
                    int received = 0;

                    // InputMessage는 항상 9바이트
                    //
                    // TCP는 한 번 ReadAsync했다고
                    // 9바이트가 모두 들어온다는 보장이 없다.
                    while (
                        received < InputMessage.Size)
                    {
                        int count =
                            await context.Stream.ReadAsync(
                                buffer,
                                received,
                                InputMessage.Size - received,
                                context.Cts.Token);

                        // 0이면 상대방 연결 종료
                        if (count == 0)
                        {
                            return;
                        }

                        received += count;
                    }

                    InputMessage message =
                        InputMessage.FromBytes(buffer);

                    try
                    {
                        MessageReceived?.Invoke(
                            message);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"TCP 메시지 처리 오류: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 정상적인 연결 종료
            }
            catch (IOException)
            {
                // 네트워크 연결 종료
            }
            catch (ObjectDisposedException)
            {
                // Stream 종료
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"TCP 수신 오류: {ex.Message}");
            }
            finally
            {
                // 현재 Context가 아직 이 연결이라면
                // 현재 연결만 제거한다.
                Interlocked.CompareExchange(
                    ref _connection,
                    null,
                    context);

                context.Dispose();
            }
        }


        // =========================================================
        // 현재 연결 종료
        // =========================================================

        public void Disconnect()
        {
            ConnectionContext? context =
                Interlocked.Exchange(
                    ref _connection,
                    null);

            context?.Dispose();
        }


        // =========================================================
        // 서버 Listen 종료
        // =========================================================

        public void StopListening()
        {
            try
            {
                _listenCts?.Cancel();

                _listener?.Stop();
            }
            catch
            {
            }
            finally
            {
                _listener = null;

                _listenCts?.Dispose();
                _listenCts = null;
            }
        }
    }
}