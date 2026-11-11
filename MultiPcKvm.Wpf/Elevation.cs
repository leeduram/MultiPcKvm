using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  관리자 권한 관련
    //
    //  관리자 권한이 필요한 경우 (상대 PC 쪽)
    //    조종당하는 PC 에서 관리자 권한으로 실행된 창(작업 관리자, 설치 프로그램 등)이 맨 앞에 있으면,
    //    일반 권한 프로그램이 보내는 마우스·키보드 입력을 Windows 가 막는다. (UIPI)
    //    이 프로그램도 관리자 권한이면 그 창도 조작할 수 있다.
    //
    //  ※ Windows 로그인 화면·확인 창(보안 화면)은 관리자 권한이어도 조작할 수 없다.
    //
    //  관리자 권한으로 올리는 방법은 하나뿐이다: "runas" 로 새로 실행 → Windows 확인 창(UAC).
    //  이미 실행 중인 프로그램의 권한을 올릴 수는 없다. 그래서 "다시 시작" 이 된다.
    // ─────────────────────────────────────────────────────────────
    public static class Elevation
    {
        // 다시 시작할 때 새 프로그램에 넘기는 값: "이 번호의 프로그램이 끝날 때까지 기다려라"
        //
        // 기다리는 이유: 이전 프로그램이 45000·45001 포트를 쥐고 있는 동안 새 프로그램이 열면
        // 포트 열기에 실패해서 검색·연결이 안 된다. (App.xaml.cs 가 처리)
        public const string WaitPidArgument = "--wait-pid";

        // 지금 관리자 권한으로 실행 중인가.
        //
        // UAC 가 켜져 있으면 관리자 계정이어도 평소에는 일반 권한으로 실행된다.
        // 이 검사는 "지금 실제로 권한이 올라가 있는가" 를 본다.
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch
                {
                    return false;
                }
            }
        }

        // 이 프로그램을 관리자 권한으로 새로 실행한다. (Windows 확인 창이 뜬다)
        //
        // 성공하면 true → 부른 쪽이 지금 프로그램을 끝내야 한다.
        // 확인 창에서 [아니요] 를 누르면 cancelled = true.
        public static bool StartSelfElevated(string arguments, out bool cancelled, out string error)
        {
            cancelled = false;
            error = "";

            var info = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "",
                Arguments = arguments,
                Verb = "runas",               // 관리자 권한으로 실행 → Windows 확인 창
                UseShellExecute = true,       // runas 는 이게 true 여야 동작한다
                WorkingDirectory = AppContext.BaseDirectory,
            };

            try
            {
                using Process? p = Process.Start(info);

                if (p == null)
                {
                    error = "실행하지 못했습니다.";
                    return false;
                }

                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 1223 = 사용자가 확인 창에서 [아니요] 를 누름
                cancelled = true;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // PowerShell 명령을 관리자 권한으로 실행하고 끝날 때까지 기다린다.
        //
        // 이미 관리자 권한이면 확인 창 없이 바로 실행된다.
        // 명령 안에서 실패하면 exit 1 로 끝내도록 써야 false 를 받을 수 있다.
        public static bool RunPowerShellAsAdmin(string script, out bool cancelled, out string error)
        {
            cancelled = false;
            error = "";

            // 스크립트를 임시 파일로 저장하지 않고 명령줄에 그대로 넘긴다.
            // 임시 파일이면 관리자 권한으로 실행되기 직전에 다른 프로그램이 내용을 바꿔치기할 수 있다.
            // -EncodedCommand 는 UTF-16 을 Base64 로 바꾼 값을 받는다. (따옴표 문제도 없다)
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            try
            {
                using Process? p = Process.Start(info);

                if (p == null)
                {
                    error = "실행하지 못했습니다.";
                    return false;
                }

                if (!p.WaitForExit(60_000))
                {
                    error = "시간이 너무 오래 걸립니다.";
                    return false;
                }

                if (p.ExitCode != 0)
                {
                    error = $"실패했습니다. (종료 코드 {p.ExitCode})";
                    return false;
                }

                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                cancelled = true;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // PowerShell 작은따옴표 문자열 안에 넣을 수 있게 바꾼다. (' → '')
        public static string PsQuote(string text) => "'" + text.Replace("'", "''") + "'";
    }
}
