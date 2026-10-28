using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class LecturePage : Page
    {
        public const int MIN_HEIGHT = 520;
        private const string AUTO_VOICE = "自动（按目标语言选择）";
        private const string AUTO_OCR = "自动";
        private const string DEFAULT_OUTPUT = "Windows 默认输出设备";

        private record Provider(string Name, string ApiUrl, string[] Models);

        // The first three items are built-in providers; saved slots follow them.
        private static readonly Provider[] PROVIDERS =
        {
            new("DeepSeek", "https://api.deepseek.com/chat/completions",
                new[] { "deepseek-chat" }),
            new("Qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
                new[] { "qwen-plus", "qwen-turbo", "qwen-max" }),
            new("Custom", string.Empty, Array.Empty<string>()),
        };

        public record ApiChoice(string Key, string Name);

        // Display names for the translation APIs. DeepSeek / Qwen go through the OpenAI-compatible config of card ①.
        private static readonly Dictionary<string, string> API_NAMES = new()
        {
            [Summarizer.SUMMARY_API] = "DeepSeek / 通义千问（用 ① 的设置）",
            ["Google"] = "Google 翻译（免费）",
            ["Google2"] = "Google 翻译 2（免费）",
        };

        private static SettingWindow? settingWindow;

        private bool initializing = true;
        private bool fillingKeyBox = false;

        private static OpenAIConfig? ApiConfig => Summarizer.Config;

        // The config object currently bound to card ①, so event handlers can be swapped on slot change.
        private OpenAIConfig? subscribedConfig;
        private const string FOLLOW_TRANSLATION = "（跟随翻译档位）";
        private bool fillingProviderBox;
        private readonly List<System.Windows.Controls.Button> providerActions = new();

        public LecturePage()
        {
            InitializeComponent();
            MainWindow.ApplySavedTheme();
            DataContext = Translator.Setting.Lecture;
            ScrollHelper.UseOwnScrollViewer(this, PageScroll);

            Loaded += (s, e) =>
            {
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(minHeight: MIN_HEIGHT);
                Summarizer.CurrentPageChanged += OnCurrentPageChanged;
                BindApiCard();
                initializing = true;
                // One failing loader (e.g. an audio device that can't be read) must not leave the page read-only.
                foreach (Action load in new Action[]
                         {
                             LoadEngine, () => LoadApiSetting(), LoadTranslateSetting, LoadDevices, LoadOcrLanguages, LoadVoices,
                             ShowSlidesInfo, ShowLiveCaptionsState, ShowSaveFolder, LoadAppearance
                         })
                {
                    try
                    {
                        load();
                    }
                    catch (Exception)
                    {
                        // Best effort: one failed status line should not block the others.
                    }
                }
                initializing = false;
            };
            Unloaded += (s, e) =>
            {
                Summarizer.CurrentPageChanged -= OnCurrentPageChanged;
                if (subscribedConfig != null)
                {
                    subscribedConfig.PropertyChanged -= OnApiConfigChanged;
                    subscribedConfig = null;
                }
                initializing = true;
            };
        }

        private void LoadAppearance()
        {
            var state = Translator.Setting.MainWindow;
            ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
                (string)item.Tag == state.Theme);
            UiScaleSlider.Value = state.UiScale;
        }

        private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || ThemeBox.SelectedItem is not ComboBoxItem item) return;
            Translator.Setting.MainWindow.Theme = (string)item.Tag;
            MainWindow.ApplySavedTheme();
        }

        private void UiScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (initializing || App.Current.MainWindow is not MainWindow window) return;
            Translator.Setting.MainWindow.UiScale = UiScaleSlider.Value;
            window.SetUiScale(Translator.Setting.MainWindow.UiScale);
        }

        // Binds card ① to the slot used for summaries, which may differ from the translation slot.
        private void BindApiCard()
        {
            var config = ApiConfig;
            ApiCard.DataContext = config;
            if (ReferenceEquals(subscribedConfig, config))
                return;
            if (subscribedConfig != null)
                subscribedConfig.PropertyChanged -= OnApiConfigChanged;
            subscribedConfig = config;
            if (config != null)
                config.PropertyChanged += OnApiConfigChanged;
        }

        // Lists named slots so summaries can use a different model from translation.
        private void LoadSummarySlots()
        {
            var setting = Translator.Setting;
            if (setting == null)
                return;

            var names = setting.OpenAIConfigs
                .Select(slot => slot.ConfigName?.Trim() ?? string.Empty)
                .Where(name => name.Length > 0)
                .ToList();

            var items = new List<string> { FOLLOW_TRANSLATION };
            items.AddRange(names);
            SummaryConfigBox.ItemsSource = items;

            string current = setting.Lecture?.SummaryConfigName?.Trim() ?? string.Empty;
            SummaryConfigBox.SelectedItem = current.Length > 0 && names.Contains(current)
                ? current
                : FOLLOW_TRANSLATION;
        }

        private void SummaryConfigBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing)
                return;
            var setting = Translator.Setting;
            if (setting?.Lecture == null || SummaryConfigBox.SelectedItem is not string choice)
                return;

            string name = choice == FOLLOW_TRANSLATION ? string.Empty : choice;
            if (string.Equals(setting.Lecture.SummaryConfigName, name, StringComparison.Ordinal))
                return;

            setting.Lecture.SummaryConfigName = name;
            setting.Save();
            BindApiCard();
            LoadApiSetting();
            TestApiResult.Text = string.Empty;
        }

        private void LoadApiSetting(bool refreshProviders = true)
        {
            var config = ApiConfig;
            if (config == null)
                return;

            int index = PROVIDERS.Length - 1;
            for (int i = 0; i < PROVIDERS.Length - 1; i++)
            {
                if (string.IsNullOrEmpty(config.ApiUrl) ||
                    config.ApiUrl.Contains(new Uri(PROVIDERS[i].ApiUrl).Host, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            int savedIndex = Translator.Setting.OpenAIConfigs.ToList().IndexOf(config);
            if (refreshProviders)
                RefreshProviders(!string.IsNullOrWhiteSpace(config.ConfigName) && savedIndex >= 0
                    ? PROVIDERS.Length + savedIndex : index);
            ModelBox.ItemsSource = PROVIDERS[index].Models;
            // Fill in DeepSeek defaults on first use.
            if (string.IsNullOrEmpty(config.ApiUrl))
                ApplyProvider(PROVIDERS[index], config);

            FillKeyBox();
            bool wasInitializing = initializing;
            initializing = true;
            try { LoadSummarySlots(); }
            finally { initializing = wasInitializing; }
        }

        private void FillKeyBox()
        {
            fillingKeyBox = true;
            ApiKeyBox.Password = ApiConfig?.ApiKey ?? string.Empty;
            fillingKeyBox = false;
            ShowApiKeyStatus();
        }

        private void OnApiConfigChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(OpenAIConfig.ApiKey))
                return;
            Dispatcher.InvokeAsync(() =>
            {
                // Changed elsewhere (e.g. the API settings window): show it here too.
                string key = ApiConfig?.ApiKey ?? string.Empty;
                if (ApiKeyBox.Password != key && !ApiKeyBox.IsKeyboardFocusWithin)
                    FillKeyBox();
                else
                    ShowApiKeyStatus();
            });
        }

        private static void ApplyProvider(Provider provider, OpenAIConfig config)
        {
            if (!string.IsNullOrEmpty(provider.ApiUrl))
            {
                config.ApiUrl = provider.ApiUrl;
                config.ApiStyle = "standard";
            }
            if (provider.Models.Length > 0 && !provider.Models.Contains(config.ModelName))
                config.ModelName = provider.Models[0];
        }

        private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || fillingProviderBox || ProviderBox.SelectedIndex < 0 || ApiConfig == null)
                return;
            if (ProviderBox.SelectedIndex >= PROVIDERS.Length)
            {
                var slots = Translator.Setting.OpenAIConfigs.ToList();
                int slotIndex = ProviderBox.SelectedIndex - PROVIDERS.Length;
                if (slotIndex >= slots.Count)
                    return;
                Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = slotIndex;
                Translator.Setting.Lecture.SummaryConfigName = string.Empty;
                Translator.Setting.Save();
                BindApiCard();
                LoadApiSetting(false);
                TestApiResult.Text = string.Empty;
                return;
            }
            var provider = PROVIDERS[ProviderBox.SelectedIndex];
            if (!string.IsNullOrWhiteSpace(ApiConfig.ConfigName))
            {
                var draft = new OpenAIConfig { ApiKey = ApiConfig.ApiKey };
                Translator.Setting.Configs[Summarizer.SUMMARY_API].Add(draft);
                Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = Translator.Setting.Configs[Summarizer.SUMMARY_API].Count - 1;
                Translator.Setting.Lecture.SummaryConfigName = string.Empty;
                BindApiCard();
                RefreshProviders(ProviderBox.SelectedIndex);
            }
            ModelBox.ItemsSource = provider.Models;
            ApplyProvider(provider, ApiConfig);
            TestApiResult.Text = string.Empty;
        }

        private void RefreshProviders(int selectedIndex)
        {
            fillingProviderBox = true;
            try
            {
                providerActions.Clear();
                while (ProviderBox.Items.Count > PROVIDERS.Length)
                    ProviderBox.Items.RemoveAt(PROVIDERS.Length);
                int position = 1;
                foreach (var slot in Translator.Setting.OpenAIConfigs)
                {
                    int index = position - 1;
                    string label = string.IsNullOrWhiteSpace(slot.ConfigName) ? $"配置 {position}" : slot.ConfigName.Trim();
                    position++;
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(new TextBlock { Text = label, MinWidth = 180, VerticalAlignment = VerticalAlignment.Center });
                    foreach (var (symbol, action) in new[] { ("⬆", -1), ("⬇", 1), ("删除", 0) })
                    {
                        var button = new System.Windows.Controls.Button
                        {
                            Content = symbol, Tag = index, Margin = new Thickness(3, 0, 0, 0),
                            Padding = new Thickness(5, 1, 5, 1),
                            Visibility = ProviderBox.IsDropDownOpen ? Visibility.Visible : Visibility.Collapsed
                        };
                        button.Click += action == 0 ? DeleteProvider_click : action < 0 ? MoveProviderUp_click : MoveProviderDown_click;
                        row.Children.Add(button);
                        providerActions.Add(button);
                    }
                    ProviderBox.Items.Add(new ComboBoxItem { Content = row });
                }
                ProviderBox.SelectedIndex = selectedIndex;
            }
            finally { fillingProviderBox = false; }
        }

        private void ProviderBox_DropDownOpened(object sender, EventArgs e)
        {
            foreach (var button in providerActions) button.Visibility = Visibility.Visible;
        }
        private void ProviderBox_DropDownClosed(object sender, EventArgs e)
        {
            foreach (var button in providerActions) button.Visibility = Visibility.Collapsed;
        }

        private void DeleteProvider_click(object sender, RoutedEventArgs e)
        {
            int index = (sender as System.Windows.Controls.Button)?.Tag is int slotIndex ? slotIndex : -1;
            var slots = Translator.Setting.Configs[Summarizer.SUMMARY_API];
            if (index < 0 || index >= slots.Count || slots.Count <= 1) return;
            if (MessageBox.Show($"删除配置“{slots[index].ConfigName}”？", "删除配置",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string name = slots[index].ConfigName;
            slots.RemoveAt(index);
            int current = Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API];
            Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = current == index
                ? Math.Min(index, slots.Count - 1) : current > index ? current - 1 : current;
            if (Translator.Setting.Lecture.SummaryConfigName == name)
                Translator.Setting.Lecture.SummaryConfigName = string.Empty;
            Translator.Setting.Save();
            BindApiCard();
            LoadApiSetting();
            TestApiResult.Text = string.Empty;
        }

        private void MoveProviderUp_click(object sender, RoutedEventArgs e) => MoveProvider(sender, -1);
        private void MoveProviderDown_click(object sender, RoutedEventArgs e) => MoveProvider(sender, 1);

        private void MoveProvider(object sender, int direction)
        {
            int index = (sender as System.Windows.Controls.Button)?.Tag is int slotIndex ? slotIndex : -1;
            var slots = Translator.Setting.Configs[Summarizer.SUMMARY_API];
            int target = index + direction;
            if (index < 0 || target < 0 || target >= slots.Count) return;
            int selected = ProviderBox.SelectedIndex;
            if (selected == PROVIDERS.Length + index) selected = PROVIDERS.Length + target;
            else if (selected == PROVIDERS.Length + target) selected = PROVIDERS.Length + index;
            (slots[index], slots[target]) = (slots[target], slots[index]);
            int current = Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API];
            if (current == index) Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = target;
            else if (current == target) Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = index;
            Translator.Setting.Save();
            RefreshProviders(selected);
            ProviderBox.IsDropDownOpen = true;
        }

        private void RefreshAsrModels()
        {
            AsrModelBox.ItemsSource = null;
            AsrModelBox.ItemsSource = Translator.Setting.Lecture.AsrModels;
            AsrModelBox.Text = Translator.Setting.Lecture.AsrModel;
        }

        private void DeleteAsrModel_click(object sender, RoutedEventArgs e)
        {
            var lecture = Translator.Setting.Lecture;
            string model = AsrModelBox.Text?.Trim() ?? string.Empty;
            if (!lecture.AsrModels.Remove(model)) return;
            if (lecture.AsrModel == model)
                lecture.AsrModel = lecture.AsrModels.FirstOrDefault() ?? LectureState.DEFAULT_ASR_MODEL;
            RefreshAsrModels();
            Translator.Setting.Save();
        }

        private void MoveAsrModelUp_click(object sender, RoutedEventArgs e) => MoveAsrModel(-1);
        private void MoveAsrModelDown_click(object sender, RoutedEventArgs e) => MoveAsrModel(1);

        private void MoveAsrModel(int direction)
        {
            var models = Translator.Setting.Lecture.AsrModels;
            int index = models.FindIndex(m => string.Equals(m, AsrModelBox.Text?.Trim(), StringComparison.OrdinalIgnoreCase));
            int target = index + direction;
            if (index < 0 || target < 0 || target >= models.Count) return;
            (models[index], models[target]) = (models[target], models[index]);
            RefreshAsrModels();
            Translator.Setting.Save();
        }

        private void OpenAsrQuota_click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("https://bailian.console.aliyun.com/cn-beijing/costing-balance/free-quota?model=asr")
                { UseShellExecute = true });

        private void SaveApiConfig_click(object sender, RoutedEventArgs e)
        {
            ApiUrlBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ModelBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            var current = ApiConfig;
            if (current == null || string.IsNullOrWhiteSpace(current.ApiUrl) || string.IsNullOrWhiteSpace(current.ModelName))
            {
                TestApiResult.Text = "请先填写 API 地址和模型。";
                return;
            }
            string? name = NameConfigDialog.Ask(Window.GetWindow(this), "保存模型配置", current.ConfigName);
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0 || Translator.Setting.OpenAIConfigs.Any(c =>
                    string.Equals(c.ConfigName, name, StringComparison.OrdinalIgnoreCase)))
            {
                TestApiResult.Text = "配置名称不能为空，也不能与已有配置重名。";
                return;
            }
            var saved = new OpenAIConfig
            {
                ConfigName = name, ApiUrl = current.ApiUrl, ApiKey = current.ApiKey,
                ModelName = current.ModelName, Temperature = current.Temperature,
                ApiStyle = current.ApiStyle, SourceLanguage = current.SourceLanguage,
                GlossaryTerms = current.GlossaryTerms
            };
            Translator.Setting.Configs[Summarizer.SUMMARY_API].Add(saved);
            Translator.Setting.ConfigIndices[Summarizer.SUMMARY_API] = Translator.Setting.Configs[Summarizer.SUMMARY_API].Count - 1;
            Translator.Setting.Lecture.SummaryConfigName = string.Empty;
            Translator.Setting.Save();
            BindApiCard();
            RefreshProviders(PROVIDERS.Length + Translator.Setting.OpenAIConfigs.ToList().IndexOf(saved));
            FillKeyBox();
            LoadSummarySlots();
            TestApiResult.Text = $"已保存配置：{name}";
        }

        // Saved on every keystroke / paste, independent of the page state, so the key can't be lost
        // by switching pages.
        private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (fillingKeyBox || ApiConfig == null)
                return;
            string key = CleanKey(ApiKeyBox.Password);
            // Only the user clearing the box may erase a saved key, never a control reloading itself.
            if (key.Length == 0 && ApiConfig.ApiKey.Length > 0 && !ApiKeyBox.IsKeyboardFocusWithin)
            {
                FillKeyBox();
                return;
            }
            if (key != ApiConfig.ApiKey)
            {
                bool firstKey = ApiConfig.ApiKey.Length == 0 && key.Length > 0;
                ApiConfig.ApiKey = key;
                if (firstKey)
                    SwitchTranslateApiToSummary();
                Translator.Setting?.Save();
            }
            ShowApiKeyStatus();
        }

        // Paragraph refinement and the glossary in translation only work when the translation API is the
        // one configured in ①. Fresh installs start on Google so translation works without a key, so the
        // first saved key moves a Google default over; a deliberately chosen API is left alone.
        private void SwitchTranslateApiToSummary()
        {
            var setting = Translator.Setting;
            if (setting == null || (setting.ApiName != "Google" && setting.ApiName != "Google2"))
                return;
            setting.ApiName = Summarizer.SUMMARY_API;
            bool wasInitializing = initializing;
            initializing = true;
            try
            {
                if (TranslateApiBox.ItemsSource is IEnumerable<ApiChoice> choices)
                    TranslateApiBox.SelectedItem = choices.FirstOrDefault(c => c.Key == setting.ApiName);
                LoadTargetLanguages();
            }
            finally
            {
                initializing = wasInitializing;
            }
        }

        // Shows that a key is stored without revealing it.
        private void ShowApiKeyStatus()
        {
            string key = ApiConfig?.ApiKey ?? string.Empty;
            ApiKeyStatus.Text = key.Length == 0
                ? "还没有保存 API Key。"
                : $"✓ 已保存：{MaskKey(key)}，" +
                  $"保存在 {AppPaths.SettingFile}";
        }

        private bool fillingAsrKeyBox = false;

        // Pasted keys sometimes carry spaces, line breaks or invisible characters.
        private static string CleanKey(string text)
        {
            return new string(text.Where(c => c > ' ' && c < 0x7F).ToArray());
        }

        private static string MaskKey(string key)
        {
            return $"{key[..Math.Min(3, key.Length)]}…{key[Math.Max(0, key.Length - 4)..]}（{key.Length} 位）";
        }

        private void ShowSaveFolder()
        {
            SaveFolderBox.Text = LectureDocument.SaveFolder;
        }

        private void ChooseSaveFolder_click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择转录文件的保存位置",
                InitialDirectory = Directory.Exists(LectureDocument.SaveFolder) ? LectureDocument.SaveFolder : string.Empty,
            };
            if (dialog.ShowDialog() != true)
                return;
            Translator.Setting.Lecture.SaveFolder = dialog.FolderName;
            ShowSaveFolder();
        }

        private void OpenSaveFolder_click(object sender, RoutedEventArgs e)
        {
            LectureDialogs.ShowInFolder(LectureDocument.SaveFolder);
        }

        private void ResetSaveFolder_click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.Lecture.SaveFolder = string.Empty;
            ShowSaveFolder();
        }

        private void LoadEngine()
        {
            var lecture = Translator.Setting.Lecture;
            EngineBox.SelectedIndex = (int)lecture.Engine;
            AliyunPanel.Visibility = lecture.Engine == RecognitionEngine.Aliyun ? Visibility.Visible : Visibility.Collapsed;
            fillingAsrKeyBox = true;
            AsrKeyBox.Password = lecture.AsrApiKey;
            fillingAsrKeyBox = false;
            ShowAsrKeyStatus();
        }

        private void EngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || EngineBox.SelectedIndex < 0)
                return;
            var engine = (RecognitionEngine)EngineBox.SelectedIndex;
            Translator.Setting.Lecture.Engine = engine;
            AliyunPanel.Visibility = engine == RecognitionEngine.Aliyun ? Visibility.Visible : Visibility.Collapsed;
            // LiveCaptions is only needed by the system engine; with the cloud engine it is closed to save power
            // (it is started again when a class with the system engine starts).
            if (engine == RecognitionEngine.Aliyun && !ClassSession.IsRunning)
            {
                Translator.CloseLiveCaptions();
                ShowLiveCaptionsState();
            }
            else if (Translator.Window != null && LiveCaptionsHandler.IsHidden)
            {
                try
                {
                    LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
                }
                catch (Exception)
                {
                    // Best effort: the LiveCaptions window may already be gone.
                }
            }
            if (ClassSession.IsRunning)
                SnackbarHost.Show("识别方式已更改。", "点“停止”再点“开始”后生效。", SnackbarType.Info, timeout: 3);
        }

        private void AsrKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (fillingAsrKeyBox)
                return;
            var lecture = Translator.Setting.Lecture;
            string key = CleanKey(AsrKeyBox.Password);
            if (key.Length == 0 && lecture.AsrApiKey.Length > 0 && !AsrKeyBox.IsKeyboardFocusWithin)
            {
                LoadEngine();
                return;
            }
            if (key != lecture.AsrApiKey)
                lecture.AsrApiKey = key;
            ShowAsrKeyStatus();
        }

        private void ShowAsrKeyStatus()
        {
            string key = Translator.Setting.Lecture.AsrApiKey;
            AsrKeyStatus.Text = key.Length == 0
                ? "还没有保存阿里云 API Key。"
                : $"✓ 已保存：{MaskKey(key)}";
        }

        private async void TestAsr_click(object sender, RoutedEventArgs e)
        {
            AsrModelBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            AsrEndpointBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            TestAsrButton.IsEnabled = false;
            TestAsrResult.Text = "测试中……";
            try
            {
                string? note = await CloudAsr.Test();
                TestAsrResult.Text = note == null ? "✓ 连接成功" : $"✓ 连接成功（{note}）";
                var lecture = Translator.Setting.Lecture;
                string model = lecture.AsrModel.Trim();
                if (!lecture.AsrModels.Any(saved => string.Equals(saved, model, StringComparison.OrdinalIgnoreCase)))
                {
                    lecture.AsrModels.Add(model);
                    RefreshAsrModels();
                    Translator.Setting.Save();
                }
            }
            catch (Exception ex)
            {
                TestAsrResult.Text = $"✗ {ex.Message}";
            }
            finally
            {
                TestAsrButton.IsEnabled = true;
            }
        }

        private void LoadTranslateSetting()
        {
            var keys = Translator.Setting.Configs.Keys.ToList();
            var choices = keys
                .OrderBy(key => key == Summarizer.SUMMARY_API ? 0 : API_NAMES.ContainsKey(key) ? 1 : 2)
                .Select(key => new ApiChoice(key, API_NAMES.TryGetValue(key, out var name) ? name : key))
                .ToList();
            TranslateApiBox.ItemsSource = choices;
            TranslateApiBox.SelectedItem = choices.FirstOrDefault(c => c.Key == Translator.Setting.ApiName);
            LoadTargetLanguages();
        }

        private void TranslateApiBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || TranslateApiBox.SelectedItem is not ApiChoice choice)
                return;
            Translator.Setting.ApiName = choice.Key;
            initializing = true;
            try
            {
                LoadTargetLanguages();
            }
            finally
            {
                initializing = false;
            }
        }

        private void LoadTargetLanguages()
        {
            var configType = Translator.Setting[Translator.Setting.ApiName].GetType();
            PropertyInfo? languagesProp = null;
            // Traverse base classes to find `SupportedLanguages`
            for (var type = configType; type != null && languagesProp == null; type = type.BaseType)
                languagesProp = type.GetProperty("SupportedLanguages", BindingFlags.Public | BindingFlags.Static);
            languagesProp ??= typeof(TranslateAPIConfig).GetProperty(
                "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            var supportedLanguages = (Dictionary<string, string>)languagesProp!.GetValue(null)!;
            string targetLang = Translator.Setting.TargetLanguage;
            if (!supportedLanguages.ContainsKey(targetLang))
                supportedLanguages[targetLang] = targetLang;    // add custom language to supported languages
            TargetLangBox.ItemsSource = supportedLanguages.Keys.ToList();
            TargetLangBox.SelectedItem = targetLang;
        }

        private void TargetLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!initializing && TargetLangBox.SelectedItem != null)
                Translator.Setting.TargetLanguage = TargetLangBox.SelectedItem.ToString();
        }

        private void TargetLangBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!initializing && !string.IsNullOrWhiteSpace(TargetLangBox.Text))
                Translator.Setting.TargetLanguage = TargetLangBox.Text.Trim();
        }

        private void APISettingButton_click(object sender, RoutedEventArgs e)
        {
            if (settingWindow != null && settingWindow.IsLoaded)
                settingWindow.Activate();
            else
            {
                settingWindow = new SettingWindow();
                settingWindow.Closed += (sender, args) => settingWindow = null;
                settingWindow.Show();
            }
        }

        private void ShowLiveCaptionsState()
        {
            LiveCaptionsButton.Content = LiveCaptionsHandler.IsHidden || Translator.Window == null
                ? "显示系统实时辅助字幕"
                : "隐藏系统实时辅助字幕";
        }

        private async void LiveCaptionsButton_click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = Translator.Window ?? await Task.Run(Translator.EnsureLiveCaptions);
                if (LiveCaptionsHandler.IsHidden)
                    LiveCaptionsHandler.RestoreLiveCaptions(window);
                else
                    LiveCaptionsHandler.HideLiveCaptions(window);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 操作失败。", ex.Message, SnackbarType.Error, timeout: 2);
            }
            ShowLiveCaptionsState();
        }

        private async void ExtractGlossary_click(object sender, RoutedEventArgs e)
        {
            GlossaryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ExtractGlossaryButton.IsEnabled = false;
            GlossaryResult.Text = "正在从课件中提取术语……";
            try
            {
                var (text, added) = await Glossary.ExtractFromSlides();
                Translator.Setting.Lecture.Glossary = text;
                GlossaryBox.Text = text;
                GlossaryResult.Text = added > 0 ? $"✓ 新增 {added} 个术语，请检查一下译法" : "没有发现新的术语";
            }
            catch (Exception ex)
            {
                GlossaryResult.Text = $"✗ {ex.Message}";
            }
            finally
            {
                ExtractGlossaryButton.IsEnabled = true;
            }
        }

        private async void TestApi_click(object sender, RoutedEventArgs e)
        {
            // Commit fields that only update on lost focus.
            ApiUrlBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ModelBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();

            TestApiButton.IsEnabled = false;
            TestApiResult.Text = "测试中……";
            try
            {
                TestApiResult.Text = await Summarizer.TestConnection(ApiConfig);
            }
            catch (Exception ex)
            {
                TestApiResult.Text = $"✗ {ex.Message}";
            }
            finally
            {
                TestApiButton.IsEnabled = true;
            }
        }

        private void LoadDevices()
        {
            bool wasInitializing = initializing;
            initializing = true;

            SourceBox.SelectedIndex = (int)Translator.Setting.Lecture.InputSource;

            var microphones = AudioDevices.List(DataFlow.Capture);
            MicBox.ItemsSource = microphones;
            MicBox.SelectedItem = microphones.FirstOrDefault(device => device.IsDefault);
            MicBox.IsEnabled = Translator.Setting.Lecture.InputSource == InputSource.Microphone;

            var outputs = new List<object> { DEFAULT_OUTPUT };
            var renderDevices = AudioDevices.List(DataFlow.Render);
            outputs.AddRange(renderDevices);
            SpeechDeviceBox.ItemsSource = outputs;
            string speechDeviceId = Translator.Setting.Lecture.SpeechDeviceId;
            SpeechDeviceBox.SelectedItem = renderDevices.FirstOrDefault(device => device.Id == speechDeviceId)
                                           ?? (object)DEFAULT_OUTPUT;
            ShowSpeechDeviceHint();

            initializing = wasInitializing;
        }

        private void RefreshDevices_click(object sender, RoutedEventArgs e)
        {
            LoadDevices();
        }

        private async void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || SourceBox.SelectedIndex < 0)
                return;
            var source = (InputSource)SourceBox.SelectedIndex;
            Translator.Setting.Lecture.InputSource = source;
            MicBox.IsEnabled = source == InputSource.Microphone;
            ShowSpeechDeviceHint();

            var window = Translator.Window;
            if (window == null)
            {
                SourceStatus.Text = Translator.Setting.Lecture.Engine == RecognitionEngine.Aliyun
                    ? "✓ 已保存。阿里云识别会直接使用这里选的声音来源。"
                    : "✓ 已保存，点“开始”时会自动设置好系统实时辅助字幕。";
                return;
            }

            bool useMicrophone = source == InputSource.Microphone;
            SourceStatus.Text = "正在设置实时字幕……";
            SourceBox.IsEnabled = false;
            bool ok;
            try
            {
                ok = await Task.Run(() => LiveCaptionsMicrophone.Set(window, useMicrophone));
            }
            catch (Exception)
            {
                ok = false;
            }
            finally
            {
                SourceBox.IsEnabled = true;
            }

            if (ok)
            {
                SourceStatus.Text = useMicrophone
                    ? "✓ 已打开实时字幕的\"包含麦克风音频\"。"
                    : "✓ 已关闭实时字幕的\"包含麦克风音频\"，只听电脑播放的声音。";
            }
            else
            {
                SourceStatus.Text = useMicrophone
                    ? "没能自动设置。已打开实时字幕的设置菜单，请在\"首选项\"里勾选\"包含麦克风音频\"，然后点\"隐藏\"。"
                    : "没能自动设置。已打开实时字幕的设置菜单，请在\"首选项\"里取消\"包含麦克风音频\"，然后点\"隐藏\"。";
                await Task.Run(() => LiveCaptionsMicrophone.ShowSettings(window));
            }
        }

        private void MicBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || MicBox.SelectedItem is not AudioDevice device || device.IsDefault)
                return;
            try
            {
                AudioDevices.SetDefault(device.Id);
                Translator.RestartLiveCaptions();
                SourceStatus.Text = $"✓ 已改用：{device.Name}";
            }
            catch (Exception ex)
            {
                SourceStatus.Text = $"✗ 切换麦克风失败：{ex.Message}";
            }
            LoadDevices();
        }

        private void SpeechDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing)
                return;
            Translator.Setting.Lecture.SpeechDeviceId =
                SpeechDeviceBox.SelectedItem is AudioDevice device ? device.Id : string.Empty;
            ShowSpeechDeviceHint();
        }

        private void ShowSpeechDeviceHint()
        {
            string speechDeviceId = Translator.Setting.Lecture.SpeechDeviceId;
            bool onDefault = string.IsNullOrEmpty(speechDeviceId) ||
                             speechDeviceId == AudioDevices.DefaultId(DataFlow.Render);
            SpeechDeviceHint.Text = onDefault
                ? "播报和电脑声音走同一个设备，实时字幕会听到播报，所以播报时会暂停接收字幕。" +
                  "线下课建议在这里直接选蓝牙耳机，并把 Windows 默认输出设为电脑扬声器。"
                : "播报只从这个设备播出，不会被实时字幕听到。";
        }

        private void LoadOcrLanguages()
        {
            var languages = new List<object> { AUTO_OCR };
            var available = SlideOcr.AvailableLanguages();
            languages.AddRange(available);
            OcrLanguageBox.ItemsSource = languages;
            string current = Translator.Setting.Lecture.OcrLanguage;
            OcrLanguageBox.SelectedItem = available.FirstOrDefault(l => l.Tag == current) ?? (object)AUTO_OCR;
        }

        private void OcrLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing)
                return;
            Translator.Setting.Lecture.OcrLanguage =
                OcrLanguageBox.SelectedItem is SlideOcr.OcrLanguage language ? language.Tag : string.Empty;
        }

        private void OnCurrentPageChanged(int? page)
        {
            Dispatcher.InvokeAsync(ShowSlidesInfo);
        }

        private void ShowSlidesInfo()
        {
            if (!SlideDeck.IsLoaded)
            {
                SlidesInfo.Text = "未上传课件：按话题变化分节。";
                ClearSlidesButton.Visibility = Visibility.Collapsed;
                return;
            }
            string page = Summarizer.CurrentPage == null ? "识别中" : $"第 {Summarizer.CurrentPage} 页";
            string ocr = SlideDeck.OcrPageCount > 0 ? $"（其中 {SlideDeck.OcrPageCount} 页用 OCR 识别）" : string.Empty;
            SlidesInfo.Text = $"{SlideDeck.FileName}\n共 {SlideDeck.Pages.Count} 页{ocr}，当前：{page}";
            ClearSlidesButton.Visibility = Visibility.Visible;
        }

        private async void LoadSlides_click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = SlideDeck.FileFilter, RestoreDirectory = true };
            if (dialog.ShowDialog() != true)
                return;

            SlidesInfo.Text = "读取中……";
            LoadSlidesButton.IsEnabled = false;
            try
            {
                await SlideDeck.Load(dialog.FileName, new Progress<string>(text => SlidesInfo.Text = text));
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 课件读取失败。", ex.Message, SnackbarType.Error,
                    timeout: 3, closeButton: true);
            }
            finally
            {
                LoadSlidesButton.IsEnabled = true;
            }
            ShowSlidesInfo();
        }

        private void ClearSlides_click(object sender, RoutedEventArgs e)
        {
            SlideDeck.Clear();
            ShowSlidesInfo();
        }

        private void LoadVoices()
        {
            var voices = new List<string> { AUTO_VOICE };
            voices.AddRange(Speaker.GetVoices());
            VoiceBox.ItemsSource = voices;

            string current = Translator.Setting.Lecture.VoiceName;
            VoiceBox.SelectedItem = voices.Contains(current) ? current : AUTO_VOICE;
        }

        private void VoiceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || VoiceBox.SelectedItem is not string voice)
                return;
            Translator.Setting.Lecture.VoiceName = voice == AUTO_VOICE ? string.Empty : voice;
        }

        private void TestSpeech_click(object sender, RoutedEventArgs e)
        {
            Speaker.Speak("这是一段试听。老师每讲完一节，你会在耳机里听到这一节的小结。");
        }

        private void SoundSettings_click(object sender, RoutedEventArgs e)
        {
            try
            {
                ShellUtil.Open("ms-settings:sound");
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 无法打开声音设置。", ex.Message, SnackbarType.Error, timeout: 2);
            }
        }

        private void ResetPrompt_click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.Lecture.SummaryPrompt = LectureState.DEFAULT_SUMMARY_PROMPT;
        }
    }
}
