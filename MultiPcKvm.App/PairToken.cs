using System.Security.Cryptography;

namespace MultiPcKvm.App
{
    // ─────────────────────────────────────────────────────────────
    //  저장된 기기 열쇠(코드 없이 연결)에 쓰는 값 다루기.
    //
    //  메시지(InputMessage)는 int 두 칸(Value1, Value2)밖에 없다.
    //  그래서 64비트 열쇠는 두 칸에, 128비트 기기 ID 는 메시지 두 개에 나눠 담는다.
    //  나누고 합치는 규칙이 양쪽 PC 에서 똑같아야 하므로 한 곳에 모았다.
    //
    //  Windows 와 상관없는 계산이라 Linux 에서도 시험할 수 있다.
    // ─────────────────────────────────────────────────────────────
    public static class PairToken
    {
        // 64비트 무작위 열쇠. 0 은 쓰지 않는다. (비어 있는 값과 헷갈리지 않게)
        //
        // Random 이 아니라 RandomNumberGenerator 를 쓰는 이유:
        //   Random 은 앞의 값들로 다음 값을 짐작할 수 있다. 열쇠는 짐작할 수 없어야 한다.
        public static long NewToken()
        {
            long token;

            do
            {
                token = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8), 0);
            }
            while (token == 0);

            return token;
        }

        // 64비트 ↔ int 두 칸.   Value1 = 위 32비트, Value2 = 아래 32비트
        public static (int High, int Low) SplitLong(long value) =>
            ((int)(value >> 32), (int)value);

        // (uint) 로 바꾸는 이유: 아래 32비트가 음수 int 로 들어 있으면
        //   그대로 long 으로 바꿀 때 위 32비트가 전부 1 로 채워져서 값이 망가진다.
        public static long JoinLong(int high, int low) =>
            ((long)high << 32) | (uint)low;

        // 기기 ID(GUID, 16바이트) ↔ 64비트 두 개
        public static (long High, long Low) SplitGuid(Guid id)
        {
            byte[] bytes = id.ToByteArray();
            return (BitConverter.ToInt64(bytes, 0), BitConverter.ToInt64(bytes, 8));
        }

        public static Guid JoinGuid(long high, long low)
        {
            byte[] bytes = new byte[16];
            BitConverter.GetBytes(high).CopyTo(bytes, 0);
            BitConverter.GetBytes(low).CopyTo(bytes, 8);
            return new Guid(bytes);
        }
    }
}
