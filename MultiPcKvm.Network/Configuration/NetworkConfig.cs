using MultiPcKvm.Common;
using System;

namespace MultiPcKvm.Network.Configuration
{
    /// <summary>
    /// Network에서 사용하는 실행 설정.
    ///
    /// Constants.cs:
    ///     프로젝트 전체에서 변하지 않는 고정값
    ///
    /// NetworkConfig:
    ///     Network 동작에 사용하는 설정값
    ///
    /// 생성 시 값의 유효성을 검사하고,
    /// 생성된 이후에는 값을 변경하지 못하도록 한다.
    /// </summary>
    public sealed class NetworkConfig
    {
        // =========================================================
        // Port
        // =========================================================

        /// <summary>
        /// TCP 입력 통신 포트
        /// </summary>
        public int TcpPort { get; }

        /// <summary>
        /// UDP 입력 통신 포트
        /// </summary>
        public int UdpPort { get; }

        /// <summary>
        /// LAN Discovery 포트
        /// </summary>
        public int DiscoveryPort { get; }


        // =========================================================
        // Connection
        // =========================================================

        /// <summary>
        /// TCP 연결 시도 제한 시간
        /// </summary>
        public TimeSpan ConnectTimeout { get; }

        /// <summary>
        /// 연결이 끊겼을 때 최대 재연결 시도 횟수
        /// </summary>
        public int MaxReconnectAttempts { get; }


        // =========================================================
        // Discovery
        // =========================================================

        /// <summary>
        /// LAN PC 검색 주기
        /// </summary>
        public TimeSpan DiscoveryInterval { get; }


        // =========================================================
        // KeepAlive
        // =========================================================

        /// <summary>
        /// 연결 상태 확인 주기
        /// </summary>
        public TimeSpan KeepAliveInterval { get; }


        // =========================================================
        // 기본 생성자
        // =========================================================

        /// <summary>
        /// 기본 네트워크 설정.
        ///
        /// 현재 프로젝트에서 사용하는 기본값:
        /// TCP      = 45000
        /// UDP      = 45000
        /// Discovery = 45001
        /// </summary>
        public NetworkConfig()
            : this(
                tcpPort: Constants.InputPort,
                udpPort: Constants.InputPort,
                discoveryPort: Constants.DiscoveryPort,
                connectTimeout: TimeSpan.FromSeconds(5),
                maxReconnectAttempts: 3,
                discoveryInterval: TimeSpan.FromSeconds(3),
                keepAliveInterval: TimeSpan.FromSeconds(5))
        {
        }


        // =========================================================
        // 사용자 지정 설정
        // =========================================================

        public NetworkConfig(
            int tcpPort,
            int udpPort,
            int discoveryPort,
            TimeSpan connectTimeout,
            int maxReconnectAttempts,
            TimeSpan discoveryInterval,
            TimeSpan keepAliveInterval)
        {
            ValidatePort(
                tcpPort,
                nameof(tcpPort));

            ValidatePort(
                udpPort,
                nameof(udpPort));

            ValidatePort(
                discoveryPort,
                nameof(discoveryPort));

            ValidatePositiveTime(
                connectTimeout,
                nameof(connectTimeout));

            ValidateNonNegative(
                maxReconnectAttempts,
                nameof(maxReconnectAttempts));

            ValidatePositiveTime(
                discoveryInterval,
                nameof(discoveryInterval));

            ValidatePositiveTime(
                keepAliveInterval,
                nameof(keepAliveInterval));


            TcpPort = tcpPort;
            UdpPort = udpPort;
            DiscoveryPort = discoveryPort;

            ConnectTimeout = connectTimeout;

            MaxReconnectAttempts =
                maxReconnectAttempts;

            DiscoveryInterval =
                discoveryInterval;

            KeepAliveInterval =
                keepAliveInterval;
        }


        // =========================================================
        // Validation
        // =========================================================

        /// <summary>
        /// TCP/UDP 포트 번호 검사.
        /// TCP/UDP 포트는 1~65535 범위만 사용할 수 있다.
        /// </summary>
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

        /// <summary>
        /// 0보다 큰 시간인지 검사.
        /// </summary>
        private static void ValidatePositiveTime(
            TimeSpan value,
            string parameterName)
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "시간 값은 0보다 커야 합니다.");
            }
        }

        /// <summary>
        /// 0 이상인지 검사.
        /// 재연결 시도 횟수는 0도 허용한다.
        /// </summary>
        private static void ValidateNonNegative(
            int value,
            string parameterName)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "값은 0 이상이어야 합니다.");
            }
        }
    }
}