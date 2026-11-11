namespace MultiPcKvm.App.Storage
{
    // ─────────────────────────────────────────────────────────────
    //  기기 한 대의 화면 배치. DB 의 screen_layout 테이블 한 줄과 대응한다.
    //
    //  컬럼 ↔ 속성
    //    device_id     TEXT     ↔ DeviceId
    //    position      TEXT     ↔ Placement.Side    ('LEFT' 'RIGHT' 'TOP' 'BOTTOM')
    //    edge_offset   INTEGER  ↔ Placement.Offset  (붙은 변을 따라 밀린 양, 픽셀)
    //    screen_width  INTEGER  ↔ ScreenWidth       (상대 화면 가로, 픽셀)
    //    screen_height INTEGER  ↔ ScreenHeight      (상대 화면 세로, 픽셀)
    //
    //  상대 화면 크기도 같이 저장하는 이유:
    //    연결하기 전에도 배치 창에 상대 화면을 실제 비율대로 그리려면 크기를 알아야 한다.
    //    크기는 연결해야 받을 수 있으므로(ScreenInfo), 받을 때마다 저장해 둔다.
    // ─────────────────────────────────────────────────────────────
    public class SavedLayout
    {
        public Guid DeviceId { get; set; }

        public ScreenPlacement Placement { get; set; } = new(RemotePosition.Left, 0);

        public int ScreenWidth { get; set; }

        public int ScreenHeight { get; set; }
    }
}
