using System.IO;    // Path, File, Directory. WPF 프로젝트는 System.IO 를 자동으로 넣어주지 않는다
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;              // Button, ContextMenu, MenuItem (메인 창 [파일] 메뉴)
using System.Windows.Controls.Primitives;   // PlacementMode
using System.Windows.Media;
using Microsoft.Win32;  // OpenFileDialog (WPF 용 파일 고르기 창)
using MultiPcKvm.App;
using MultiPcKvm.Common;
using MultiPcKvm.Network.Connection;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  MainWindow — 파일 보내기 · 받기
    //
    //  버튼은 두 곳에 있다. 하는 일은 같다.
    //    · 메인 창: 조종 중인 기기 줄의 [파일] → 메뉴 [파일 보내기] [파일 받기]
    //    · 화면 보기 창(ViewerWindow): 창 모드 위쪽 줄, 전체 화면이면 위쪽 가운데 막대
    //
    //  [파일 보내기] 조종하는 A → 조종당하는 B
    //    A 에서 파일 고르기 창이 뜬다 → 고른 파일을 B 로 보낸다
    //    → B 의 "다운로드\MultiPcKvm" 폴더에 저장된다
    //
    //  [파일 받기]  조종당하는 B → 조종하는 A
    //    A 가 B 에게 "파일 골라 줘"(MessageType.FileRequest) 를 보낸다
    //    → B 의 화면에 "보낼 파일 고르기" 창이 뜬다
    //    → A 가 그 창을 고른다. 화면 보기 창으로 보면서 골라도 되고,
    //      화면 보기 없이 B 의 모니터를 직접 보면서 평소처럼 커서를 넘겨 골라도 된다.
    //    → B 가 고른 파일을 A 로 보낸다 → A 의 "다운로드\MultiPcKvm" 폴더에 저장된다
    //
    //  파일 데이터를 실제로 주고받는 것은 김기령님 파일 전송 기능(IFileTransfer)이 한다.
    //  약속은 MultiPcKvm.Common 의 FileTransfer.cs, 만드는 곳은 ScreenShareFactory.cs.
    //  여기는 허락 여부, 저장 위치, 진행 표시만 맡는다.
    //
    //  필드(공용 상태)·생성자·시작·Tick 은 MainWindow.xaml.cs 에 있다.
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // 파일 전송 기능. 아직 없으면 null.
        private IFileTransfer? _fileTransfer;

        // 지금 보내는 중이면 멈추는 손잡이, 아니면 null. (한 번에 한 묶음만 보낸다)
        private CancellationTokenSource? _fileSendCts;

        // 지금 보내는 상대 주소. (연결이 끊기면 Tick 에서 보내기를 멈추려고)
        private string? _fileSendIp;

        // 조종당하는 쪽: "보낼 파일 고르기" 창이 지금 떠 있는가. (요청이 여러 번 와도 하나만 띄운다)
        private Window? _fileRequestOwner;

        // 받기에 이미 내준 저장 경로. 두 파일이 거의 동시에 오면 같은 이름을 내주지 않도록.
        // (파일은 다 받은 뒤에야 폴더에 생기므로, 폴더만 보고는 겹침을 못 막는다)
        private readonly HashSet<string> _reservedSavePaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _savePathLock = new();

        private const string NoEngineText = "파일 전송 기능이 아직 연결되지 않았습니다.";


        // ── 시작 · 정리 ───────────────────────────────────────

        // 파일 받기 준비. (StartServices 에서 부른다)
        private void StartFileTransfer()
        {
            _fileTransfer = ScreenShareFactory.CreateFileTransfer();

            if (_fileTransfer == null)
            {
                DiagLog.Write("파일 전송 | 기능 없음 (ScreenShareFactory)");
                return;
            }

            // 파일 전송 쪽이 물어보는 것들에 답할 함수를 건넨다.
            _fileTransfer.CanTransfer = CanTransferWith;
            _fileTransfer.ChooseSavePath = ChooseSavePathFor;

            _fileTransfer.FileReceived += OnFileReceived;
            _fileTransfer.Progress += OnFileProgress;
            _fileTransfer.Failed += OnFileFailed;

            try
            {
                _fileTransfer.StartListening();
                DiagLog.Write($"파일 전송 준비 | 포트 {Constants.FileTransferPort}");
            }
            catch (Exception ex)
            {
                DiagLog.Write($"파일 전송 준비 실패 | {ex.Message}");
                ShowHint("파일 받기를 준비하지 못했습니다: " + ex.Message, 8);
                _fileTransfer.Dispose();
                _fileTransfer = null;
            }
        }

        // 파일 전송 정리. (Cleanup 에서 부른다)
        private void StopFileTransfer()
        {
            _fileSendCts?.Cancel();

            try
            {
                _fileTransfer?.StopListening();
                _fileTransfer?.Dispose();
            }
            catch (Exception ex)
            {
                DiagLog.Write($"파일 전송 정리 실패 | {ex.Message}");
            }

            _fileTransfer = null;
        }

        // 보내는 도중 연결이 끊겼으면 멈춘다. (Tick 에서 0.3초마다 부른다)
        // 끊긴 뒤에도 계속 보내면 조종 관계가 아닌 PC 로 파일이 가게 된다. (받는 쪽도 거절하지만 미리 막는다)
        private void CheckFileSendLink()
        {
            if (_fileSendCts != null && _fileSendIp != null &&
                !_fileSendCts.IsCancellationRequested && !CanTransferWith(_fileSendIp))
            {
                DiagLog.Write($"파일 보내기 멈춤 | 연결 끊김 | {_fileSendIp}");
                _fileSendCts.Cancel();
            }
        }


        // ── 허락 · 저장 위치 (파일 전송 쪽이 묻는다. 어느 스레드에서든 온다) ──

        // 이 주소와 파일을 주고받아도 되는가.
        //
        // "지금 조종 관계인 상대" 일 때만 허락한다. 이게 없으면 같은 공유기의 누구나 내 PC 에 파일을 넣을 수 있다.
        //   · 내가 조종당하는 중 : 코드(또는 열쇠)를 통과한 상대 (_incomingPaired)
        //   · 내가 조종하는 중   : 내가 연결한 상대 (_session)
        // 둘 다 입력 줄(TCP)이 살아 있어야 한다.
        //
        // 주소는 "지금 연결된 입력 줄(TCP 45000)의 상대 실제 주소" 와 비교한다. (PeerConnection.ConnectedIpAddress)
        // 화면 보기(CanViewScreen)와 같은 방식이다.
        private bool CanTransferWith(string ip)
        {
            PeerConnection? peer = _peer;

            if (peer == null || !peer.IsTcpConnected || (!_incomingPaired && _session == null))
                return false;

            return string.Equals(peer.ConnectedIpAddress, ip, StringComparison.OrdinalIgnoreCase);
        }

        // 받은 파일을 어디에 저장할지. null 이면 받지 않는다.
        //
        // 항상 "다운로드\MultiPcKvm" 폴더 안. 이름은 ReceivedFileName 으로 걸러서
        // 폴더 밖으로 나가거나 기존 파일을 덮어쓰지 못하게 한다.
        private string? ChooseSavePathFor(IncomingFile file)
        {
            // 허락은 CanTransfer 에서 이미 물었겠지만, 저장 위치를 내주는 곳에서 한 번 더 확인한다.
            if (!CanTransferWith(file.FromIp))
            {
                DiagLog.Write($"파일 받기 거절 | {file.FromIp} | {file.FileName} | 조종 관계 아님");
                return null;
            }

            try
            {
                string folder = ReceiveFolder();
                Directory.CreateDirectory(folder);

                string safe = ReceivedFileName.Sanitize(file.FileName);

                lock (_savePathLock)
                {
                    string name = ReceivedFileName.MakeUnique(safe, candidate =>
                    {
                        string full = Path.Combine(folder, candidate);
                        return File.Exists(full) || Directory.Exists(full) || _reservedSavePaths.Contains(full);
                    });

                    string path = Path.Combine(folder, name);
                    _reservedSavePaths.Add(path);

                    DiagLog.Write($"파일 받기 시작 | {file.FromIp} | {file.FileName} ({file.Size} 바이트) → {path}");
                    return path;
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write($"파일 저장 위치 실패 | {ex.Message}");
                return null;
            }
        }

        // 받은 파일 폴더: 이 PC 의 "다운로드" 폴더 안 MultiPcKvm
        //
        // 다운로드 폴더는 사용자가 D 드라이브 등으로 옮겼을 수 있어서 Windows 에 물어본다.
        // 물어보지 못하면 기본 위치(사용자 폴더\Downloads)를 쓴다.
        // internal: 삭제(Uninstaller)도 같은 폴더를 찾는 데 쓴다.
        internal static string ReceiveFolder()
        {
            string? downloads = null;

            try
            {
                if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out IntPtr p) == 0)
                {
                    downloads = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write($"다운로드 폴더 찾기 실패 | {ex.Message}");
            }

            if (string.IsNullOrEmpty(downloads))
            {
                downloads = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }

            return Path.Combine(downloads, "MultiPcKvm");
        }

        // Windows "다운로드" 폴더의 번호 (FOLDERID_Downloads)
        private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);


        // ── 파일 전송 쪽에서 오는 소식 (어느 스레드에서든 온다 → 화면 스레드로 넘긴다) ──

        private void OnFileReceived(IncomingFile file, string savedPath)
        {
            DiagLog.Write($"파일 받음 | {file.FromIp} | {savedPath}");

            Dispatcher.BeginInvoke(() =>
                ReportTransfer($"받음: {Path.GetFileName(savedPath)} → {Path.GetDirectoryName(savedPath)}", 8));
        }

        private void OnFileProgress(FileProgress p)
        {
            Dispatcher.BeginInvoke(() =>
            {
                string verb = p.Sending ? "보내는 중" : "받는 중";
                string percent = p.Total > 0 ? $"{p.Done * 100 / p.Total}%" : "";

                ReportTransfer($"{verb}: {p.FileName} {percent} ({Megabytes(p.Done)} / {Megabytes(p.Total)})", 4, log: false);
            });
        }

        private void OnFileFailed(string fileName, string reason)
        {
            DiagLog.Write($"파일 전송 실패 | {fileName} | {reason}");

            Dispatcher.BeginInvoke(() => ReportTransfer($"실패: {fileName} — {reason}", 8));
        }

        // 진행 상황을 보여준다. 메인 창 안내 줄과 화면 보기 창 둘 다.
        // (조종하는 쪽은 화면 보기 창을 보고 있고, 조종당하는 쪽은 메인 창만 있다)
        private void ReportTransfer(string text, int seconds, bool log = true)
        {
            if (log)
                DiagLog.Write("파일 | " + text);

            ShowHint(text, seconds);
            _viewerWindow?.ShowTransferStatus(text);
        }

        private static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";


        // ── 메인 창: 조종 중인 기기 줄의 [파일] 버튼 ──
        //
        // 줄에 버튼이 이미 많아서(배치·삭제·화면·연결 끊기) 두 개를 더 넣으면 기기 이름이 가려진다.
        // 그래서 [파일] 하나를 두고, 누르면 [파일 보내기] [파일 받기] 메뉴를 띄운다.
        private void FileMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not DeviceItem item)
                return;

            // 내가 이 기기를 조종 중일 때만. (버튼도 그때만 보이지만 혹시 몰라 한 번 더 확인)
            if (_session == null || _connectedItem != item)
            {
                ShowHint("먼저 이 기기에 연결하세요.", 4);
                return;
            }

            var send = new MenuItem { Header = "파일 보내기  (이 PC → 상대 PC)" };
            send.Click += (s, args) => SendFilesToPeer(this);

            var request = new MenuItem { Header = "파일 받기  (상대 PC → 이 PC)" };
            request.Click += (s, args) => RequestFilesFromPeer();

            var menu = new ContextMenu
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
            };

            menu.Items.Add(send);
            menu.Items.Add(request);
            menu.IsOpen = true;
        }


        // ── [파일 보내기] (조종하는 쪽. 메인 창 메뉴, 화면 보기 창 버튼) ──
        //
        // owner: 파일 고르기 창을 어느 창 위에 띄울지. (누른 버튼이 있는 창)
        private async void SendFilesToPeer(Window? owner)
        {
            if (_fileTransfer == null)
            {
                ReportTransfer(NoEngineText, 6);
                return;
            }

            PeerConnection? peer = _peer;

            if (_session == null || peer == null)
            {
                ReportTransfer("연결이 끊겨 보낼 수 없습니다.", 6);
                return;
            }

            if (_fileSendCts != null)
            {
                ReportTransfer("이미 파일을 보내는 중입니다. 끝난 뒤에 다시 눌러 주세요.", 5);
                return;
            }

            // 내 PC 에서 고르는 창.
            // 화면 보기 창에서 눌렀으면 그 창이 비활성화되어 키보드가 내 PC 로 돌아온다.
            // (그래야 파일 이름을 내 PC 에 칠 수 있다)
            var dialog = new OpenFileDialog
            {
                Title = "상대 PC 로 보낼 파일 고르기 (여러 개 가능)",
                Multiselect = true,
            };

            if (dialog.ShowDialog(owner ?? this) != true || dialog.FileNames.Length == 0)
                return;

            await SendFilesAsync(peer.IpAddress, dialog.FileNames);
        }


        // ── [파일 받기] (조종하는 쪽. 메인 창 메뉴, 화면 보기 창 버튼) ──

        private async void RequestFilesFromPeer()
        {
            if (_fileTransfer == null)
            {
                ReportTransfer(NoEngineText, 6);
                return;
            }

            PeerConnection? peer = _peer;

            if (_session == null || peer == null)
            {
                ReportTransfer("연결이 끊겨 받을 수 없습니다.", 6);
                return;
            }

            try
            {
                // 입력 줄로 "파일 골라 줘" 를 보낸다.
                await peer.SendAsync(new InputMessage { Type = MessageType.FileRequest });

                ReportTransfer(
                    "상대 PC 화면에 '보낼 파일 고르기' 창을 띄웠습니다. 상대 PC 화면에서 골라 주세요. " +
                    $"받은 파일은 이 PC 의 {ReceiveFolder()} 에 저장됩니다.", 10);
            }
            catch (Exception ex)
            {
                ReportTransfer("파일 받기 요청을 보내지 못했습니다: " + ex.Message, 8);
            }
        }


        // ── 조종당하는 쪽: "파일 골라 줘" 를 받았다 (Pairing.OnPeerMessage 에서 화면 스레드로 넘어온다) ──
        //
        // 이 PC 화면에 파일 고르기 창을 띄운다. 조종하는 사람이 고른다. (화면 보기 창으로 보거나, 이 모니터를 직접 보면서)
        // 고르면 조종하는 PC 로 보낸다.
        private async Task HandleFileRequestAsync()
        {
            // 코드(또는 열쇠)를 통과한 상대가 아니면 무시한다. (아무나 내 PC 에 창을 띄우지 못하게)
            if (!_incomingPaired || _peerItem == null)
                return;

            if (_fileTransfer == null)
            {
                ShowHint("상대가 파일을 요청했지만 " + NoEngineText, 8);
                DiagLog.Write("파일 요청 받음 | 파일 전송 기능 없음");
                return;
            }

            // 이미 떠 있으면 앞으로만 가져온다.
            if (_fileRequestOwner != null)
            {
                _fileRequestOwner.Activate();
                return;
            }

            if (_fileSendCts != null)
            {
                ShowHint("이미 파일을 보내는 중입니다.", 5);
                return;
            }

            DiagLog.Write($"파일 요청 받음 | {_peerItem.IpAddress}");

            // 고르기 창의 주인으로 쓸 보이지 않는 작은 창.
            //
            // 메인 창은 트레이로 숨겨져 있을 수 있다. 숨은 창을 주인으로 하면 고르기 창이 뒤에 깔리거나 안 보인다.
            // 맨 위(Topmost) 창을 주인으로 하면 고르기 창도 맨 위에 뜬다.
            // 그래서 조종하는 사람이 화면 보기 창으로 바로 볼 수 있다.
            var owner = new Window
            {
                Title = "MultiPcKvm — 파일 보내기",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,      // 완전히 투명하게 (창 테두리·배경이 보이지 않게)
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Width = 1,
                Height = 1,

                // 반드시 "주 모니터" 가운데에 띄운다.
                // 화면 보기는 주 모니터만 보내므로(김기령님 ScreenSharer), 다른 모니터에 뜨면 조종하는 사람이 못 본다.
                // (CenterScreen 은 마우스가 있는 모니터에 띄울 수 있어서 쓰지 않는다)
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.PrimaryScreenWidth / 2,
                Top = SystemParameters.PrimaryScreenHeight / 2,
            };

            _fileRequestOwner = owner;

            string[] files;

            try
            {
                owner.Show();
                owner.Activate();

                var dialog = new OpenFileDialog
                {
                    Title = "조종하는 PC 로 보낼 파일 고르기 (여러 개 가능)",
                    Multiselect = true,
                };

                files = dialog.ShowDialog(owner) == true ? dialog.FileNames : Array.Empty<string>();
            }
            finally
            {
                owner.Close();
                _fileRequestOwner = null;
            }

            if (files.Length == 0)
            {
                DiagLog.Write("파일 요청 | 고르지 않고 닫음");
                return;
            }

            // 고르는 동안 연결이 끊겼을 수 있다. 다시 확인한다.
            // 보낼 곳은 지금 나를 조종 중인 PC 의 실제 주소 (입력 줄의 상대 주소)
            string? ip = _peer?.ConnectedIpAddress;

            if (ip == null || !CanTransferWith(ip))
            {
                ShowHint("연결이 끊겨서 파일을 보내지 않았습니다.", 6);
                return;
            }

            await SendFilesAsync(ip, files);
        }


        // ── 보내기 (양쪽 공용) ────────────────────────────────

        // 고른 파일들을 하나씩 차례로 보낸다. (동시에 보내면 대역폭을 나눠 먹어 마우스가 끊길 수 있다)
        private async Task SendFilesAsync(string ip, IReadOnlyList<string> files)
        {
            IFileTransfer? engine = _fileTransfer;

            if (engine == null)
            {
                ReportTransfer(NoEngineText, 6);
                return;
            }

            if (_fileSendCts != null)
            {
                ReportTransfer("이미 파일을 보내는 중입니다.", 5);
                return;
            }

            var cts = new CancellationTokenSource();
            _fileSendCts = cts;
            _fileSendIp = ip;

            int sent = 0;
            bool stopped = false;

            try
            {
                foreach (string path in files)
                {
                    string name = Path.GetFileName(path);

                    if (cts.IsCancellationRequested || !CanTransferWith(ip))
                    {
                        stopped = true;
                        break;
                    }

                    ReportTransfer($"보내는 중: {name}", 4);

                    try
                    {
                        if (await engine.SendFileAsync(ip, path, cts.Token))
                        {
                            sent++;
                            ReportTransfer($"보냄: {name}", 6);
                        }
                        // false 면 이유는 파일 전송 쪽이 Failed 로 알려준다. (OnFileFailed)
                    }
                    catch (OperationCanceledException)
                    {
                        stopped = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        ReportTransfer($"보내지 못함: {name} — {ex.Message}", 8);
                    }
                }
            }
            finally
            {
                // 이 아래는 항상 화면 스레드에서 돈다. (await 뒤에 원래 스레드로 돌아온다)
                // Tick 의 CheckFileSendLink 도 화면 스레드라 서로 엇갈리지 않는다.
                _fileSendCts = null;
                _fileSendIp = null;
                cts.Dispose();
            }

            if (stopped)
                ReportTransfer($"연결이 끊겨 보내기를 멈췄습니다. ({files.Count}개 중 {sent}개 보냄)", 8);
            else if (files.Count > 1)
                ReportTransfer($"{files.Count}개 중 {sent}개 보냄", 8);
        }
    }
}
