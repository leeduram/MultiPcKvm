using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace MultiPcKvm.Network.Discovery
{
    /// <summary>
    /// 내 PC 의 IPv4 어댑터 주소 하나.
    /// </summary>
    public sealed class LocalAdapterAddress
    {
        public IPAddress Address { get; }

        // 192.168.0.12 → 0xC0A8000C (비교·계산용)
        public uint AddressValue { get; }

        public int PrefixLength { get; }

        // Windows 어댑터 번호 (IP_UNICAST_IF 에 쓴다)
        public int InterfaceIndex { get; }

        public string AdapterName { get; }

        public LocalAdapterAddress(
            IPAddress address,
            int prefixLength,
            int interfaceIndex,
            string adapterName)
        {
            Address = address;
            AddressValue = LocalRoutes.ToUInt32(address);
            PrefixLength = prefixLength;
            InterfaceIndex = interfaceIndex;
            AdapterName = adapterName;
        }

        /// <summary>
        /// remote 가 이 어댑터와 같은 망인지.
        /// </summary>
        public bool Contains(uint remote)
        {
            if (PrefixLength < 1 || PrefixLength > 32)
                return false;

            uint mask =
                uint.MaxValue << (32 - PrefixLength);

            return (AddressValue & mask) == (remote & mask);
        }

        /// <summary>
        /// 이 망의 Broadcast 주소. /31, /32 는 없음(null).
        /// </summary>
        public IPAddress? SubnetBroadcast
        {
            get
            {
                if (PrefixLength < 1 || PrefixLength > 30)
                    return null;

                uint mask =
                    uint.MaxValue << (32 - PrefixLength);

                return LocalRoutes.FromUInt32(
                    AddressValue | ~mask);
            }
        }
    }


    /// <summary>
    /// 상대 PC 로 나갈 "내 어댑터" 를 정한다.
    ///
    /// 문제:
    ///   랜선 직결(169.254.x.x) 어댑터와 다른 169.254 어댑터
    ///   (Npcap Loopback, IP 없는 Hyper-V 스위치 등)가 같이 켜져 있으면
    ///   Windows 는 169.254.x.x 로 가는 패킷을 우선순위 높은 어댑터 하나로만 보낸다.
    ///   그게 랜선이 아니면 상대에게 도착하지 않는다.
    ///
    /// 해결:
    ///   LanDiscovery 가 "상대의 패킷이 어느 내 어댑터로 들어왔는지" 를 기억해 두고
    ///   (Remember), TCP/UDP 는 보내기 전에 그 어댑터를 물어본다 (TryResolve).
    ///
    /// 어댑터가 하나뿐인 보통의 경우:
    ///   상대 IP 와 같은 망인 내 어댑터가 2개 이상일 때만 지정한다.
    ///   그 외에는 지금처럼 Windows 가 고르게 둔다. (동작 변화 없음)
    /// </summary>
    public static class LocalRoutes
    {
        // 상대 IP → (내 주소, 어댑터 번호)
        private static readonly ConcurrentDictionary<uint, LocalAdapterAddress> _learned =
            new ConcurrentDictionary<uint, LocalAdapterAddress>();


        // ---------------------------------------------------------
        // 내 IPv4 어댑터 목록 (기억해 두고 바뀔 때만 다시 읽기)
        //
        // 어댑터 목록 읽기(GetAllNetworkInterfaces)는 가볍지 않다.
        // 검색은 3초마다, 연결할 때마다 이걸 찾으므로 매번 읽지 않고 기억해 둔다.
        //
        // 다시 읽는 때
        //   · Windows 가 주소가 바뀌었다고 알려줄 때 (랜선 꽂기/뽑기, 와이파이 바꾸기 등)
        //     NetworkChange.NetworkAddressChanged / NetworkAvailabilityChanged
        //   · 혹시 알림을 놓쳐도 60초가 지나면
        // ---------------------------------------------------------

        private static readonly object _adapterLock = new object();

        private static List<LocalAdapterAddress>? _cachedAdapters;

        private static DateTime _cachedAtUtc;

        private static int _adapterVersion;

        private static readonly TimeSpan AdapterCacheLifetime = TimeSpan.FromSeconds(60);

        /// <summary>
        /// 어댑터 목록이 바뀌었다고 알림이 올 때마다 1씩 커진다.
        /// (LanDiscovery 가 "랜선을 막 꽂았다" 를 알아채는 데 쓴다)
        /// </summary>
        public static int AdapterVersion =>
            Volatile.Read(ref _adapterVersion);

        static LocalRoutes()
        {
            try
            {
                NetworkChange.NetworkAddressChanged += (_, _) => InvalidateAdapters();
                NetworkChange.NetworkAvailabilityChanged += (_, _) => InvalidateAdapters();
            }
            catch (Exception ex)
            {
                // 알림을 못 받아도 60초마다 다시 읽으므로 동작은 한다.
                Console.WriteLine($"네트워크 변경 알림 등록 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 기억해 둔 어댑터 목록을 버린다. 다음 GetAdapters 때 새로 읽는다.
        /// </summary>
        public static void InvalidateAdapters()
        {
            lock (_adapterLock)
            {
                _cachedAdapters = null;
                _adapterVersion++;
            }
        }

        /// <summary>
        /// 켜져 있는(Up) IPv4 어댑터 주소 목록. Loopback · Tunnel · 127.x 제외.
        /// 169.254.x.x 는 포함한다.
        ///
        /// 기억해 둔 목록의 복사본을 돌려준다. (받은 쪽이 고쳐도 괜찮다)
        /// </summary>
        public static List<LocalAdapterAddress> GetAdapters()
        {
            lock (_adapterLock)
            {
                if (_cachedAdapters != null &&
                    DateTime.UtcNow - _cachedAtUtc < AdapterCacheLifetime)
                {
                    return new List<LocalAdapterAddress>(_cachedAdapters);
                }
            }

            int versionBefore = AdapterVersion;

            List<LocalAdapterAddress> fresh = ReadAdapters();

            lock (_adapterLock)
            {
                // 읽는 도중에 또 바뀌었다는 알림이 왔으면 기억하지 않는다. (다음에 다시 읽음)
                if (_adapterVersion == versionBefore)
                {
                    _cachedAdapters = fresh;
                    _cachedAtUtc = DateTime.UtcNow;
                }
            }

            return new List<LocalAdapterAddress>(fresh);
        }

        // 실제로 Windows 에서 어댑터 목록을 읽는다.
        private static List<LocalAdapterAddress> ReadAdapters()
        {
            List<LocalAdapterAddress> result =
                new List<LocalAdapterAddress>();

            foreach (NetworkInterface nic in
                     NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                // IPv4 가 꺼진 어댑터는 GetIPProperties 를 부르기 전에 건너뛴다.
                // (아래 catch 로도 잡히지만, 어댑터마다 NetworkInformationException 이 나서
                //  디버그 출력에 수십 줄씩 찍힌다)
                if (!nic.Supports(NetworkInterfaceComponent.IPv4))
                    continue;

                IPInterfaceProperties properties;
                int interfaceIndex;

                try
                {
                    properties = nic.GetIPProperties();

                    // IPv4 가 꺼진 어댑터면 예외가 난다.
                    interfaceIndex =
                        properties.GetIPv4Properties()?.Index ?? -1;
                }
                catch (Exception ex) when (
                    ex is NetworkInformationException ||
                    ex is PlatformNotSupportedException)
                {
                    continue;
                }

                if (interfaceIndex < 0)
                    continue;

                foreach (UnicastIPAddressInformation info in
                         properties.UnicastAddresses)
                {
                    if (info.Address.AddressFamily !=
                        AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    // 127.x.x.x
                    if (info.Address.GetAddressBytes()[0] == 127)
                        continue;

                    result.Add(
                        new LocalAdapterAddress(
                            info.Address,
                            GetPrefixLength(info),
                            interfaceIndex,
                            nic.Name));
                }
            }

            return result;
        }


        // ---------------------------------------------------------
        // 기억 / 조회
        // ---------------------------------------------------------

        /// <summary>
        /// "remote 의 패킷이 local 어댑터로 들어왔다" 를 기억한다.
        /// LanDiscovery 가 DISCOVER / RESPONSE 를 받을 때마다 부른다.
        /// </summary>
        public static void Remember(
            IPAddress remote,
            LocalAdapterAddress local)
        {
            if (remote.AddressFamily != AddressFamily.InterNetwork)
                return;

            _learned[ToUInt32(remote)] = local;
        }

        /// <summary>
        /// remote 로 보낼 때 지정해야 할 내 어댑터를 찾는다.
        ///
        /// true  : 같은 망 어댑터가 2개 이상이라 지정이 필요하고, 어느 쪽인지 안다.
        /// false : 지정할 필요가 없거나(보통의 경우), 어느 쪽인지 모른다.
        ///         → 호출한 쪽은 지금처럼 Windows 에 맡긴다.
        /// </summary>
        public static bool TryResolve(
            IPAddress? remote,
            out LocalAdapterAddress? local)
        {
            local = null;

            if (remote == null ||
                remote.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            uint remoteValue =
                ToUInt32(remote);

            List<LocalAdapterAddress> candidates;

            try
            {
                candidates =
                    GetAdapters().FindAll(a => a.Contains(remoteValue));
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"어댑터 조회 실패: {ex.Message}");

                return false;
            }

            // 같은 망 어댑터가 0개 또는 1개 → Windows 가 알아서 맞게 보낸다.
            if (candidates.Count <= 1)
                return false;

            if (!_learned.TryGetValue(remoteValue, out LocalAdapterAddress? learned))
            {
                Console.WriteLine(
                    $"{remote} 와 같은 망 어댑터가 {candidates.Count}개인데 " +
                    "어느 쪽으로 들어왔는지 아직 모릅니다. (검색 후 다시 시도)");

                return false;
            }

            // 기억한 어댑터가 지금도 켜져 있는지 확인
            local = candidates.Find(
                a => a.AddressValue == learned.AddressValue);

            return local != null;
        }

        /// <summary>
        /// TryResolve 로 찾은 어댑터가 있으면 그 주소, 없으면 null.
        /// </summary>
        public static IPAddress? ResolveLocalAddress(
            IPAddress? remote)
        {
            return TryResolve(remote, out LocalAdapterAddress? local)
                ? local!.Address
                : null;
        }


        // ---------------------------------------------------------
        // 계산 도우미
        // ---------------------------------------------------------

        // PrefixLength 를 못 읽으면 IPv4Mask 로, 그것도 없으면 /24 로 본다.
        internal static int GetPrefixLength(
            UnicastIPAddressInformation info)
        {
            try
            {
                int prefixLength = info.PrefixLength;

                if (prefixLength > 0 &&
                    prefixLength <= 32)
                {
                    return prefixLength;
                }
            }
            catch (PlatformNotSupportedException)
            {
            }

            IPAddress? mask = info.IPv4Mask;

            if (mask == null ||
                mask.Equals(IPAddress.Any))
            {
                return 24;
            }

            return System.Numerics.BitOperations.PopCount(
                ToUInt32(mask));
        }

        // IPAddress ↔ uint (네트워크 순서, 192.168.0.1 → 0xC0A80001)
        internal static uint ToUInt32(
            IPAddress address)
        {
            byte[] bytes =
                address.GetAddressBytes();

            return
                ((uint)bytes[0] << 24) |
                ((uint)bytes[1] << 16) |
                ((uint)bytes[2] << 8) |
                (uint)bytes[3];
        }

        internal static IPAddress FromUInt32(
            uint value)
        {
            return new IPAddress(
                new[]
                {
                    (byte)(value >> 24),
                    (byte)(value >> 16),
                    (byte)(value >> 8),
                    (byte)value
                });
        }
    }
}
