using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using ComboBox = System.Windows.Controls.ComboBox;

namespace LiveCaptionsTranslator
{
    public partial class SettingWindow : FluentWindow
    {
        private System.Windows.Controls.Button currentSelected;
        private Dictionary<string, FrameworkElement> sectionReferences;

        public SettingWindow()
        {
            InitializeComponent();
            MainWindow.ApplySavedTheme();
            DataContext = Translator.Setting;

            Loaded += (sender, args) =>
            {
                if (Translator.Setting.MainWindow.Theme == "System")
                    SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
                Initialize();
                SelectButton(PromptButton);
            };
        }

        private void Initialize()
        {
            sectionReferences = new Dictionary<string, FrameworkElement>
            {
                { "General", ContentPanel },
                { "Prompt", PromptSection }
            };

            foreach (var apiName in TranslateAPI.TRANSLATE_FUNCTIONS.Keys.Where(apiName =>
                         !TranslateAPI.NO_CONFIG_APIS.Contains(apiName)))
            {
                sectionReferences[apiName] = FindName($"{apiName}Section") as StackPanel;
                SwitchConfig(apiName, Translator.Setting.ConfigIndices[apiName]);
            }
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configs = Translator.Setting.Configs[apiName];
                var configIndex = Translator.Setting.ConfigIndices[apiName];

                var type = Type.GetType($"LiveCaptionsTranslator.models.{apiName}Config");
                var config = Activator.CreateInstance(type) as TranslateAPIConfig;
                configs.Insert(configIndex + 1, config);
                SwitchConfig(apiName, configIndex + 1);

                Translator.Setting.OnPropertyChanged("Configs");

                // Ask for a name right away so the new slot is identifiable in the drop-down.
                string newName = NameConfigDialog.Ask(this, "命名配置", "");
                if (newName != null)
                {
                    configs[configIndex + 1].ConfigName = newName;
                    configs[configIndex + 1].OnPropertyChanged("ConfigName");
                    RefreshConfigComboBox(apiName);
                    Translator.Setting.Save();
                }
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configs = Translator.Setting.Configs[apiName];
                var configIndex = Translator.Setting.ConfigIndices[apiName];

                if (configs.Count <= 1)
                {
                    (FindName($"{apiName}DeleteFlyout") as Flyout)?.Show();
                    return;
                }
                configs.RemoveAt(configIndex);
                SwitchConfig(apiName, Math.Max(0, Math.Min(configs.Count - 1, configIndex)));

                Translator.Setting.OnPropertyChanged("Configs");
            }
        }

