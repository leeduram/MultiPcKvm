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
    //  MainWindow — 설정 · 트레이 · 종료
    //
    //  [X] 를 눌렀을 때 트레이로 숨기기 / 정말 끄기, 트레이 아이콘 동작.
    //  자동 실행·숨긴 채 시작·관리자 권한 설정과 [관리자 권한으로 다시 시작].
    //
    //  MainWindow 는 할 일이 많아서 주제별로 파일을 나눴다. (partial = 한 클래스를 여러 파일에 나눠 쓰기)
    //  필드(공용 상태)·생성자·시작·Tick 은 MainWindow.xaml.cs 에 있다.
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // ── 종료 ──────────────────────────────────────────────

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // [X] 를 눌렀는데 "닫으면 트레이로 숨기기" 가 켜져 있으면 끄지 않고 숨긴다.
            // 트레이 메뉴 [종료] 로 끄는 중(_exiting)이면 그대로 끈다.
            if (!_exiting && CloseToTrayBox.IsChecked == true)
            {
                e.Cancel = true;
                Hide();
                DiagLog.Write("창 숨김 | 트레이로");
                return;
            }

            Cleanup();
        }

        // 프로그램을 끝낼 때 정리한다. 여러 번 불려도 한 번만 한다.
        //
        // 반드시 해야 한다. 안 하면 후킹이 걸린 채로 프로세스가 남을 수 있고,
        // 트레이 아이콘이 마우스를 올릴 때까지 남아 있다.
        private bool _cleanedUp;

        private void Cleanup()
        {
            if (_cleanedUp)
                return;

            _cleanedUp = true;

            _timer.Stop();

            // 열려 있는 연결 기록을 닫는다. (6주차) 줄을 내리기 전에 해야 이유가 "프로그램 종료" 로 남는다.
            EndConnectionLogs("프로그램 종료");

            TearDownPeer();
            _discovery?.Stop();
            _tray.Dispose();
            StopScreenSharing();
            StopFileTransfer();

            // 커서 숨김 창도 닫는다. (커서가 숨겨진 채로 끝나지 않게)
            _cursorHider?.Close();
            _cursorHider = null;

            // 마지막으로 남은 기록을 파일에 다 쓰고 끝낸다. (맨 마지막에)
            DiagLog.Write("프로그램 끝냄");
            DiagLog.Stop();
        }

        // ── 트레이 ────────────────────────────────────────────

        private readonly TrayIcon _tray;

        // 트레이 메뉴 [종료] 로 끄는 중인가. (이때는 [X] 와 달리 트레이로 숨기지 않는다)
        private bool _exiting;

        // 트레이 아이콘 왼쪽 클릭 → 숨긴 창을 다시 띄운다.
        private void ShowFromTray()
        {
            Show();

            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            // 다른 창 뒤에 숨어 있으면 앞으로 가져온다.
            Activate();
        }

        // 프로그램을 끝낸다. (트레이 메뉴 [종료], 또는 창이 완전히 닫혔을 때)
        //
        // 숨긴 채로 시작해서 창이 한 번도 안 뜬 경우에도 확실히 끝나도록
        // 창 닫기에 기대지 않고 정리 후 바로 Shutdown 한다.
        public void ExitApp()
        {
            // Shutdown 이 창을 닫으면 Closed 에서 또 불린다. 두 번 하지 않는다.
            if (_exiting)
                return;

            _exiting = true;
            Cleanup();
            System.Windows.Application.Current.Shutdown();
        }


        // ── 설정 (자동 실행·트레이·마지막 연결 기기) ───────────
        //
        // 어디에 저장하나
        //   · Windows 시작 시 자동 실행 → 레지스트리 Run (AutoStart.cs). DB 에 따로 적지 않는다.
        //   · 나머지                   → DB 의 app_setting 표 (이름 → 값)

        private static class SettingKeys
        {
            public const string StartHidden = "start_hidden";   // "1" 이면 자동 실행 때 트레이에 숨긴 채로 시작
            public const string CloseToTray = "close_to_tray";  // "1" 이면 [X] 를 눌러도 끄지 않고 트레이로 숨김
            public const string LastTarget = "last_target";     // 마지막으로 내가 조종한 기기 ID. 켜면 이 기기에 자동 연결
            public const string RunAsAdmin = "run_as_admin";    // "1" 이면 켤 때 관리자 권한으로 실행 (App.xaml.cs 도 읽는다)
            public const string AutoStartTask = "autostart_task";       // "1" 이면 자동 실행을 작업 스케줄러로 등록해 둠
            public const string AutoStartTaskPath = "autostart_task_path"; // 그때 등록한 실행 파일 경로 (폴더를 옮겼는지 확인용)
        }

        // App.xaml.cs 가 넣어준다. 켤 때 관리자 권한 확인 창에서 [아니요] 를 눌렀으면 true.
        public bool ElevationDeclined { get; set; }

        // 자동 실행 때 트레이에 숨긴 채로 시작할지. (App.xaml.cs 가 읽는다)
        public bool StartHiddenEnabled => StartHiddenBox.IsChecked == true;

        private void LoadSettings()
        {
            // 자동 실행은 두 가지 방식 중 하나로 켜져 있다.
            //   일반 권한   → 레지스트리 Run      (AutoStart.IsEnabled 가 직접 확인)
            //   관리자 권한 → 작업 스케줄러        (등록했다고 DB 에 적어둔 것으로 판단)
            bool autoStartRun = AutoStart.IsEnabled();
            bool autoStartTask = ReadSetting(SettingKeys.AutoStartTask) == "1";
            bool autoStart = autoStartRun || autoStartTask;

            // 프로그램 폴더를 옮겼으면 자동 실행 경로도 지금 위치로 고친다.
            if (autoStartRun)
                AutoStart.RefreshPathIfEnabled();

            if (autoStartTask)
                RefreshTaskPathIfNeeded();

            AutoStartBox.IsChecked = autoStart;
            StartHiddenBox.IsChecked = ReadSetting(SettingKeys.StartHidden) == "1";
            StartHiddenBox.IsEnabled = autoStart;
            CloseToTrayBox.IsChecked = ReadSetting(SettingKeys.CloseToTray) == "1";
            RunAsAdminBox.IsChecked = ReadSetting(SettingKeys.RunAsAdmin) == "1";

            UpdateAdminState();

            if (ElevationDeclined)
                ShowHint("관리자 권한 확인 창에서 [아니요] 를 눌러 일반 권한으로 실행 중입니다.", 8);

            // 마지막으로 조종한 기기가 있으면, 검색에 잡히는 대로 자동 연결한다. (TryAutoConnect)
            if (Guid.TryParse(ReadSetting(SettingKeys.LastTarget), out Guid target))
            {
                _autoTarget = target;
                _autoConnectPending = true;
                DiagLog.Write($"자동 연결 대기 | 마지막으로 조종한 기기 {target}");
            }

            DiagLog.Write($"설정 | 자동 실행 {autoStart}({(autoStartTask ? "작업 스케줄러" : "레지스트리")}) · 숨긴 채 시작 {StartHiddenBox.IsChecked} · " +
                          $"닫으면 트레이 {CloseToTrayBox.IsChecked} · 관리자로 실행 {RunAsAdminBox.IsChecked} · 지금 관리자 {Elevation.IsElevated}");
        }

        // 지금 권한을 화면에 보여주고, 이미 관리자면 [관리자 권한으로 다시 시작] 을 잠근다.
        private void UpdateAdminState()
        {
            bool elevated = Elevation.IsElevated;

            AdminStateText.Text = elevated ? "지금: 관리자 권한으로 실행 중" : "지금: 일반 권한으로 실행 중";
            RestartAdminButton.IsEnabled = !elevated;
        }

        // 작업 스케줄러에 등록한 경로가 지금 실행 파일과 다르면 다시 등록한다.
        //
        // 관리자 권한으로 실행 중일 때만 한다. (그래야 확인 창 없이 조용히 된다)
        // 일반 권한이면 다음에 관리자 권한으로 켰을 때 고쳐진다.
        private async void RefreshTaskPathIfNeeded()
        {
            string exe = Environment.ProcessPath ?? "";

            if (!Elevation.IsElevated ||
                string.Equals(ReadSetting(SettingKeys.AutoStartTaskPath), exe, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? error = await Task.Run(() => AutoStart.RegisterTask(out _));

            if (error == null)
            {
                WriteSetting(SettingKeys.AutoStartTaskPath, exe);
                DiagLog.Write($"자동 실행 경로 고침 (작업 스케줄러) | {exe}");
            }
        }

        // 자동 실행을 원하는 상태로 맞춘다. (켜기/끄기 × 일반/관리자)
        //
        // 레지스트리와 작업 스케줄러 둘 다 켜져 있으면 로그인할 때 두 번 실행된다.
        // 그래서 항상 둘 중 하나만 남긴다.
        //
        // 작업 스케줄러 등록·삭제는 관리자 권한이 필요해서 확인 창이 뜨고 몇 초 걸린다.
        // 그동안 창이 멈추지 않게 화면 스레드 밖에서 한다. DB 는 끝난 뒤 화면 스레드에서 적는다.
        //
        // 성공하면 null, 실패하면 이유.
        private async Task<string?> ApplyAutoStartAsync(bool enable, bool asAdmin)
        {
            bool taskRegistered = ReadSetting(SettingKeys.AutoStartTask) == "1";

            SetSettingsEnabled(false);

            try
            {
                if (!enable)
                {
                    string? runError = AutoStart.Set(false);

                    if (runError != null)
                        return runError;

                    if (taskRegistered)
                    {
                        string? taskError = await Task.Run(() => AutoStart.UnregisterTask(out _));

                        if (taskError != null)
                            return "작업 스케줄러에서 지우지 못했습니다: " + taskError;

                        WriteSetting(SettingKeys.AutoStartTask, null);
                    }

                    return null;
                }

                if (asAdmin)
                {
                    // 관리자 권한 → 작업 스케줄러에 넣고, 레지스트리 쪽은 지운다.
                    string? taskError = await Task.Run(() => AutoStart.RegisterTask(out _));

                    if (taskError != null)
                        return "작업 스케줄러에 등록하지 못했습니다: " + taskError;

                    WriteSetting(SettingKeys.AutoStartTask, "1");
                    WriteSetting(SettingKeys.AutoStartTaskPath, Environment.ProcessPath ?? "");

                    return AutoStart.Set(false);
                }

                // 일반 권한 → 작업 스케줄러 쪽을 먼저 지우고(실패하면 그대로 둔다), 레지스트리에 넣는다.
                if (taskRegistered)
                {
                    string? taskError = await Task.Run(() => AutoStart.UnregisterTask(out _));

                    if (taskError != null)
                        return "작업 스케줄러에서 지우지 못했습니다: " + taskError;

                    WriteSetting(SettingKeys.AutoStartTask, null);
                }

                return AutoStart.Set(true);
            }
            finally
            {
                SetSettingsEnabled(true);
            }
        }

        // 자동 실행을 바꾸는 동안 설정 체크박스를 잠근다. (겹쳐 누르지 못하게)
        private void SetSettingsEnabled(bool enabled)
        {
            AutoStartBox.IsEnabled = enabled;
            RunAsAdminBox.IsEnabled = enabled;
            StartHiddenBox.IsEnabled = enabled && AutoStartBox.IsChecked == true;
        }

        private string? ReadSetting(string key)
        {
            try
            {
                return _store?.GetSetting(key);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"설정 읽기 실패 | {key} | {ex.Message}");
                return null;
            }
        }

        private void WriteSetting(string key, string? value)
        {
            if (_store == null)
            {
                ShowHint("저장소를 열지 못해서 설정을 저장할 수 없습니다.", 5);
                return;
            }

            try
            {
                _store.SetSetting(key, value);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"설정 저장 실패 | {key} | {ex.Message}");
                ShowHint("설정을 저장하지 못했습니다: " + ex.Message, 5);
            }
        }

        // 체크박스는 Click 으로 받는다. (Checked 는 코드에서 값을 넣을 때도 불려서 저장이 되풀이된다)

        private async void AutoStartBox_Click(object sender, RoutedEventArgs e)
        {
            bool enable = AutoStartBox.IsChecked == true;
            bool asAdmin = RunAsAdminBox.IsChecked == true;

            if (enable && asAdmin)
                ShowHint("관리자 권한 자동 실행을 등록합니다. Windows 확인 창에서 [예] 를 눌러주세요.", 10);

            string? error = await ApplyAutoStartAsync(enable, asAdmin);

            if (error != null)
            {
                // 못 바꿨으면 체크도 원래대로 돌린다. (화면과 실제가 달라지지 않게)
                AutoStartBox.IsChecked = !enable;
                ShowHint("자동 실행 설정을 바꾸지 못했습니다: " + error, 8);
            }
            else
            {
                ShowHint(enable ? "Windows 시작 시 자동으로 실행됩니다." : "자동 실행을 껐습니다.", 4);
            }

            // 자동 실행이 꺼져 있으면 "숨긴 채로 시작" 은 의미가 없어서 잠근다.
            StartHiddenBox.IsEnabled = AutoStartBox.IsChecked == true;

            DiagLog.Write($"설정 | 자동 실행 {AutoStartBox.IsChecked} (관리자 {asAdmin}) | {error ?? "성공"}");
        }

        // [관리자 권한으로 실행] 체크
        //
        // 켜면: 다음부터 켤 때 관리자 권한으로 실행한다. (App.xaml.cs 가 확인 창을 띄워 다시 실행)
        //       자동 실행도 켜져 있으면 작업 스케줄러로 옮긴다. (로그인할 때 확인 창 없이 관리자로 켜지게)
        // 끄면: 반대로 되돌린다.
        private async void RunAsAdminBox_Click(object sender, RoutedEventArgs e)
        {
            bool asAdmin = RunAsAdminBox.IsChecked == true;

            if (AutoStartBox.IsChecked == true)
            {
                ShowHint("자동 실행 방식을 바꿉니다. Windows 확인 창에서 [예] 를 눌러주세요.", 10);

                string? error = await ApplyAutoStartAsync(true, asAdmin);

                if (error != null)
                {
                    // 자동 실행 방식을 못 바꿨으면 이 설정도 되돌린다. (둘이 어긋나지 않게)
                    RunAsAdminBox.IsChecked = !asAdmin;
                    ShowHint("설정을 바꾸지 못했습니다: " + error, 8);
                    DiagLog.Write($"설정 | 관리자로 실행 {asAdmin} 실패 | {error}");
                    return;
                }
            }

            WriteSetting(SettingKeys.RunAsAdmin, asAdmin ? "1" : "0");
            DiagLog.Write($"설정 | 관리자로 실행 {asAdmin}");

            ShowHint(asAdmin && !Elevation.IsElevated
                ? "다음에 켤 때부터 관리자 권한으로 실행됩니다. 지금 바로 바꾸려면 [관리자 권한으로 다시 시작] 을 누르세요."
                : asAdmin
                    ? "관리자 권한으로 실행하도록 저장했습니다."
                    : "다음에 켤 때부터 일반 권한으로 실행됩니다.", 8);
        }

        // [관리자 권한으로 다시 시작]
        //
        // 관리자 권한으로 새로 띄우고 지금 프로그램은 끝낸다.
        // 새 프로그램은 지금 프로그램이 끝날 때까지 기다렸다가 시작한다. (포트가 겹치지 않게)
        // 지금 연결은 끊긴다. 마지막으로 조종하던 기기면 새 프로그램이 다시 자동 연결한다.
        private void RestartAdminButton_Click(object sender, RoutedEventArgs e)
        {
            string args = $"{Elevation.WaitPidArgument} {Environment.ProcessId}";

            DiagLog.Write("관리자 권한으로 다시 시작 요청");

            if (Elevation.StartSelfElevated(args, out bool cancelled, out string error))
            {
                ExitApp();
                return;
            }

            ShowHint(cancelled
                ? "관리자 권한 확인 창에서 [아니요] 를 눌러 다시 시작하지 않았습니다."
                : "관리자 권한으로 다시 시작하지 못했습니다: " + error, 6);
        }

        private void StartHiddenBox_Click(object sender, RoutedEventArgs e)
        {
            WriteSetting(SettingKeys.StartHidden, StartHiddenBox.IsChecked == true ? "1" : "0");
        }

        private void CloseToTrayBox_Click(object sender, RoutedEventArgs e)
        {
            WriteSetting(SettingKeys.CloseToTray, CloseToTrayBox.IsChecked == true ? "1" : "0");
        }
    }
}
