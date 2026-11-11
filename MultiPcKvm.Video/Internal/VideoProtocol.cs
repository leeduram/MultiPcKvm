namespace MultiPcKvm.Video.Internal
{
    // ─────────────────────────────────────────────────────────────
    //  화면 보기 줄(TCP 45002) 위에서 주고받는 모양.
    //
    //  1) 보는 쪽 → 보내는 쪽 : 인사  [M K V S][버전 1]
    //  2) 보내는 쪽 → 보는 쪽 : 허락  [1 허락 / 0 거절][이유 글자]
    //     (CanView 가 false 면 거절하고 끊는다)
    //  3) 보내는 쪽 → 보는 쪽 : 이후 계속
    //       [1] 화면 : [가로 4][세로 4][압축 전 길이 4][압축 길이 4][압축 데이터]
    //       [2] 살아있음 : 바뀐 화면이 없어도 1초마다 보낸다. (끊김 감지용)
    //  4) 그만 볼 때는 보는 쪽이 줄을 끊는다. 보내는 쪽은 그걸 보고 멈춘다.
    //
    //  숫자는 리틀 엔디언.
    // ─────────────────────────────────────────────────────────────
    internal static class VideoProtocol
    {
        public static readonly byte[] Magic = { (byte)'M', (byte)'K', (byte)'V', (byte)'S' };

        public const byte Version = 1;

        public const byte TypeFrame = 1;
        public const byte TypeKeepAlive = 2;

        // 압축된 화면 한 장의 최대 크기 (이보다 크면 잘못된 데이터)
        public const int MaxCompressedLength = 128 * 1024 * 1024;

        public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);

        // 이 시간 동안 아무것도 안 오면 끊긴 것으로 본다.
        public static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(6);

        // 쓰기가 이 시간 넘게 막히면 끊긴 것으로 본다. (상대 PC 가 갑자기 꺼진 경우 등)
        public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    }
}
