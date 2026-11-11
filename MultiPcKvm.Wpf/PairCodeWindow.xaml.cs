using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  연결하기 전에 상대 PC 화면에 뜬 4자리 코드를 입력받는 작은 창.
    //
    //  ShowDialog() 로 띄우면 이 창을 닫기 전까지 뒤 창을 못 건드린다.
    //  true 가 돌아오면 Code 에 입력한 숫자가 들어있다.
    // ─────────────────────────────────────────────────────────────
    public partial class PairCodeWindow : Window
    {
        public int Code { get; private set; }

        public PairCodeWindow(string targetName)
        {
            InitializeComponent();

            TargetText.Text = targetName;

            // 창이 뜨자마자 바로 입력할 수 있게 커서를 넣어준다.
            Loaded += (s, e) => CodeBox.Focus();
        }

        // 숫자가 아닌 글자는 아예 안 들어가게 막는다.
        //
        // 입력한 뒤에 검사해서 지우는 것보다 처음부터 막는 게 깔끔하다.
        private void CodeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
        }

        private void CodeBox_KeyDown(object sender, KeyEventArgs e)
        {
            // 붙여넣기나 조합으로 이상한 게 들어갔을 때를 대비해
            // 오류 글자만 지워준다.
            ErrorText.Text = "";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string text = CodeBox.Text.Trim();

            if (text.Length != 4 || !int.TryParse(text, out int code))
            {
                ErrorText.Text = "4자리 숫자를 입력하세요.";
                CodeBox.Focus();
                return;
            }

            Code = code;

            // DialogResult 에 true 를 넣으면 창이 닫히고
            // ShowDialog() 가 true 를 돌려준다.
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
