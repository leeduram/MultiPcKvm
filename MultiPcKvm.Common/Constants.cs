using System;

namespace MultiPcKvm.Common
{
    // 프로그램 전체에서 쓰는 고정값 모음.
    //
    // 왜 따로 모아두냐면:
    //   포트 번호 같은 걸 여기저기 숫자로 흩어 놓으면
    //   나중에 값을 바꿀 때 한 군데를 빠뜨리게 되고,
    //   그러면 "보내는 쪽은 45000, 받는 쪽은 45001" 이 돼서 연결이 안 된다.
    //   원인 찾기도 어렵다. 그래서 한 곳에만 적어두고 다 같이 가져다 쓴다.
    public static class Constants
    {
        // 마우스/키보드 입력을 주고받는 포트
        public const int InputPort = 45000;

        // 같은 네트워크에 있는 PC를 찾을 때 쓰는 포트 (나중에 사용)
        public const int DiscoveryPort = 45001;
        // 영상용 포트 (화면 보기)
        public const int VideoPort = 45002;

        // 파일 보내기·받기 포트 (TCP)
        public const int FileTransferPort = 45003;
    }
}
