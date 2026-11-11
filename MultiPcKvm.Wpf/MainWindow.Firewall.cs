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
    //  MainWindow — 방화벽
    //
    //  방화벽이 우리 포트를 막는지 확인하고, 막혀 있으면 [방화벽 허용] 버튼을 연다.
    //  실제 확인·허용은 FirewallHelper 가 한다.
    //
    //  MainWindow 는 할 일이 많아서 주제별로 파일을 나눴다. (partial = 한 클래스를 여러 파일에 나눠 쓰기)
    //  필드(공용 상태)·생성자·시작·Tick 은 MainWindow.xaml.cs 에 있다.
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // ── 방화벽 ────────────────────────────────────────────
        //
        //  [방화벽 허용] 버튼은 "규칙 없음" 이나 "차단" 일 때만 누를 수 있다.
        //  이미 허용돼 있으면 버튼을 잠근다. (눌러도 할 일이 없으므로)
        //  확인하지 못했을 때(Unknown)도 잠근다. 원인을 모르는데 규칙을 건드리지 않는다.

        // 확인이 겹쳐 돌지 않게 한다. (주기 확인과 버튼 뒤 확인이 동시에 도는 경우)
        private bool _firewallChecking;

        // [방화벽 허용] 을 눌러서 설정하는 중인가. 이 동안에는 버튼을 다시 열지 않는다.
        private bool _firewallFixing;

        // 마지막으로 기록한 확인 결과. 같으면 다시 기록하지 않는다.
        private string? _lastFirewallDetail;

        // 마지막 확인 결과(아직 확인 전이면 null)와 확인한 시각 (Environment.TickCount64)
        private FirewallStatus? _firewallStatus;
        private long _firewallCheckedAt;

        // 프로그램을 켠 시각 (Environment.TickCount64). 켠 직후 몇 분은 방화벽을 자주 다시 확인한다.
        private readonly long _startedAt = Environment.TickCount64;

        // ── 처음 켤 때 Windows 확인 창 문제 ──
        //
        //  처음 실행하면 Windows 가 "이 앱의 일부 기능을 차단했습니다" 창을 띄운다.
        //  그런데 우리 확인(CheckFirewallAsync)은 켜자마자 돌아서, 사용자가 그 창에서 [허용] 을 누르기 "전" 결과를 보여준다.
        //  예전에는 5분 뒤에야 다시 확인해서, [허용] 을 눌렀는데도 "허용 규칙 없음" 이 한참 떠 있었다.
        //
        //  그래서 허용이 아닌 동안에는
        //    · 켠 뒤 3분 동안 10초마다 다시 확인한다. (Tick)
        //    · 이 창이 다시 선택될 때 바로 다시 확인한다. (Windows 확인 창을 닫으면 보통 이 창으로 돌아온다)
        //  허용으로 확인되면 더는 자주 보지 않는다. (규칙 수백 개를 읽는 일이라 계속 하면 낭비)
        //
        //  ※ Windows 확인 창은 고른 네트워크 종류(보통 "개인")만 허용하고 나머지는 차단 규칙을 만든다.
        //    지금 네트워크가 "공용"(학교·카페 와이파이, 랜선 직결 등)이면 [허용] 을 눌러도 차단으로 나오는 게 맞다.
        //    그때는 [방화벽 허용] 버튼을 누르면 모든 네트워크 종류에서 우리 포트를 연다.
        private void RecheckFirewallIfNeeded(int minGapMs)
        {
            if (_firewallStatus is not FirewallStatus status || status == FirewallStatus.Allowed)
                return;     // 아직 첫 확인 전이거나 이미 허용

            if (Environment.TickCount64 - _firewallCheckedAt < minGapMs)
                return;

            _ = CheckFirewallAsync();
        }

        // Tick 에서 부른다. 켠 뒤 3분 동안, 허용이 아니면 10초마다 다시 확인.
        private void RecheckFirewallAfterStart()
        {
            if (Environment.TickCount64 - _startedAt < 180_000)
                RecheckFirewallIfNeeded(10_000);
        }

        // 네트워크가 바뀐 시각 (Environment.TickCount64). 0 이면 바뀐 적 없음. Tick 이 보고 방화벽을 다시 확인한다.
        private long _networkChangedAt;

        // 네트워크가 바뀌면 알려 달라고 Windows 에 부탁한다. (StartServices 에서 한 번)
        // 알림은 다른 스레드에서 오므로 시각만 적어 두고, 확인은 Tick(화면 스레드)이 한다.
        private void WatchNetworkChanges()
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (s, e) =>
                Interlocked.Exchange(ref _networkChangedAt, Environment.TickCount64);

            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (s, e) =>
                Interlocked.Exchange(ref _networkChangedAt, Environment.TickCount64);
        }

        private async Task CheckFirewallAsync()
        {
            // 설정하는 중에는 주기 확인을 건너뛴다. (설정이 끝나면 버튼 쪽에서 다시 확인한다)
            if (_firewallChecking || _firewallFixing)
                return;

            _firewallChecking = true;

            try
            {
                string detail = "";

                // 방화벽 규칙 읽기는 느릴 수 있어서 화면 스레드 밖에서 한다.
                // (화면 스레드에서 하면 그동안 창이 멈춘다)
                FirewallStatus status = await Task.Run(() => FirewallHelper.Check(out detail));

                FirewallStatus? before = _firewallStatus;
                _firewallStatus = status;
                _firewallCheckedAt = Environment.TickCount64;

                // 결과가 바뀌었을 때만 기록한다.
                if (detail != _lastFirewallDetail)
                {
                    DiagLog.Write("방화벽 확인 | " + detail);
                    _lastFirewallDetail = detail;
                }

                // 확인하는 사이에 [방화벽 허용] 을 눌렀으면, 설정 중 문구를 덮어쓰지 않는다.
                if (_firewallFixing)
                    return;

                FirewallText.Text = status switch
                {
                    FirewallStatus.Allowed => "방화벽: 허용됨",
                    FirewallStatus.Missing => "방화벽: 허용 규칙 없음 → [방화벽 허용] 을 눌러 주세요",
                    FirewallStatus.Blocked => "방화벽: 지금 네트워크에서 차단됨 → [방화벽 허용] 을 눌러 주세요",
                    _                      => "방화벽: 확인하지 못했습니다",
                };

                // 마우스를 올리면 자세한 이유를 보여준다. (어느 포트·어느 네트워크 종류에서 막혔는지)
                FirewallText.ToolTip = status == FirewallStatus.Blocked
                    ? detail + "\nWindows 확인 창의 [허용] 은 고른 네트워크 종류만 엽니다. 지금 네트워크가 다른 종류면 차단으로 나옵니다."
                    : detail;

                // 막혀 있다가 허용으로 바뀌었으면 알려준다. (Windows 확인 창에서 [허용] 을 누른 경우 등)
                if (before is FirewallStatus old && old != FirewallStatus.Allowed && status == FirewallStatus.Allowed)
                    ShowHint("방화벽 허용을 확인했습니다.", 4);

                // 규칙이 없거나 막혀 있을 때만 버튼을 누를 수 있다.
                FirewallButton.IsEnabled =
                    status == FirewallStatus.Missing || status == FirewallStatus.Blocked;
            }
            finally
            {
                _firewallChecking = false;
            }
        }

        private async void FirewallButton_Click(object sender, RoutedEventArgs e)
        {
            // 두 번 누르지 못하게 먼저 잠근다.
            _firewallFixing = true;
            FirewallButton.IsEnabled = false;
            FirewallText.Text = "방화벽: 설정 중... Windows 확인 창에서 [예] 를 눌러주세요";

            bool cancelled = false;
            string error = "";

            // 관리자 권한 창(Windows 확인 창)이 떠 있는 동안 기다리므로 화면 스레드 밖에서 한다.
            bool ok = await Task.Run(() => FirewallHelper.Fix(out cancelled, out error));

            DiagLog.Write($"방화벽 허용 시도 | {(ok ? "완료" : cancelled ? "사용자가 취소" : "실패: " + error)}");

            if (cancelled)
                ShowHint("방화벽 설정을 취소했습니다. 다시 하려면 [방화벽 허용] 을 누르세요.", 6);
            else if (!ok)
                ShowHint("방화벽 설정 실패: " + error, 8);

            _firewallFixing = false;

            // 실제로 열렸는지는 다시 읽어서 확인한다. (여기서 버튼 상태도 다시 정해진다)
            // 주기 확인이 막 돌고 있으면 건너뛰어지므로, 끝날 때까지 기다렸다가 확인한다.
            while (_firewallChecking)
                await Task.Delay(100);

            await CheckFirewallAsync();
        }
    }
}
