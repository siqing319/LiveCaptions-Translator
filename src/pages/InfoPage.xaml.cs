using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class InfoPage : Page
    {
        public const int MIN_HEIGHT = 330;

        public InfoPage()
        {
            InitializeComponent();
            MainWindow.ApplySavedTheme();
            Version.Text = UpdateChecker.CurrentVersionText;
            AutoCheckBox.IsChecked = Translator.Setting?.Lecture.AutoCheckUpdate ?? true;
            UpdateStatus.Text = UpdateChecker.CurrentBuild == null ? "开发版本，不检查更新" : "尚未检查";
            CheckUpdateButton.IsEnabled = UpdateChecker.CurrentBuild != null;
            if (UpdateChecker.Latest != null)
                ShowUpdateResult(UpdateChecker.Latest);

            Loaded += (s, e) =>
            {
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(minHeight: MIN_HEIGHT);
            };
        }

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            UpdateLinkBlock.Visibility = Visibility.Collapsed;
            UpdateStatus.Text = "正在检查……";
            var info = await UpdateChecker.CheckAsync();
            CheckUpdateButton.IsEnabled = true;
            if (info == null)
            {
                UpdateStatus.Text = "检查失败，请稍后再试";
                return;
            }
            if (Translator.Setting != null)
                Translator.Setting.Lecture.LastUpdateCheck = DateTime.UtcNow;
            UpdateChecker.MarkNotified(info);
            ShowUpdateResult(info);
        }

        private void ShowUpdateResult(UpdateInfo info)
        {
            if (!info.IsNewer)
            {
                UpdateStatus.Text = "已是最新版本";
                UpdateLinkBlock.Visibility = Visibility.Collapsed;
                return;
            }
            UpdateStatus.Text = string.Empty;
            UpdateLinkText.Text = $"发现新版本 {info.Tag}";
            UpdateLink.NavigateUri = new Uri(info.Url);
            UpdateLinkBlock.Visibility = Visibility.Visible;
        }

        private void AutoCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting != null)
                Translator.Setting.Lecture.AutoCheckUpdate = AutoCheckBox.IsChecked == true;
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            ShellUtil.Open(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}
