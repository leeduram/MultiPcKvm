using Microsoft.Win32;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  Windows 시작 시 자동 실행
    //
    //  레지스트리 "현재 사용자 Run" 에 이 프로그램 경로를 적어두면
    //  Windows 에 로그인할 때 자동으로 실행된다.
    //
    //    HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
    //      MultiPcKvm = "C:\...\MultiPcKvm.Wpf.exe" --autostart
    //
    //  HKEY_CURRENT_USER(현재 사용자)라 관리자 권한이 필요 없다.
    //  작업 관리자 → 시작 앱 목록에도 이 이름으로 보인다. (거기서 꺼도 된다)
    //
    //  --autostart 를 붙이는 이유:
    //    사용자가 직접 켠 건지, Windows 가 켠 건지 구분하려고.
    //    Windows 가 켰을 때만 "트레이에 숨긴 채로 시작" 설정을 따른다. (App.xaml.cs)
    //
    //  켜져 있는지는 레지스트리 값 자체로 판단한다. 따로 DB 에 적지 않는다.
    //  (작업 관리자에서 끄거나 지운 경우도 그대로 반영되도록)
    //
    //  단, "관리자 권한으로 실행" 이 켜져 있으면 레지스트리 대신 작업 스케줄러를 쓴다. (아래 설명)
    // ─────────────────────────────────────────────────────────────
    public static class AutoStart
    {
        public const string Argument = "--autostart";

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MultiPcKvm";

        // 레지스트리에 적을 실행 명령. 경로에 공백이 있을 수 있어서 따옴표로 감싼다.
        private static string Command =>
            $"\"{Environment.ProcessPath}\" {Argument}";

        public static bool IsEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }

        // 켜거나 끈다. 실패하면 이유를 돌려준다. (성공하면 null)
        public static string? Set(bool enable)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

                if (enable)
                    key.SetValue(ValueName, Command);
                else
                    key.DeleteValue(ValueName, throwOnMissingValue: false);

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // ═════════════════════════════════════════════════════
        //  관리자 권한으로 자동 실행 (작업 스케줄러)
        // ═════════════════════════════════════════════════════
        //
        // 위의 레지스트리 Run 방식은 관리자 권한으로 실행할 수 없다.
        // Windows 가 로그인할 때 확인 창(UAC)을 띄울 수 없어서, 관리자 권한이 필요한 프로그램은 건너뛴다.
        //
        // 그래서 "관리자 권한으로 실행" 이 켜져 있으면 작업 스케줄러에 등록한다.
        //   · 로그인할 때 실행 (이 사용자만)
        //   · 가장 높은 권한으로 실행 → 확인 창 없이 관리자 권한으로 켜진다
        //   · 등록·삭제할 때만 확인 창이 한 번 뜬다 (작업 스케줄러에 관리자 작업을 넣는 일이라)
        //
        // 작업 스케줄러 기본값 중 이 프로그램에 맞지 않는 것을 바꾼다.
        //   · 3일 넘게 실행되면 강제 종료        → 제한 없음 (계속 켜져 있어야 한다)
        //   · 배터리로 돌면 시작 안 함·중지       → 노트북에서도 실행
        //   · 실행 우선순위 7 (보통보다 낮음)      → 4 (보통). 낮으면 입력 전달이 밀릴 수 있다
        //
        // 작업 스케줄러 → 작업 스케줄러 라이브러리 에서 "MultiPcKvm" 으로 보인다.

        public const string TaskName = "MultiPcKvm";

        // 작업을 등록한다. (이미 있으면 덮어쓴다) 실패하면 이유를 돌려준다.
        public static string? RegisterTask(out bool cancelled)
        {
            string exe = Environment.ProcessPath ?? "";
            string dir = AppContext.BaseDirectory;
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

            string script = $@"
$ErrorActionPreference = 'Stop'
try {{
  $a = New-ScheduledTaskAction -Execute {Elevation.PsQuote(exe)} -Argument '{Argument}' -WorkingDirectory {Elevation.PsQuote(dir)}
  $t = New-ScheduledTaskTrigger -AtLogOn -User {Elevation.PsQuote(user)}
  $p = New-ScheduledTaskPrincipal -UserId {Elevation.PsQuote(user)} -LogonType Interactive -RunLevel Highest
  $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Priority 4
  Register-ScheduledTask -TaskName '{TaskName}' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null
  exit 0
}} catch {{ exit 1 }}
";

            return Elevation.RunPowerShellAsAdmin(script, out cancelled, out string error)
                ? null
                : (cancelled ? "취소했습니다." : error);
        }

        // 작업을 지운다. (없으면 그냥 성공) 실패하면 이유를 돌려준다.
        public static string? UnregisterTask(out bool cancelled)
        {
            string script = $@"
$ErrorActionPreference = 'Stop'
try {{
  Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue
  exit 0
}} catch {{ exit 1 }}
";

            return Elevation.RunPowerShellAsAdmin(script, out cancelled, out string error)
                ? null
                : (cancelled ? "취소했습니다." : error);
        }

        // 켜져 있는데 적힌 경로가 지금 실행 파일과 다르면 지금 경로로 고친다.
        //
        // 프로그램 폴더를 옮기거나 새로 받으면 예전 경로가 남아서
        // Windows 시작 때 "파일을 찾을 수 없음" 이 되기 때문이다.
        public static void RefreshPathIfEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

                if (key?.GetValue(ValueName) is string current &&
                    !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, Command);
                }
            }
            catch
            {
                // 못 고쳐도 프로그램 동작에는 문제없다.
            }
        }
    }
}
