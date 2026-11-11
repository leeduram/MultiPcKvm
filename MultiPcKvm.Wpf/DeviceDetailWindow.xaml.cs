using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiPcKvm.App;
using MultiPcKvm.App.Storage;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  기기 상세 창 — 동작 (10주차)
    //
    //  두 가지를 보여준다.
    //    · DB 에 저장된 정보 : 기기 정보 · 화면 배치 · 연결 통계 · 최근 연결 기록
    //                         (6주차 GetDeviceDetail. 열 때, 동작 후, [새로 고침]/F5 때 다시 읽는다)
    //    · 지금 상태         : 조종 중 / 상대가 조종 중 / 켜져 있음 / 꺼져 있음
    //                         (메인 창의 기기 목록(DeviceItem)을 0.5초마다 본다)
    //
    //  버튼([연결]/[연결 끊기] · [화면 배치] · [삭제])은 메인 창 기기 목록의 버튼과 같은 코드를 부른다.
    //  새로 만든 동작은 없다. 그래서 확인 창 · 안내 · 막는 조건이 목록에서 누를 때와 똑같다.
    //  상태에 따라 버튼이 켜지고 꺼지며, 꺼진 버튼에 마우스를 올리면 이유가 뜬다.
    // ─────────────────────────────────────────────────────────────
    public partial class DeviceDetailWindow : Window
    {
        private readonly MainWindow _main;
        private readonly Guid _deviceId;
        private readonly ConnectionHistoryClient _client = new();

        // 지금 상태를 다시 보는 주기
        private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

        // 알림(토스트)을 지우는 타이머
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

        // 마지막으로 본 "연결되어 있는가". 바뀌는 순간 알림을 띄우고 기록을 다시 읽는다.
        private bool _wasLinked;

        // 마지막으로 읽어 온 상세. 배치를 바꿨는지 비교할 때 쓴다.
        private DeviceDetail? _detail;

        // 요청 번호. 빠르게 여러 번 다시 읽어도 마지막 결과만 그린다. (9주차 연결 기록 창과 같은 방식)
        private int _requestNo;
        private int _pendingNo;
        private const int LoadingDelayMs = 150;

        public Guid DeviceId => _deviceId;

        public DeviceDetailWindow(MainWindow main, Guid deviceId)
        {
            InitializeComponent();

            _main = main;
            _deviceId = deviceId;

            _stateTimer.Tick += (s, e) => UpdateState();
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                ToastPanel.Visibility = Visibility.Collapsed;
            };

            Loaded += async (s, e) =>
            {
                _wasLinked = IsLinked(_main.FindDeviceItem(_deviceId));
                UpdateState();
                _stateTimer.Start();
                await LoadAsync();
            };

            Closed += (s, e) =>
            {
                _stateTimer.Stop();
                _toastTimer.Stop();
            };
        }


        // ═════════════════════════════════════════════════════
        //  DB 에서 읽기 (상세)
        // ═════════════════════════════════════════════════════

        private async Task LoadAsync()
        {
            int myNo = ++_requestNo;
            _pendingNo = myNo;
            _ = ShowLoadingLaterAsync(myNo);

            DetailResponse response = await _client.GetDeviceDetailAsync(_deviceId);

            if (myNo != _requestNo)
                return;     // 그사이 다시 읽기를 또 눌렀다

            _pendingNo = 0;

            // ── 없는 기기 (404): 삭제됐거나 아직 저장 안 된 기기 ──
            if (response.Code == StoreResult.NotFound)
            {
                _detail = null;
                ShowNotice("저장되지 않은 기기입니다",
                    "삭제됐거나, 아직 연결 코드를 통과하지 않아 저장되지 않은 기기입니다.", showBack: true);
                return;
            }

            // ── 오류 (500 등) ──
            if (response.Detail is not DeviceDetail detail)
            {
                ShowNotice("기기 정보를 불러오지 못했습니다", $"[{(int)response.Code}] {response.Message}", showBack: true);
                return;
            }

            _detail = detail;
            Fill(detail);
            HideNotice();
        }

        // 읽는 데 0.15초 넘게 걸릴 때만 "불러오는 중…" (짧으면 깜빡이지 않게)
        private async Task ShowLoadingLaterAsync(int requestNo)
        {
            await Task.Delay(LoadingDelayMs);

            if (_pendingNo == requestNo && _detail == null)
                ShowNotice("불러오는 중…", "", showBack: false);
        }

        // 읽어 온 상세를 화면에 채운다.
        private void Fill(DeviceDetail detail)
        {
            TrustedDevice d = detail.Device;

            Title = $"기기 상세 — {d.DeviceName}";
            NameText.Text = d.DeviceName;

            InfoText.Text =
                $"마지막 IP {d.LastIp ?? "-"}  ·  처음 연결 {d.PairedAt.ToLocalTime():yyyy-MM-dd HH:mm}  ·  " +
                $"마지막 확인 {(d.LastSeenAt is DateTime seen ? seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-")}  ·  " +
                $"신뢰 {(d.IsTrusted ? "함" : "안 함")}";

            LayoutText.Text = detail.Layout is SavedLayout layout
                ? $"{ScreenLayout.SideName(layout.Placement.Side)}  ·  밀린 양 {layout.Placement.Offset}px  ·  상대 화면 {layout.ScreenWidth}×{layout.ScreenHeight}"
                : "저장된 배치 없음 (연결하면 내 화면 왼쪽, 가운데 맞춤으로 시작합니다)";

            StatsText.Text = detail.ConnectionCount == 0
                ? "연결 기록 없음"
                : $"연결 {detail.ConnectionCount}회  ·  마지막 연결 " +
                  $"{(detail.LastConnectedAt is DateTime last ? last.ToLocalTime().ToString("MM/dd HH:mm") : "-")}  ·  " +
                  $"평균 지연 {(detail.AvgLatencyMs is double ms ? $"{ms:F1}ms" : "-")}  ·  메시지 {detail.TotalMessages:N0}개";

            RecentList.ItemsSource = detail.RecentConnections
                .Select(r => HistoryRow.From(r, d.DeviceName))
                .ToList();

            RecentTitle.Text = detail.ConnectionCount > detail.RecentConnections.Count
                ? $"최근 연결 기록 ({detail.RecentConnections.Count}건 / 전체 {detail.ConnectionCount}건)"
                : $"최근 연결 기록 ({detail.RecentConnections.Count}건)";
        }


        // ═════════════════════════════════════════════════════
        //  지금 상태 (메인 창 기기 목록 기준)
        // ═════════════════════════════════════════════════════

        private static bool IsLinked(DeviceItem? item) =>
            item != null && (item.IsConnected || item.IsControllingMe);

        // 상태 표시와 버튼을 지금 상태에 맞춘다. 0.5초마다 + 동작 직후에 부른다.
        private void UpdateState()
        {
            DeviceItem? item = _main.FindDeviceItem(_deviceId);
            bool linked = IsLinked(item);

            // ── 상태 표시 ──
            (string text, string back, string fore) = item switch
            {
                null => ("메인 목록에 없음", "#E5E7EB", "#374151"),
                { IsConnected: true } => ("조종 중", "#DCFCE7", "#166534"),
                { IsControllingMe: true } => ("상대가 이 PC 를 조종 중", "#FFEDD5", "#9A3412"),
                { IsOnline: true } => ("켜져 있음", "#DBEAFE", "#1E40AF"),
                _ => ("꺼져 있음", "#E5E7EB", "#374151"),
            };

            StateText.Text = text;
            StateBadge.Background = Brush(back);
            StateText.Foreground = Brush(fore);

            // ── [연결] / [연결 끊기] ── (목록 버튼과 같은 글자·같은 잠금)
            ActionButton.Content = item?.ActionText ?? "연결";
            ActionButton.IsEnabled = item != null && item.CanAct;
            ActionButton.ToolTip = item == null
                ? "메인 창 목록에 없는 기기라 연결할 수 없습니다. (검색되지 않았거나 신뢰하지 않는 기기)"
                : item.CanAct
                    ? (linked ? "연결을 끊습니다" : "이 기기에 연결합니다")
                    : !item.IsOnline
                        ? "꺼져 있는 기기입니다. 상대 프로그램이 켜지면 연결할 수 있습니다."
                        : "다른 기기와 연결 중이라 지금은 누를 수 없습니다.";

            // ── [화면 배치] ── 저장된 기기만 (배치는 저장된 기기마다 DB 에 있다)
            LayoutButton.IsEnabled = item is { IsSaved: true };
            LayoutButton.ToolTip = LayoutButton.IsEnabled
                ? "상대 화면이 내 화면 어느 쪽에 있는지 정합니다"
                : "저장된 기기만 화면 배치를 정할 수 있습니다.";

            // ── [삭제] ── 저장된 기기 + 연결 중이 아닐 때
            DeleteButton.IsEnabled = item is { IsSaved: true } && !linked;
            DeleteButton.ToolTip = DeleteButton.IsEnabled
                ? "저장된 기기에서 삭제합니다 (화면 배치 · 연결 기록도 함께 삭제)"
                : linked
                    ? "연결 중인 기기는 삭제할 수 없습니다. 먼저 연결을 끊으세요."
                    : "메인 창 목록의 저장된 기기만 삭제할 수 있습니다.";

            // ── 연결 상태가 바뀌는 순간: 알림 + 기록 다시 읽기 ──
            if (linked != _wasLinked)
            {
                _wasLinked = linked;
                ShowToast(linked ? "연결됐습니다." : "연결이 끊겼습니다. 연결 기록을 다시 불러옵니다.");

                // 끊긴 기록은 끊는 순간(또는 다음 Tick, 0.3초 안)에 DB 에 남는다. 조금 기다렸다 읽는다.
                _ = ReloadLaterAsync(linked ? 0 : 700);
            }
        }

        private async Task ReloadLaterAsync(int delayMs)
        {
            if (delayMs > 0)
                await Task.Delay(delayMs);

            await LoadAsync();
        }

        private static SolidColorBrush Brush(string hex) =>
            new((Color)ColorConverter.ConvertFromString(hex));


        // ═════════════════════════════════════════════════════
        //  버튼 — 메인 창 목록의 버튼과 같은 동작
        // ═════════════════════════════════════════════════════

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_main.FindDeviceItem(_deviceId) is not DeviceItem item)
                return;

            // 연결/끊기는 시간이 걸린다. 결과는 0.5초마다 보는 상태로 알 수 있다. (UpdateState → 알림)
            _main.RunDeviceAction(item);
            UpdateState();
        }

        private async void LayoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (_main.FindDeviceItem(_deviceId) is not DeviceItem item)
                return;

            string before = DescribeLayout(_detail?.Layout);

            _main.RunLayout(item);      // 배치 창 (닫힐 때까지 기다린다)

            await LoadAsync();

            if (DescribeLayout(_detail?.Layout) != before)
                ShowToast("화면 배치를 저장했습니다.");
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_main.FindDeviceItem(_deviceId) is not DeviceItem item)
                return;

            _main.RunDelete(item);      // 확인 창 → 삭제 (아니오를 누르면 아무것도 안 함)

            if (!item.IsSaved)
            {
                ShowToast("기기를 삭제했습니다. 화면 배치와 연결 기록도 함께 지워졌습니다.");
                UpdateState();
                await LoadAsync();      // 이제 없는 기기 → "저장되지 않은 기기입니다"
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F5)
                return;

            e.Handled = true;
            await LoadAsync();
        }

        // 없는 기기 · 오류 화면의 [목록으로]
        private void BackButton_Click(object sender, RoutedEventArgs e) => Close();


        // ═════════════════════════════════════════════════════
        //  안내 · 알림
        // ═════════════════════════════════════════════════════

        private void ShowNotice(string text, string detail, bool showBack)
        {
            NoticeText.Text = text;
            NoticeDetail.Text = detail;
            BackButton.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
            NoticePanel.Visibility = Visibility.Visible;
            ContentPanel.Visibility = Visibility.Hidden;
        }

        private void HideNotice()
        {
            NoticePanel.Visibility = Visibility.Collapsed;
            ContentPanel.Visibility = Visibility.Visible;
        }

        // 아래쪽에 3초 동안 뜨는 알림 (토스트)
        private void ShowToast(string text)
        {
            ToastText.Text = text;
            ToastPanel.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private static string DescribeLayout(SavedLayout? layout) =>
            layout == null
                ? "-"
                : $"{layout.Placement.Side}/{layout.Placement.Offset}/{layout.ScreenWidth}x{layout.ScreenHeight}";
    }
}
