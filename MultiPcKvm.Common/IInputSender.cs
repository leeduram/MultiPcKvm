using System;

namespace MultiPcKvm.Common
{
    // ─────────────────────────────────────────────────────────────
    //  Windows 담당(이두람)이 만들 것의 설계도 ② — 입력을 "만들어 내는" 쪽
    // ─────────────────────────────────────────────────────────────
    //
    // 네트워크로 받은 메시지대로 이 PC에서 실제로
    // 마우스를 움직이거나 키를 누르는 역할.
    // 실제 구현은 Windows 의 SendInput 을 쓴다.
    //
    // ①(IInputCapture)의 정반대라고 보면 된다.
    //   IInputCapture  : 내 입력 → InputMessage
    //   IInputSender   : InputMessage → 이 PC의 입력
    //
    // 구현 예정: WindowsInputSender
    public interface IInputSender
    {
        // 받은 메시지대로 이 PC에서 입력을 실행한다.
        void Apply(InputMessage message);
    }
}
