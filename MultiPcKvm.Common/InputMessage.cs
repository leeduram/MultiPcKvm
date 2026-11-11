using System;
using System.Collections.Generic;
using System.Text;

namespace MultiPcKvm.Common
{
    // 두 PC가 주고받는 메시지 한 개.
    // struct 로 만든 이유: 크기가 작고 자주 만들어지는 데이터라 class 보다 가볍다.
    public struct InputMessage
    {
        // 이 메시지를 바이트로 바꿨을 때의 크기 (항상 9바이트로 고정)
        //   0번   : Type   (1바이트)
        //   1~4번 : Value1 (4바이트)
        //   5~8번 : Value2 (4바이트)
        // 크기가 항상 같아야 받는 쪽에서 "9바이트씩 잘라 읽으면 끝"이 된다.
        public const int Size = 9;

        // 무슨 일이 일어났는지 (마우스 이동인지, 키 눌림인지)
        public MessageType Type { get; set; }

        // 첫 번째 숫자. 종류에 따라 뜻이 달라진다.
        //   MouseMove   → X 좌표
        //   MouseButton → 버튼 번호
        //   KeyDown/Up  → 키 번호
        public int Value1 { get; set; }

        // 두 번째 숫자.
        //   MouseMove   → Y 좌표
        //   MouseButton → 눌림 1 / 뗌 0
        //   KeyDown/Up  → 안 씀 (0)
        public int Value2 { get; set; }


        // 이 메시지를 바이트 9개로 바꾼다. (네트워크로 보내기 위해)
        // 네트워크는 숫자나 구조체를 모르고 오직 바이트만 보낼 수 있다.
        public byte[] ToBytes()
        {
            // 빈 칸 9개짜리 배열을 만든다
            byte[] result = new byte[Size];

            // 0번 칸: 종류를 숫자로 바꿔서 넣는다 (MouseMove 면 1)
            result[0] = (byte)Type;

            // 1~4번 칸: Value1 을 바이트 4개로 쪼개서 넣는다
            // Array.Copy(원본, 원본시작, 대상, 대상시작, 개수)
            Array.Copy(BitConverter.GetBytes(Value1), 0, result, 1, 4);

            // 5~8번 칸: Value2 도 같은 방식으로
            Array.Copy(BitConverter.GetBytes(Value2), 0, result, 5, 4);

            return result;
        }


        // 받은 바이트 9개를 다시 메시지로 되돌린다.
        // static 인 이유: 메시지를 만들기 전에 부르는 함수라서
        // (아직 InputMessage 가 없는 상태에서 호출해야 한다)
        public static InputMessage FromBytes(byte[] data)
        {
            InputMessage msg = new InputMessage();

            // 0번 칸의 숫자를 다시 종류로 되돌린다
            msg.Type = (MessageType)data[0];

            // 1번 위치부터 4바이트를 읽어서 정수로 만든다
            msg.Value1 = BitConverter.ToInt32(data, 1);

            // 5번 위치부터 4바이트를 읽어서 정수로 만든다
            msg.Value2 = BitConverter.ToInt32(data, 5);

            return msg;
        }
    }
}