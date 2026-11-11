using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;    // Path, File, FileInfo (진단 기록 파일). WPF 프로젝트는 System.IO 를 자동으로 넣어주지 않는다
using System.Runtime.InteropServices;
using System.Text;
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
    //  진단 기록 (원인 찾기용)
    //
    //  검색·연결에서 무슨 일이 있었는지 시간순으로 텍스트 파일에 남긴다.
    //
    //  왜 필요한가:
    //    "같은 네트워크인데 오프라인", "가끔만 연결됨" 같은 문제는
    //    화면만 봐서는 어디서 막혔는지 알 수 없다.
    //    VS 출력 창에는 우리 코드의 기록이 안 남는다. (예외 이름만 찍힌다)
    //
    //  위치: 실행 파일 옆 (bin\Debug\net10.0-windows\kvm-log.txt)
    //    bin 폴더는 git 에 안 올라가므로 저장소를 더럽히지 않는다.
    //
    //  크기: 1MB 가 넘으면 kvm-log.old.txt 로 옮기고 새로 시작한다.
    //        (켤 때만이 아니라 쓰는 도중에도 확인한다. 트레이에 며칠 켜 두어도 끝없이 커지지 않게)
    //
    //  ─── 쓰는 방식 ───
    //    Write 는 줄을 통에 넣기만 하고 바로 돌아온다. 파일에 쓰는 건 전용 스레드 하나가 한다.
    //    예전에는 부르는 자리에서 매번 파일을 열고 쓰고 닫았다. (File.AppendAllText)
    //    화면 스레드·네트워크 스레드가 디스크를 기다리느라 잠깐씩 멈췄다.
    //
    //    전용 스레드는 파일을 한 번 열어 두고 계속 쓴다.
    //    통이 비면 바로 디스크에 내려 쓴다(Flush). 그래서 프로그램이 갑자기 죽어도 거의 다 남는다.
    //    쓰는 동안에도 메모장으로 열어 볼 수 있게 공유 모드로 연다.
    //
    //  ─── 콘솔 출력도 여기로 모은다 ───
    //    팀원 코드(검색·연결·영상·파일)와 KvmSession 은 기록을 Console.WriteLine 으로 남긴다.
    //    WPF 프로그램(exe)에는 콘솔 창이 없어서 그 기록이 아무 데도 안 남았다.
    //    그래서 콘솔 출력을 받아서 "[콘솔] ..." 으로 이 파일에 같이 적는다. (ConsoleToLog)
    //
    //    단, 검색처럼 3초마다 같은 줄을 찍는 것이 있어서 그대로 적으면 파일이 금방 찬다.
    //    그래서 똑같은 줄은 10분에 한 번만 적는다. 내용이 바뀌면(예: "직접 253/253" → "이번엔 쉼") 바로 적는다.
    //
    //  기록 실패는 무시한다. 기록 때문에 프로그램이 멈추면 안 된다.
    //  통이 가득 차면(1만 줄, 디스크가 멈춘 경우 등) 새 줄은 버린다. 부르는 쪽을 기다리게 하지 않는다.
    // ─────────────────────────────────────────────────────────────
    internal static class DiagLog
    {
        public static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "kvm-log.txt");

        private static readonly string OldPath =
            Path.Combine(AppContext.BaseDirectory, "kvm-log.old.txt");

        private const long MaxBytes = 1_000_000;

        // 쓸 줄을 모아 두는 통
        private static readonly BlockingCollection<string> _queue =
            new BlockingCollection<string>(new ConcurrentQueue<string>(), boundedCapacity: 10_000);

        private static Thread? _writer;
        private static readonly object _startLock = new object();

        public static void Start()
        {
            EnsureWriter();

            // 콘솔 출력(팀원 코드 등)도 이 파일로 모은다. (위 설명)
            try
            {
                Console.SetOut(new ConsoleToLog());
            }
            catch
            {
            }

            Write("");
            Write("════════ 프로그램 시작 ════════");
            Write($"PC 이름 {Environment.MachineName}");

            // 이 PC 의 네트워크 주소들. (어느 망에 붙어 있었는지 나중에 확인하려고)
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;

                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            Write($"내 주소 | {nic.Name} | {a.Address}/{a.PrefixLength}");
                    }
                }
            }
            catch (Exception ex)
            {
                Write("내 주소 확인 실패 | " + ex.Message);
            }
        }

        public static void Write(string text)
        {
            try
            {
                EnsureWriter();

                // 시각은 부른 순간의 것을 적는다. (파일에 쓰는 건 조금 뒤라도)
                _queue.TryAdd($"{DateTime.Now:HH:mm:ss.fff}  {text}");
            }
            catch
            {
                // 끝내는 중이라 통이 닫혔으면 버린다.
            }
        }

        // 남은 줄을 다 쓰고 끝낸다. (프로그램 끝낼 때 부른다. 최대 1초 기다림)
        public static void Stop()
        {
            try
            {
                _queue.CompleteAdding();
                _writer?.Join(1000);
            }
            catch
            {
            }
        }

        // ── 콘솔 출력 받기 ────────────────────────────────────
        //
        // Console.WriteLine 으로 쓴 글자를 받아서, 한 줄이 끝날 때마다 기록에 넣는다.
        // 여러 스레드가 동시에 쓸 수 있어서 lock 으로 한 줄씩 모은다.
        private sealed class ConsoleToLog : TextWriter
        {
            private readonly StringBuilder _line = new StringBuilder();
            private readonly object _lock = new object();

            // 같은 줄을 마지막으로 적은 시각. 10분 안에 똑같은 줄이 오면 적지 않는다.
            private readonly Dictionary<string, DateTime> _lastWritten = new Dictionary<string, DateTime>();
            private static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(10);

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {
                lock (_lock)
                {
                    if (value == '\n')
                        EndLine();
                    else if (value != '\r')
                        _line.Append(value);
                }
            }

            public override void Write(string? value)
            {
                if (value == null)
                    return;

                lock (_lock)
                {
                    foreach (char c in value)
                    {
                        if (c == '\n')
                            EndLine();
                        else if (c != '\r')
                            _line.Append(c);
                    }
                }
            }

            public override void WriteLine(string? value)
            {
                lock (_lock)
                {
                    Write(value);
                    EndLine();
                }
            }

            public override void WriteLine() => Write('\n');

            // 한 줄이 끝났다. (lock 안에서 부른다)
            private void EndLine()
            {
                string text = _line.ToString().Trim();
                _line.Clear();

                // 빈 줄은 적지 않는다. (화면 정리용으로 찍는 빈 줄이 많다)
                if (text.Length == 0)
                    return;

                DateTime now = DateTime.Now;

                if (_lastWritten.TryGetValue(text, out DateTime last) && now - last < RepeatInterval)
                    return;

                // 기억이 너무 많아지면 비운다. (줄 종류가 1천 개를 넘을 일은 거의 없다)
                if (_lastWritten.Count > 1000)
                    _lastWritten.Clear();

                _lastWritten[text] = now;

                // 바깥 DiagLog.Write 로 넣는다. (그냥 Write 라고 쓰면 이 클래스의 Write 가 불려서 무한 반복)
                DiagLog.Write("[콘솔] " + text);
            }
        }

        // 전용 스레드를 한 번만 띄운다.
        private static void EnsureWriter()
        {
            if (_writer != null)
                return;

            lock (_startLock)
            {
                if (_writer != null)
                    return;

                var thread = new Thread(WriterMain)
                {
                    IsBackground = true,    // 이 스레드 때문에 프로그램이 안 꺼지는 일이 없게
                    Name = "DiagLog",
                    Priority = ThreadPriority.BelowNormal,
                };

                thread.Start();
                _writer = thread;
            }
        }

        private static void WriterMain()
        {
            StreamWriter? file = null;

            // 파일을 못 열면(권한 없음 등) 잠시 쉬었다가 다시 해 본다. 줄마다 열기를 시도하면 낭비라서.
            DateTime retryAt = DateTime.MinValue;

            try
            {
                foreach (string line in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (file == null)
                        {
                            if (DateTime.Now < retryAt)
                                continue;

                            file = Open();

                            if (file == null)
                            {
                                retryAt = DateTime.Now.AddSeconds(5);
                                continue;
                            }
                        }

                        file.WriteLine(line);

                        // 더 쓸 줄이 없으면 디스크로 내려 쓴다.
                        if (_queue.Count == 0)
                            file.Flush();

                        // 너무 커졌으면 옮기고 새로 시작한다.
                        if (file.BaseStream.Length > MaxBytes)
                        {
                            file.Flush();
                            file.Dispose();
                            file = null;
                            RotateIfLarge();
                        }
                    }
                    catch
                    {
                        // 쓰다가 실패하면 파일을 닫고 다음 줄에서 다시 연다.
                        try { file?.Dispose(); } catch { }
                        file = null;
                    }
                }
            }
            finally
            {
                try
                {
                    file?.Flush();
                    file?.Dispose();
                }
                catch
                {
                }
            }
        }

        // 기록 파일을 이어 쓰기로 연다. 이미 1MB 가 넘었으면 옮기고 새로 연다.
        private static StreamWriter? Open()
        {
            try
            {
                RotateIfLarge();

                var stream = new FileStream(
                    FilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);   // 쓰는 중에도 다른 프로그램이 열어 볼 수 있게

                // 예전(File.AppendAllText)과 같은 UTF-8 (BOM 없음)
                return new StreamWriter(stream, new UTF8Encoding(false));
            }
            catch
            {
                return null;
            }
        }

        private static void RotateIfLarge()
        {
            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    File.Copy(FilePath, OldPath, overwrite: true);
                    File.Delete(FilePath);
                }
            }
            catch
            {
            }
        }
    }
}
