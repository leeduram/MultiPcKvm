namespace MultiPcKvm.App
{
    // ─────────────────────────────────────────────────────────────
    //  상대 화면이 내 화면 옆 "어디에" 붙어 있는가.
    //
    //  Side   : 붙은 쪽 (왼쪽·오른쪽·위·아래)
    //  Offset : 붙은 변을 따라 얼마나 밀려 있는가 (픽셀)
    //
    //    · 오른쪽/왼쪽에 붙었으면 세로로 밀린 양.
    //      상대 화면 맨 위가 내 화면 맨 위보다 Offset 만큼 아래에 있다.
    //    · 위/아래에 붙었으면 가로로 밀린 양.
    //      상대 화면 왼쪽 끝이 내 화면 왼쪽 끝보다 Offset 만큼 오른쪽에 있다.
    //    · 음수면 반대 방향(위쪽·왼쪽)으로 밀린 것이다.
    //
    //  예) 내 화면 1920×1080, 상대 1920×1080 을 오른쪽에 300 픽셀 내려서 붙임
    //
    //        ┌────────┐
    //        │  내 PC  │┌────────┐   ← 상대 맨 위가 300 아래
    //        │        ││  상대   │
    //        └────────┘│        │
    //                  └────────┘
    //      → Side = Right, Offset = 300
    //
    //  왜 (x, y) 좌표 대신 "쪽 + 밀린 양" 으로 저장하나:
    //    이 방식은 두 화면이 반드시 닿아 있다. 떨어진 배치를 저장할 방법이 아예 없다.
    //    (x, y) 로 저장하면 떨어진 배치도 저장되고, 그러면 커서가 넘어갈 곳이 없다.
    //
    //  record 로 만든 이유:
    //    입력 처리(후킹) 쪽은 다른 스레드에서 이 값을 읽는다.
    //    Side 와 Offset 을 따로 바꾸면 그 사이에 "새 Side + 옛 Offset" 이 읽힐 수 있다.
    //    통째로 새 객체로 바꿔 끼우면 항상 짝이 맞는 값이 읽힌다.
    //
    //  처음 위치(아직 배치를 정하지 않은 기기)는 ScreenLayout.DefaultPlacement 가 정한다.
    //  화면 크기에 따라 달라지기 때문에 고정값으로 두지 않았다.
    // ─────────────────────────────────────────────────────────────
    public sealed record ScreenPlacement(RemotePosition Side, int Offset);


    // ─────────────────────────────────────────────────────────────
    //  화면 배치 계산. 화면(WPF)과 입력 처리(InputRouter) 가 같이 쓴다.
    //
    //  좌표 기준: 내 화면 왼쪽 위가 (0, 0). 단위는 실제 픽셀.
    //  Windows 디스플레이 설정의 모니터 배치와 같은 방식이다.
    //
    //  Windows 나 숫자만 다루므로 Linux 에서도 시험할 수 있다.
    // ─────────────────────────────────────────────────────────────
    public static class ScreenLayout
    {
        // 처음 위치: 상대 화면을 내 화면 "왼쪽" 에, 세로 가운데를 맞춰 붙인다.
        //
        // 가운데 맞춤이라 맞닿은 변의 위아래에 남는 길이가 같다.
        //   상대가 더 작으면 → 내 화면 쪽 위아래가 같은 길이만큼 남는다
        //   상대가 더 크면   → 상대 화면 쪽 위아래가 같은 길이만큼 남는다
        //   크기가 같으면    → 딱 맞는다 (밀린 양 0)
        //
        // 예) 내 1080, 상대 1440 → 밀린 양 = (1080 - 1440) / 2 = -180
        //     상대 화면이 내 화면보다 위로 180, 아래로 180 더 나온다.
        //
        // 차이가 홀수면 나눗셈에서 1 픽셀이 한쪽으로 간다. (픽셀은 반으로 못 나눈다)
        public static ScreenPlacement DefaultPlacement(
            int localWidth, int localHeight,
            int remoteWidth, int remoteHeight)
        {
            return Clamp(
                new ScreenPlacement(RemotePosition.Left, (localHeight - remoteHeight) / 2),
                localWidth, localHeight, remoteWidth, remoteHeight);
        }

        // 두 화면이 최소한 얼마나 맞닿아 있어야 하는가.
        //
        // 1 픽셀만 닿아도 "닿아 있다" 고 할 수는 있지만,
        // 그러면 그 1 픽셀을 정확히 노려야 커서가 넘어간다. 사실상 못 쓴다.
        // 그래서 짧은 쪽 변 길이의 1/10 은 겹치게 한다. (1080 이면 108 픽셀)
        public static int MinOverlap(int localLength, int remoteLength)
        {
            int shorter = Math.Min(localLength, remoteLength);
            return Math.Max(1, shorter / 10);
        }

        // Offset 이 가질 수 있는 범위. 이 안이면 최소 겹침 이상 맞닿아 있다.
        //
        //   localLength  : 붙은 변 방향의 내 화면 길이 (오른쪽/왼쪽이면 세로, 위/아래면 가로)
        //   remoteLength : 같은 방향의 상대 화면 길이
        //
        //   가장 위(왼쪽)로 밀었을 때: 상대의 아래 끝이 내 위 끝보다 min 만큼 아래
        //     → Offset = -(remoteLength - min)
        //   가장 아래(오른쪽)로 밀었을 때: 상대의 위 끝이 내 아래 끝보다 min 만큼 위
        //     → Offset = localLength - min
        public static (int Min, int Max) OffsetRange(int localLength, int remoteLength)
        {
            int overlap = MinOverlap(localLength, remoteLength);
            return (-(remoteLength - overlap), localLength - overlap);
        }

        // Offset 을 범위 안으로 자른다.
        //
        // 저장해 둔 배치는 그때의 상대 화면 크기 기준이다.
        // 연결 후 실제 크기를 받아보니 다르면(해상도를 바꿨다든지) 범위를 벗어날 수 있다.
        // 그래도 항상 맞닿아 있도록 쓰기 직전에 자른다.
        public static ScreenPlacement Clamp(
            ScreenPlacement placement,
            int localWidth, int localHeight,
            int remoteWidth, int remoteHeight)
        {
            (int localLength, int remoteLength) = AlongEdge(
                placement.Side, localWidth, localHeight, remoteWidth, remoteHeight);

            (int min, int max) = OffsetRange(localLength, remoteLength);

            int offset = Math.Clamp(placement.Offset, min, max);

            return offset == placement.Offset
                ? placement
                : placement with { Offset = offset };
        }

        // 배치 → 상대 화면의 왼쪽 위 좌표 (내 화면 왼쪽 위 기준)
        public static (int X, int Y) RemoteTopLeft(
            ScreenPlacement placement,
            int localWidth, int localHeight,
            int remoteWidth, int remoteHeight)
        {
            return placement.Side switch
            {
                RemotePosition.Right  => (localWidth, placement.Offset),
                RemotePosition.Left   => (-remoteWidth, placement.Offset),
                RemotePosition.Top    => (placement.Offset, -remoteHeight),
                _ /* Bottom */        => (placement.Offset, localHeight),
            };
        }

        // 끌어다 놓은 위치 → 가장 가까운 "맞닿은" 배치
        //
        // x, y: 사용자가 상대 화면을 끌어다 놓은 왼쪽 위 좌표 (떨어져 있거나 겹쳐 있을 수 있다)
        //
        // 네 쪽에 각각 붙여 보고, 놓은 자리에서 가장 조금 움직이는 쪽을 고른다.
        //   예) 오른쪽 조금 떨어진 곳에 놓으면 → 오른쪽에 붙는다
        //       내 화면 위에 겹쳐 놓으면       → 가장 가까운 변으로 밀려난다
        // Windows 디스플레이 설정에서 모니터를 놓았을 때 달라붙는 것과 같다.
        public static ScreenPlacement Snap(
            int x, int y,
            int localWidth, int localHeight,
            int remoteWidth, int remoteHeight)
        {
            ScreenPlacement? best = null;
            long bestDistance = long.MaxValue;

            foreach (RemotePosition side in new[]
                     {
                         RemotePosition.Right, RemotePosition.Left,
                         RemotePosition.Top, RemotePosition.Bottom,
                     })
            {
                // 그 쪽에 붙였을 때 밀린 양: 놓은 자리를 그대로 쓰되 범위 안으로 자른다.
                bool vertical = side == RemotePosition.Right || side == RemotePosition.Left;

                ScreenPlacement candidate = Clamp(
                    new ScreenPlacement(side, vertical ? y : x),
                    localWidth, localHeight, remoteWidth, remoteHeight);

                (int cx, int cy) = RemoteTopLeft(
                    candidate, localWidth, localHeight, remoteWidth, remoteHeight);

                // 놓은 자리에서 얼마나 움직였는가 (거리의 제곱. 비교만 하므로 제곱근은 필요 없다)
                long dx = cx - x;
                long dy = cy - y;
                long distance = dx * dx + dy * dy;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return best!;
        }

        // 붙은 변 방향의 (내 길이, 상대 길이)
        //   오른쪽/왼쪽 → 세로 길이끼리, 위/아래 → 가로 길이끼리
        public static (int Local, int Remote) AlongEdge(
            RemotePosition side,
            int localWidth, int localHeight,
            int remoteWidth, int remoteHeight)
        {
            return side == RemotePosition.Right || side == RemotePosition.Left
                ? (localHeight, remoteHeight)
                : (localWidth, remoteWidth);
        }

        // 화면 표시용 이름
        public static string SideName(RemotePosition side) => side switch
        {
            RemotePosition.Right => "오른쪽",
            RemotePosition.Left  => "왼쪽",
            RemotePosition.Top   => "위쪽",
            _                    => "아래쪽",
        };

        // 반대쪽 (상대 화면에서 돌아올 때 미는 쪽)
        public static RemotePosition Opposite(RemotePosition side) => side switch
        {
            RemotePosition.Right => RemotePosition.Left,
            RemotePosition.Left  => RemotePosition.Right,
            RemotePosition.Top   => RemotePosition.Bottom,
            _                    => RemotePosition.Top,
        };

        // DB 저장값 (screen_layout.position 의 CHECK 와 같은 글자)
        public static string ToDb(RemotePosition side) => side switch
        {
            RemotePosition.Right => "RIGHT",
            RemotePosition.Left  => "LEFT",
            RemotePosition.Top   => "TOP",
            _                    => "BOTTOM",
        };

        public static RemotePosition FromDb(string text) => text switch
        {
            "LEFT"   => RemotePosition.Left,
            "TOP"    => RemotePosition.Top,
            "BOTTOM" => RemotePosition.Bottom,
            _        => RemotePosition.Right,
        };
    }
}
