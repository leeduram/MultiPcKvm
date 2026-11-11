using MultiPcKvm.Common;
using MultiPcKvm.Network.Configuration;
using MultiPcKvm.Network.Transport;
using System;
using System.Threading.Tasks;

namespace MultiPcKvm.Network.Connection
{
    /// <summary>
    /// 상대 PC 한 대와의 연결을 관리한다.
    ///
    /// TCP:
    /// - KeyDown
    /// - KeyUp
    /// - MouseButton
    /// - Ping
    /// - Pong
    /// - ScreenInfo
    /// - PairRequest
    /// - PairResult
    /// - Unpair
    /// - HelloIdHigh / HelloIdLow
    /// - PairTokenRequest / PairTokenGrant
    /// - MouseWheel / FileRequest
    ///
    /// UDP:
    /// - MouseMove
    ///
    /// MouseMoveDelta:
    /// - 로컬 InputRouter 전용
    /// - 네트워크로 보내지 않는다.
    /// </summary>
    public class PeerConnection
    {
        private readonly NetworkConfig _config;

        // 실제로 접속할 상대 TCP 포트
        private readonly int _remoteTcpPort;

        // 실제로 접속할 상대 UDP 포트
        private readonly int _remoteUdpPort;


        // =========================================================
        // 상대 PC 정보
        // =========================================================

        public Guid DeviceId { get; }

        public string ComputerName { get; }

        public string IpAddress { get; }


        // =========================================================
        // Transport
        // =========================================================

        public TcpTransport Tcp { get; }

        public UdpTransport Udp { get; }

        // =========================================================
        // 상태
        // =========================================================

        public bool IsTcpConnected =>
            Tcp.IsConnected;

        public bool IsUdpReady =>
            Udp.IsConnected;

        public bool IsConnected =>
            IsTcpConnected &&
            IsUdpReady;

        /// <summary>
        /// 지금 연결된 TCP 상대의 실제 IP 문자열. 연결이 없으면 null.
        ///
        /// IpAddress(검색에 잡힌 주소)와 다를 수 있다.
        /// 화면 보기·파일 전송 허락(CanView / CanTransfer)에서 요청한 IP 와 비교할 때 이걸 쓰면
        /// 상대가 랜선·와이파이 두 주소를 가진 경우에도 정확하다.
        /// </summary>
        public string? ConnectedIpAddress =>
            Tcp.RemoteAddress?.ToString();


        // =========================================================
        // 이벤트
        // =========================================================

        public event Action<InputMessage>? MessageReceived;


        // =========================================================
        // 일반 생성자
        // =========================================================

        /// <summary>
        /// 실제 두 PC가 통신하는 기본 생성자.
        ///
        /// 상대 TCP/UDP 포트는 NetworkConfig의
        /// TcpPort / UdpPort를 사용한다.
        /// </summary>
        public PeerConnection(
            NetworkConfig config,
            Guid deviceId,
            string computerName,
            string ipAddress)
            : this(
                config,
                deviceId,
                computerName,
                ipAddress,
                config?.TcpPort
                ?? throw new ArgumentNullException(
                    nameof(config)),
                config?.UdpPort
                ?? throw new ArgumentNullException(
                    nameof(config)))
        {
        }


        // =========================================================
        // 테스트용 생성자
        // =========================================================

        /// <summary>
        /// 상대 PC의 TCP/UDP 포트를 별도로 지정한다.
        ///
        /// 이 생성자를 이용하면 같은 PC에서
        /// 서로 다른 포트를 사용하여
        /// 두 개의 PeerConnection을 테스트할 수 있다.
        ///
        /// 예:
        ///
        /// Peer A
        /// Local  : TCP/UDP 45000
        /// Remote : TCP/UDP 45002
        ///
        /// Peer B
        /// Local  : TCP/UDP 45002
        /// Remote : TCP/UDP 45000
        /// </summary>
        public PeerConnection(
            NetworkConfig config,
            Guid deviceId,
            string computerName,
            string ipAddress,
            int remoteTcpPort,
            int remoteUdpPort)
        {
            _config =
                config
                ?? throw new ArgumentNullException(
                    nameof(config));

            ValidatePort(
                remoteTcpPort,
                nameof(remoteTcpPort));

            ValidatePort(
                remoteUdpPort,
                nameof(remoteUdpPort));

            DeviceId = deviceId;

            ComputerName =
                computerName
                ?? string.Empty;

            IpAddress =
                ipAddress
                ?? throw new ArgumentNullException(
                    nameof(ipAddress));

            _remoteTcpPort =
                remoteTcpPort;

            _remoteUdpPort =
                remoteUdpPort;


            // -----------------------------------------------------
            // Transport 생성
            //
            // Transport의 Local Port는
            // NetworkConfig에서 결정한다.
            // -----------------------------------------------------

            Tcp =
                new TcpTransport(
                    _config);

            Udp =
                new UdpTransport(
                    _config);


            // -----------------------------------------------------
            // TCP / UDP 메시지를
            // PeerConnection 하나의 이벤트로 통합
            // -----------------------------------------------------

            Tcp.MessageReceived +=
                OnMessageReceived;

            Udp.MessageReceived +=
                OnMessageReceived;

            // TCP 가 (다시) 붙을 때마다 UDP 상대 주소를 다시 넣는다.
            //
            // Disconnect() 는 UDP 상대 주소까지 지운다.
            // 그런데 다시 연결될 때 ConnectAsync() 를 거치지 않는 경우가 있다.
            //   · 상대가 나에게 걸어온 연결 (조종당하는 쪽은 ConnectAsync 를 부르지 않는다)
            //   · 상대가 먼저 걸어와서 이미 TCP 가 붙어 있어 ConnectAsync 를 건너뛴 경우
            // 그러면 UDP 주소가 비어 있어서 마우스 이동을 보내지도 받지도 못한다.
            // ("UDP 상대 PC가 설정되지 않았습니다")
            Tcp.Connected +=
                OnTcpConnected;


            // -----------------------------------------------------
            // UDP 상대 Endpoint를 미리 설정
            //
            // ConnectAsync()를 실행하지 않아도
            // 수신 준비 후 상대 주소를 알 수 있도록 한다.
            // -----------------------------------------------------

            Udp.SetRemoteEndpoint(
                IpAddress,
                _remoteUdpPort);
        }