        private void PriorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configIndex = Translator.Setting.ConfigIndices[apiName];
                SwitchConfig(apiName, configIndex - 1);
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configIndex = Translator.Setting.ConfigIndices[apiName];
                SwitchConfig(apiName, configIndex + 1);
            }
        }

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button)
            {
                SelectButton(button);
                string targetSection = button.Tag.ToString();
                if (sectionReferences.TryGetValue(targetSection, out FrameworkElement element))
                    element.BringIntoView();
            }
        }

        // The info flyout is the sibling of the hovered button.
        private static Flyout? InfoFlyout(object sender)
        {
            return ((sender as FrameworkElement)?.Parent as StackPanel)?.Children.OfType<Flyout>().FirstOrDefault();
        }

        private void APIUrlInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            InfoFlyout(sender)?.Show();
        }

        private void APIUrlInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            InfoFlyout(sender)?.Hide();
        }

        private async void LoadModelsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string apiName && apiName == "LMStudio")
            {
                string baseUrl = (Translator.Setting["LMStudio"] as LMStudioConfig)?.ApiUrl ?? "";

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    System.Windows.MessageBox.Show("请先填写接口地址。",
                        "加载模型", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                button.IsEnabled = false;
                try
                {
                    var models = await ModelsApiService.FetchModelsAsync(apiName, baseUrl);
                    var comboBox = FindName($"{apiName}ModelComboBox") as ComboBox;
                    if (comboBox != null)
                    {
                        comboBox.ItemsSource = models;
                        if (models.Count > 0)
                            System.Windows.MessageBox.Show($"已加载 {models.Count} 个模型。",
                                "加载模型", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                        else
                            System.Windows.MessageBox.Show("没有找到模型或无法连接，请确认服务已启动。",
                                "加载模型", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                }
                finally
                {
                    button.IsEnabled = true;
                }
            }
        }

        private void LMStudioModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is ModelsApiService.ModelInfo mi)
            {
                var config = Translator.Setting["LMStudio"] as LMStudioConfig;
                if (config != null)
                {
                    config.ModelName = mi.Id;
                    if (sender is ComboBox cb)
                        cb.Text = mi.Id;
                }
            }
        }

        private void SwitchConfig(string apiName, int index)
        {
            if (index < 0 || index >= Translator.Setting.Configs[apiName].Count)
                return;

            if (Translator.Setting.ConfigIndices[apiName] != index)
                Translator.Setting.ConfigIndices[apiName] = index;

            if (FindName($"{apiName}Index") is TextBlock indexTextBlock)
            {
                int total = Translator.Setting.Configs[apiName].Count;
                indexTextBlock.Text = $"{index + 1}/{total}";
            }
            RefreshConfigComboBox(apiName);
            SyncApiStyleComboBox(apiName);
            Translator.Setting.OnPropertyChanged(null);
        }

        /// <summary>
        /// Item shown in the configuration picker: the slot name plus its position, so that
        /// unnamed slots are still distinguishable.
        /// </summary>
        public class ConfigSlotItem
        {
            public int Index { get; set; }
            public string Label { get; set; } = "";
            public override string ToString() => Label;
        }

        /// <summary>
        /// Rebuilds the drop-down that lists every saved configuration of one API type.
        /// The item source is replaced, so the selection is restored afterwards to avoid
        /// triggering a spurious switch.
        /// </summary>
        private void RefreshConfigComboBox(string apiName)
        {
            var comboBox = FindName($"{apiName}ConfigComboBox") as ComboBox;
            if (comboBox == null)
                return;

            var configs = Translator.Setting.Configs[apiName];
            int current = Translator.Setting.ConfigIndices[apiName];
            if (current < 0 || current >= configs.Count)
                current = 0;

            var items = new List<ConfigSlotItem>();
            for (int i = 0; i < configs.Count; i++)
            {
                string name = configs[i].ConfigName;
                string label = string.IsNullOrWhiteSpace(name)
                    ? $"配置 {i + 1}"
                    : $"{name}";
                items.Add(new ConfigSlotItem { Index = i, Label = label });
            }

            bool wasUpdating = isRefreshingComboBox;
            isRefreshingComboBox = true;
            try
            {
                comboBox.ItemsSource = items;
                comboBox.SelectedIndex = current;
            }
            finally
            {
                isRefreshingComboBox = wasUpdating;
            }
        }

        private bool isRefreshingComboBox = false;

        private void ConfigComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isRefreshingComboBox)
                return;
            if (sender is not ComboBox comboBox)
                return;
            if (comboBox.Tag is not string apiName)
                return;
            if (comboBox.SelectedItem is not ConfigSlotItem item)
                return;

            SwitchConfig(apiName, item.Index);
        }

        private void RenameConfigButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button button)
                return;
            if (button.Tag is not string apiName)
                return;

            var configs = Translator.Setting.Configs[apiName];
            int index = Translator.Setting.ConfigIndices[apiName];
            if (index < 0 || index >= configs.Count)
                return;

            string current = configs[index].ConfigName ?? "";
            string newName = NameConfigDialog.Ask(this, "命名配置", current);
            if (newName == null)
                return;

            configs[index].ConfigName = newName;
            configs[index].OnPropertyChanged("ConfigName");
            RefreshConfigComboBox(apiName);
            Translator.Setting.Save();
        }

        private void ApiStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isRefreshingComboBox)
                return;
            if (sender is not ComboBox comboBox)
                return;
            if (comboBox.Tag is not string apiName)
                return;
            if (comboBox.SelectedItem is not ComboBoxItem item)
                return;

            string style = item.Tag as string;
            if (string.IsNullOrEmpty(style))
                return;

            if (Translator.Setting[apiName] is OpenAIConfig config && config.ApiStyle != style)
            {
                config.ApiStyle = style;
                Translator.Setting.Save();
            }
        }

        /// <summary>
        /// Keeps the "接口类型" combo in sync with the stored configuration when the slot
        /// changes, without re-triggering its own selection handler.
        /// </summary>
        private void SyncApiStyleComboBox(string apiName)
        {
            var comboBox = FindName($"{apiName}ApiStyleComboBox") as ComboBox;
            if (comboBox == null)
                return;
            if (Translator.Setting[apiName] is not OpenAIConfig config)
                return;

            bool wasUpdating = isRefreshingComboBox;
            isRefreshingComboBox = true;
            try
            {
                for (int i = 0; i < comboBox.Items.Count; i++)
                {
                    if (comboBox.Items[i] is ComboBoxItem item && item.Tag as string == config.ApiStyle)
                    {
                        comboBox.SelectedIndex = i;
                        return;
                    }
                }
            }
            finally
            {
                isRefreshingComboBox = wasUpdating;
            }
        }

        private void SelectButton(System.Windows.Controls.Button button)
        {
            if (currentSelected != null)
                currentSelected.Background = Brushes.Transparent;
            // The theme brush is not present when the window is rendered outside a themed
            // application, so fall back to a neutral highlight instead of throwing.
            try
            {
                button.Background = (Brush)FindResource("ControlFillColorSecondaryBrush");
            }
            catch (Exception)
            {
                button.Background = new SolidColorBrush(Color.FromArgb(32, 128, 128, 128));
            }
            currentSelected = button;
        }
    }
}
