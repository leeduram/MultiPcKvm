using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  기기 목록에 한 줄로 표시될 정보.
    //
    //  INotifyPropertyChanged 를 구현하는 이유:
    //    값이 바뀌었을 때 화면이 저절로 다시 그려지게 하려는 것이다.
    //    이게 없으면 값을 바꿔도 화면은 예전 값을 그대로 보여준다.
    //
    //    예: 연결이 되면 StatusText 를 "연결됨" 으로 바꾸는데,
    //        알림을 보내야 화면의 글자도 같이 바뀐다.
    // ─────────────────────────────────────────────────────────────
    public class DeviceItem : INotifyPropertyChanged
    {
        public Guid DeviceId { get; set; }

        public string IpAddress { get; set; } = "";

        public int TcpPort { get; set; }

        // 마지막으로 응답이 온 시각.
        // 일정 시간 지나면 목록에서 지운다 (PC 가 꺼졌을 수 있으므로)
        public DateTime LastSeen { get; set; } = DateTime.Now;

        // 지금 쓰는 IP(IpAddress)로 마지막 대답이 온 시각.
        //
        // LastSeen 과 다른 점: LastSeen 은 "어느 주소로든" 대답이 오면 갱신되고,
        // 이건 "지금 쓰는 주소" 로 온 대답일 때만 갱신된다.
        // 같은 기기가 랜선·와이파이 두 주소로 대답할 때, 지금 주소가 아직 살아 있는지
        // 판단하는 데 쓴다. (MainWindow.ChooseAddress)
        public DateTime IpLastSeen { get; set; } = DateTime.MinValue;


        private string _name = "";
        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (Set(ref _isConnected, value))
                {
                    // 연결 여부가 바뀌면 버튼 글자도 같이 바뀌어야 한다.
                    OnPropertyChanged(nameof(ActionText));
                }
            }
        }

        // 이 기기가 지금 내 PC 를 조종하고 있는가. (조종당하는 쪽에서만 true)
        //
        // IsConnected 는 "내가 이 기기를 조종 중" 이라는 뜻이라 따로 둔다.
        // 이게 true 면 조종당하는 쪽에서도 [연결 끊기] 를 눌러 끊을 수 있다.
        private bool _isControllingMe;
        public bool IsControllingMe
        {
            get => _isControllingMe;
            set
            {
                if (Set(ref _isControllingMe, value))
                    OnPropertyChanged(nameof(ActionText));
            }
        }

        // 버튼을 잠갔는가.
        //
        // 누군가 내 PC 를 조종하는 동안에는 목록의 모든 버튼을 잠근다.
        // 한 방향만 허용하기 때문이다.
        //   · 조종 중인 상대에게 반대로 연결 → 서로 조종 (뺐음)
        //   · 다른 기기에 연결 → 지금 줄을 정리하면서 조종 중인 상대가 끊김
        private bool _isLocked;
        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                if (Set(ref _isLocked, value))
                    OnPropertyChanged(nameof(CanAct));
            }
        }

        // 버튼을 누를 수 있는가
        public bool CanAct => !IsLocked;

        // DB 에 저장된 신뢰 기기인가. (한 번 연결 코드를 통과한 기기)
        //
        // 저장된 기기는 꺼져 있어도 목록에서 지우지 않고 "오프라인" 으로 남긴다.
        // 화면에 [삭제] 버튼이 보이는 것도 저장된 기기뿐이다.
        private bool _isSaved;
        public bool IsSaved
        {
            get => _isSaved;
            set => Set(ref _isSaved, value);
        }

        // 지금 검색에 잡히는가. (프로그램이 켜져 있는가)
        //
        // 처음부터 검색으로 찾은 기기는 true 로 시작한다.
        // 저장된 기기를 목록에 미리 띄울 때는 false 로 시작하고,
        // 검색에 잡히는 순간 true 가 된다.
        private bool _isOnline = true;
        public bool IsOnline
        {
            get => _isOnline;
            set => Set(ref _isOnline, value);
        }

        // 목록에 마우스를 올리면 보이는 연결 통계. (6주차)
        // 예) "연결 3회 · 마지막 연결 10/12 14:03 · 평균 지연 2.1ms"
        // 저장된 기기만 채운다. null 이면 풍선 도움말이 뜨지 않는다.
        private string? _detailText;
        public string? DetailText
        {
            get => _detailText;
            set => Set(ref _detailText, value);
        }

        // 버튼에 표시될 글자
        // 내가 조종 중이든, 상대가 나를 조종 중이든 [연결 끊기] 로 보인다.
        public string ActionText => IsConnected || IsControllingMe ? "연결 끊기" : "연결";


        // ── 알림 장치 ─────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // 값이 실제로 바뀐 경우에만 알린다.
        // 같은 값을 계속 넣을 때마다 화면을 다시 그리면 낭비다.
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
