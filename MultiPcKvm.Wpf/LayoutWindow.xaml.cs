using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiPcKvm.App;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  화면 배치 창. 상대 PC 화면을 끌어서 내 화면 옆 원하는 자리에 놓는다.
    //
    //  ShowDialog() 가 true 를 돌려주면 Result 에 고른 배치가 들어 있다.
    //
    //  좌표가 두 종류라 헷갈리지 않게 이름을 나눴다.
    //    실제 좌표(real)  : 실제 화면 픽셀. 내 화면 왼쪽 위가 (0, 0). ScreenLayout 이 쓰는 좌표.
    //    배치판 좌표(board): 이 창의 배치판(Canvas) 위 위치. 실제 좌표를 _scale 배 줄인 것.
    //
    //  계산(어디에 붙을지)은 전부 ScreenLayout 이 하고, 여기는 그리기와 마우스만 맡는다.
    //  그래야 커서가 실제로 넘어가는 규칙(InputRouter)과 배치 창이 같은 계산을 쓴다.
    // ─────────────────────────────────────────────────────────────
    public partial class LayoutWindow : Window
    {
        // 저장을 누르면 채워진다.
        public ScreenPlacement Result { get; private set; }

        private readonly int _localWidth;
        private readonly int _localHeight;
        private readonly int _remoteWidth;
        private readonly int _remoteHeight;

        // 지금 배치 (놓을 때마다 바뀐다)
        private ScreenPlacement _placement;

        // 실제 좌표 → 배치판 좌표 변환 값
        //   board = origin + real × scale
        private double _scale;
        private double _originX;
        private double _originY;

        // 끄는 중인가, 그리고 잡은 위치 (상대 네모 왼쪽 위에서 마우스까지 거리)
        private bool _dragging;
        private Point _grab;

        // remoteKnown: 상대 화면 크기를 실제로 받아 본 적이 있는가.
        //              없으면 내 화면과 같은 크기로 가정했다고 알려준다.
        // current    : 지금 배치. null 이면 아직 안 정함 → 처음 위치(왼쪽, 가운데)로 보여준다.
        public LayoutWindow(
            string remoteName,
            int localWidth, int localHeight,
            int remoteWidth, int remoteHeight,
            bool remoteKnown,
            ScreenPlacement? current)
        {
            InitializeComponent();

            _localWidth = Math.Max(1, localWidth);
            _localHeight = Math.Max(1, localHeight);
            _remoteWidth = Math.Max(1, remoteWidth);
            _remoteHeight = Math.Max(1, remoteHeight);

            // 저장된 배치가 지금 크기 기준으로 범위를 벗어났을 수 있다. 맞닿게 자른다.
            _placement = current == null
                ? ScreenLayout.DefaultPlacement(_localWidth, _localHeight, _remoteWidth, _remoteHeight)
                : ScreenLayout.Clamp(current, _localWidth, _localHeight, _remoteWidth, _remoteHeight);

            Result = _placement;

            TitleText.Text = $"화면 배치 — {remoteName}";
            LocalText.Text = $"내 PC\n{_localWidth}×{_localHeight}";
            RemoteText.Text = remoteKnown
                ? $"{remoteName}\n{_remoteWidth}×{_remoteHeight}"
                : $"{remoteName}\n크기 모름 (연결하면 맞춰짐)";

            ComputeScale();
            DrawLocal();
            DrawRemote(_placement);
            UpdatePositionText();
        }


        // ── 그리기 ────────────────────────────────────────────

        // 줄이는 비율을 정한다.
        //
        // 상대 화면이 내 화면 어느 쪽에 붙든 배치판 안에 다 들어오도록,
        // "내 화면 + 사방에 상대 화면 하나씩" 크기를 기준으로 정한다.
        // 끄는 도중에 비율이 바뀌면 화면이 출렁여서, 창을 여는 순간 한 번만 정한다.
        private void ComputeScale()
        {
            const double padding = 8;

            double totalWidth = _localWidth + 2.0 * _remoteWidth;
            double totalHeight = _localHeight + 2.0 * _remoteHeight;

            _scale = Math.Min(
                (Board.Width - 2 * padding) / totalWidth,
                (Board.Height - 2 * padding) / totalHeight);

            // 내 화면을 배치판 한가운데에 둔다.
            _originX = (Board.Width - _localWidth * _scale) / 2;
            _originY = (Board.Height - _localHeight * _scale) / 2;
        }

        private void DrawLocal()
        {
            LocalBox.Width = _localWidth * _scale;
            LocalBox.Height = _localHeight * _scale;
            Canvas.SetLeft(LocalBox, _originX);
            Canvas.SetTop(LocalBox, _originY);
        }

        // 배치대로 상대 네모를 놓는다.
        private void DrawRemote(ScreenPlacement placement)
        {
            PlaceBox(RemoteBox, placement);
        }

        // 네모 하나를 배치 위치에 놓는다. (상대 네모, 미리보기 네모 공용)
        private void PlaceBox(FrameworkElement box, ScreenPlacement placement)
        {
            (int x, int y) = ScreenLayout.RemoteTopLeft(
                placement, _localWidth, _localHeight, _remoteWidth, _remoteHeight);

            box.Width = _remoteWidth * _scale;
            box.Height = _remoteHeight * _scale;
            Canvas.SetLeft(box, _originX + x * _scale);
            Canvas.SetTop(box, _originY + y * _scale);
        }

        // 아래 설명 줄. 예) "오른쪽 · 300 픽셀 아래로 밀림"
        private void UpdatePositionText()
        {
            string side = ScreenLayout.SideName(_placement.Side);
            int offset = _placement.Offset;

            bool vertical = _placement.Side == RemotePosition.Right ||
                            _placement.Side == RemotePosition.Left;

            string shift = offset == 0
                ? (vertical ? "위 끝 맞춤" : "왼쪽 끝 맞춤")
                : vertical
                    ? $"{Math.Abs(offset)} 픽셀 {(offset > 0 ? "아래로" : "위로")} 밀림"
                    : $"{Math.Abs(offset)} 픽셀 {(offset > 0 ? "오른쪽으로" : "왼쪽으로")} 밀림";

            PositionText.Text = $"상대 화면 위치: 내 화면 {side} · {shift}";
        }

        // 배치판 위의 상대 네모 왼쪽 위 → 가장 가까운 맞닿은 배치
        private ScreenPlacement SnapFromBoard(double boardLeft, double boardTop)
        {
            int realX = (int)Math.Round((boardLeft - _originX) / _scale);
            int realY = (int)Math.Round((boardTop - _originY) / _scale);

            return ScreenLayout.Snap(
                realX, realY, _localWidth, _localHeight, _remoteWidth, _remoteHeight);
        }


        // ── 끌기 ──────────────────────────────────────────────

        private void RemoteBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;

            // 네모의 어디를 잡았는지 기억한다.
            // 안 그러면 끄는 순간 네모 왼쪽 위가 마우스 위치로 튄다.
            Point mouse = e.GetPosition(Board);
            _grab = new Point(
                mouse.X - Canvas.GetLeft(RemoteBox),
                mouse.Y - Canvas.GetTop(RemoteBox));

            // 마우스가 네모 밖으로 빨리 움직여도 계속 이벤트를 받도록 붙잡는다.
            RemoteBox.CaptureMouse();

            GhostBox.Visibility = Visibility.Visible;
            PlaceBox(GhostBox, _placement);

            e.Handled = true;
        }

        private void RemoteBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
                return;

            Point mouse = e.GetPosition(Board);

            double left = mouse.X - _grab.X;
            double top = mouse.Y - _grab.Y;

            // 끄는 동안은 마우스를 그대로 따라간다. (떨어져 있어도 된다)
            Canvas.SetLeft(RemoteBox, left);
            Canvas.SetTop(RemoteBox, top);

            // 놓으면 붙을 자리를 점선 네모로 미리 보여준다.
            PlaceBox(GhostBox, SnapFromBoard(left, top));
        }

        private void RemoteBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
                return;

            FinishDrag();

            // ReleaseMouseCapture 는 LostMouseCapture 를 부르지만,
            // _dragging 을 먼저 false 로 바꿔서 두 번 처리되지 않는다.
            RemoteBox.ReleaseMouseCapture();

            e.Handled = true;
        }

        // 끄는 도중 다른 창이 뜨는 등으로 마우스를 놓친 경우도 같은 자리에서 끝낸다.
        private void RemoteBox_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_dragging)
                FinishDrag();
        }

        // 놓은 자리에서 가장 가까운 맞닿은 자리로 붙인다.
        private void FinishDrag()
        {
            _dragging = false;

            _placement = SnapFromBoard(Canvas.GetLeft(RemoteBox), Canvas.GetTop(RemoteBox));

            GhostBox.Visibility = Visibility.Collapsed;
            DrawRemote(_placement);
            UpdatePositionText();
        }


        // ── 버튼 ──────────────────────────────────────────────

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _placement = ScreenLayout.DefaultPlacement(
                _localWidth, _localHeight, _remoteWidth, _remoteHeight);
            DrawRemote(_placement);
            UpdatePositionText();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            Result = _placement;

            // true 를 넣으면 창이 닫히고 ShowDialog() 가 true 를 돌려준다.
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
