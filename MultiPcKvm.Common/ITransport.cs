using System;
using System.Threading.Tasks;

namespace MultiPcKvm.Common
{
    // ─────────────────────────────────────────────────────────────
    //  네트워크 담당(김기령)이 만들 것의 "설계도"
    // ─────────────────────────────────────────────────────────────
    //
    // 인터페이스(interface)란:
    //   "이런 기능들이 있을 거다" 라는 약속만 적어두고, 실제 내용은 안 적는 것.
    //   실제 내용은 이 약속을 따르는 클래스가 채운다.
    //
    // 왜 필요한가 — 이게 두 명이 동시에 작업할 수 있는 이유다:
    //   App 은 통신이 TCP 인지 UDP 인지 WebRTC 인지 알 필요가 없다.
    //   "보낸다 / 받는다" 만 알면 된다.
    //   그래서 네트워크 담당이 아직 아무것도 안 만들었어도,
    //   App 담당은 이 설계도만 보고 코드를 미리 쓸 수 있다.
    //
    // 나중에 인터넷 연결(WebRTC)을 추가할 때도
    // 이 설계도를 따르는 클래스를 하나 더 만들면 끝이고,
    // App 코드는 한 줄도 안 고쳐도 된다.
    //
    // 구현 예정: TcpTransport (지금), WebRtcTransport (나중)
    public interface ITransport
    {
        // 메시지가 도착할 때마다 발생한다.
        // 이걸 쓰는 쪽에서 "메시지 오면 이렇게 처리해줘" 를 등록해둔다.
        event Action<InputMessage>? MessageReceived;

        // 지금 상대와 연결되어 있는가
        bool IsConnected { get; }

        // 상대가 접속해 오기를 기다린다. (조작당하는 쪽)
        void StartListening(int port);

        // 상대에게 접속한다. (조작하는 쪽)
        // 성공하면 true, 실패하면 false 를 돌려준다.
        Task<bool> ConnectAsync(string ip, int port);

        // 메시지 하나를 상대에게 보낸다.
        Task SendAsync(InputMessage message);

        // 연결을 끊는다.
        void Disconnect();
    }
}
