namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  작업 표시줄 오른쪽 아래(알림 영역)의 트레이 아이콘.
    //
    //    왼쪽 클릭  → ShowRequested  (숨긴 창을 다시 띄운다)
    //    오른쪽 클릭 → 메뉴 [종료]    → ExitRequested (프로그램을 끝낸다)
    //
    //  WPF 에는 트레이 아이콘이 없어서 WinForms 의 NotifyIcon 을 쓴다.
    //  (csproj 의 UseWindowsForms 설명 참고. 이름이 겹치지 않게 전체 이름으로 쓴다)
    //
    //  프로그램이 켜져 있는 동안 항상 보인다.
    //  창을 숨겼을 때 다시 꺼낼 방법이 이것뿐이라, 숨길 때만 보이게 하면 헷갈린다.
    // ─────────────────────────────────────────────────────────────
    public sealed class TrayIcon : IDisposable
    {
        private readonly System.Windows.Forms.NotifyIcon _icon;

        public event Action? ShowRequested;
        public event Action? ExitRequested;

        public TrayIcon()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("종료", null, (s, e) => ExitRequested?.Invoke());

            _icon = new System.Windows.Forms.NotifyIcon
            {
                Text = "MultiPcKvm",
                Icon = LoadIcon(),
                ContextMenuStrip = menu,      // 오른쪽 클릭하면 이 메뉴가 뜬다
                Visible = true,
            };

            // 왼쪽 클릭만 창 띄우기. (오른쪽 클릭은 위 메뉴가 처리한다)
            _icon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                    ShowRequested?.Invoke();
            };
        }

        // 실행 파일의 아이콘을 쓴다. 못 읽으면 Windows 기본 프로그램 아이콘.
        private static System.Drawing.Icon LoadIcon()
        {
            try
            {
                string? path = Environment.ProcessPath;

                if (path != null)
                {
                    System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);

                    if (icon != null)
                        return icon;
                }
            }
            catch
            {
            }

            return System.Drawing.SystemIcons.Application;
        }

        // 꼭 불러야 한다. 안 하면 프로그램이 끝난 뒤에도 아이콘이 남아 있다가
        // 마우스를 올려야 사라진다.
        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
