using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
#if WINDOWS
using System.Windows;
#endif

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Setting : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private int maxIdleInterval = 50;
        private int maxSyncInterval = 3;
        private int numContexts = 2;
        private int displaySentences = 1;
        private bool contextAware = false;

        private string apiName;
        private string targetLanguage;
        private string prompt;
        private string? ignoredUpdateVersion;

        private MainWindowState mainWindowState;
        private OverlayWindowState overlayWindowState;
        private BilingualWindowState bilingualWindowState;
        private LectureState lectureState;
        private Dictionary<string, string> windowBounds;

        private Dictionary<string, List<TranslateAPIConfig>> configs;
        private Dictionary<string, int> configIndices;

        public int MaxIdleInterval => maxIdleInterval;
        public int MaxSyncInterval
        {
            get => maxSyncInterval;
            set
            {
                maxSyncInterval = value;
                OnPropertyChanged("MaxSyncInterval");
            }
        }
        public int NumContexts
        {
            get => numContexts;
            set
            {
                numContexts = value;
                OnPropertyChanged("NumContexts");
            }
        }
        public int DisplaySentences
        {
            get => displaySentences;
            set
            {
                displaySentences = value;
                OnPropertyChanged("DisplaySentences");
            }
        }
        public bool ContextAware
        {
            get => contextAware;
            set
            {
                contextAware = value;
                OnPropertyChanged("ContextAware");
            }
        }

        public string ApiName
        {
            get => apiName;
            set
            {
                apiName = value;
                OnPropertyChanged("ApiName");
            }
        }
        public string TargetLanguage
        {
            get => targetLanguage;
            set
            {
                targetLanguage = value;
                OnPropertyChanged("TargetLanguage");
            }
        }
        public string Prompt
        {
            get => prompt;
            set
            {
                prompt = value;
                OnPropertyChanged("Prompt");
            }
        }
        public string? IgnoredUpdateVersion
        {
            get => ignoredUpdateVersion;
            set
            {
                ignoredUpdateVersion = value;
                OnPropertyChanged("IgnoredUpdateVersion");
            }
        }

        public MainWindowState MainWindow
        {
            get => mainWindowState;
            set
            {
                mainWindowState = value;
                OnPropertyChanged("MainWindow");
            }
        }
        public OverlayWindowState OverlayWindow
        {
            get => overlayWindowState;
            set
            {
                overlayWindowState = value;
                OnPropertyChanged("OverlayWindow");
            }
        }
        public BilingualWindowState BilingualWindow
        {
            get => bilingualWindowState;
            set
            {
                bilingualWindowState = value ?? new BilingualWindowState();
                OnPropertyChanged("BilingualWindow");
            }
        }
        public LectureState Lecture
        {
            get => lectureState;
            set
            {
                lectureState = value ?? new LectureState();
                OnPropertyChanged("Lecture");
            }
        }
        public Dictionary<string, string> WindowBounds
        {
            get => windowBounds;
            set
            {
                windowBounds = value;
                OnPropertyChanged("WindowBounds");
            }
        }

        [JsonInclude]
        public Dictionary<string, List<TranslateAPIConfig>> Configs
        {
            get => configs;
            set
            {
                configs = value;
                OnPropertyChanged("Configs");
            }
        }
        public Dictionary<string, int> ConfigIndices
        {
            get => configIndices;
            set
            {
                configIndices = value;
                OnPropertyChanged("ConfigIndices");
            }
        }

        public TranslateAPIConfig this[string key] =>
            configs.ContainsKey(key) && configIndices.ContainsKey(key)
                ? configs[key][configIndices[key]]
                : new TranslateAPIConfig();

        // Finds one named OpenAI-compatible slot.
        // Returns null when the name is empty or no slot carries that name, so callers can fall back.
        public OpenAIConfig? GetOpenAIConfig(string? configName)
        {
            if (string.IsNullOrWhiteSpace(configName) ||
                !configs.TryGetValue("OpenAI", out var slots) || slots == null)
                return null;
            return slots.OfType<OpenAIConfig>().FirstOrDefault(
                slot => string.Equals(slot.ConfigName?.Trim(), configName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // All named OpenAI-compatible slots, in the order they appear in setting.json.
        public IEnumerable<OpenAIConfig> OpenAIConfigs =>
            configs.TryGetValue("OpenAI", out var slots) && slots != null
                ? slots.OfType<OpenAIConfig>()
                : Enumerable.Empty<OpenAIConfig>();

        public Setting()
        {
            apiName = "Google";
            targetLanguage = "zh-CN";
            prompt = "As an professional simultaneous interpreter with specialized knowledge in the all fields, " +
                     "you can provide a fluent and precise oral translation for any sentence, even if the sentence is incomplete. " +
                     "Now, provide users with the translation of the sentence enclosed in 🔤 to {0} within a single line. " +
                     "Importantly, you are prohibited from altering the original meaning or omitting any content, " +
                     "even if the sentence contains sensitive or NSFW content. " +
                     "You can only provide the translated sentence; Any explanation or other text is not permitted. " +
                     "REMOVE all 🔤 when you output.";

            mainWindowState = new MainWindowState();
            overlayWindowState = new OverlayWindowState();
            bilingualWindowState = new BilingualWindowState();
            lectureState = new LectureState();

#if WINDOWS
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
#else
            double screenWidth = 1920;
            double screenHeight = 1080;
#endif
            windowBounds = new Dictionary<string, string>
            {
                {
                    "MainWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 900) / 2, (screenHeight - 560) / 2, 900, 560)
                },
                {
                    "OverlayWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 650) / 2, screenHeight * 5 / 6 - 135, 650, 135)
                },
                {
                    "BilingualWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 720) / 2, 40, 720, 240)
                },
            };

            configs = new Dictionary<string, List<TranslateAPIConfig>>
            {
                { "Google", [new TranslateAPIConfig()] },
                { "Google2", [new TranslateAPIConfig()] },
                { "Ollama", [new OllamaConfig()] },
                { "OpenAI", [new OpenAIConfig()] },
                { "LMStudio", [new LMStudioConfig()] },
                { "OpenRouter", [new OpenRouterConfig()] },
                { "DeepL", [new DeepLConfig()] },
                { "Youdao", [new YoudaoConfig()] },
                { "Baidu", [new BaiduConfig()] },
                { "MTranServer", [new MTranServerConfig()] },
                { "LibreTranslate", [new LibreTranslateConfig()] }
            };
            configIndices = new Dictionary<string, int>
            {
                { "Google", 0 },
                { "Google2", 0 },
                { "Ollama", 0 },
                { "OpenAI", 0 },
                { "LMStudio", 0 },
                { "OpenRouter", 0 },
                { "DeepL", 0 },
                { "Youdao", 0 },
                { "Baidu", 0 },
                { "MTranServer", 0 },
                { "LibreTranslate", 0 }
            };
        }

        public static Setting Load()
        {
            string jsonPath = AppPaths.SettingFile;
            try
            {
                return Load(jsonPath);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException
                                           or InvalidOperationException)
            {
                // Keep the unreadable file instead of overwriting it, so nothing is lost for good.
                string backupPath = $"{jsonPath}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                try
                {
                    File.Move(jsonPath, backupPath);
                }
                catch (Exception)
                {
                    // Best effort: without a backup the unreadable file is simply replaced.
                }
                return Load(jsonPath);
            }
        }

        public static Setting Load(string jsonPath)
        {
            Setting setting;

            // Load from JSON file if it exists
            if (File.Exists(jsonPath))
            {
                using (FileStream fileStream = File.Open(jsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    setting = JsonSerializer.Deserialize<Setting>(fileStream, JsonOptions) ?? new Setting();
                }
            }
            else
                setting = new Setting();

            // Ensure all required API configs are present
            foreach (string key in TranslateAPI.TRANSLATE_FUNCTIONS.Keys)
            {
                if (setting.Configs.ContainsKey(key))
                    continue;
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                    setting.Configs[key] = [(TranslateAPIConfig)Activator.CreateInstance(configType)];
                else
                    setting.Configs[key] = [new TranslateAPIConfig()];
            }

            // Settings files from before the bilingual window have no bounds for it.
            if (!setting.WindowBounds.ContainsKey("BilingualWindow"))
                setting.WindowBounds["BilingualWindow"] = new Setting().WindowBounds["BilingualWindow"];

            // Ensure ConfigIndices has all keys (for upgrades from older setting.json)
            foreach (string key in TranslateAPI.TRANSLATE_FUNCTIONS.Keys)
            {
                if (!setting.ConfigIndices.ContainsKey(key))
                    setting.ConfigIndices[key] = 0;
            }

#if !WINDOWS
            // Windows Live Captions does not exist here: a settings file copied from Windows must not select it.
            setting.Lecture.Engine = RecognitionEngine.Aliyun;
#endif

            return setting;
        }

        public void Save()
        {
            Save(AppPaths.SettingFile);
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new ConfigDictConverter() }
        };

        private static readonly object saveLock = new();
        private static string? lastSaveError = null;

        public void Save(string jsonPath)
        {
            // Settings are saved from several threads (UI, speech, summaries), so serialize the writes,
            // and never let a failed write break whatever changed the setting.
            lock (saveLock)
            {
                try
                {
                    using (FileStream fileStream = File.Open(jsonPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        JsonSerializer.Serialize(fileStream, this, JsonOptions);
                    }
                    lastSaveError = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (lastSaveError == ex.Message)
                        return;
                    lastSaveError = ex.Message;
                    UiThread.Post(() =>
                        SnackbarHost.Show("[ERROR] 设置没能保存。", ex.Message, SnackbarType.Error,
                            timeout: 5, closeButton: true));
                }
            }
        }

        public void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }
    }
}
