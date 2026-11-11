using System.Diagnostics;
using System.Windows;
using MultiPcKvm.App.Storage;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  프로그램 시작
    //
    //  창을 띄울지, 트레이에 숨긴 채로 시작할지 여기서 정한다.
    //
    //    Windows 가 자동 실행으로 켰고(--autostart) + "숨긴 채로 시작" 이 켜져 있으면
    //      → 창을 띄우지 않는다. 검색·연결은 그대로 돌아간다. 트레이 아이콘을 누르면 창이 뜬다.
    //    그 밖 (사용자가 직접 켬, 또는 설정이 꺼져 있음)
    //      → 창을 띄운다.
    //
    //  맨 먼저: Uninstall.exe 로 켜졌으면(또는 --uninstall) 삭제만 하고 끝낸다. (Uninstaller.cs)
    //
    //  그 전에 두 가지를 먼저 처리한다.
    //    --wait-pid 번호 : [관리자 권한으로 다시 시작] 으로 켜졌다. 이전 프로그램이 끝날 때까지 기다린다.
    //    "관리자 권한으로 실행" 이 켜져 있는데 지금 일반 권한이면 → 확인 창을 띄워 관리자로 다시 실행하고 끝낸다.
    // ─────────────────────────────────────────────────────────────
    public partial class App : Application
    {
        private void App_Startup(object sender, StartupEventArgs e)
        {
            // 0) 삭제 모드. 다른 일(DB 열기·관리자 재실행·검색)은 하나도 하지 않는다.
            //    (DB 를 열면 지우려는 파일을 다시 만들게 된다)
            if (Uninstaller.IsRequested(e.Args))
            {
                Uninstaller.Run();
                Shutdown();
                return;
            }

            // 프로그램 폴더에 Uninstall.exe 가 있는지 챙긴다. (없으면 만든다)
            Uninstaller.EnsureUninstallExe();

            bool fromAutoStart = e.Args.Any(a =>
                string.Equals(a, AutoStart.Argument, StringComparison.OrdinalIgnoreCase));

            // 1) 이전 프로그램이 끝나길 기다린다. (포트를 놓을 때까지. 최대 10초)
            WaitForPreviousInstance(e.Args);

            // 2) "관리자 권한으로 실행" 이면 관리자로 다시 실행하고 이 프로그램은 끝낸다.
            //    아직 포트를 열기 전이라 기다릴 필요 없이 바로 끝내면 된다.
            bool elevationDeclined = false;

            if (!Elevation.IsElevated && ReadRunAsAdmin())
            {
                string args = fromAutoStart ? AutoStart.Argument : "";

                if (Elevation.StartSelfElevated(args, out bool cancelled, out _))
                {
                    Shutdown();
                    return;
                }

                // [아니요] 를 눌렀으면 일반 권한으로 그냥 켠다. (창에 안내가 뜬다)
                elevationDeclined = cancelled;
            }

            var window = new MainWindow { ElevationDeclined = elevationDeclined };
            MainWindow = window;

            // 준비 작업(DB·설정·검색)을 먼저 한다. 그래야 "숨긴 채로 시작" 설정을 읽을 수 있다.
            // 창을 띄우면 Loaded 에서 또 부르지만, 한 번만 하도록 막혀 있다.
            window.StartServices();

            if (fromAutoStart && window.StartHiddenEnabled)
                return;     // 트레이에만 있는다

            window.Show();
        }

        // --wait-pid 번호 가 있으면 그 프로그램이 끝날 때까지 기다린다.
        private static void WaitForPreviousInstance(string[] args)
        {
            int index = Array.FindIndex(args, a =>
                string.Equals(a, Elevation.WaitPidArgument, StringComparison.OrdinalIgnoreCase));

            if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int pid))
                return;

            try
            {
                using Process previous = Process.GetProcessById(pid);
                previous.WaitForExit(10_000);
            }
            catch
            {
                // 이미 끝났으면 GetProcessById 가 예외를 던진다. 기다릴 필요 없다.
            }
        }

        // DB 에서 "관리자 권한으로 실행" 설정을 읽는다. 못 읽으면 꺼진 것으로 본다.
        // (창이 만들어지기 전이라 MainWindow 의 저장소를 쓸 수 없어서 여기서 잠깐 연다)
        private static bool ReadRunAsAdmin()
        {
            try
            {
                var db = new KvmDatabase(KvmDatabase.DefaultPath);
                db.EnsureCreated();
                return new DeviceRepository(db).GetSetting("run_as_admin") == "1";
            }
            catch
            {
                return false;
            }
        }

        // Windows 를 끄거나 로그아웃할 때.
        //
        // "닫으면 트레이로 숨기기" 가 켜져 있으면 창 닫기를 막는데,
        // Windows 가 끌 때까지 막으면 "이 앱 때문에 종료할 수 없습니다" 가 뜬다.
        // 그래서 이때는 숨기지 않고 정말로 끝낸다.
        private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            (MainWindow as MainWindow)?.ExitApp();
        }
    }
}
