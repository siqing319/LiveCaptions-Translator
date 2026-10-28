using System.Windows;
using System.Windows.Navigation;
using Wpf.Ui.Controls;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class WelcomeWindow : FluentWindow
    {
        public WelcomeWindow()
        {
            InitializeComponent();
            MainWindow.ApplySavedTheme();

            Loaded += (s, e) =>
            {
                if (Translator.Setting.MainWindow.Theme == "System")
                    SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            ShellUtil.Open(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}
