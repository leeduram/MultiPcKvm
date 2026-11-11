using MultiPcKvm.Common;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  화면 보기·파일 전송 기능을 만드는 곳. (김기령님 MultiPcKvm.Video 와 WPF 를 잇는 한 지점)
    //
    //    ScreenViewer       : 조종하는 쪽. 상대 화면 받기 (45002)
    //    ScreenSharer       : 조종당하는 쪽. 내 화면 보내기 (45002)
    //    FileTransferEngine : 양쪽 공용. 파일 하나 보내기·받기 (45003)
    //
    //  WPF 는 약속(MultiPcKvm.Common 의 ScreenShare.cs, FileTransfer.cs)만 보고 쓴다.
    //  나중에 영상 방식을 바꿔도(H.264 등) 같은 약속을 지키면 여기와 WPF 는 그대로다.
    //
    //  null 을 돌려주면 그 기능만 꺼진다. (시험 중 한 기능만 끄고 싶을 때)
    //    · 조종하는 쪽: 화면 창에 "영상 기능이 아직 연결되지 않았습니다" (창 위 마우스·키보드 전달은 된다)
    //    · 조종당하는 쪽: 화면 보내기를 시작하지 않는다.
    //    · 파일 버튼: "파일 전송 기능이 아직 연결되지 않았습니다"
    // ─────────────────────────────────────────────────────────────
    public static class ScreenShareFactory
    {
        // 조종하는 쪽: 상대 화면 받기
        public static IScreenViewer? CreateViewer() => new MultiPcKvm.Video.ScreenViewer();

        // 조종당하는 쪽: 내 화면 보내기
        public static IScreenSharer? CreateSharer() => new MultiPcKvm.Video.ScreenSharer();

        // 파일 보내기·받기 (양쪽 PC 공용)
        public static IFileTransfer? CreateFileTransfer() => new MultiPcKvm.Video.FileTransferEngine();
    }
}
