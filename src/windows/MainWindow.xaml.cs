using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.utils;
using Button = Wpf.Ui.Controls.Button;

namespace LiveCaptionsTranslator
{
    public partial class MainWindow : FluentWindow
    {
        public OverlayWindow? OverlayWindow { get; set; } = null;
        public BilingualWindow? BilingualWindow { get; set; } = null;
        private static bool bilingualHintShown = false;
        private const double DEFAULT_WIDTH = 900;
        private const double DEFAULT_HEIGHT = 560;

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int HOTKEY_END_SECTION = 0x5301;
        private const int HOTKEY_TOGGLE_SPEECH = 0x5302;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainWindow()
        {
            InitializeComponent();
            ApplySavedTheme();
            SetUiScale(Translator.Setting.MainWindow.UiScale);

            SourceInitialized += (s, e) => RegisterHotKeys();
            Closed += (s, e) =>
            {
                UnregisterHotKeys();
                BilingualWindow?.Close();
            };
            Speaker.MutedChanged += muted => Dispatcher.InvokeAsync(() => ShowSpeakerState(muted));
            ClassSession.StateChanged += () => Dispatcher.InvokeAsync(ShowSessionState);

            Loaded += (s, e) =>
            {
                if (Translator.Setting.MainWindow.Theme == "System")
                    SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
                RootNavigation.Navigate(typeof(CaptionPage));
                CheckForFirstUse();
            };

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;

            var windowState = WindowHandler.LoadState(this, Translator.Setting);
            if (windowState.Left <= 0 || windowState.Left >= screenWidth ||
                windowState.Top <= 0 || windowState.Top >= screenHeight)
            {
                WindowHandler.RestoreState(this, new Rect(
                    (screenWidth - DEFAULT_WIDTH) / 2, (screenHeight - DEFAULT_HEIGHT) / 2, DEFAULT_WIDTH, DEFAULT_HEIGHT));
            }
            else
                WindowHandler.RestoreState(this, windowState);
            // A saved window shorter than the minimum height (e.g. a thin caption strip) gets the default height.
            if (Height < MinHeight)
                Height = DEFAULT_HEIGHT;
            KeepOnScreen();

            ToggleTopmost(Translator.Setting.MainWindow.Topmost);
            ShowSpeakerState(Speaker.Muted);
            ShowLogCard(Translator.Setting.MainWindow.CaptionLogEnabled);
        }

        public static void ApplySavedTheme()
        {
            string theme = Translator.Setting?.MainWindow.Theme ?? "System";
            if (theme == "System")
            {
                ApplicationThemeManager.ApplySystemTheme();
                if (Application.Current.MainWindow is MainWindow main && main.IsLoaded)
                    SystemThemeWatcher.Watch(main, WindowBackdropType.Mica, true);
            }
            else
            {
                foreach (Window window in Application.Current.Windows)
                    SystemThemeWatcher.UnWatch(window);
                ApplicationThemeManager.Apply(theme == "Dark" ? ApplicationTheme.Dark : ApplicationTheme.Light);
            }
        }

        public void SetUiScale(double scale)
        {
            UiScaleTransform.ScaleX = scale;
            UiScaleTransform.ScaleY = scale;
        }

        private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            var state = Translator.Setting.MainWindow;
            state.UiScale = Math.Round(state.UiScale + Math.Sign(e.Delta) * 0.05, 2);
            SetUiScale(state.UiScale);
            e.Handled = true;
        }