        // =========================================================
        // 수신 대기
        // =========================================================

        /// <summary>
        /// TCP와 UDP의 수신을 시작한다.
        ///
        /// 두 PC 모두 StartListening()을
        /// 호출하는 구조를 사용한다.
        /// </summary>
        public void StartListening()
        {
            Tcp.StartListening(
                _config.TcpPort);

            Udp.StartListening(
                _config.UdpPort);
        }


        // =========================================================
        // 상대 PC 연결
        // =========================================================

        /// <summary>
        /// TCP 연결을 만들고
        /// UDP 상대 Endpoint를 설정한다.
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            // -----------------------------------------------------
            // TCP
            // -----------------------------------------------------

            bool tcpConnected =
                await Tcp.ConnectAsync(
                    IpAddress,
                    _remoteTcpPort);

            if (!tcpConnected)
            {
                return false;
            }


            // -----------------------------------------------------
            // UDP
            // -----------------------------------------------------

            bool udpReady =
                await Udp.ConnectAsync(
                    IpAddress,
                    _remoteUdpPort);

            if (!udpReady)
            {
                Tcp.Disconnect();

                return false;
            }

            return true;
        }


        // =========================================================
        // 메시지 전송
        // =========================================================

        /// <summary>
        /// 메시지 종류에 따라
        /// TCP 또는 UDP를 선택한다.
        ///
        /// MouseMoveDelta는 로컬 전용이므로
        /// 네트워크로 전송하지 않는다.
        /// </summary>
        public async Task SendAsync(
            InputMessage message)
        {
            switch (message.Type)
            {
                // -------------------------------------------------
                // UDP
                // 절대 좌표 MouseMove만 전송
                // -------------------------------------------------

                case MessageType.MouseMove:

                    await Udp.SendAsync(
                        message);

                    break;


                // -------------------------------------------------
                // TCP
                //
                // 입력 이벤트 + 제어 메시지는 순서/도착이 보장돼야 하므로
                // 전부 TCP 로 보낸다.
                //
                // ScreenInfo / PairRequest / PairResult / Unpair,
                // HelloIdHigh / HelloIdLow / PairTokenRequest / PairTokenGrant,
                // MouseWheel / FileRequest 도 여기.
                // (App 쪽 peer.Tcp.SendAsync 우회 → peer.SendAsync 로 되돌려도 됨)
                // -------------------------------------------------

                case MessageType.MouseButton:

                case MessageType.KeyDown:

                case MessageType.KeyUp:

                case MessageType.Ping:

                case MessageType.Pong:

                case MessageType.ScreenInfo:

                case MessageType.PairRequest:

                case MessageType.PairResult:

                case MessageType.Unpair:

                case MessageType.HelloIdHigh:

                case MessageType.HelloIdLow:

                case MessageType.PairTokenRequest:

                case MessageType.PairTokenGrant:

                case MessageType.MouseWheel:

                case MessageType.FileRequest:

                    await Tcp.SendAsync(
                        message);

                    break;


                // -------------------------------------------------
                // Local Only
                // -------------------------------------------------

                case MessageType.MouseMoveDelta:

                    throw new InvalidOperationException(
                        "MouseMoveDelta는 로컬 전용 메시지입니다. " +
                        "네트워크로 전송할 수 없습니다.");


                default:

                    throw new ArgumentOutOfRangeException(
                        nameof(message.Type),
                        message.Type,
                        "지원하지 않는 메시지 타입입니다.");
            }
        }


        // =========================================================
        // 메시지 수신
        // =========================================================

        private void OnMessageReceived(
            InputMessage message)
        {
            MessageReceived?.Invoke(
                message);
        }


        // =========================================================
        // TCP 가 (다시) 붙음 → UDP 상대 주소 다시 넣기
        // =========================================================

        private void OnTcpConnected(
            System.Net.IPAddress? remote)
        {
            // 주소는 생성자 · ConnectAsync 와 같게 IpAddress 를 쓴다.
            // (UDP 수신은 이 주소에서 온 패킷만 받으므로 세 곳이 같아야 한다)
            if (Udp.SetRemoteEndpoint(
                    IpAddress,
                    _remoteUdpPort))
            {
                Console.WriteLine(
                    $"UDP 대상 설정: {IpAddress}:{_remoteUdpPort} (TCP 연결됨, 상대 {remote?.ToString() ?? "?"})");
            }
        }


        // =========================================================
        // 연결 종료
        // =========================================================

        /// <summary>
        /// 상대 PC와의 현재 연결만 해제한다.
        ///
        /// TCP:
        /// 현재 TCP 연결만 종료
        ///
        /// UDP:
        /// 상대 Endpoint만 해제
        ///
        /// Listen은 유지한다.
        /// </summary>
        public void Disconnect()
        {
            Tcp.Disconnect();

            Udp.Disconnect();
        }


        // =========================================================
        // Listen 종료
        // =========================================================

        /// <summary>
        /// TCP/UDP의 수신 대기를 완전히 종료한다.
        /// </summary>
        public void StopListening()
        {
            Tcp.StopListening();

            Udp.StopListening();
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