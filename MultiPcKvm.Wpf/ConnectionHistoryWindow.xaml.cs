using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiPcKvm.App.Storage;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  연결 기록 창 — 동작 (8주차)
    //
    //  지금 조건(상태 · 검색어 · 정렬 · 페이지)을 필드에 들고 있다가,
    //  무엇이든 바뀌면 LoadAsync 로 다시 불러와서 표와 페이지 버튼을 새로 그린다.
    //
    //    상태 탭 · 정렬 · 검색  → 1페이지로 되돌리고 불러오기   (이전 페이지 번호를 끌고 가지 않는다)
    //    페이지 번호 · 이전/다음 → 그 페이지만 불러오기          (조건은 그대로)
    //    [새로 고침] · F5        → 지금 페이지를 다시 불러오기  (창을 열어 둔 사이 생긴 연결도 보이게)
    //
    //  검색창의 글자는 탭·정렬·새로 고침 때도 같이 적용한다. (8주차 리뷰 반영)
    //  Enter 를 안 누른 채 탭을 바꿔도, 화면에 보이는 검색어와 실제 조건이 어긋나지 않게 한다.
    //
    //  데이터는 ConnectionHistoryClient 가 다른 스레드에서 가져온다. 이 창은 결과만 그린다.
    // ─────────────────────────────────────────────────────────────
    public partial class ConnectionHistoryWindow : Window
    {
        private readonly ConnectionHistoryClient _client = new();

        // ── 지금 조건 ─────────────────────────────────────────
        private string _status = "all";
        private string _sort = ConnectionQuery.DefaultSort;
        private string? _keyword;
        private int _page = 1;

        // 한 줄에 보일 페이지 번호 버튼 수 (예: 3 4 [5] 6 7)
        private const int PageButtonCount = 5;

        // 요청 번호. 빠르게 여러 번 누르면 앞 요청의 답이 늦게 와서 최신 화면을 덮어쓸 수 있다.
        // 마지막으로 보낸 요청의 답만 그린다.
        private int _requestNo;

        // 창이 다 만들어지기 전에는 탭·정렬 이벤트를 무시한다. (InitializeComponent 중에도 이벤트가 온다)
        private bool _ready;

        // 이 시간보다 오래 걸릴 때만 "불러오는 중…" 을 띄운다. (8주차 리뷰 반영)
        // 보통은 몇 ms 만에 끝나서, 바로 띄우면 페이지를 넘길 때마다 안내가 깜빡인다.
        private const int LoadingDelayMs = 150;

        // 지금 응답을 기다리는 요청 번호. 0 이면 기다리는 것 없음.
        private int _pendingNo;

        public ConnectionHistoryWindow()
        {
            InitializeComponent();
            _ready = true;

            // 창이 뜨면 바로 첫 페이지를 불러온다.
            Loaded += async (s, e) => await LoadAsync();
        }


        // ═════════════════════════════════════════════════════
        //  조건 바꾸기 (화면 이벤트)
        // ═════════════════════════════════════════════════════

        // 상태 탭 [전체 / 연결 중 / 종료]
        private async void StatusTab_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready || sender is not RadioButton { Tag: string status })
                return;

            _status = status;
            ApplyKeywordBox();
            _page = 1;
            await LoadAsync();
        }

        // 정렬
        private async void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || SortBox.SelectedItem is not ComboBoxItem { Tag: string sort })
                return;

            _sort = sort;
            ApplyKeywordBox();
            _page = 1;
            await LoadAsync();
        }

        // 검색창에서 Enter
        private async void KeywordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;
            await SearchAsync();
        }

        // [검색]
        private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchAsync();

        private async Task SearchAsync()
        {
            ApplyKeywordBox();
            _page = 1;
            await LoadAsync();
        }

        // 검색창 글자를 조건에 넣는다. 빈칸만 있으면 검색어 없음 (전체)
        // 검색어가 바뀌었으면 true
        private bool ApplyKeywordBox()
        {
            string text = KeywordBox.Text.Trim();
            string? keyword = text.Length == 0 ? null : text;

            bool changed = keyword != _keyword;
            _keyword = keyword;
            return changed;
        }

        // [새로 고침]
        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        // F5 = 새로 고침 (창 어디에 있든)
        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F5)
                return;

            e.Handled = true;
            await RefreshAsync();
        }

        // 지금 조건 그대로 다시 불러온다.
        // 검색창 글자가 바뀌어 있으면 검색과 같으므로 1페이지부터.
        private async Task RefreshAsync()
        {
            if (ApplyKeywordBox())
                _page = 1;

            await LoadAsync();
        }

        // 줄을 더블클릭하면 그 기기의 상세 창을 연다. (10주차)
        // 상세 창은 메인 창이 관리한다. (이미 열려 있으면 그 창을 앞으로)
        private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 머리줄·스크롤 막대를 더블클릭한 경우는 무시한다. (줄 위에서 누른 것만)
            if (ItemsControl.ContainerFromElement(HistoryList, e.OriginalSource as DependencyObject)
                    is not ListViewItem { Content: HistoryRow row })
                return;

            if (Owner is MainWindow main)
                main.OpenDeviceDetail(row.DeviceId);
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e) => await GoToPageAsync(_page - 1);

        private async void NextButton_Click(object sender, RoutedEventArgs e) => await GoToPageAsync(_page + 1);

        private async void PageNumber_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: int page })
                await GoToPageAsync(page);
        }

        private async Task GoToPageAsync(int page)
        {
            if (page < 1)
                return;

            _page = page;
            await LoadAsync();
        }


        // ═════════════════════════════════════════════════════
        //  불러오기 · 그리기
        // ═════════════════════════════════════════════════════

        private async Task LoadAsync()
        {
            int myNo = ++_requestNo;

            var query = new ConnectionQuery
            {
                Page = _page,
                Size = ConnectionQuery.DefaultSize,
                Sort = _sort,
                Status = _status,
                Keyword = _keyword,
            };

            QueryText.Text = "?" + ConnectionHistoryClient.ToQueryString(query);

            // 불러오는 동안: 오래 걸리면(150ms 넘게) 안내를 띄우고 페이지 버튼을 잠근다.
            _pendingNo = myNo;
            _ = ShowLoadingLaterAsync(myNo);

            HistoryResponse response = await _client.GetHistoryAsync(query);

            // 그사이 다른 조건으로 또 요청했으면 이 답은 버린다.
            if (myNo != _requestNo)
                return;

            _pendingNo = 0;

            // ── 실패 (400 / 500) ──
            if (response.Page is not PagedResult<ConnectionHistoryItem> page)
            {
                HistoryList.ItemsSource = null;
                ShowNotice("기록을 불러오지 못했습니다.", $"[{(int)response.Code}] {response.Message}");
                CountText.Text = "";
                DrawPageButtons(0, 0, hasPrevious: false, hasNext: false);
                return;
            }

            // 기록이 줄어서(기기 삭제 등) 지금 페이지가 마지막 페이지 뒤가 됐으면 마지막 페이지로 다시 간다.
            if (page.Items.Count == 0 && page.TotalPages > 0 && _page > page.TotalPages)
            {
                _page = page.TotalPages;
                await LoadAsync();
                return;
            }

            HistoryList.ItemsSource = page.Items.Select(HistoryRow.From).ToList();

            // ── 빈 결과 ──
            // 조건(상태·검색어)을 걸었는데 없으면 "검색 결과가 없습니다", 아무 조건 없이 없으면 기록 자체가 없는 것.
            if (page.TotalElements == 0)
            {
                bool filtered = _keyword != null || _status != "all";

                if (filtered)
                    ShowNotice("검색 결과가 없습니다", DescribeFilter());
                else
                    ShowNotice("아직 연결 기록이 없습니다", "다른 PC 와 연결하면 여기에 기록이 쌓입니다.");
            }
            else
            {
                HideNotice();
            }

            CountText.Text = page.TotalElements == 0
                ? "0건"
                : $"전체 {page.TotalElements:N0}건 · {page.Page}/{page.TotalPages} 페이지";

            DrawPageButtons(page.Page, page.TotalPages, page.HasPrevious, page.HasNext);
        }

        // LoadingDelayMs 가 지나도 그 요청이 안 끝났으면 "불러오는 중…" 을 띄운다.
        private async Task ShowLoadingLaterAsync(int requestNo)
        {
            await Task.Delay(LoadingDelayMs);

            if (_pendingNo != requestNo)
                return;     // 이미 끝났거나 더 새 요청이 있다

            ShowNotice("불러오는 중…", "");
            SetPagingEnabled(false);    // 누른 채로 또 누르지 않게
        }

        // 페이지 번호 버튼을 새로 만든다. 지금 페이지를 가운데 두고 최대 5개.
        // [이전]/[다음] 은 응답의 HasPrevious / HasNext 를 그대로 따른다.
        private void DrawPageButtons(int current, int totalPages, bool hasPrevious, bool hasNext)
        {
            PageNumbers.Children.Clear();

            PrevButton.IsEnabled = hasPrevious;
            NextButton.IsEnabled = hasNext;

            if (totalPages <= 0)
                return;

            int first = Math.Max(1, current - PageButtonCount / 2);
            int last = Math.Min(totalPages, first + PageButtonCount - 1);
            first = Math.Max(1, last - PageButtonCount + 1);

            for (int p = first; p <= last; p++)
            {
                var button = new Button
                {
                    Content = p.ToString(),
                    Tag = p,
                    Style = (Style)FindResource("PageButton"),
                };

                // 지금 페이지는 굵게 표시하고 누를 수 없게 한다.
                if (p == current)
                {
                    button.FontWeight = FontWeights.Bold;
                    button.IsEnabled = false;
                }
                else
                {
                    button.Click += PageNumber_Click;
                }

                PageNumbers.Children.Add(button);
            }
        }

        // 불러오는 동안 페이지 버튼을 잠근다. (다 불러오면 DrawPageButtons 가 다시 정한다)
        private void SetPagingEnabled(bool enabled)
        {
            PrevButton.IsEnabled = enabled;
            NextButton.IsEnabled = enabled;

            foreach (UIElement child in PageNumbers.Children)
                child.IsEnabled = enabled;
        }

        private void ShowNotice(string text, string detail)
        {
            NoticeText.Text = text;
            NoticeDetail.Text = detail;
            NoticePanel.Visibility = Visibility.Visible;
        }

        private void HideNotice() => NoticePanel.Visibility = Visibility.Collapsed;

        // "상태: 종료 · 검색어: 노트북"
        private string DescribeFilter()
        {
            var parts = new List<string>();

            if (_status != "all")
                parts.Add("상태: " + (_status == "open" ? "연결 중" : "종료"));

            if (_keyword != null)
                parts.Add("검색어: " + _keyword);

            return string.Join(" · ", parts);
        }
    }


    // 표의 한 줄. 저장소의 기록을 사람이 읽는 글자로 바꾼 것.
    // 시각은 DB 에 UTC 로 있으므로 여기서 이 PC 의 시간대로 바꾼다.
    // DeviceId : 줄을 더블클릭했을 때 어느 기기의 상세 창을 열지 (10주차)
    internal sealed record HistoryRow(
        string DeviceName,
        string ConnectedText,
        string DisconnectedText,
        string ReasonText,
        string LatencyText,
        string MessageText,
        Guid DeviceId)
    {
        // 연결 기록 창의 줄 (기기 이름이 기록에 들어 있다)
        public static HistoryRow From(ConnectionHistoryItem item) => From(item, item.DeviceName);

        // 기기 상세 창의 "최근 연결 기록" 줄 (기기 이름은 상세에서 따로 받는다. 10주차)
        public static HistoryRow From(ConnectionRecord item, string deviceName) => new(
            deviceName,
            item.ConnectedAt.ToLocalTime().ToString("MM/dd HH:mm:ss"),
            item.DisconnectedAt is DateTime end ? end.ToLocalTime().ToString("MM/dd HH:mm:ss") : "연결 중",
            item.DisconnectReason ?? "-",
            item.AvgLatencyMs is double ms ? $"{ms:F1}ms" : "-",
            item.MessageCount.ToString("N0"),
            item.DeviceId);
    }
}
