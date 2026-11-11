using System.Windows;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  MainWindow — [연결 기록] 버튼 (8주차)
    //
    //  누르면 연결 기록 창을 새 창으로 띄운다.
    //  이미 열려 있으면 하나 더 만들지 않고 그 창을 앞으로 가져온다.
    //  창은 메인 창과 따로 움직인다. (Show — 열어 둔 채로 메인 창도 쓸 수 있다)
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        private ConnectionHistoryWindow? _historyWindow;

        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_historyWindow != null)
            {
                if (_historyWindow.WindowState == WindowState.Minimized)
                    _historyWindow.WindowState = WindowState.Normal;

                _historyWindow.Activate();
                return;
            }

            _historyWindow = new ConnectionHistoryWindow { Owner = this };
            _historyWindow.Closed += (s, args) => _historyWindow = null;
            _historyWindow.Show();
        }
    }
}
