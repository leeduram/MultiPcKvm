using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MultiPcKvm.Common;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  Windows 방화벽이 우리 포트를 막고 있는지 확인하고, 막혀 있으면 열어준다.
    //
    //  Windows 가 처음 실행할 때 "허용할까요?" 창을 띄우긴 한다. 그런데:
    //    · 창이 안 뜨거나(다른 창 뒤에 숨는 등) 모르고 넘어가면 규칙이 아예 없다
    //    · [취소] 나 X 를 누르면 차단 규칙이 생기고, 다시는 묻지 않는다
    //    · 창에서 고른 네트워크 종류(개인/공용)와 다른 곳에 가면 막힌다
    //      (랜선 직결·핫스팟은 보통 "공용" 으로 잡힌다)
    //  이 경우 오류도 없이 조용히 막혀서 사용자는 원인을 알 수 없다.
    //  이 클래스는 그런 경우를 찾아서 버튼 한 번으로 복구하게 해준다.
    //
    //  확인해야 하는 것 (밖에서 들어오는 방향)
    //    UDP 45001  기기 검색
    //    UDP 45000  마우스 이동
    //    TCP 45000  연결 (키보드·클릭·코드 확인)
    //    UDP·TCP 45002  화면 보기 (영상. 어느 쪽을 쓸지는 영상 쪽이 정해서 둘 다 연다)
    //    TCP 45003  파일 보내기·받기
    //
    //  확인은 관리자 권한 없이 된다. 여는 것만 관리자 권한이 필요하다.
    // ─────────────────────────────────────────────────────────────
    public enum FirewallStatus
    {
        Allowed,    // 지금 네트워크에서 필요한 포트가 모두 들어올 수 있음
        Missing,    // 허용 규칙이 없음 (조용히 막힘)
        Blocked,    // 차단 규칙이 있음 (허용 규칙이 있어도 차단이 이긴다)
        Unknown,    // 확인하지 못함 (방화벽 정보를 못 읽음)
    }

    // 방화벽 규칙 하나를 판단에 필요한 만큼만 옮겨 담은 것.
    // Windows 에서 읽은 값과 시험용 값을 똑같이 다루려고 따로 뺐다.
    public record FirewallRuleInfo(
        string Name,
        bool Enabled,
        bool Inbound,
        bool Allow,
        int Profiles,        // 1 도메인, 2 개인, 4 공용 (여러 개면 더한 값)
        int Protocol,        // 6 TCP, 17 UDP, 256 전부
        string? LocalPorts,  // "45000,45001", "45000-45010", "*" 등. 비어 있으면 전부
        string? Program,     // 비어 있으면 모든 프로그램
        string? Service);    // 비어 있지 않으면 특정 서비스 전용 규칙 → 우리와 무관

    public static class FirewallHelper
    {
        private const int ProtoTcp = 6;
        private const int ProtoUdp = 17;
        private const int ProtoAny = 256;

        // 우리가 넣는 규칙 이름. 다시 누르면 지우고 새로 넣는다. (중복 방지)
        // internal: 삭제(Uninstaller)가 같은 이름으로 지운다.
        internal const string UdpRuleName = "MultiPcKvm (UDP)";
        internal const string TcpRuleName = "MultiPcKvm (TCP)";

        // 들어와야 하는 (프로토콜, 포트)
        private static readonly (int Protocol, int Port)[] Needed =
        {
            (ProtoUdp, Constants.DiscoveryPort),
            (ProtoUdp, Constants.InputPort),
            (ProtoTcp, Constants.InputPort),

            // 화면 보기(영상). TCP·UDP 중 어느 쪽을 쓸지는 영상 쪽이 정하므로 둘 다 연다.
            (ProtoUdp, Constants.VideoPort),
            (ProtoTcp, Constants.VideoPort),

            // 파일 보내기·받기
            (ProtoTcp, Constants.FileTransferPort),
        };


        // ═════════════════════════════════════════════════════
        //  확인 (관리자 권한 필요 없음)
        // ═════════════════════════════════════════════════════

        // detail: 기록 파일에 남길 한 줄 설명
        public static FirewallStatus Check(out string detail)
        {
            try
            {
                // Windows 방화벽 설정 객체 (COM). 규칙 읽기는 일반 권한으로 된다.
                Type? type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");

                if (type == null)
                {
                    detail = "방화벽 정보를 읽을 수 없음 (HNetCfg.FwPolicy2 없음)";
                    return FirewallStatus.Unknown;
                }

                dynamic policy = Activator.CreateInstance(type)!;

                // 지금 붙어 있는 네트워크의 종류. (둘 이상이면 더한 값. 예: 개인+공용 = 6)
                int activeProfiles = (int)policy.CurrentProfileTypes;

                // 방화벽이 꺼져 있는 종류는 검사할 필요가 없다.
                int checkProfiles = 0;

                foreach (int bit in new[] { 1, 2, 4 })
                {
                    if ((activeProfiles & bit) != 0 && IsFirewallOn(bit))
                        checkProfiles |= bit;
                }

                var rules = new List<FirewallRuleInfo>();

                foreach (dynamic r in policy.Rules)
                {
                    // 규칙 하나를 못 읽어도 나머지는 계속 읽는다.
                    // (규칙 종류에 따라 일부 값을 읽을 때 오류가 나는 경우가 있다)
                    try
                    {
                        rules.Add(new FirewallRuleInfo(
                            Name: (string)(r.Name ?? ""),
                            Enabled: (bool)r.Enabled,
                            Inbound: (int)r.Direction == 1,
                            Allow: (int)r.Action == 1,
                            Profiles: (int)r.Profiles,
                            Protocol: (int)r.Protocol,
                            LocalPorts: (string?)r.LocalPorts,
                            Program: (string?)r.ApplicationName,
                            Service: (string?)r.serviceName));
                    }
                    catch
                    {
                        // 이 규칙은 건너뛴다
                    }
                }

                string exe = Environment.ProcessPath ?? "";

                FirewallStatus status = Evaluate(rules, checkProfiles, exe, out string why);

                detail = $"{status} | 네트워크 종류 {ProfileName(activeProfiles)} | 규칙 {rules.Count}개 | {why}";
                return status;
            }
            catch (Exception ex)
            {
                detail = "방화벽 확인 실패 | " + ex.Message;
                return FirewallStatus.Unknown;
            }
        }

        // 규칙 목록만 보고 판단한다. (Windows 와 상관없는 순수 계산이라 따로 시험할 수 있다)
        //
        // checkProfiles: 검사할 네트워크 종류. 0 이면 방화벽이 전부 꺼져 있다는 뜻 → 허용
        //
        // 판단 순서:
        //   1. 우리 포트에 걸리는 차단 규칙이 하나라도 있으면 → Blocked  (차단이 허용보다 우선)
        //   2. 검사할 네트워크 종류마다, 포트 셋 모두 허용 규칙이 있으면 → Allowed
        //   3. 그 밖 → Missing
        public static FirewallStatus Evaluate(
            IEnumerable<FirewallRuleInfo> rules,
            int checkProfiles,
            string exePath,
            out string why)
        {
            if (checkProfiles == 0)
            {
                why = "방화벽 꺼져 있음";
                return FirewallStatus.Allowed;
            }

            var list = rules.Where(r => r.Enabled && r.Inbound && string.IsNullOrEmpty(r.Service)).ToList();

            foreach (int bit in new[] { 1, 2, 4 })
            {
                if ((checkProfiles & bit) == 0)
                    continue;

                foreach (var (proto, port) in Needed)
                {
                    FirewallRuleInfo? block = list.FirstOrDefault(r =>
                        !r.Allow && Applies(r, bit, proto, port, exePath));

                    if (block != null)
                    {
                        why = $"차단 규칙 '{block.Name}' ({ProtoName(proto)} {port}, {ProfileName(bit)})";
                        return FirewallStatus.Blocked;
                    }
                }
            }

            foreach (int bit in new[] { 1, 2, 4 })
            {
                if ((checkProfiles & bit) == 0)
                    continue;

                foreach (var (proto, port) in Needed)
                {
                    bool allowed = list.Any(r => r.Allow && Applies(r, bit, proto, port, exePath));

                    if (!allowed)
                    {
                        why = $"허용 규칙 없음 ({ProtoName(proto)} {port}, {ProfileName(bit)})";
                        return FirewallStatus.Missing;
                    }
                }
            }

            why = "필요한 포트 모두 허용";
            return FirewallStatus.Allowed;
        }

        // 이 규칙이 (네트워크 종류, 프로토콜, 포트, 우리 프로그램) 에 적용되는가
        private static bool Applies(FirewallRuleInfo r, int profileBit, int proto, int port, string exePath)
        {
            if ((r.Profiles & profileBit) == 0)
                return false;

            if (r.Protocol != ProtoAny && r.Protocol != proto)
                return false;

            // 특정 프로그램 전용 규칙이면 우리 프로그램일 때만 적용된다.
            if (!string.IsNullOrEmpty(r.Program))
            {
                string rulePath = Environment.ExpandEnvironmentVariables(r.Program);

                if (!string.Equals(rulePath, exePath, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return PortMatches(r.LocalPorts, port);
        }

        // "45000,45001", "45000-45010", "*", "Any", 빈 값 을 이해한다.
        // 그 밖의 특수 값(RPC 등)은 우리 포트와 무관한 것으로 본다.
        private static bool PortMatches(string? ports, int port)
        {
            if (string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*" ||
                ports.Trim().Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string raw in ports.Split(','))
            {
                string part = raw.Trim();
                int dash = part.IndexOf('-');

                if (dash > 0 &&
                    int.TryParse(part[..dash], out int from) &&
                    int.TryParse(part[(dash + 1)..], out int to))
                {
                    if (port >= from && port <= to)
                        return true;
                }
                else if (int.TryParse(part, out int single) && single == port)
                {
                    return true;
                }
            }

            return false;
        }

        // 그 네트워크 종류의 방화벽이 켜져 있는가. (레지스트리, 관리자 권한 필요 없음)
        // 읽지 못하면 켜져 있다고 본다. (확인을 건너뛰는 쪽보다 안전하다)
        private static bool IsFirewallOn(int profileBit)
        {
            string key = profileBit switch
            {
                1 => "DomainProfile",
                2 => "StandardProfile",
                _ => "PublicProfile",
            };

            try
            {
                object? v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + key,
                    "EnableFirewall", 1);

                return v is not int i || i != 0;
            }
            catch
            {
                return true;
            }
        }


        // ═════════════════════════════════════════════════════
        //  열기 (관리자 권한 필요 → Windows 확인 창이 한 번 뜬다)
        // ═════════════════════════════════════════════════════

        // 성공하면 true. 사용자가 확인 창에서 [아니요] 를 누르면 cancelled = true.
        //
        // 하는 일:
        //   1. 우리 프로그램에 걸린 "들어오는 방향 차단" 규칙을 지운다.
        //      (Windows 확인 창에서 취소하면 생기는 규칙. 이게 있으면 허용 규칙이 무시된다)
        //   2. 예전에 넣은 우리 규칙을 지우고 (중복 방지)
        //   3. 포트 규칙을 새로 넣는다. 모든 네트워크 종류, 같은 망에서 온 것만 허용.
        //      포트로 여는 이유: 프로그램 경로로 열면 폴더를 옮기거나 새로 받을 때마다 다시 막힌다.
        //      "같은 망에서 온 것만" 이라 인터넷 쪽에서는 들어올 수 없다.
        public static bool Fix(out bool cancelled, out string error)
        {
            cancelled = false;
            error = "";

            string exe = (Environment.ProcessPath ?? "").Replace("'", "''");

            string script = $@"
$ErrorActionPreference = 'SilentlyContinue'
Get-NetFirewallApplicationFilter -Program '{exe}' | Get-NetFirewallRule |
    Where-Object {{ $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' }} | Remove-NetFirewallRule
Remove-NetFirewallRule -DisplayName '{UdpRuleName}','{TcpRuleName}'
New-NetFirewallRule -DisplayName '{UdpRuleName}' -Direction Inbound -Protocol UDP -LocalPort {Constants.InputPort},{Constants.DiscoveryPort},{Constants.VideoPort} -RemoteAddress LocalSubnet -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName '{TcpRuleName}' -Direction Inbound -Protocol TCP -LocalPort {Constants.InputPort},{Constants.VideoPort},{Constants.FileTransferPort} -RemoteAddress LocalSubnet -Action Allow -Profile Any | Out-Null
";

            // 스크립트를 임시 파일로 저장하지 않고 명령줄에 그대로 넘긴다.
            // 임시 파일이면 관리자 권한으로 실행되기 직전에 다른 프로그램이 내용을 바꿔치기할 수 있다.
            // -EncodedCommand 는 UTF-16 을 Base64 로 바꾼 값을 받는다. (따옴표 문제도 없다)
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                Verb = "runas",               // 관리자 권한으로 실행 → Windows 확인 창
                UseShellExecute = true,       // runas 는 이게 true 여야 동작한다
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            try
            {
                using Process? p = Process.Start(info);

                if (p == null)
                {
                    error = "실행하지 못했습니다.";
                    return false;
                }

                // 규칙 목록이 많으면 10초 넘게 걸리기도 한다. 넉넉히 기다린다.
                if (!p.WaitForExit(60_000))
                {
                    error = "시간이 너무 오래 걸립니다.";
                    return false;
                }

                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 1223 = 사용자가 확인 창에서 [아니요] 를 누름
                cancelled = true;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }


        // ── 표시용 ────────────────────────────────────────────

        private static string ProtoName(int proto) => proto == ProtoTcp ? "TCP" : proto == ProtoUdp ? "UDP" : "전부";

        public static string ProfileName(int profiles)
        {
            var names = new List<string>();
            if ((profiles & 1) != 0) names.Add("도메인");
            if ((profiles & 2) != 0) names.Add("개인");
            if ((profiles & 4) != 0) names.Add("공용");
            return names.Count == 0 ? "없음" : string.Join("+", names);
        }
    }
}
