namespace MultiPcKvm.Common
{
    // ─────────────────────────────────────────────────────────────
    //  파일 보내기·받기 — 약속
    //
    //  파일 데이터를 실제로 주고받는 부분은 김기령님, 버튼·저장 위치·진행 표시는 이두람(WPF) 담당이다.
    //  둘이 만나는 지점을 여기 정해 둔다. (화면 보기의 ScreenShare.cs 와 같은 방식)
    //
    //  양쪽 PC 에 IFileTransfer 가 하나씩 있다. 보내기와 받기를 둘 다 한다.
    //  포트는 Constants.FileTransferPort (45003, TCP). 방화벽 버튼이 열어 둔다.
    //
    //  ─── [파일 보내기] (조종하는 A → 조종당하는 B) ───
    //    A 의 화면 보기 창 [파일 보내기] → A 에서 파일 고르기
    //    → A.SendFileAsync(B 의 IP, 파일)
    //    → B: CanTransfer(A 의 IP) 확인 → ChooseSavePath 로 저장 위치 받기 → 저장 → FileReceived
    //
    //  ─── [파일 받기] (조종당하는 B → 조종하는 A) ───
    //    A 의 [파일 받기] → 입력 줄(45000)로 MessageType.FileRequest 를 보냄 (WPF 가 함)
    //    → B 의 화면에 "보낼 파일 고르기" 창이 뜸. A 가 화면 보기 창으로 보면서 고른다.
    //    → B.SendFileAsync(A 의 IP, 파일) → 이후는 위와 같다 (보내는 쪽·받는 쪽만 바뀜)
    //
    //    상대 PC 의 폴더 목록을 주고받는 기능 없이, 상대 화면에 뜬 창으로 고르게 한 것이다.
    //    그래서 김기령님 쪽은 "파일 하나 보내기 / 받기" 만 있으면 된다.
    // ─────────────────────────────────────────────────────────────


    // 들어오는 파일 정보 (받기 시작할 때 알 수 있는 것)
    public sealed record IncomingFile(string FromIp, string FileName, long Size);

    // 진행 상황. Sending = true 면 보내는 중, false 면 받는 중.
    public sealed record FileProgress(string FileName, long Done, long Total, bool Sending);


    public interface IFileTransfer : IDisposable
    {
        // ── 받기 ─────────────────────────────────────────────

        // 받을 준비를 한다. (포트 열기) 프로그램이 켜져 있는 동안 계속 켜 둔다.
        // 실패하면 예외를 던진다. (예: 포트를 다른 프로그램이 쓰는 중)
        void StartListening();

        // 주고받던 것을 멈추고 포트를 닫는다. 여러 번 불려도 된다.
        void StopListening();

        // 파일이 들어오려 할 때 받아도 되는지 WPF 에게 묻는다. (보낸 쪽 IP 를 넘긴다)
        // 반드시 확인해야 한다. 안 하면 같은 공유기의 누구나 내 PC 에 파일을 넣을 수 있다.
        // WPF 는 "지금 조종 관계인 상대" 일 때만 true 를 준다. 어느 스레드에서 불러도 된다.
        Func<string, bool>? CanTransfer { get; set; }

        // 어디에 저장할지 WPF 에게 묻는다. 전체 경로를 돌려준다. null 이면 받지 않는다.
        // (WPF 는 받은 파일 폴더 안에, 같은 이름이 있으면 번호를 붙여서 준다)
        // 파일 이름은 보낸 쪽이 준 것이라 믿으면 안 된다. WPF 가 경로 문자(\ / ..)를 걸러낸다.
        Func<IncomingFile, string?>? ChooseSavePath { get; set; }

        // 다 받아서 저장했다. (파일 정보, 저장한 경로)
        event Action<IncomingFile, string>? FileReceived;

        // ── 보내기 ───────────────────────────────────────────

        // 파일 하나를 상대(ip)에게 보낸다. 끝까지 보내고 상대가 저장까지 했으면 true.
        // 상대가 거절했거나 실패하면 false (이유는 Failed 로 알려도 된다)
        Task<bool> SendFileAsync(string ip, string localPath, CancellationToken cancellationToken);

        // ── 공통 ─────────────────────────────────────────────

        // 진행 상황. 너무 자주 부르지 않아도 된다. (예: 1% 또는 0.2초마다)
        event Action<FileProgress>? Progress;

        // 실패했다. (파일 이름, 사람이 읽을 이유) 보내기·받기 둘 다.
        event Action<string, string>? Failed;
    }
}
