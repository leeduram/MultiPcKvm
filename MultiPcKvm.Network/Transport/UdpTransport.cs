using MultiPcKvm.Common;
using MultiPcKvm.Network.Configuration;
using MultiPcKvm.Network.Discovery;
using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MultiPcKvm.Network.Transport
{
    /// <summary>
    /// ITransport의 UDP 구현체.
    ///
    /// 현재 프로젝트에서는 절대 좌표 MouseMove만 UDP로 전송한다.
    ///
    /// MouseMoveDelta는 로컬 InputRouter 전용 데이터이므로
    /// 네트워크로 전송하지 않는다.
    /// </summary>
    public class UdpTransport : ITransport
    {
        private readonly NetworkConfig _config;

        // 실제 로컬 UDP 포트
        private int _localPort;

        // UDP 소켓
        private UdpClient? _udpClient;

        // ReceiveLoop 취소
        private CancellationTokenSource? _receiveCts;

        // ReceiveLoop 중복 실행 방지
        private bool _receiveLoopStarted;

        // 상대 PC Endpoint
        private IPEndPoint? _remoteEndPoint;

        private bool _hasRemoteEndPoint;

        // 송신 Sequence
        private uint _sendSequence;

        // 수신 Sequence
        private uint _lastReceivedSequence;

        private bool _hasReceivedSequence;

        // 여러 SendAsync 동시 실행 방지
        private readonly SemaphoreSlim _sendLock =
            new SemaphoreSlim(1, 1);

        // 통계 Lock
        private readonly object _statisticsLock =
            new object();

        // 1초 단위 통계 출력
        private Timer? _statisticsTimer;


        // =========================================================
        // UDP 통계
        // =========================================================

        private long _sentPackets;
        private long _sendFailures;
        private long _receivedPackets;
        private long _estimatedLostPackets;
        private long _outOfOrderPackets;
        private long _invalidPackets;
        private long _rejectedPackets;


        // =========================================================
        // UDP Packet 구조
        //
        // [0~1]   Magic         2 byte
        // [2]     Version       1 byte
        // [3~6]   Sequence      4 byte
        // [7~15]  InputMessage  9 byte
        //
        // 총 16 byte
        // =========================================================

        private const ushort PacketMagic =
            0x4D50;

        private const byte PacketVersion =
            1;

        private const int PacketHeaderSize =
            7;

        private const int PacketSize =
            PacketHeaderSize + InputMessage.Size;


        public event Action<InputMessage>? MessageReceived;


        /// <summary>
        /// UDP에서는 TCP와 달리 실제 연결 상태를 확인할 수 없다.
        ///
        /// 여기서는 UDP 소켓이 존재하고
        /// 상대 Endpoint가 설정되어 있으면 준비된 것으로 본다.
        /// </summary>
        public bool IsConnected =>
            _udpClient != null &&
            _hasRemoteEndPoint;

        /// <summary>
        /// 상대 주소가 설정돼 있는지 (소켓 상태와 무관)
        ///
        /// IsConnected 가 false 일 때 원인 구분용:
        ///   HasRemoteEndPoint == true  → 소켓(포트) 문제
        ///   HasRemoteEndPoint == false → 상대 미설정
        /// </summary>
        public bool HasRemoteEndPoint =>
            _hasRemoteEndPoint;

        /// <summary>
        /// 마지막 StartListening 실패 원인 (성공하면 null)
        ///
        /// 예: "포트 45000: 각 소켓 주소는 하나만 사용할 수 있습니다..."
        /// </summary>
        public string? LastListenError { get; private set; }


        // =========================================================
        // 생성자
        // =========================================================

        public UdpTransport()
            : this(new NetworkConfig())
        {
        }

        public UdpTransport(
            NetworkConfig config)
            : this(
                config,
                config?.UdpPort
                ?? throw new ArgumentNullException(
                    nameof(config)))
        {
        }

        /// <summary>
        /// 로컬 UDP 포트를 직접 지정할 수 있는 생성자.
        ///
        /// 한 PC에서 두 개의 PeerConnection을 테스트할 때 사용한다.
        ///
        /// 예:
        /// A = 45000
        /// B = 45002
        /// </summary>
        public UdpTransport(
            NetworkConfig config,
            int localPort)
        {
            _config =
                config
                ?? throw new ArgumentNullException(
                    nameof(config));

            ValidatePort(
                localPort,
                nameof(localPort));

            _localPort = localPort;
        }


        // =========================================================
        // UDP 수신 시작
        // =========================================================

        public void StartListening(int port)
        {
            try
            {
                ValidatePort(
                    port,
                    nameof(port));

                if (_udpClient != null)
                {
                    // 이미 다른 포트에 Bind되어 있다면
                    // 잘못된 재사용을 막는다.
                    //
                    // 여기서 예외를 던지면 아래 catch 가
                    // 잘 쓰고 있던 소켓까지 닫아버리므로
                    // 오류만 기록하고 그대로 둔다.
                    if (_localPort != port)
                    {
                        LastListenError =
                            $"UDP 소켓이 이미 {_localPort}번 포트에 " +
                            $"Bind되어 있습니다. (요청 포트 {port})";

                        Console.WriteLine(
                            $"UDP Listen 시작 실패: {LastListenError}");

                        return;
                    }

                    StartReceiveLoopIfNeeded();

                    return;
                }

                _localPort = port;

                EnsureSocket(_localPort);

                LastListenError = null;

                Console.WriteLine(
                    $"UDP Listen 시작: {_localPort}");
            }
            catch (Exception ex)
            {
                LastListenError =
                    $"포트 {port}: {ex.Message}";

                Console.WriteLine(
                    $"UDP Listen 시작 실패 (포트 {port}): {ex.Message}");

                // 소켓만 정리한다.
                //
                // StopListening() 을 부르면 생성자에서 넣어둔
                // 상대 주소(_remoteEndPoint)까지 지워져서
                // 포트 충돌인데도 "상대 미설정"처럼 보였다.
                CloseSocket();
            }
        }


        // =========================================================
        // 상대 Endpoint 설정
        // =========================================================

        public bool SetRemoteEndpoint(
            string ip,
            int port)
        {
            try
            {
                ValidatePort(
                    port,
                    nameof(port));

                if (!IPAddress.TryParse(
                        ip,
                        out IPAddress? address))
                {
                    Console.WriteLine(
                        $"잘못된 UDP IP 주소: {ip}");

                    return false;
                }

                _remoteEndPoint =
                    new IPEndPoint(
                        address,
                        port);

                _hasRemoteEndPoint = true;

                // 상대가 바뀌었으니 나갈 어댑터도 다시 정한다.
                ApplyOutgoingInterface();

                // 새로운 상대를 지정했으므로
                // 수신 Sequence를 새로 시작한다.
                _hasReceivedSequence = false;
                _lastReceivedSequence = 0;

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"UDP Endpoint 설정 실패: {ex.Message}");

                return false;
            }
        }


        // =========================================================
        // 상대 PC 설정
        // =========================================================

        public Task<bool> ConnectAsync(
            string ip,
            int port)
        {
            try
            {
                ValidatePort(
                    port,
                    nameof(port));

                // 로컬 UDP Socket이 없으면 만든다.
                EnsureSocket(_localPort);

                bool success =
                    SetRemoteEndpoint(
                        ip,
                        port);

                if (!success)
                {
                    return Task.FromResult(false);
                }

                Console.WriteLine(
                    $"UDP 대상 설정: {ip}:{port}");

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"UDP 대상 설정 실패: {ex.Message}");

                return Task.FromResult(false);
            }
        }


        // =========================================================
        // Socket 생성
        // =========================================================

        private void EnsureSocket(
            int localPort)
        {
            if (_udpClient != null)
            {
                StartReceiveLoopIfNeeded();
                return;
            }

            _receiveCts =
                new CancellationTokenSource();

            // 지정한 로컬 포트에 Bind
            _udpClient =
                new UdpClient(localPort);

            ConfigureSocket();

            ApplyOutgoingInterface();

            StartStatisticsTimer();

            StartReceiveLoopIfNeeded();
        }


        // =========================================================
        // Windows UDP 설정
        // =========================================================

        // =========================================================
        // 나갈 어댑터 지정 (IP_UNICAST_IF)
        // =========================================================

        // Windows: IP_UNICAST_IF
        private const int IpUnicastIf = 31;

        /// <summary>
        /// 상대와 같은 망인 내 어댑터가 2개 이상이면
        /// (예: 랜선 169.254.x.x + Npcap Loopback 169.254.x.x)
        /// 검색 때 상대가 들어온 어댑터로만 보내도록 지정한다.
        ///
        /// 소켓을 다시 Bind 하지 않고 IP_UNICAST_IF 를 쓰는 이유:
        ///   받는 쪽(IsExpectedRemote)이 출발 포트까지 확인하므로
        ///   보내는 포트가 지금과 같아야 한다.
        ///
        /// 지정할 필요가 없으면 0 으로 되돌린다. (Windows 가 고름 = 지금과 같음)
        /// </summary>
        private void ApplyOutgoingInterface()
        {
            UdpClient? client = _udpClient;

            if (client == null ||
                !OperatingSystem.IsWindows())
            {
                return;
            }

            int interfaceIndex = 0;

            if (_remoteEndPoint != null &&
                LocalRoutes.TryResolve(
                    _remoteEndPoint.Address,
                    out LocalAdapterAddress? local) &&
                local != null)
            {
                interfaceIndex = local.InterfaceIndex;
            }

            try
            {
                // IPv4 는 어댑터 번호를 네트워크 바이트 순서로 넘겨야 한다.
                client.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    (SocketOptionName)IpUnicastIf,
                    IPAddress.HostToNetworkOrder(interfaceIndex));

                if (interfaceIndex != 0)
                {
                    Console.WriteLine(
                        $"UDP 보내기 어댑터 지정: #{interfaceIndex} → {_remoteEndPoint}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"UDP 어댑터 지정 실패: {ex.Message}");
            }
        }

        private void ConfigureSocket()
        {
            if (_udpClient == null)
                return;

            // 수신 버퍼를 1MB로 늘려서
            // 짧은 시간에 UDP 패킷이 몰리는 상황을 조금 더 흡수한다.
            _udpClient.Client.ReceiveBufferSize = 1 << 20;

            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                // Windows에서 상대 UDP 포트가 닫혀 있을 때
                // SIO_UDP_CONNRESET으로 수신 루프가 종료되는 문제 방지
                _udpClient.Client.IOControl(
                    (IOControlCode)(-1744830452),
                    new byte[] { 0, 0, 0, 0 },
                    null);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"UDP Windows 옵션 설정 실패: {ex.Message}");
            }
        }


        // =========================================================
        // 통계 Timer
        // =========================================================

        private void StartStatisticsTimer()
        {
            lock (_statisticsLock)
            {
                if (_statisticsTimer != null)
                    return;

                _statisticsTimer =
                    new Timer(
                        _ => LogStatistics(),
                        null,
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(1));
            }
        }

        private void StopStatisticsTimer()
        {
            lock (_statisticsLock)
            {
                _statisticsTimer?.Dispose();
                _statisticsTimer = null;
            }
        }


        private void LogStatistics()
        {
            long sent =
                Interlocked.Exchange(
                    ref _sentPackets,
                    0);

            long sendFailures =
                Interlocked.Exchange(
                    ref _sendFailures,
                    0);

            long received =
                Interlocked.Exchange(
                    ref _receivedPackets,
                    0);

            long lost =
                Interlocked.Exchange(
                    ref _estimatedLostPackets,
                    0);

            long outOfOrder =
                Interlocked.Exchange(
                    ref _outOfOrderPackets,
                    0);

            long invalid =
                Interlocked.Exchange(
                    ref _invalidPackets,
                    0);

            long rejected =
                Interlocked.Exchange(
                    ref _rejectedPackets,
                    0);

            // 아무 이벤트도 없으면 로그를 출력하지 않는다.
            if (
                sent == 0 &&
                sendFailures == 0 &&
                received == 0 &&
                lost == 0 &&
                outOfOrder == 0 &&
                invalid == 0 &&
                rejected == 0)
            {
                return;
            }

            Console.WriteLine(
                $"UDP 통계: " +
                $"송신 {sent}, " +
                $"수신 {received}, " +
                $"유실 추정 {lost}, " +
                $"순서역전/중복 {outOfOrder}, " +
                $"잘못된 패킷 {invalid}, " +
                $"거부 {rejected}, " +
                $"송신실패 {sendFailures}");
        }


        // =========================================================
        // ReceiveLoop
        // =========================================================

        private void StartReceiveLoopIfNeeded()
        {
            if (_receiveLoopStarted)
                return;

            if (_udpClient == null ||
                _receiveCts == null)
            {
                return;
            }

            _receiveLoopStarted = true;

            _ = ReceiveLoopAsync(
                _receiveCts.Token);
        }


        // =========================================================
        // 메시지 전송
        // =========================================================

        public async Task SendAsync(
            InputMessage message)
        {
            if (_udpClient == null)
            {
                throw new InvalidOperationException(
                    "UDP 소켓이 생성되지 않았습니다.");
            }

            if (!_hasRemoteEndPoint ||
                _remoteEndPoint == null)
            {
                throw new InvalidOperationException(
                    "UDP 상대 PC가 설정되지 않았습니다.");
            }

            // UDP는 절대좌표 MouseMove만 전송한다.
            //
            // MouseMoveDelta는 InputRouter에서만 사용한다.
            if (message.Type != MessageType.MouseMove)
            {
                throw new InvalidOperationException(
                    "UDP에서는 MouseMove만 전송할 수 있습니다. " +
                    "MouseMoveDelta는 로컬 전용입니다.");
            }

            await _sendLock.WaitAsync();

            try
            {
                // 중요:
                // Sequence 생성과 실제 송신을 같은 Lock 안에서 수행한다.
                //
                // 이전:
                // CreatePacket()
                //     ↓
                // _sendSequence++
                //     ↓
                // WaitAsync()
                //
                // 이 구조에서는 동시에 호출될 경우 경합 가능.
                //
                // 현재:
                // WaitAsync()
                //     ↓
                // CreatePacket()
                //     ↓
                // SendAsync()
                //
                // 따라서 Sequence 순서와 실제 송신 순서가 맞는다.
                byte[] packet =
                    CreatePacket(message);

                try
                {
                    await _udpClient.SendAsync(
                        packet,
                        packet.Length,
                        _remoteEndPoint);

                    Interlocked.Increment(
                        ref _sentPackets);
                }
                catch (SocketException)
                {
                    // UDP는 전송 실패가 발생할 수 있다.
                    //
                    // UDP 실패 때문에 TCP나 전체 연결을
                    // 강제로 종료하지 않는다.
                    Interlocked.Increment(
                        ref _sendFailures);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }


        // =========================================================
        // Packet 생성
        // =========================================================

        // 보낼 패킷을 담는 배열. 매번 새로 만들지 않고 다시 쓴다.
        //
        // 마우스 이동은 초당 수백 번 보내므로, 예전처럼 메시지마다 배열을 4개씩
        // (패킷, ToBytes, BitConverter 2번) 만들면 메모리 정리(GC)가 자주 돈다.
        //
        // 안전한 이유: CreatePacket 과 실제 전송이 모두 _sendLock 안에서 일어나고,
        // 전송이 끝날 때까지(await) 잠금을 풀지 않는다. 그래서 다른 전송과 겹치지 않는다.
        private readonly byte[] _sendPacket =
            new byte[PacketSize];

        private byte[] CreatePacket(
            InputMessage message)
        {
            byte[] packet =
                _sendPacket;

            // Magic
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(0, 2),
                PacketMagic);

            // Version
            packet[2] =
                PacketVersion;

            // Sequence
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(3, 4),
                _sendSequence);

            // InputMessage (InputMessage.ToBytes 와 같은 배치: 종류 1 + 값1 4 + 값2 4, 리틀 엔디언)
            Span<byte> body =
                packet.AsSpan(PacketHeaderSize, InputMessage.Size);

            body[0] = (byte)message.Type;
            BinaryPrimitives.WriteInt32LittleEndian(body.Slice(1, 4), message.Value1);
            BinaryPrimitives.WriteInt32LittleEndian(body.Slice(5, 4), message.Value2);

            // 반드시 Send Lock 안에서 실행됨
            _sendSequence++;

            return packet;
        }


        // =========================================================
        // 수신
        // =========================================================

        private async Task ReceiveLoopAsync(
            CancellationToken cancellationToken)
        {
            if (_udpClient == null)
                return;

            int consecutiveSocketErrors = 0;

            const int maxConsecutiveSocketErrors = 5;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    UdpReceiveResult result;

                    try
                    {
                        result =
                            await _udpClient.ReceiveAsync(
                                cancellationToken);

                        // 정상 수신했으므로 오류 카운터 초기화
                        consecutiveSocketErrors = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        // 정상 종료
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        // 소켓 종료
                        break;
                    }
                    catch (SocketException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        consecutiveSocketErrors++;

                        // 계속 같은 오류가 발생하면
                        // 잠깐 쉬어서 바쁜 루프를 방지한다.
                        if (consecutiveSocketErrors
                            >= maxConsecutiveSocketErrors)
                        {
                            await Task.Delay(
                                TimeSpan.FromMilliseconds(100),
                                cancellationToken);

                            // 100ms 쉬었으면
                            // 다시 오류 횟수를 세기 시작한다.
                            consecutiveSocketErrors = 0;
                        }

                        continue;
                    }

                    // =====================================================
                    // 패킷 하나 처리
                    // =====================================================

                    try
                    {
                        if (!_hasRemoteEndPoint ||
                            _remoteEndPoint == null)
                        {
                            Interlocked.Increment(
                                ref _rejectedPackets);

                            continue;
                        }

                        if (!IsExpectedRemote(
                                result.RemoteEndPoint))
                        {
                            Interlocked.Increment(
                                ref _rejectedPackets);

                            continue;
                        }

                        if (!TryParsePacket(
                                result.Buffer,
                                out uint sequence,
                                out InputMessage message))
                        {
                            Interlocked.Increment(
                                ref _invalidPackets);

                            continue;
                        }

                        if (!ValidateSequence(sequence))
                        {
                            continue;
                        }

                        Interlocked.Increment(
                            ref _receivedPackets);

                        try
                        {
                            MessageReceived?.Invoke(
                                message);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(
                                $"UDP 메시지 처리 오류: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        // 패킷 하나 처리 중 발생한 오류가
                        // 수신 루프 전체를 죽이지 않도록 한다.
                        Interlocked.Increment(
                            ref _invalidPackets);

                        Console.WriteLine(
                            $"UDP 패킷 처리 오류: {ex.Message}");

                        continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 정상 종료
            }
            catch (ObjectDisposedException)
            {
                // 정상적인 소켓 종료 과정
            }
            finally
            {
                _receiveLoopStarted = false;
            }
        }


        // =========================================================
        // 상대 PC 확인
        // =========================================================

        private bool IsExpectedRemote(
            IPEndPoint endpoint)
        {
            if (_remoteEndPoint == null)
                return false;

            return
                endpoint.Address.Equals(
                    _remoteEndPoint.Address)
                &&
                endpoint.Port ==
                    _remoteEndPoint.Port;
        }


        // =========================================================
        // Packet 검증
        // =========================================================

        private bool TryParsePacket(
    byte[] data,
    out uint sequence,
    out InputMessage message)
        {
            sequence = 0;
            message = default;

            try
            {
                // ---------------------------------------------------------
                // 전체 패킷 크기 검사
                // ---------------------------------------------------------

                if (data.Length != PacketSize)
                    return false;


                // ---------------------------------------------------------
                // Magic 검사
                // ---------------------------------------------------------

                ushort magic =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        data.AsSpan(0, 2));

                if (magic != PacketMagic)
                    return false;


                // ---------------------------------------------------------
                // Version 검사
                // ---------------------------------------------------------

                if (data[2] != PacketVersion)
                    return false;


                // ---------------------------------------------------------
                // Sequence 읽기
                // ---------------------------------------------------------

                sequence =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        data.AsSpan(3, 4));


                // ---------------------------------------------------------
                // InputMessage 영역 추출
                // ---------------------------------------------------------

                // (배열을 새로 만들지 않고 받은 데이터에서 바로 읽는다)
                ReadOnlySpan<byte> messageBytes =
                    data.AsSpan(PacketHeaderSize, InputMessage.Size);


                // ---------------------------------------------------------
                // MessageType 확인
                // ---------------------------------------------------------

                byte rawType =
                    messageBytes[0];

                MessageType messageType =
                    (MessageType)rawType;

                // 현재 UDP는 MouseMove만 허용한다.
                //
                // MouseMoveDelta는 로컬 전용이다.
                if (messageType != MessageType.MouseMove)
                    return false;


                // ---------------------------------------------------------
                // InputMessage 복원
                // ---------------------------------------------------------

                message = new InputMessage
                {
                    Type = messageType,
                    Value1 = BinaryPrimitives.ReadInt32LittleEndian(messageBytes.Slice(1, 4)),
                    Value2 = BinaryPrimitives.ReadInt32LittleEndian(messageBytes.Slice(5, 4))
                };

                return true;
            }
            catch
            {
                // 패킷 하나가 이상하더라도
                // ReceiveLoop 전체가 죽지 않도록 한다.
                return false;
            }
        }


        // =========================================================
        // Sequence 검사
        // =========================================================

        private bool ValidateSequence(
            uint sequence)
        {
            // 첫 번째 패킷
            if (!_hasReceivedSequence)
            {
                _hasReceivedSequence = true;

                _lastReceivedSequence =
                    sequence;

                return true;
            }

            // 오래된 패킷 / 중복 패킷
            if (!IsSequenceNewer(
                    sequence,
                    _lastReceivedSequence))
            {
                Interlocked.Increment(
                    ref _outOfOrderPackets);

                return false;
            }

            // 다음에 기대하는 Sequence
            uint expected =
                _lastReceivedSequence + 1;

            if (sequence != expected)
            {
                uint lostCount =
                    sequence - expected;

                Interlocked.Add(
                    ref _estimatedLostPackets,
                    lostCount);
            }

            _lastReceivedSequence =
                sequence;

            return true;
        }


        // =========================================================
        // Sequence 비교
        // =========================================================

        private static bool IsSequenceNewer(
            uint sequence,
            uint previous)
        {
            if (sequence == previous)
                return false;

            // uint overflow 고려
            return
                (sequence - previous)
                < 0x80000000u;
        }


        // =========================================================
        // 상대 Endpoint만 해제
        // =========================================================

        public void Disconnect()
        {
            // UDP Socket은 계속 살아있고
            // 상대 Endpoint만 제거한다.
            _remoteEndPoint = null;
            _hasRemoteEndPoint = false;

            _hasReceivedSequence = false;
            _lastReceivedSequence = 0;
        }


        // =========================================================
        // UDP Listen 완전 종료
        // =========================================================

        /// <summary>
        /// UDP 수신을 완전히 종료한다.
        ///
        /// 소켓 + 상대 주소 + Sequence 를 모두 초기화한다.
        /// </summary>
        public void StopListening()
        {
            CloseSocket();

            _remoteEndPoint = null;
            _hasRemoteEndPoint = false;

            _sendSequence = 0;

            _lastReceivedSequence = 0;
            _hasReceivedSequence = false;
        }


        // =========================================================
        // 소켓만 정리
        // =========================================================

        /// <summary>
        /// 소켓 / 수신 루프 / 통계 타이머만 정리한다.
        ///
        /// 상대 주소(_remoteEndPoint)는 그대로 남긴다.
        /// → StartListening 실패 후에도 상대 설정 정보가 유지되어
        ///   다시 StartListening 하면 바로 사용할 수 있다.
        /// </summary>
        private void CloseSocket()
        {
            try
            {
                _receiveCts?.Cancel();

                _udpClient?.Close();
                _udpClient?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _udpClient = null;

                _receiveCts?.Dispose();
                _receiveCts = null;

                _receiveLoopStarted = false;

                StopStatisticsTimer();
            }
        }


        // =========================================================
        // Port 검증
        // =========================================================

        private static void ValidatePort(
            int port,
            string parameterName)
        {
            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    port,
                    "포트 번호는 1~65535 범위여야 합니다.");
            }
        }
    }
}