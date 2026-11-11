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
    //  창의 동작 부분. 모양은 MainWindow.xaml 에 있다.
    //
    //  할 일이 많아서 주제별로 파일을 나눴다. (partial = 한 클래스를 여러 파일에 나눠 쓰기)
    //
    //    MainWindow.xaml.cs            필드(공용 상태) · 생성자 · 시작(StartServices) · Tick
    //    MainWindow.Discovery.cs       검색 · 기기 목록 · 주소 고르기
    //    MainWindow.Store.cs           저장된 기기 (DB) · 삭제
    //    MainWindow.Layout.cs          화면 배치
    //    MainWindow.Pairing.cs         대기 상태 · 메시지 받기 · 연결 승인(코드·열쇠)
    //    MainWindow.Connection.cs      연결 · 끊기 · 다시 잇기 · 자동 연결
    //    MainWindow.Settings.cs        설정 · 트레이 · 종료 · 관리자 권한
    //    MainWindow.Firewall.cs        방화벽
    //    MainWindow.ScreenView.cs      화면 보기 (상대 화면 보면서 조종)
    //    MainWindow.FileTransfer.cs    파일 보내기 · 받기 (화면 보기 창의 버튼)
    //    DiagLog.cs                    진단 기록 (kvm-log.txt)
    //    CursorHider.cs                커서가 상대 PC 로 넘어가 있는 동안 내 PC 커서 숨기기
    //
    //  나눌 때 코드는 옮기기만 하고 한 줄도 바꾸지 않았다.
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow : Window
    {
        // 화면에 보이는 기기 목록.
        //
        // ObservableCollection 을 쓰는 이유:
        //   항목을 넣거나 빼면 화면이 저절로 따라 바뀐다.
        //   일반 List 를 쓰면 매번 직접 다시 그려줘야 한다.
        private readonly ObservableCollection<DeviceItem> _devices = new();

        private LanDiscovery? _discovery;

        // 신뢰 기기 저장소 (SQLite). 열기에 실패하면 null 이고,
        // 그래도 연결 기능은 그대로 동작한다. 저장만 안 될 뿐이다.
        private DeviceRepository? _store;

        // 상대와의 연결.
        //
        // 기기를 발견하면 곧바로 만들어서 "대기 상태" 로 둔다.
        // 대기 상태에서는 후킹을 걸지 않고, 받아서 실행만 한다.
        //
        // 이렇게 해야 상대가 [연결] 을 눌렀을 때 받아줄 사람이 있다.
        // 양쪽 다 [연결] 을 눌러야만 통하는 구조면 아무도 못 붙는다.
        private PeerConnection? _peer;
        private DeviceItem? _peerItem;

        // 대기 상태에서 받은 입력을 실행하는 쪽.
        // SafeInputSender 로 감싸서 연결이 끊기면 눌린 키를 풀어준다.
        private SafeInputSender? _receiver;

        // 마지막으로 진짜 입력을 받은 시각.
        // 최근에 받았다면 상대가 나를 조종 중이라는 뜻이다.
        private DateTime _lastInputAt = DateTime.MinValue;

        // 내가 조종하는 중일 때만 만들어진다. (후킹 포함)
        private KvmSession? _session;
        private DeviceItem? _connectedItem;

        private readonly DispatcherTimer _timer = new DispatcherTimer();

        private int _myWidth;
        private int _myHeight;

        // 이 PC 를 조종하려면 입력해야 하는 4자리 코드.
        //
        // 프로그램을 켤 때마다 새로 뽑는다.
        // 같은 공유기에 붙은 사람이면 누구나 검색에 잡히기 때문에,
        // 이게 없으면 남의 PC 를 마음대로 조종할 수 있게 된다.
        private readonly int _myCode = Random.Shared.Next(1000, 10000);

        // 코드를 보내고 답을 기다릴 때 쓴다.
        //
        // TaskCompletionSource = "나중에 채워질 결과 상자".
        //   답이 올 때까지 기다리다가, 네트워크 스레드에서 답이 도착하면
        //   그때 결과를 넣어준다. 기다리던 쪽이 깨어난다.
        private TaskCompletionSource<int>? _pairWaiter;

        // 안내 문구를 잠깐 붙잡아두는 시각.
        // 오류 메시지가 0.3초마다 덮어써져 사라지는 걸 막는다.
        private DateTime _hintHoldUntil = DateTime.MinValue;

        // 상대가 나에게 걸어와서 코드까지 통과한 상태인지.
        //
        // 이게 켜져 있는 동안 내가 반대로 연결을 걸면 그 줄이 끊긴다.
        // 이유는 ConnectTo 안에 적어뒀다.
        private bool _incomingPaired;

        // 마지막으로 통과한 확인 수단 (코드 또는 저장된 기기 열쇠). 끊겼을 때 다시 붙는 데 쓴다.
        // null 은 아직 없다는 뜻. (일부러 끊었을 때도 null 로 지워서 다시 붙지 않게 한다)
        private PairAuth? _lastAuth;

        // 연결 확인에 쓴 것. 코드(4자리)이거나 저장된 기기 열쇠(64비트)다.
        private sealed record PairAuth(bool IsToken, long Value);

        // 지금 떠 있는 코드 입력 창. 없으면 null.
        // 다른 기기가 먼저 이 PC 에 연결하면 이 창을 닫는다. (HandleIncomingPair)
        private PairCodeWindow? _pairDialog;

        // 재연결이 진행 중인지. 0.3초마다 겹쳐서 도는 걸 막는다.
        private bool _reconnecting;
        private int _reconnectCount;

        public MainWindow()
        {
            InitializeComponent();

            _myWidth = GetSystemMetrics(0);
            _myHeight = GetSystemMetrics(1);

            MyNameText.Text = Environment.MachineName;
            MyInfoText.Text = $"{_myWidth} × {_myHeight}";
            MyCodeText.Text = _myCode.ToString();

            DeviceList.ItemsSource = _devices;

            // 0.3초마다 상태를 갱신한다.
            //
            // DispatcherTimer 를 쓰는 이유:
            //   화면의 글자를 바꾸는 일은 반드시 화면 담당 스레드에서 해야 한다.
            //   다른 스레드에서 건드리면 프로그램이 죽는다.
            _timer.Interval = TimeSpan.FromMilliseconds(300);
            _timer.Tick += (s, e) => Tick();
            _timer.Start();

            // 트레이 아이콘. 왼쪽 클릭 → 창 띄우기, 오른쪽 클릭 메뉴 [종료] → 프로그램 종료
            _tray = new TrayIcon();
            _tray.ShowRequested += ShowFromTray;
            _tray.ExitRequested += ExitApp;

            // 창이 처음 화면에 뜰 때 준비 작업을 한다.
            // (트레이에 숨긴 채로 시작하면 창이 안 뜨므로, 그때는 App 이 StartServices 를 직접 부른다)
            Loaded += (s, e) => StartServices();

            // 이 창이 다시 선택되면, 방화벽이 아직 허용이 아닐 때 바로 다시 확인한다.
            // (처음 실행 때 Windows 확인 창에서 [허용] 을 누르고 돌아온 경우. MainWindow.Firewall.cs)
            Activated += (s, e) => RecheckFirewallIfNeeded(3_000);

            // 창이 완전히 닫히면 프로그램을 끝낸다.
            // App.xaml 의 ShutdownMode 가 OnExplicitShutdown 이라 직접 불러야 한다.
            // (트레이로 숨기는 동안 창이 없어도 프로그램이 꺼지지 않게 하려고 그렇게 했다)
            Closed += (s, e) => ExitApp();
        }


        // ── 검색 시작 ─────────────────────────────────────────

        // 이미 준비했는가. (창을 숨겼다 다시 띄우면 Loaded 가 또 오므로 한 번만 하게)
        private bool _started;

        // 준비 작업: 기록 시작 → DB → 설정 → 방화벽 확인 → 검색 시작
        //
        // 예전에는 창이 뜰 때(Loaded) 했는데, 트레이에 숨긴 채로 시작하면 창이 뜨지 않는다.
        // 그래서 따로 빼서, 창이 뜰 때도 부르고 숨긴 채 시작할 때도 App 이 직접 부른다.
        //
        // async void 지만 DB 열기(OpenStore)와 설정 읽기는 첫 await 전에 끝난다.
        // 그래서 이 함수가 돌아온 직후 App 이 StartHiddenEnabled 를 읽어도 된다.
        public async void StartServices()
        {
            if (_started)
                return;

            _started = true;

            // 저장된 기기를 먼저 목록에 띄운다. (검색보다 먼저)
            //
            // 순서가 중요한 이유:
            //   검색이 먼저 돌면 같은 기기가 "새로 찾은 기기" 로 먼저 들어가고,
            //   그다음 저장된 기기가 또 들어가서 두 줄이 된다.
            DiagLog.Start();

            OpenStore();

            // 설정(자동 실행·트레이·마지막 연결 기기)을 읽어 화면 체크박스에 반영한다.
            LoadSettings();

            // 화면 보내기 준비. (영상 기능이 있을 때만. 나를 조종 중인 PC 가 요청하면 보낸다)
            StartScreenSharing();

            // 파일 받기 준비. (파일 전송 기능이 있을 때만. 조종 관계인 상대가 보낸 파일만 받는다)
            StartFileTransfer();

            // 방화벽이 우리 포트를 막고 있는지 확인한다. (화면 아래 "방화벽: ..." 줄)
            // 규칙을 수백 개 읽느라 시간이 걸릴 수 있어서 기다리지 않고 따로 돌린다.
            _ = CheckFirewallAsync();

            // 네트워크가 바뀌면 방화벽을 다시 확인하도록 알림을 받는다. (Tick 9번)
            WatchNetworkChanges();

            try
            {
                _discovery = new LanDiscovery();

                // 기기를 찾을 때마다 불린다.
                //
                // 주의: 이 이벤트는 네트워크 스레드에서 발생한다.
                //       화면을 직접 건드리면 안 되므로 Dispatcher 로 넘긴다.
                _discovery.PcDiscovered += pc =>
                {
                    // BeginInvoke: 화면 스레드에 맡기고 바로 돌아온다.
                    // (예전 Invoke 는 화면 스레드가 처리할 때까지 검색 받기 스레드를 붙잡아 두었다.
                    //  창이 바쁠 때 다음 대답 받기가 그만큼 밀렸다) 순서는 그대로 지켜진다.
                    Dispatcher.BeginInvoke(() => OnDeviceFound(pc));
                };

                // 검색 도중 오류가 나면 화면에 띄운다.
                //
                // 예전에는 검색이 조용히 실패해서 화면에 "검색 중..." 만 떠 있었다.
                // 원인을 알 수가 없어서 cmd 로 포트를 직접 확인해야 했다.
                // 김기령님이 오류를 밖으로 알려주게 바꿔줘서 여기서 받는다.
                //
                // 이것도 네트워크 스레드에서 오므로 화면 담당 스레드로 넘긴다.
                _discovery.ErrorOccurred += (where, ex) =>
                {
                    DiagLog.Write($"검색 오류 | {where} | {ex.GetType().Name}: {ex.Message}");

                    Dispatcher.InvokeAsync(() =>
                        ShowHint($"{where}: {ex.Message}", 8));
                };

                // StartAsync 는 실패하면 예외를 던진다. (아래 catch 로 온다)
                // 흔한 원인: 45001 포트를 다른 프로그램이 쓰는 중
                await _discovery.StartAsync(Constants.InputPort);

                DiagLog.Write($"검색 시작 | 내 ID {_discovery.DeviceId}");

                // 이 PC 의 고정 ID 를 파일에 저장하지 못했으면 알려준다.
                // 동작은 하지만, 다음에 켤 때 ID 가 바뀌어서
                // 상대 목록에 이 PC 가 새 기기처럼 뜰 수 있다.
                if (DeviceIdentity.LastError != null)
                    ShowHint(DeviceIdentity.LastError, 8);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"검색 시작 실패 | {ex.GetType().Name}: {ex.Message}");
                ShowHint("기기 검색을 시작하지 못했습니다: " + ex.Message, 10);
            }
        }

        // ── 주기 작업 ─────────────────────────────────────────

        private void Tick()
        {
            // 0) 지금 연결이 살아 있는가. (내가 조종 중이든, 조종당하는 중이든)
            //    이 동안에는 검색을 쉰다. 아래 6) 에 이유를 적어뒀다.
            bool linkBusy = IsLinkBusy();

            // 파일을 보내는 도중 연결이 끊겼으면 보내기를 멈춘다. (MainWindow.FileTransfer.cs)
            CheckFileSendLink();

            // 1) 한동안 응답이 없는 기기는 목록에서 지운다.
            //    PC 가 꺼졌거나 프로그램을 닫았을 수 있다.
            //    단, 지금 연결 중인 기기는 남겨둔다.
            //
            //    연결 중에는 아예 지우지 않는다.
            //    검색을 쉬는 동안에는 대답도 안 오니까, 그대로 두면
            //    12초 뒤 멀쩡한 기기가 목록에서 사라진다.
            //    (특히 조종당하는 쪽은 상대 항목이 "연결됨" 표시가 없어서 지워진다)
            if (!linkBusy)
            {
                DateTime limit = DateTime.Now.AddSeconds(-12);

                for (int i = _devices.Count - 1; i >= 0; i--)
                {
                    DeviceItem d = _devices[i];

                    if (d.IsConnected || d.LastSeen >= limit)
                        continue;

                    // 저장된 기기는 지우지 않고 "오프라인" 으로만 바꾼다.
                    // 다음에 켜지면 검색에 잡혀서 다시 온라인이 된다.
                    if (d.IsSaved)
                    {
                        if (d.IsOnline)
                            DiagLog.Write($"오프라인 처리 | {d.Name} {d.IpAddress} | 12초 넘게 대답 없음");

                        d.IsOnline = false;
                        d.StatusText = "오프라인 · 마지막 IP " + d.IpAddress;
                    }
                    else
                    {
                        DiagLog.Write($"목록에서 뺌 | {d.Name} {d.IpAddress} | 12초 넘게 대답 없음");
                        _devices.RemoveAt(i);
                    }
                }
            }

            EmptyPanel.Visibility = _devices.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            SearchingText.Text = linkBusy
                ? "연결 중 · 검색 쉬는 중"
                : _devices.Count == 0
                    ? "검색 중..."
                    : $"{_devices.Count}대 발견";

            // 2) 줄이 끊겼으면 "누가 걸었는지" 기억도 지운다.
            //    안 지우면 상대가 프로그램을 껐는데도 계속 조종당하는 줄 안다.
            if (_peer == null || !_peer.IsTcpConnected)
            {
                _incomingPaired = false;

                // 누가 연결했는지(Hello)도 그 줄과 함께 잊는다.
                // 다음 줄에서 Hello 없이 들어온 요청을 예전 기기로 착각하지 않게.
                lock (_helloLock)
                {
                    _helloHigh = null;
                    _claimedPeerId = null;
                }
            }

            // 조종당하던 연결이 끝났으면 연결 기록을 닫는다. (6주차, MainWindow.ConnectionLog.cs)
            CheckIncomingLog();

            // 3) 상대가 나를 조종 중인지 확인한다.
            bool beingControlled = IsBeingControlled();

            foreach (DeviceItem d in _devices)
            {
                bool isPeer = _peerItem != null && d.DeviceId == _peerItem.DeviceId;

                // 이 기기가 지금 나를 조종 중인가 (코드까지 통과한 상대)
                d.IsControllingMe = isPeer && _incomingPaired && _session == null;

                // 이 줄이 지금 연결 상대인가 (내가 조종 중이든, 나를 조종 중이든)
                bool isMyLink = d.IsConnected || d.IsControllingMe;

                // 버튼을 잠그는 경우 두 가지
                //   · 연결 중(조종하는 쪽·조종당하는 쪽 둘 다) → 연결 상대가 아닌 모든 버튼
                //     연결 상대의 [연결 끊기] 만 열어둔다. 양쪽 다 언제든 끊을 수 있어야 안전하다.
                //     예전에는 조종하는 쪽은 다른 기기를 누르면 지금 연결을 끊고 갈아탔다.
                //     실수로 누르면 조종이 끊기므로 막았다. 다른 기기로 가려면 먼저 끊는다.
                //   · 꺼져 있는 기기(회색) → 그 줄의 [연결] 버튼
                //     마지막으로 저장된 IP 로 붙으려다 실패하기 때문이다.
                //     네트워크를 바꾸면 IP 가 달라져 있다.
                //     검색에 다시 잡히면 새 IP 로 바뀌고 버튼도 풀린다.
                bool anyLink = _session != null || beingControlled;

                d.IsLocked = !isMyLink && (anyLink || !d.IsOnline);

                if (isPeer && beingControlled)
                    d.StatusText = d.IpAddress + " · 이 기기가 내 PC 를 조종 중";
                else if (!d.IsConnected && d.IsOnline)
                    d.StatusText = d.IsSaved
                        ? d.IpAddress + " · 저장된 기기"
                        : d.IpAddress;

                // 꺼진 기기(IsOnline = false)의 글자는 여기서 건드리지 않는다.
                // 켤 때 넣어둔 "마지막 연결 9/28 14:03" 같은 정보가 지워지지 않게 하려는 것이다.
            }

            // 잠깐 보여줘야 할 안내가 있으면 그게 사라질 때까지 덮어쓰지 않는다.
            // 안 그러면 0.3초마다 지워져서 오류 메시지를 볼 수가 없다.
            if (DateTime.Now >= _hintHoldUntil)
            {
                HintText.Text = beingControlled
                        ? "상대가 이 PC 를 조종하고 있습니다. 그 기기의 [연결 끊기] 를 누르면 이 PC 에서도 끊을 수 있습니다."
                        : _session != null
                            ? CrossHint(_session.Router.EffectivePlacement.Side)
                            : "연결할 기기의 [연결] 을 누르세요.";
            }

            // 4) 내가 조종 중이면 상태를 보여준다.
            if (_session != null && _connectedItem != null)
            {
                var m = _session.Monitor;

                string state = m.State switch
                {
                    LinkState.Healthy => "연결됨",
                    LinkState.Unstable => "불안정",
                    _ => "끊김"
                };

                string latency = m.AvgRttMs < 0
                    ? "측정 중"
                    : $"{m.AvgRttMs:F1}ms";

                string where = _session.Router.Location == CursorLocation.Remote
                    ? " · 커서가 이 기기에 있음"
                    : "";

                // 상대 화면 크기를 같이 보여준다.
                //
                // 이게 실제 상대 해상도와 다르면 커서가 화면 끝까지 못 가거나
                // 화면 밖으로 나가려 한다. 눈으로 바로 확인할 수 있게 띄운다.
                string screen =
                    $"{_session.Router.RemoteWidth}×{_session.Router.RemoteHeight}";

                // 전송 실패가 있으면 그걸 먼저 보여준다.
                //
                // 삼키기는 켜졌는데 상대가 안 움직이는 증상의 원인이
                // 대부분 여기에 있다. 안 보이면 원인을 못 찾는다.
                string fail = _session.SendFailures > 0
                    ? $" · 전송실패 {_session.SendFailures}"
                    : "";

                _connectedItem.StatusText =
                    $"{screen} · {state} · {latency}{where}{fail}";

                if (_session.SendFailures > 0 &&
                    !string.IsNullOrEmpty(_session.LastSendError) &&
                    DateTime.Now >= _hintHoldUntil)
                {
                    HintText.Text = "전송 오류: " + _session.LastSendError;
                }
            }

            // 5) 조종 중인데 줄이 끊겼으면 알아서 다시 붙는다.
            //
            //    끊기는 이유는 여러 가지다. 공유기가 잠깐 말썽이거나,
            //    상대가 프로그램을 다시 켰거나, 노트북이 잠들었다가 깼거나.
            //    그때마다 사용자가 [연결 끊기] → [연결] → 코드 입력을
            //    다시 하게 만들 이유가 없다.
            if (_session != null &&
                _peer != null &&
                !_peer.IsTcpConnected &&
                !_reconnecting)
            {
                _reconnectCount++;

                if (_reconnectCount >= 7)       // 0.3초 × 7 ≈ 2초마다 한 번
                {
                    _reconnectCount = 0;
                    _ = ReconnectAsync();
                }
            }
            else
            {
                _reconnectCount = 0;
            }

            // 6) 재검색 요청 (LanDiscovery 가 주기적으로 뿌리게 한다)
            _rediscoverCount++;

            if (_rediscoverCount >= 10)     // 0.3초 × 10 = 3초
            {
                _rediscoverCount = 0;

                // 연결이 살아 있는 동안에는 검색을 쉰다.
                //
                // 검색은 이제 브로드캐스트 1개 + 같은 망 IP 250여 개에 하나씩 보낸다.
                // 없는 IP 로 보내면 Windows 가 먼저 "이 IP 누구야?"(ARP) 를
                // 브로드캐스트로 묻는다. 와이파이는 브로드캐스트를 가장 느린 속도로
                // 보내서, 수백 개가 몰리면 그동안 공중을 차지한다.
                // 그 사이 마우스 신호와 Ping 이 줄을 서서 지연이 튄다.
                // (같은 강의실에서 예전 5ms → 검색 추가 후 10~50ms)
                //
                // 연결 중에는 새 기기를 찾을 일이 없으니 쉬어도 잃는 게 없다.
                // 연결이 끊기면(linkBusy = false) 바로 다시 찾기 시작한다.
                //
                // 이전 검색이 아직 돌고 있을 때도 새로 시작하지 않는다.
                // 시험해보니 환경에 따라 한 번에 3초 넘게 걸리기도 했다.
                // 3초마다 무조건 새로 부르면 안 끝난 검색 위에 계속 쌓인다.
                //
                // ★ 단, 10초가 넘도록 안 끝나면 기다리지 않고 새로 시작한다.
                //   예전에는 시간 제한이 없어서, 검색 한 번이 어떤 이유로 끝나지 않으면
                //   그 뒤로 검색이 영영 멈췄다. 그러면 먼저 켠 PC 가
                //   나중에 켠 PC 를 끝까지 못 찾아서 회색으로 남는다.
                bool stuck = _discoverTask != null &&
                             !_discoverTask.IsCompleted &&
                             _discoverWatch.Elapsed.TotalSeconds > 10;

                if (stuck)
                    DiagLog.Write($"검색 #{_discoverRound} 이 {_discoverWatch.Elapsed.TotalSeconds:F0}초째 안 끝남 → 기다리지 않고 새로 시작");

                if (!linkBusy &&
                    _discovery != null &&
                    (_discoverTask == null || _discoverTask.IsCompleted || stuck))
                {
                    StartDiscoverRound();
                }
            }

            // 7) 대기 상태일 때도 내 화면 크기를 주기적으로 알려준다.
            //
            //    상대가 보내온 것에 답만 하면, 그 첫 메시지가 유실됐을 때
            //    영영 서로의 크기를 모르게 된다.
            //    양쪽이 각자 보내면 한쪽만 도착해도 복구된다.
            _screenInfoCount++;

            if (_screenInfoCount >= 7)      // 0.3초 × 7 ≈ 2초
            {
                _screenInfoCount = 0;

                if (_session == null && _peer?.IsTcpConnected == true)
                    _ = SendMyScreenInfoAsync();

                // 상대 화면 크기를 새로 알게 됐으면 화면 배치 DB 에 저장한다.
                PersistRemoteSize();
            }

            // 8) 켜자마자 마지막으로 조종한 기기에 자동 연결 (조건이 맞을 때만)
            TryAutoConnect();

            // 9) 방화벽 상태를 다시 확인한다.
            //
            //    와이파이 → 핫스팟처럼 네트워크를 바꾸면 네트워크 종류(개인/공용)가 바뀌어서
            //    아까는 허용이던 게 지금은 막힐 수 있다. 프로그램을 다시 켜지 않아도 알 수 있게 한다.
            //
            //    · 네트워크가 바뀌면 3초 뒤에 바로 확인한다. (바뀐 직후에는 종류가 아직 안 정해졌을 수 있어서 3초)
            //    · 그 밖에는 5분마다 한 번. (누가 방화벽 설정을 직접 바꾼 경우 대비)
            //    예전에는 1분마다 했는데, 규칙 수백 개를 하나씩 읽는 일이라 배터리·CPU 를 계속 썼다.
            //    네트워크가 바뀔 때 바로 보므로 오히려 더 빨리 알아챈다.
            // 켠 직후에는 허용이 아닌 동안 10초마다 다시 본다. (처음 실행 때 Windows 확인 창 대응)
            RecheckFirewallAfterStart();

            _firewallCount++;

            long changedAt = Interlocked.Read(ref _networkChangedAt);
            bool networkChanged = changedAt != 0 && Environment.TickCount64 - changedAt > 3000;

            if (_firewallCount >= 1000 || networkChanged)      // 0.3초 × 1000 = 5분
            {
                _firewallCount = 0;

                // 확인하는 사이 또 바뀌었으면 그 알림은 남겨 둔다. (다음 Tick 에서 한 번 더 본다)
                if (networkChanged)
                    Interlocked.CompareExchange(ref _networkChangedAt, 0, changedAt);

                _ = CheckFirewallAsync();
            }
        }

        private int _rediscoverCount;
        private int _screenInfoCount;
        private int _firewallCount;

        // 지금 돌고 있는 검색. 끝나기 전에는 다음 검색을 시작하지 않는다.
        private Task? _discoverTask;

        // 검색 한 번이 얼마나 걸리는지 재려고 쓴다. (몇 번째 검색인지도)
        private readonly Stopwatch _discoverWatch = new Stopwatch();
        private int _discoverRound;

        // 검색을 한 번 보내고, 끝나면 걸린 시간을 기록한다.
        private void StartDiscoverRound()
        {
            if (_discovery == null)
                return;

            int round = ++_discoverRound;
            _discoverWatch.Restart();

            // 화면 스레드 밖에서 돌린다.
            // 검색 한 번에 패킷을 수백 개 보내는데, 대부분 바로 끝나는 보내기라
            // 화면 스레드에서 부르면 그 전부가 화면 스레드에서 돌아서 창이 잠깐씩 멈췄다.
            LanDiscovery discovery = _discovery;
            Task task = Task.Run(() => discovery.DiscoverAsync());
            _discoverTask = task;

            // 끝났을 때 기록한다. (화면 스레드를 붙잡지 않으려고 ContinueWith)
            // 10번에 한 번, 또는 3초 넘게 걸렸거나 실패했을 때만 적는다.
            // 3초마다 전부 적으면 기록이 너무 길어져서 정작 중요한 줄을 못 찾는다.
            var watch = Stopwatch.StartNew();
            task.ContinueWith(t =>
            {
                double ms = watch.Elapsed.TotalMilliseconds;

                if (t.IsFaulted)
                    DiagLog.Write($"검색 #{round} 실패 | {ms:F0}ms | {t.Exception?.GetBaseException().Message}");
                else if (ms > 3000 || round % 10 == 1)
                    DiagLog.Write($"검색 #{round} 끝 | {ms:F0}ms");
            });
        }

        // 상대가 지금 내 PC 를 조종 중인가.
        //
        // 두 가지로 판단한다.
        //   · 코드까지 통과해서 상대가 붙어 있다  (_incomingPaired)
        //   · 최근 2초 안에 진짜 입력을 받았다
        //
        // _incomingPaired 를 같이 보는 이유:
        //   입력만으로 판단하면, 붙기는 했는데 아직 마우스를 안 넘긴
        //   2~3초 동안은 버튼이 열려 있다. 그 사이에 누르면 줄이 끊긴다.
        private bool IsBeingControlled() =>
            _session == null &&
            (_incomingPaired ||
             (DateTime.Now - _lastInputAt).TotalSeconds < 2);

        // 두 PC 사이 연결이 실제로 살아 있는가.
        //
        // "조종 관계가 있다" 만 보면 안 되고 줄이 살아 있어야 한다.
        // 줄이 끊겨 재연결 중일 때는 검색을 다시 해야 한다.
        // 상대가 프로그램을 다시 켜서 IP 가 바뀌었을 수도 있기 때문이다.
        private bool IsLinkBusy() =>
            _peer != null &&
            _peer.IsTcpConnected &&
            (_session != null || _incomingPaired);


        // 안내 문구를 몇 초간 붙잡아둔다.
        private void ShowHint(string text, int seconds = 4)
        {
            HintText.Text = text;
            _hintHoldUntil = DateTime.Now.AddSeconds(seconds);
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }
}
