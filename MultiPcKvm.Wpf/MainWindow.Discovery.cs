using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;    // Path, File, FileInfo (진단 기록 파일). WPF 프로젝트는 System.IO 를 자동으로 넣어주지 않는다
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MultiPcKvm.App;
using MultiPcKvm.App.Storage;
using MultiPcKvm.Common;
using MultiPcKvm.Network.Configuration;
using MultiPcKvm.Network.Connection;
using MultiPcKvm.Network.Discovery;
using MultiPcKvm.Windows;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  MainWindow — 검색 · 기기 목록
    //
    //  기기를 찾았을 때 목록에 넣고 갱신한다. (OnDeviceFound)
    //  같은 기기가 랜선·와이파이 두 주소로 대답할 때 어느 주소를 쓸지 정한다. (ChooseAddress)
    //
    //  MainWindow 는 할 일이 많아서 주제별로 파일을 나눴다. (partial = 한 클래스를 여러 파일에 나눠 쓰기)
    //  필드(공용 상태)·생성자·시작·Tick 은 MainWindow.xaml.cs 에 있다.
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // 이미 기록한 (기기, 대답한 주소, 쓰는 주소) 조합. 같은 대답을 매번 기록하지 않으려고.
        // 기기 수 × 주소 수만큼만 쌓이므로 크기 걱정은 없다.
        private readonly HashSet<string> _replyLogKeys = new HashSet<string>();

        private void OnDeviceFound(DiscoveredPc pc)
        {
            // 이미 목록에 있으면 정보만 갱신한다.
            DeviceItem? found = _devices.FirstOrDefault(d => d.DeviceId == pc.DeviceId);

            // ID 로 못 찾았으면 IP 로 한 번 더 찾는다. (예비 장치)
            //
            // 예전에는 LanDiscovery 가 켤 때마다 DeviceId 를 새로 뽑아서
            // 상대가 프로그램을 껐다 켜면 같은 PC 가 목록에 두 번 떴다.
            // 지금은 DeviceIdentity 가 ID 를 파일에 저장해서 매번 같은 값을 쓰므로
            // 대부분 위의 ID 검색에서 찾는다.
            //
            // 그래도 남겨두는 경우:
            //   · 상대 PC 가 아직 예전 버전일 때
            //   · 상대 PC 가 ID 파일 저장에 실패했을 때 (DeviceIdentity.LastError)
            //
            // 같은 공유기 안에서 한 IP 는 한 PC 만 쓸 수 있으니,
            // IP 가 같으면 같은 PC 가 다시 켜진 것으로 본다.
            //
            // ★ 단, 저장된 기기는 IP 로 찾지 않는다.
            //   저장된 기기의 IP 는 "마지막으로 봤을 때" 의 값이다.
            //   그 PC 가 꺼진 사이 공유기가 같은 IP 를 다른 PC 에 줬을 수 있다.
            //   IP 로 합치면 남의 PC 가 내 신뢰 기기의 ID 를 덮어쓰게 된다.
            found ??= _devices.FirstOrDefault(d => !d.IsSaved && d.IpAddress == pc.IpAddress);

            if (found != null)
            {
                bool wasOffline = !found.IsOnline;
                string oldIp = found.IpAddress;

                // 이번 대답의 주소를 쓸지, 지금 주소를 계속 쓸지 고른다.
                // (같은 기기가 랜선·와이파이 두 주소로 대답하는 경우. 아래 ChooseAddress)
                string ip = ChooseAddress(found, pc.IpAddress, wasOffline);

                // 이름이나 IP 가 바뀌었는지 덮어쓰기 전에 확인해둔다.
                bool changed = found.Name != pc.ComputerName ||
                               oldIp != ip;

                // 새 ID 로 갈아끼운다.
                // 새 항목을 만드는 게 아니라 같은 항목을 고치는 것이라,
                // 이 항목을 붙잡고 있는 연결(_peerItem, _connectedItem)도 그대로 유지된다.
                found.DeviceId = pc.DeviceId;
                found.Name = pc.ComputerName;
                found.IpAddress = ip;
                found.TcpPort = pc.TcpPort;
                found.LastSeen = DateTime.Now;
                found.IsOnline = true;

                // 지금 쓰는 주소로 대답이 왔으면 "살아 있음" 시각을 갱신한다.
                if (ip == pc.IpAddress)
                    found.IpLastSeen = DateTime.Now;

                // 기록은 뭔가 달라졌을 때만 남긴다.
                //   · 꺼져 있다가 켜짐 · 쓰는 주소가 바뀜 · 처음 보는 (대답한 주소, 쓰는 주소) 조합
                // 대답은 기기마다 3초에 몇 번씩 와서, 매번 적으면 하루에 수 MB 가 쌓이고
                // 정작 중요한 줄을 찾기 어렵다. (꺼짐은 Tick 의 "오프라인 처리" 줄로 남는다)
                bool firstCombo = _replyLogKeys.Add($"{pc.DeviceId}|{pc.IpAddress}|{ip}");

                if (wasOffline || oldIp != ip || firstCombo)
                {
                    DiagLog.Write($"대답 받음 | {pc.ComputerName} {pc.IpAddress} {pc.DeviceId}" +
                                  (wasOffline ? " | 오프라인 → 온라인" : "") +
                                  (oldIp != ip ? $" | IP 바뀜 {oldIp} → {ip}" : "") +
                                  (ip != pc.IpAddress ? $" | 지금 주소 {ip} 유지" : ""));
                }

                // ★ 상대 IP 가 바뀌었는데, 연결 준비(대기 상태)가 옛 IP 로 되어 있으면 새로 만든다.
                //
                // 대기 상태(PeerConnection)는 만들 때의 IP 를 계속 쓴다.
                // 그대로 두면 [연결] 은 옛 IP 로 붙으려다 실패하고,
                // 조종당하는 쪽은 옛 IP 에서 온 마우스 이동만 받고 새 IP 에서 온 건 버린다.
                // (와이파이 ↔ 핫스팟처럼 네트워크를 바꾸면 IP 가 바뀐다)
                //
                // 지금 줄이 이어져 있으면 건드리지 않는다. 쓰고 있는 연결을 끊게 되기 때문이다.
                if (_peer != null &&
                    found == _peerItem &&
                    _peer.IpAddress != found.IpAddress &&
                    _session == null &&
                    !_peer.IsTcpConnected)
                {
                    DiagLog.Write($"대기 상태 다시 만듦 | {_peer.IpAddress} → {found.IpAddress}");
                    TearDownPeer();
                }

                // 저장된 기기인데 이름·IP 가 바뀌었으면 DB 도 고친다. (U)
                // 매번 쓰지 않고 바뀌었을 때만 쓴다. 검색 대답은 3초마다 오기 때문이다.
                if (found.IsSaved && changed)
                    UpdateSavedDevice(found);

                // 꺼져 있던 저장된 기기가 방금 켜졌다면 대기 상태로 들어간다.
                // (처음 찾은 기기는 아래에서 들어가는데, 저장된 기기는 여기로 온다)
                if (_peer == null)
                    EnsureStandby(found);

                return;
            }

            var item = new DeviceItem
            {
                DeviceId = pc.DeviceId,
                Name = pc.ComputerName,
                IpAddress = pc.IpAddress,
                TcpPort = pc.TcpPort,
                LastSeen = DateTime.Now,
                IpLastSeen = DateTime.Now,
                StatusText = pc.IpAddress
            };

            _devices.Add(item);

            DiagLog.Write($"새 기기 | {pc.ComputerName} {pc.IpAddress} {pc.DeviceId}");

            // 첫 기기를 찾았으면 대기 상태로 들어간다.
            // 이래야 상대가 연결해 왔을 때 받아줄 수 있다.
            EnsureStandby(item);
        }


        // ── 같은 기기가 여러 주소로 대답할 때 ─────────────────
        //
        // 검색은 켜져 있는 네트워크마다 따로 외친다. (랜선 직결 지원)
        // 그래서 두 PC 가 같은 와이파이에 붙은 채 랜선까지 꽂혀 있으면
        // 상대가 랜선 주소(169.254.x.x)와 와이파이 주소로 두 번 대답한다.
        //
        // 대답이 올 때마다 주소를 바꾸면 3초마다 왔다 갔다 하고,
        // 연결을 누르는 순간 느린 와이파이 쪽으로 잡힐 수 있다.
        //
        // 규칙:
        //   1. 더 좋은 경로면 바로 바꾼다.        (유선 > 무선 > 가상·알 수 없음)
        //   2. 같거나 나쁜 경로면, 지금 주소가 10초 넘게 대답이 없을 때만 바꾼다.
        //      (랜선을 뽑으면 10초 뒤 와이파이로 넘어간다)
        //   3. 꺼져 있다가 다시 잡힌 기기는 새 주소를 그대로 쓴다.
        //      (저장된 주소는 옛날 값이라 믿을 수 없다)

        private string ChooseAddress(DeviceItem item, string answeredIp, bool wasOffline)
        {
            string current = item.IpAddress;

            if (answeredIp == current || string.IsNullOrEmpty(current) || wasOffline)
                return answeredIp;

            int newRank = PathRank(answeredIp);
            int currentRank = PathRank(current);

            if (newRank > currentRank)
            {
                DiagLog.Write($"경로 바꿈 | {item.Name} {current}({RankName(currentRank)}) → {answeredIp}({RankName(newRank)}) | 더 좋은 경로");
                return answeredIp;
            }

            bool currentAlive = (DateTime.Now - item.IpLastSeen).TotalSeconds < 10;

            if (!currentAlive)
            {
                DiagLog.Write($"경로 바꿈 | {item.Name} {current} → {answeredIp} | 지금 주소가 10초 넘게 대답 없음");
                return answeredIp;
            }

            return current;
        }

        // 상대 주소가 내 PC 의 어느 어댑터 쪽 망에 있는지 보고 경로의 좋고 나쁨을 매긴다.
        //   2 = 유선 (랜선)
        //   1 = 무선 (와이파이)
        //   0 = 가상 어댑터(VPN, Hyper-V, VMware 등) 또는 알 수 없음
        //
        // 상대가 두 주소로 대답하면(랜선+와이파이) 대답마다 불린다. 3초에 몇 번씩이다.
        // 그때마다 어댑터 목록을 새로 읽으면 화면 스레드에서 시간이 걸려서, 읽은 결과를 기억해 둔다. (AdapterRanks)
        private static int PathRank(string ip)
        {
            if (!System.Net.IPAddress.TryParse(ip, out System.Net.IPAddress? peer) ||
                peer.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return 0;
            }

            uint p = ToUInt32(peer);

            // 상대 주소가 어느 어댑터의 망 안에 있으면 그 어댑터로 가는 경로다. (먼저 맞는 것)
            foreach (var (network, mask, rank) in AdapterRanks.Get())
            {
                if ((p & mask) == network)
                    return rank;
            }

            return 0;
        }

        // 내 PC 어댑터들의 (망 주소, 망 크기, 경로 등급) 목록을 기억해 둔다.
        //
        // 네트워크가 바뀌면(랜선 꽂기·뽑기, 와이파이 바꾸기, 주소 바뀜) Windows 가 알려준다.
        // 그때 기억을 지우고, 다음에 필요할 때 다시 읽는다.
        private static class AdapterRanks
        {
            private static (uint Network, uint Mask, int Rank)[]? _cache;

            static AdapterRanks()
            {
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (s, e) => _cache = null;
                System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (s, e) => _cache = null;
            }

            public static (uint Network, uint Mask, int Rank)[] Get()
            {
                var cached = _cache;

                if (cached != null)
                    return cached;

                cached = Read();

                // 못 읽었으면 기억하지 않는다. 다음에 다시 읽어 본다. (그동안은 빈 목록 → 등급 0)
                if (cached == null)
                    return Array.Empty<(uint, uint, int)>();

                _cache = cached;
                return cached;
            }

            private static (uint Network, uint Mask, int Rank)[]? Read()
            {
                var list = new List<(uint, uint, int)>();

                try
                {
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                            continue;

                        int rank = RankOf(nic);

                        foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                                continue;

                            int prefix = ua.PrefixLength;

                            if (prefix < 1 || prefix > 32)
                                continue;

                            uint mask = uint.MaxValue << (32 - prefix);
                            list.Add((ToUInt32(ua.Address) & mask, mask, rank));
                        }
                    }
                }
                catch
                {
                    // 어댑터 정보를 못 읽으면 판단하지 않는다.
                    return null;
                }

                return list.ToArray();
            }
        }

        private static int RankOf(System.Net.NetworkInformation.NetworkInterface nic)
        {
            // 이름이나 설명에 가상 어댑터 표시가 있으면 종류와 상관없이 가장 낮게 본다.
            // Hyper-V 같은 가상 어댑터는 종류가 "Ethernet" 으로 나와서 유선으로 오해하기 쉽다.
            string text = (nic.Name + " " + nic.Description).ToLowerInvariant();

            if (text.Contains("virtual") || text.Contains("hyper-v") || text.Contains("vmware") ||
                text.Contains("virtualbox") || text.Contains("vpn") || text.Contains("loopback"))
            {
                return 0;
            }

            return nic.NetworkInterfaceType switch
            {
                System.Net.NetworkInformation.NetworkInterfaceType.Ethernet => 2,
                System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet => 2,
                System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetT => 2,
                System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetFx => 2,
                System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 => 1,
                _ => 0,
            };
        }

        private static string RankName(int rank) => rank switch
        {
            2 => "유선",
            1 => "무선",
            _ => "기타",
        };

        private static uint ToUInt32(System.Net.IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }
    }
}
