using System.Diagnostics;
using System.IO;    // Path, File, Directory. WPF 프로젝트는 System.IO 를 자동으로 넣어주지 않는다
using System.Windows;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  프로그램 삭제 (Uninstall.exe)
    //
    //  프로그램 폴더의 Uninstall.exe 를 더블클릭하면 이 PC 를 프로그램 쓰기 전 상태로 되돌린다.
    //  사용자가 명령어를 칠 필요가 없다.
    //
    //  ─── Uninstall.exe 는 무엇인가 ───
    //    MultiPcKvm.Wpf.exe 를 이름만 바꿔 복사한 것이다. (빌드·게시할 때 csproj 가 자동으로 만든다)
    //    .NET 실행 파일은 이름이 바뀌어도 같은 프로그램(MultiPcKvm.Wpf.dll)을 실행한다.
    //    그래서 따로 프로그램을 만들지 않고, "내 이름이 Uninstall 이면 삭제 모드" 로 동작한다.
    //    (MultiPcKvm.Wpf.exe --uninstall 로 켜도 같다)
    //
    //  ─── 지우는 것 (MultiPcKvm 이 만든 것 전부) ───
    //    1. 실행 중인 MultiPcKvm            → 끈다 (파일을 잡고 있으면 못 지우므로)
    //    2. 자동 실행 (레지스트리 Run 값)
    //    3. 자동 실행 (작업 스케줄러 "MultiPcKvm", 관리자로 실행을 켰던 경우)
    //    4. 방화벽 규칙 "MultiPcKvm (UDP)", "MultiPcKvm (TCP)"
    //       + Windows 가 처음 실행 때 물어보고 만든 이 프로그램 규칙 (허용·차단 모두)
    //    5. %APPDATA%\MultiPcKvm             → 저장된 기기·설정 DB, 이 PC ID
    //    6. 다운로드\MultiPcKvm (받은 파일)  → 사용자 파일이라 따로 물어본다
    //    7. 프로그램 폴더 (실행 파일, 진단 기록, Uninstall.exe 자신)
    //       → 실행 중인 파일은 스스로 못 지우므로, 이 프로그램이 끝난 뒤 2초 있다가 지워지게 해 둔다.
    //
    //  3, 4 는 관리자 권한이 필요하다. 지울 게 있을 때만 Windows 확인 창이 한 번 뜬다.
    //
    //  ─── 안전장치 ───
    //    프로그램 폴더는 이름에 "MultiPcKvm" 이 들어 있을 때만 통째로 지운다.
    //    바탕화면처럼 다른 파일이 같이 있는 폴더에 풀어 놓았을 때 남의 파일까지 지우면 안 되기 때문이다.
    //    (그때는 진단 기록만 지우고 "폴더는 직접 지워 주세요" 라고 알린다)
    //
    //  삭제 모드에서는 진단 기록(DiagLog)을 쓰지 않는다. 쓰면 지우려는 파일을 다시 만들게 된다.
    // ─────────────────────────────────────────────────────────────
    internal static class Uninstaller
    {
        public const string Argument = "--uninstall";

        private const string Title = "MultiPcKvm 삭제";

        // 메인 프로그램의 프로세스 이름 (MultiPcKvm.Wpf.exe)
        private const string MainProcessName = "MultiPcKvm.Wpf";

        // 삭제 모드로 켜졌는가. (이름이 Uninstall 이거나 --uninstall)
        public static bool IsRequested(string[] args)
        {
            if (args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase)))
                return true;

            string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            return name.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
        }

        // Uninstall.exe 가 프로그램 폴더에 없으면 만든다. (평소 켤 때 부른다)
        //
        // 빌드·게시할 때 csproj 가 만들어 두지만, 게시 방법에 따라 빠질 수 있어서 한 번 더 챙긴다.
        // 내 실행 파일(작은 시작 파일)을 Uninstall.exe 라는 이름으로 복사할 뿐이다.
        // 폴더에 쓸 수 없으면(Program Files 등) 조용히 넘어간다.
        public static void EnsureUninstallExe()
        {
            try
            {
                string? self = Environment.ProcessPath;

                if (self == null ||
                    !string.Equals(Path.GetFileNameWithoutExtension(self), MainProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return;     // dotnet 으로 실행 중이거나 이름이 다르면 하지 않는다
                }

                // 시작 파일은 수백 KB 다. 너무 크면(단일 파일 게시: 프로그램 전체가 들어 있음) 복사하지 않는다.
                if (new FileInfo(self).Length > 5 * 1024 * 1024)
                    return;

                string target = Path.Combine(Path.GetDirectoryName(self)!, "Uninstall.exe");

                if (!File.Exists(target))
                    File.Copy(self, target);
            }
            catch
            {
            }
        }

        public static void Run()
        {
            string programDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            string mainExe = Path.Combine(programDir, MainProcessName + ".exe");
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiPcKvm");
            string received = MainWindow.ReceiveFolder();

            // ── 1) 정말 지울지 묻는다 ──
            MessageBoxResult answer = MessageBox.Show(
                "MultiPcKvm 을 이 PC 에서 지웁니다.\n\n" +
                "지우는 것\n" +
                "  · 프로그램 폴더 (실행 파일, 진단 기록)\n" +
                "  · 저장된 기기와 설정\n" +
                "  · 윈도우 시작 시 자동 실행 등록\n" +
                "  · 방화벽 규칙\n\n" +
                "실행 중인 MultiPcKvm 은 꺼집니다. 계속할까요?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            // 받은 파일은 사용자 파일이라 따로 묻는다. (기본은 지우지 않음)
            bool deleteReceived = false;
            int receivedCount = CountFiles(received);

            if (receivedCount > 0)
            {
                deleteReceived = MessageBox.Show(
                    $"받은 파일 {receivedCount}개가 있습니다.\n{received}\n\n이 파일들도 지울까요?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                    == MessageBoxResult.Yes;
            }

            var done = new List<string>();
            var failed = new List<string>();

            // ── 2) 실행 중인 프로그램 끄기 ──
            List<Process> running = StopRunningInstances(out bool stopFailed);

            // ── 3) 관리자 권한이 필요한 것 (작업 스케줄러, 방화벽, 관리자로 실행 중인 프로그램 끄기) ──
            bool taskExists = ScheduledTaskExists();
            bool rulesExist = FirewallRulesExist(mainExe);

            if (taskExists || rulesExist || stopFailed)
            {
                string script = $@"
$ErrorActionPreference = 'SilentlyContinue'
Stop-Process -Name '{MainProcessName}' -Force
Unregister-ScheduledTask -TaskName '{AutoStart.TaskName}' -Confirm:$false
Remove-NetFirewallRule -DisplayName '{FirewallHelper.UdpRuleName}','{FirewallHelper.TcpRuleName}'
Get-NetFirewallApplicationFilter -Program {Elevation.PsQuote(mainExe)} | Get-NetFirewallRule | Remove-NetFirewallRule
exit 0
";

                bool ok = Elevation.RunPowerShellAsAdmin(script, out bool cancelled, out string error);

                if (ok)
                {
                    if (taskExists) done.Add("자동 실행 (작업 스케줄러)");
                    if (rulesExist) done.Add("방화벽 규칙");
                }
                else
                {
                    string why = cancelled ? "Windows 확인 창에서 취소함" : error;

                    if (taskExists) failed.Add($"자동 실행 (작업 스케줄러) — {why}");
                    if (rulesExist) failed.Add($"방화벽 규칙 — {why}");

                    // 관리자로 실행 중인 프로그램을 못 껐으면 파일을 지울 수 없다. 여기서 멈춘다.
                    if (stopFailed)
                    {
                        MessageBox.Show(
                            "관리자 권한으로 실행 중인 MultiPcKvm 을 끄지 못했습니다.\n" +
                            "트레이 아이콘을 오른쪽 클릭해서 [종료] 한 뒤 다시 Uninstall 을 실행해 주세요.",
                            Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }

            // 꺼질 때까지 기다린다. (파일을 놓아야 지울 수 있다)
            foreach (Process p in running)
            {
                try { p.WaitForExit(5000); } catch { }
                p.Dispose();
            }

            // ── 4) 자동 실행 (레지스트리) ──
            if (AutoStart.IsEnabled())
            {
                if (AutoStart.Set(false) is string error)
                    failed.Add("자동 실행 (레지스트리) — " + error);
                else
                    done.Add("자동 실행 (레지스트리)");
            }

            // ── 5) 저장된 기기·설정 ──
            if (Directory.Exists(appData))
            {
                if (DeleteDirectory(appData, out string error))
                    done.Add("저장된 기기와 설정");
                else
                    failed.Add($"저장된 기기와 설정 ({appData}) — {error}");
            }

            // ── 6) 받은 파일 ──
            if (deleteReceived)
            {
                if (DeleteDirectory(received, out string error))
                    done.Add("받은 파일");
                else
                    failed.Add($"받은 파일 ({received}) — {error}");
            }

            // ── 7) 프로그램 폴더 ──
            bool removeFolder = IsSafeToRemoveWholeFolder(programDir);

            if (!removeFolder)
            {
                // 폴더를 통째로 지우지 않을 때는 진단 기록만 지운다.
                foreach (string log in new[] { "kvm-log.txt", "kvm-log.old.txt" })
                {
                    try { File.Delete(Path.Combine(programDir, log)); } catch { }
                }

                failed.Add(
                    $"프로그램 폴더 — 폴더 이름에 MultiPcKvm 이 없어서 다른 파일이 섞여 있을 수 있습니다.\n" +
                    $"      직접 지워 주세요: {programDir}");
            }

            // ── 결과 알리기 ──
            if (removeFolder)
                done.Add("프로그램 폴더 (이 창을 닫으면 2초 뒤 지워집니다)");

            string message = "삭제를 마쳤습니다.\n";

            if (done.Count > 0)
                message += "\n지운 것\n" + string.Join("\n", done.Select(d => "  · " + d)) + "\n";

            if (!deleteReceived && receivedCount > 0)
                message += $"\n남겨 둔 것\n  · 받은 파일 ({received})\n";

            if (failed.Count > 0)
                message += "\n지우지 못한 것\n" + string.Join("\n", failed.Select(f => "  · " + f)) + "\n";

            MessageBox.Show(message, Title, MessageBoxButton.OK,
                failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            // 창을 닫은 뒤에 폴더를 지운다. (이 프로그램이 끝나야 실행 파일을 지울 수 있다)
            if (removeFolder)
                ScheduleFolderRemoval(programDir);
        }


        // ── 실행 중인 프로그램 끄기 ────────────────────────────
        //
        // 트레이에 숨어 있을 수 있어서 창 닫기로는 안 된다. 바로 끈다.
        // 끄면 후킹·커서 숨김 창도 Windows 가 같이 정리한다. (연결된 상대는 연결이 끊긴 것으로 보고 스스로 푼다)
        //
        // 관리자로 실행 중인 것은 일반 권한으로 못 끈다 → stopFailed = true (관리자 단계에서 끈다)
        private static List<Process> StopRunningInstances(out bool stopFailed)
        {
            stopFailed = false;
            var list = new List<Process>();
            int me = Environment.ProcessId;

            foreach (Process p in Process.GetProcessesByName(MainProcessName))
            {
                if (p.Id == me)
                {
                    p.Dispose();
                    continue;
                }

                try
                {
                    p.Kill();
                }
                catch
                {
                    stopFailed = true;
                }

                list.Add(p);
            }

            return list;
        }


        // ── 지울 게 있는지 확인 (관리자 권한 없이) ─────────────
        //
        // 없으면 Windows 확인 창을 띄우지 않으려고 미리 본다.
        // 확인하다 실패하면 "있다" 로 본다. (못 지우고 남는 것보다 확인 창 한 번이 낫다)

        // 작업 스케줄러에 "MultiPcKvm" 작업이 있는가
        private static bool ScheduledTaskExists()
        {
            try
            {
                var info = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{AutoStart.TaskName}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,          // 검은 창이 뜨지 않게
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using Process? p = Process.Start(info);

                if (p == null)
                    return true;

                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(10_000);

                return p.ExitCode == 0;     // 0 = 있음
            }
            catch
            {
                return true;
            }
        }

        // 우리 규칙(이름) 또는 이 프로그램 경로에 걸린 규칙이 있는가
        private static bool FirewallRulesExist(string mainExe)
        {
            try
            {
                Type? type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");

                if (type == null)
                    return true;

                dynamic policy = Activator.CreateInstance(type)!;

                foreach (dynamic r in policy.Rules)
                {
                    try
                    {
                        string name = (string)(r.Name ?? "");
                        string app = (string)(r.ApplicationName ?? "");

                        if (name == FirewallHelper.UdpRuleName ||
                            name == FirewallHelper.TcpRuleName ||
                            string.Equals(app, mainExe, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // 이 규칙은 건너뛴다
                    }
                }

                return false;
            }
            catch
            {
                return true;
            }
        }


        // ── 파일·폴더 ─────────────────────────────────────────

        private static int CountFiles(string folder)
        {
            try
            {
                return Directory.Exists(folder)
                    ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        // 폴더를 통째로 지운다. 방금 끈 프로그램이 파일을 놓는 데 잠깐 걸릴 수 있어서 몇 번 다시 해 본다.
        private static bool DeleteDirectory(string folder, out string error)
        {
            error = "";

            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, recursive: true);

                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Thread.Sleep(300);
                }
            }

            return false;
        }

        // 프로그램 폴더를 통째로 지워도 되는가. (위 "안전장치" 설명)
        private static bool IsSafeToRemoveWholeFolder(string dir)
        {
            try
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

                // 폴더 이름에 MultiPcKvm 이 들어 있어야 한다.
                if (!Path.GetFileName(full).Contains("MultiPcKvm", StringComparison.OrdinalIgnoreCase))
                    return false;

                // 정말 이 프로그램 폴더인지 (본체가 있는지)
                if (!File.Exists(Path.Combine(full, MainProcessName + ".dll")))
                    return false;

                // 드라이브 맨 위나 Windows 가 쓰는 폴더 자체는 절대 지우지 않는다.
                string? root = Path.GetPathRoot(full);

                if (root != null && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
                    return false;

                foreach (Environment.SpecialFolder special in new[]
                         {
                             Environment.SpecialFolder.Desktop,
                             Environment.SpecialFolder.MyDocuments,
                             Environment.SpecialFolder.UserProfile,
                             Environment.SpecialFolder.ProgramFiles,
                             Environment.SpecialFolder.ProgramFilesX86,
                             Environment.SpecialFolder.Windows,
                             Environment.SpecialFolder.ApplicationData,
                             Environment.SpecialFolder.LocalApplicationData,
                         })
                {
                    string path = Environment.GetFolderPath(special);

                    if (path.Length > 0 &&
                        string.Equals(Path.TrimEndingDirectorySeparator(path), full, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        // 이 프로그램이 끝난 뒤 폴더를 지우게 해 둔다.
        //
        // 실행 중인 exe 는 지울 수 없어서, 보이지 않는 명령 창에 "2초 기다렸다가 폴더 지우기" 를 맡기고 끝낸다.
        // 명령 창의 작업 폴더는 임시 폴더로 둔다. (지울 폴더 안에 있으면 그 폴더가 잡혀서 안 지워진다)
        private static void ScheduleFolderRemoval(string dir)
        {
            try
            {
                string temp = Path.GetTempPath();

                // 이 프로그램도 지울 폴더를 붙잡지 않게 작업 폴더를 옮긴다.
                Environment.CurrentDirectory = temp;

                var info = new ProcessStartInfo(
                    "cmd.exe",
                    $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = temp,
                };

                Process.Start(info)?.Dispose();
            }
            catch
            {
                // 못 맡겼으면 폴더가 남는다. (사용자가 직접 지우면 된다)
            }
        }
    }
}
