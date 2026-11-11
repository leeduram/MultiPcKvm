using System.Windows;
using System.Windows.Input;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  MainWindow — 기기 상세 창 (10주차)
    //
    //  여는 곳 두 군데
    //    · 메인 창 기기 목록 : 기기 이름을 더블클릭
    //    · [연결 기록] 창    : 줄을 더블클릭
    //
    //  기기마다 창은 하나만. 이미 열려 있으면 그 창을 앞으로 가져온다.
    //
    //  상세 창의 버튼은 여기 있는 Run… 함수를 통해 목록 버튼과 "같은 코드" 를 부른다.
    //  (확인 창 · 안내 · 막는 조건을 두 군데에 따로 만들면 어긋나기 쉽다)
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        private readonly Dictionary<Guid, DeviceDetailWindow> _detailWindows = new();

        // 기기 이름 더블클릭
        private void DeviceName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: DeviceItem item })
            {
                e.Handled = true;
                OpenDeviceDetail(item.DeviceId);
            }
        }

        internal void OpenDeviceDetail(Guid deviceId)
        {
            if (_detailWindows.TryGetValue(deviceId, out DeviceDetailWindow? open))
            {
                if (open.WindowState == WindowState.Minimized)
                    open.WindowState = WindowState.Normal;

                open.Activate();
                return;
            }

            var window = new DeviceDetailWindow(this, deviceId) { Owner = this };
            window.Closed += (s, e) => _detailWindows.Remove(deviceId);
            _detailWindows[deviceId] = window;
            window.Show();
        }

        // 메인 창 기기 목록에서 그 기기를 찾는다. 목록에 없으면 null.
        // (꺼져 있는 저장된 기기도 목록에 "오프라인" 으로 있다. 신뢰하지 않는 기기는 없다)
        internal DeviceItem? FindDeviceItem(Guid deviceId) =>
            _devices.FirstOrDefault(d => d.DeviceId == deviceId);

        // ── 목록 버튼과 같은 동작 ──
        // 목록 버튼은 Tag 에 DeviceItem 을 달고 부른다. 같은 모양으로 넘겨서 그 코드를 그대로 쓴다.

        internal void RunDeviceAction(DeviceItem item) =>
            DeviceAction_Click(new FrameworkElement { Tag = item }, new RoutedEventArgs());

        internal void RunLayout(DeviceItem item) =>
            LayoutButton_Click(new FrameworkElement { Tag = item }, new RoutedEventArgs());

        internal void RunDelete(DeviceItem item) =>
            DeleteDevice_Click(new FrameworkElement { Tag = item }, new RoutedEventArgs());
    }
}