        private void TopmostButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleTopmost(!this.Topmost);
        }

        private void OverlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var symbolIcon = button?.Icon as SymbolIcon;

            if (OverlayWindow == null)
            {
                symbolIcon.Symbol = SymbolRegular.ClosedCaption24;
                symbolIcon.Filled = true;

                OverlayWindow = new OverlayWindow();
                OverlayWindow.SizeChanged +=
                    (s, e) => WindowHandler.SaveState(OverlayWindow, Translator.Setting);
                OverlayWindow.LocationChanged +=
                    (s, e) => WindowHandler.SaveState(OverlayWindow, Translator.Setting);

                double screenWidth = SystemParameters.PrimaryScreenWidth;
                double screenHeight = SystemParameters.PrimaryScreenHeight;

                var windowState = WindowHandler.LoadState(OverlayWindow, Translator.Setting);
                if (windowState.Left <= 0 || windowState.Left >= screenWidth ||
                    windowState.Top <= 0 || windowState.Top >= screenHeight)
                {
                    WindowHandler.RestoreState(OverlayWindow, new Rect(
                        (screenWidth - 480) / 2, screenHeight * 5 / 6 - 100, 480, 100));
                }
                else
                {
                    // A saved width of exactly 650 means the former default: use the current, smaller default.
                    if (Math.Abs(windowState.Width - 650) < 1)
                        windowState.Width = 480;
                    WindowHandler.RestoreState(OverlayWindow, windowState);
                }

                OverlayWindow.Show();
            }
            else
            {
                symbolIcon.Symbol = SymbolRegular.ClosedCaptionOff24;
                symbolIcon.Filled = false;

                switch (OverlayWindow.OnlyMode)
                {
                    case CaptionVisible.TranslationOnly:
                        OverlayWindow.OnlyMode = CaptionVisible.SubtitleOnly;
                        OverlayWindow.OnlyMode = CaptionVisible.Both;
                        break;
                    case CaptionVisible.SubtitleOnly:
                        OverlayWindow.OnlyMode = CaptionVisible.Both;
                        break;
                }

                OverlayWindow.Close();
                OverlayWindow = null;
            }
        }

        private void BilingualModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (BilingualWindow != null)
            {
                BilingualWindow.Close();
                return;
            }

            BilingualWindow = new BilingualWindow();
            BilingualWindow.Closed += (s, args) =>
            {
                BilingualWindow = null;
                if (BilingualModeButton.Icon is SymbolIcon icon)
                    icon.Filled = false;
            };
            if (BilingualModeButton.Icon is SymbolIcon symbolIcon)
                symbolIcon.Filled = true;
            BilingualWindow.Show();

            if (!bilingualHintShown && Translator.Setting.Lecture.Engine == models.RecognitionEngine.LiveCaptions)
            {
                bilingualHintShown = true;
                SnackbarHost.Show("双语浮窗按阿里云百炼引擎设计",
                    "系统字幕引擎下也能用，但原文更新可能较慢。", SnackbarType.Info, timeout: 3, closeButton: true);
            }
        }

        private void LogOnlyButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var symbolIcon = button?.Icon as SymbolIcon;

            if (Translator.LogOnlyFlag)
            {
                Translator.LogOnlyFlag = false;
                symbolIcon.Filled = false;
            }
            else
            {
                Translator.LogOnlyFlag = true;
                symbolIcon.Filled = true;
            }

            Translator.ClearContexts();
        }

        private void EndSectionButton_Click(object sender, RoutedEventArgs e)
        {
            EndSection();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClassSession.IsRunning)
            {
                long lectureId = ClassSession.CurrentLectureId;
                ClassSession.Stop();
                await LectureSaving.SaveAfterStop(lectureId);
            }
            else
            {
                RootNavigation.Navigate(typeof(CaptionPage));
                await ClassSession.Start();
            }
        }

        private void ShowSessionState()
        {
            StartButton.IsEnabled = !ClassSession.IsStarting;
            StartButton.Content = ClassSession.IsStarting ? "启动中…" : ClassSession.IsRunning ? "停止" : "开始";
            StartButton.Appearance = ClassSession.IsRunning ? ControlAppearance.Secondary : ControlAppearance.Primary;
            if (StartButton.Icon is SymbolIcon icon)
                icon.Symbol = ClassSession.IsRunning ? SymbolRegular.Stop16 : SymbolRegular.Play16;
        }

        private void SpeechSwitch_Click(object sender, RoutedEventArgs e)
        {
            Speaker.Muted = SpeechSwitch.IsChecked != true;
        }

        public static void EndSection()
        {
            Summarizer.RequestEndSection();
            SnackbarHost.Show("正在总结本节……", "", SnackbarType.Info, timeout: 1);
        }

        private void ShowSpeakerState(bool muted)
        {
            SpeechSwitch.IsChecked = !muted;
        }

        private void RegisterHotKeys()
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(HotKeyHook);
            // Ctrl+Alt+S: end the current section; Ctrl+Alt+M: mute / unmute speech.
            bool ok = RegisterHotKey(handle, HOTKEY_END_SECTION, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x53);
            ok &= RegisterHotKey(handle, HOTKEY_TOGGLE_SPEECH, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4D);
            if (!ok)
                SnackbarHost.Show("[WARNING] 快捷键注册失败。",
                    "Ctrl+Alt+S / Ctrl+Alt+M 可能被其他程序占用。", SnackbarType.Warning,
                    timeout: 3, closeButton: true);
        }

        private void UnregisterHotKeys()
        {
            var handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_END_SECTION);
            UnregisterHotKey(handle, HOTKEY_TOGGLE_SPEECH);
        }

        private IntPtr HotKeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_HOTKEY)
                return IntPtr.Zero;

            switch (wParam.ToInt32())
            {
                case HOTKEY_END_SECTION:
                    EndSection();
                    handled = true;
                    break;
                case HOTKEY_TOGGLE_SPEECH:
                    Speaker.Muted = !Speaker.Muted;
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        private void CaptionLogButton_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.MainWindow.CaptionLogEnabled = !Translator.Setting.MainWindow.CaptionLogEnabled;
            ShowLogCard(Translator.Setting.MainWindow.CaptionLogEnabled);
        }

        private void MainWindow_LocationChanged(object sender, EventArgs e)
        {
            var window = sender as Window;
            WindowHandler.SaveState(window, Translator.Setting);
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            MainWindow_LocationChanged(sender, e);
        }

        public void ToggleTopmost(bool enabled)
        {
            var button = TopmostButton as Button;
            var symbolIcon = button?.Icon as SymbolIcon;
            symbolIcon.Filled = enabled;
            this.Topmost = enabled;
            Translator.Setting.MainWindow.Topmost = enabled;
        }

        private void CheckForFirstUse()
        {
            if (!Translator.FirstUseFlag)
                return;

            RootNavigation.Navigate(typeof(LecturePage));
            if (Translator.Window != null)
                LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);

            Dispatcher.InvokeAsync(() =>
            {
                var welcomeWindow = new WelcomeWindow
                {
                    Owner = this
                };
                welcomeWindow.Show();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        public void ShowLogCard(bool enabled)
        {
            if (CaptionLogButton.Icon is SymbolIcon icon)
            {
                if (enabled)
                    icon.Symbol = SymbolRegular.History24;
                else
                    icon.Symbol = SymbolRegular.HistoryDismiss24;
            }
        }

        // Only grows the window when a page needs more room. Shrinking it to a thin caption strip
        // would hide most of the navigation; the overlay window serves that purpose.
        public void AutoHeightAdjust(int minHeight = -1)
        {
            if (minHeight > 0 && Height < minHeight)
            {
                Height = minHeight;
                KeepOnScreen();
            }
        }

        private void KeepOnScreen()
        {
            var workArea = SystemParameters.WorkArea;
            double height = Math.Max(Height, MinHeight);
            if (height > workArea.Height)
                Height = height = workArea.Height;
            if (Top + height > workArea.Bottom)
                Top = Math.Max(workArea.Top, workArea.Bottom - height);
        }
    }
}
