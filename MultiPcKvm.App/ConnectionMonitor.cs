namespace MultiPcKvm.App
{
    // 연결 상태
    public enum LinkState
    {
        Lost = 0,       // 끊김 — 삼키기를 유지하면 안 된다
        Unstable = 1,   // 불안정 — 응답을 놓쳤지만 아직 끊긴 건 아니다
        Healthy = 2     // 정상
    }

    // ─────────────────────────────────────────────────────────────
    //  상대 PC와의 연결이 살아있는지 판단한다.
    //
    //  이 클래스가 하는 일은 딱 하나다.
    //  "지금 삼키기를 유지해도 되는가?" 에 답하는 것.
    //
    //  ─── 왜 이렇게 복잡하게 만드는가 ───
    //
    //  판단을 틀리는 경우가 두 가지 있는데 피해가 전혀 다르다.
    //
    //   (가) 멀쩡한데 끊겼다고 판단
    //        → 커서가 내 PC로 돌아온다. 다시 넘기면 된다. 귀찮은 정도.
    //
    //   (나) 끊겼는데 멀쩡하다고 판단
    //        → 키보드·마우스가 죽은 채로 남는다. 심각하다.
    //
    //  (나)는 감시 스레드와 Windows 자체 타임아웃이 이미 막아준다.
    //  그래서 이 클래스의 목표는 (가) 를 최대한 줄이는 것이다.
    //
    //  ─── 어떻게 줄이는가 ───
    //
    //  ① 추측을 안 해도 되는 건 추측하지 않는다.
    //     랜선이 빠지거나 상대가 프로그램을 끄면 OS가 즉시 알려준다.
    //     그건 확정이므로 기다릴 필요 없이 바로 끊긴 것으로 처리한다.
    //
    //  ② 어쩔 수 없이 추측해야 할 때는, 고정된 시간이 아니라
    //     "평소 이 연결의 속도" 를 기준으로 판단한다.
    //     같은 공유기 안이면 평소 2ms, 인터넷 너머면 평소 200ms 인데
    //     둘 다 2초로 자르면 한쪽은 너무 느리고 한쪽은 너무 성급하다.
    //
    //  ③ 한 번 놓쳤다고 끊지 않는다. 연속으로 놓쳐야 끊는다.
    // ─────────────────────────────────────────────────────────────
    public class ConnectionMonitor
    {
        // ── 설정 ──────────────────────────────────────────────

        // 몇 번 연속으로 응답을 놓치면 끊긴 것으로 볼지
        public int MissesToLose { get; set; } = 3;

        // 아무리 빨라도 이보다 짧게는 안 기다린다.
        // 너무 짧으면 잠깐 버벅인 것만으로 끊긴 것으로 오해한다.
        public int MinTimeoutMs { get; set; } = 300;

        // 아무리 느려도 이보다 오래는 안 기다린다.
        // 이 시간을 넘기면 (나) 쪽 피해가 커지기 시작한다.
        public int MaxTimeoutMs { get; set; } = 3000;


        // ── 상태 ──────────────────────────────────────────────

        private LinkState _state = LinkState.Lost;
        public LinkState State => _state;

        // 연속으로 응답을 놓친 횟수
        private int _misses;
        public int Misses => _misses;

        // 상태가 바뀔 때 알려준다. (이전 상태, 새 상태, 이유)
        public event Action<LinkState, LinkState, string>? StateChanged;


        // ── 응답 시간 학습 ────────────────────────────────────
        //
        // 평소 왕복 시간(RTT)과 그 흔들림 정도를 계속 갱신한다.
        // 이 두 값으로 "이 정도 안 오면 이상한 거다" 는 기준을 만든다.
        //
        // 이 계산 방식은 TCP 가 재전송 시점을 정할 때 쓰는 공식과 같다.
        // 수십 년 검증된 것이라 그대로 가져다 쓴다.

        private double _avgRtt = -1;    // 평소 왕복 시간 (아직 모르면 -1)
        private double _devRtt = 0;     // 왕복 시간이 얼마나 들쭉날쭉한지

        public double AvgRttMs => _avgRtt;
        public double DevRttMs => _devRtt;

        // 지금 기준으로 "이 시간 안에 안 오면 놓친 것" 으로 볼 값
        public int TimeoutMs
        {
            get
            {
                // 아직 한 번도 응답을 못 받았으면 최소값으로 시작한다.
                if (_avgRtt < 0)
                    return MinTimeoutMs;

                // 평소 시간 + 흔들림의 4배.
                //
                // 왜 4배인가:
                //   평소 100ms 인데 가끔 130ms 까지 늦어진다면 흔들림은 30 정도다.
                //   100 + 30×4 = 220ms 까지는 "늦긴 해도 정상 범위" 로 본다.
                //   흔들림이 큰 연결일수록 자동으로 더 너그러워진다.
                double t = _avgRtt + (_devRtt * 4);

                return (int)Math.Clamp(t, MinTimeoutMs, MaxTimeoutMs);
            }
        }


        // ── 바깥에서 알려주는 사건들 ──────────────────────────

        // 상대로부터 응답이 왔다. rttMs = 보내고 받기까지 걸린 시간
        public void OnPongReceived(double rttMs)
        {
            // 응답 시간 학습을 갱신한다.
            if (_avgRtt < 0)
            {
                // 첫 응답 — 비교할 게 없으니 그대로 받아들인다.
                _avgRtt = rttMs;
                _devRtt = rttMs / 2;
            }
            else
            {
                // 이번 값이 평소와 얼마나 다른가
                double diff = rttMs - _avgRtt;

                // 평소값을 이번 값 쪽으로 1/8 만큼 움직인다.
                //
                // 왜 1/8씩만 움직이나:
                //   한 번 튄 값에 휘둘리지 않기 위해서다.
                //   여러 번 계속 느려져야 평소값이 실제로 올라간다.
                _avgRtt += diff / 8;

                // 흔들림도 같은 방식으로 갱신한다.
                // Math.Abs = 절대값. 빨라졌든 느려졌든 "차이" 만 본다.
                _devRtt += (Math.Abs(diff) - _devRtt) / 4;
            }

            _misses = 0;
            SetState(LinkState.Healthy, $"응답 도착 ({rttMs:F0}ms)");
        }

        // 응답을 기다렸는데 시간 안에 안 왔다.
        public void OnPingTimedOut()
        {
            _misses++;

            if (_misses >= MissesToLose)
            {
                SetState(LinkState.Lost, $"응답을 {_misses}번 연속으로 놓침");
            }
            else
            {
                SetState(LinkState.Unstable, $"응답 놓침 ({_misses}/{MissesToLose})");
            }
        }

        // 확실하게 끊긴 경우. 추측이 아니라 확정이므로 곧바로 끊긴 것으로 처리한다.
        //
        // 이런 때 부른다:
        //   · 랜선이 빠지거나 와이파이가 끊겼다 (OS가 알려준다)
        //   · 상대가 프로그램을 정상 종료했다 (TCP가 종료 신호를 보낸다)
        //   · 소켓에서 오류가 났다
        public void OnHardFailure(string reason)
        {
            _misses = MissesToLose;
            SetState(LinkState.Lost, $"확정 — {reason}");
        }

        // 연결이 (다시) 맺어졌다.
        public void OnConnected()
        {
            _misses = 0;

            // 학습값을 초기화한다.
            // 상대가 바뀌었거나 경로가 바뀌었을 수 있어서, 예전 속도를 믿으면 안 된다.
            _avgRtt = -1;
            _devRtt = 0;

            SetState(LinkState.Unstable, "연결됨 — 첫 응답 대기 중");
        }


        // ── 이 클래스를 쓰는 쪽이 물어볼 것 ───────────────────

        // 지금 삼키기를 유지해도 되는가?
        //
        // 불안정(Unstable) 일 때도 true 를 준다.
        // 응답 한 번 놓쳤다고 커서를 뺏어오면 너무 예민하기 때문이다.
        // 정말 끊겼으면 어차피 몇 번 더 놓쳐서 Lost 가 된다.
        public bool CanKeepSwallowing => _state != LinkState.Lost;


        // ── 내부 ──────────────────────────────────────────────

        private void SetState(LinkState next, string reason)
        {
            if (_state == next)
                return;

            LinkState prev = _state;
            _state = next;

            StateChanged?.Invoke(prev, next, reason);
        }
    }
}
