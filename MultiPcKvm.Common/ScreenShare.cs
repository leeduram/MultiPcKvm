namespace MultiPcKvm.Common
{
    // ─────────────────────────────────────────────────────────────
    //  화면 보기 (상대 화면을 조종하는 PC 에서 보면서 조종하기) — 약속
    //
    //  영상은 팀원(MultiPcKvm.Video), 화면 표시·입력은 본인(MultiPcKvm.Wpf) 담당이다.
    //  둘이 만나는 지점을 여기 정해 둔다. 이 모양만 맞으면 서로 따로 만들 수 있다.
    //
    //    조종하는 PC (A)                          조종당하는 PC (B)
    //    ────────────────                         ────────────────
    //    WPF [화면] 버튼                           WPF 가 켤 때 StartListening
    //      → IScreenViewer.StartAsync(B 의 IP) ──▶ IScreenSharer
    //                                               CanView(A 의 IP) 로 허락 확인
    //                                               캡처 → 인코딩 → 전송 (영상 포트 45002)
    //      ◀── FrameReceived(VideoFrame) ────────
    //    WPF 가 창에 그린다
    //    창 위의 마우스·키보드 → 기존 입력 줄(45000)로 보낸다 (영상과 상관없음)
    //
    //  영상 포트는 Constants.VideoPort (45002). 입력(45000)과 나눈 이유:
    //    같은 줄로 보내면 영상이 대역폭을 다 먹어서 마우스가 끊긴다.
    //    방화벽 버튼은 45002 를 TCP·UDP 둘 다 연다. (어느 쪽을 쓸지는 영상 쪽이 정한다)
    // ─────────────────────────────────────────────────────────────


    // 디코딩이 끝난 화면 한 장.
    //
    // 픽셀 형식은 32비트 (한 픽셀 4바이트: 파랑·초록·빨강·남는 바이트 순).
    // WPF 는 Bgr32 로 그린다. 네 번째 바이트는 보지 않으므로 아무 값이어도 된다. (255 로 채울 필요 없음)
    //
    // DirtyRects: 직전 화면에서 바뀐 곳들. (선택)
    //   있으면 WPF 는 그 부분만 복사하고 그 부분만 다시 그린다. (화면 일부만 바뀔 때 훨씬 가볍다)
    //   null 이면 화면 전체가 바뀐 것으로 본다. (예전처럼 전체 복사)
    //   해상도가 바뀐 첫 장은 null 로 주거나 전체 칸을 줘야 한다.
    //   Pixels 는 항상 "화면 전체" 여야 한다. (바뀐 칸만 담은 배열이 아니다)
    //
    // 크기는 상대 화면 해상도와 달라도 된다. (예: 대역폭 때문에 절반 크기로 보냄)
    // WPF 는 창 크기에 맞춰 늘리거나 줄여서 그리고,
    // 마우스 좌표는 영상 크기가 아니라 상대 화면 크기(ScreenInfo)로 바꿔서 보낸다.
    public sealed class VideoFrame
    {
        public VideoFrame(int width, int height, int stride, byte[] pixels)
            : this(width, height, stride, pixels, null)
        {
        }

        public VideoFrame(int width, int height, int stride, byte[] pixels, IReadOnlyList<FrameRect>? dirtyRects)
        {
            Width = width;
            Height = height;
            Stride = stride;
            Pixels = pixels;
            DirtyRects = dirtyRects;
        }

        // 직전 화면에서 바뀐 곳들. null 이면 전체가 바뀐 것. (위 설명)
        // 이벤트가 돌아온 뒤에는 WPF 가 쓰지 않으므로, 영상 쪽이 같은 목록을 다시 써도 된다.
        public IReadOnlyList<FrameRect>? DirtyRects { get; }

        // 가로·세로 픽셀 수
        public int Width { get; }
        public int Height { get; }

        // 한 줄의 바이트 수. 보통 Width × 4 이지만, 줄 끝에 빈 바이트가 붙는 경우가 있어서 따로 받는다.
        public int Stride { get; }

        // 픽셀 데이터. 길이는 Stride × Height 이상이어야 한다.
        public byte[] Pixels { get; }
    }


    // 화면 안의 네모 한 칸 (픽셀). 바뀐 곳을 알려줄 때 쓴다.
    public readonly record struct FrameRect(int X, int Y, int Width, int Height);


    // 조종하는 쪽: 상대 화면 받기
    public interface IScreenViewer : IDisposable
    {
        // 화면 한 장이 도착했다. 어느 스레드에서 불려도 된다. (WPF 가 화면 스레드로 넘긴다)
        //
        // 받은 VideoFrame 은 WPF 가 바로 복사해서 쓰므로, 다음 프레임에 같은 배열을 다시 써도 된다.
        // 단, 이벤트가 돌아온 뒤에만 다시 써야 한다. (이벤트 안에서 WPF 가 복사를 끝낸다)
        event Action<VideoFrame>? FrameReceived;

        // 영상이 멈췄다. (상대가 거절, 연결 끊김, 오류 등) 이유를 사람이 읽을 글자로 준다.
        // 이게 불린 뒤에는 FrameReceived 가 더 오지 않아야 한다.
        event Action<string>? Stopped;

        // 상대(ip)에게 화면을 보내 달라고 하고 받기 시작한다.
        // 연결까지 되면 true, 안 되면 false (이유는 Stopped 로 알려도 된다)
        Task<bool> StartAsync(string ip);

        // 그만 받는다. 상대도 보내기를 멈추게 한다. 여러 번 불려도 된다.
        void Stop();
    }


    // 조종당하는 쪽: 내 화면 보내기
    public interface IScreenSharer : IDisposable
    {
        // 보내 달라는 요청이 왔을 때 허락할지 WPF 에게 묻는다. (요청한 쪽 IP 를 넘긴다)
        //
        // 반드시 확인해야 한다. 안 하면 같은 공유기에 있는 누구나 내 화면을 볼 수 있다.
        // WPF 는 "지금 코드(또는 저장된 기기 열쇠)를 통과해서 나를 조종 중인 PC" 일 때만 true 를 준다.
        // 어느 스레드에서 불러도 된다.
        Func<string, bool>? CanView { get; set; }

        // 요청을 받을 준비를 한다. (영상 포트 열기) 프로그램이 켜져 있는 동안 계속 켜 둔다.
        // 실패하면 예외를 던진다. (예: 포트를 다른 프로그램이 쓰는 중)
        void StartListening();

        // 보내던 것을 멈추고 포트를 닫는다. 여러 번 불려도 된다.
        void StopListening();
    }
}
