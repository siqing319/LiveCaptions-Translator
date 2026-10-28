using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveCaptionsTranslator.models
{
    public enum SpeakMode
    {
        Off = 0,
        SummaryOnly = 1,
        SummaryAndTranslation = 2,
        // Simultaneous interpretation: every translated sentence is read at once, summaries are not.
        Interpretation = 3,
    }

    public enum SegmentMode
    {
        // Slides (if loaded) or topic changes decide the sections; time is only the fallback.
        Auto = 0,
        // A new section every `SummaryIntervalMinutes`.
        FixedTime = 1,
    }

    public enum InputSource
    {
        // Sound played by the computer (online class, video).
        ComputerAudio = 0,
        // Sound in the classroom, through a microphone.
        Microphone = 1,
    }

    public enum RecognitionEngine
    {
        // Windows 11 Live Captions: free, runs locally.
        LiveCaptions = 0,
        // Alibaba Cloud Model Studio (DashScope) real-time speech recognition.
        Aliyun = 1,
    }

    public class LectureState : INotifyPropertyChanged
    {
        public const string DEFAULT_ASR_ENDPOINT = "wss://dashscope.aliyuncs.com/api-ws/v1/inference";
        public const string DEFAULT_ASR_MODEL = "paraformer-realtime-v2";

        public event PropertyChangedEventHandler? PropertyChanged;

        public const string DEFAULT_SUMMARY_PROMPT =
            "You are a teaching assistant helping a student follow a lecture given in a foreign language. " +
            "The user message is the speech-recognition transcript of the latest section of the lecture " +
            "(it may contain recognition errors), one line per sentence with its time. " +
            "Summarize this section in {0} for the student. " +
            "If the slide page of this section is given, use it to correct recognition errors and terminology. " +
            "Line 1: a short title of the section (no more than 15 words). " +
            "Then 2 to 4 lines, each starting with \"- \", covering the key ideas, definitions, formulas or examples. " +
            "When a technical term appears, keep the original term in parentheses after the translation. " +
            "Output plain text only, no Markdown headings, no preamble. " +
            "If the section has no substantive content (small talk, silence, noise), output only the title line.";

        public const string NATURAL_NOTES_HINT =
            " Keep the required headings, order, lists and facts. Write clear, natural Chinese when the output is Chinese. " +
            "Avoid canned reversals such as 'not A but B', empty lead-ins, repeated sentence patterns and decorative dashes. " +
            "Use concrete numbers and examples only when they appear in the source; never invent or omit facts, qualifiers or terminology. " +
            "Do not force a casual tone onto technical material.";

        // Added to the translation and summary prompts: the subject of the class and the glossary, if given.
        public static string SubjectHint()
        {
            string subject = Translator.Setting?.Lecture.Subject?.Trim() ?? string.Empty;
            string hint = subject.Length == 0
                ? string.Empty
                : $" This is a university lecture on {subject}; use the standard terminology of that field, " +
                  "and prefer its technical reading of a word over the everyday one.";
            return hint + LiveCaptionsTranslator.utils.Glossary.PromptHint();
        }

        private bool summaryEnabled = true;
        private int summaryIntervalMinutes = 2;
        // Name of the OpenAI config slot used for section summaries. Empty = the currently selected slot.
        // Kept separate so a translation-only configuration can still be used for captions.
        private string summaryConfigName = string.Empty;
        private SegmentMode segmentMode = SegmentMode.Auto;
        private int maxSectionMinutes = 6;
        private string summaryPrompt = DEFAULT_SUMMARY_PROMPT;
        private SpeakMode speakMode = SpeakMode.SummaryOnly;
        private int speechRate = 2;
        private int speechVolume = 100;
        private string voiceName = string.Empty;
        private string speechDeviceId = string.Empty;
        private InputSource inputSource = InputSource.ComputerAudio;
        private string ocrLanguage = string.Empty;
        private bool forceOcr = false;
        private bool speechMuted = false;
        private bool showOriginal = true;
        private bool captionsOnly = false;
        private bool englishOnly = false;
        private RecognitionEngine engine = RecognitionEngine.LiveCaptions;
        private string asrApiKey = "";
        private string asrModel = DEFAULT_ASR_MODEL;
        private List<string> asrModels = new() { DEFAULT_ASR_MODEL, "fun-asr-realtime" };
        private string asrLanguage = "en";
        private string asrEndpoint = DEFAULT_ASR_ENDPOINT;
        private bool refineParagraphs = true;
        private string subject = "";
        private string glossary = "";
        private string asrVocabularyId = "";
        private string asrVocabularyHash = "";
        private string asrVocabularyModel = "";
        private string saveFolder = "";
        private string currentCourse = "";
        private bool notesDraft = true;
        private bool autoCheckUpdate = true;
        private DateTime lastUpdateCheck = DateTime.MinValue;
        private int lastNotifiedUpdateBuild = 0;
        private DateTime lastUpdateNotice = DateTime.MinValue;

        public bool SummaryEnabled
        {
            get => summaryEnabled;
            set
            {
                summaryEnabled = value;
                OnPropertyChanged("SummaryEnabled");
            }
        }
        public int SummaryIntervalMinutes
        {
            get => summaryIntervalMinutes;
            set
            {
                summaryIntervalMinutes = Math.Clamp(value, 1, 60);
                OnPropertyChanged("SummaryIntervalMinutes");
            }
        }
        public string SummaryConfigName
        {
            get => summaryConfigName ?? string.Empty;
            set
            {
                summaryConfigName = value ?? string.Empty;
                OnPropertyChanged("SummaryConfigName");
            }
        }
        public SegmentMode SegmentMode
        {
            get => segmentMode;
            set
            {
                segmentMode = value;
                OnPropertyChanged("SegmentMode");
                OnPropertyChanged("SegmentModeIndex");
            }
        }
        [System.Text.Json.Serialization.JsonIgnore]
        public int SegmentModeIndex
        {
            get => (int)segmentMode;
            set => SegmentMode = (SegmentMode)Math.Clamp(value, 0, 1);
        }
        // In Auto mode, a section is closed after this long even if the topic has not changed.
        public int MaxSectionMinutes
        {
            get => maxSectionMinutes;
            set
            {
                maxSectionMinutes = Math.Clamp(value, 2, 30);
                OnPropertyChanged("MaxSectionMinutes");
            }
        }
        public string SummaryPrompt
        {
            get => summaryPrompt;
            set
            {
                summaryPrompt = value;
                OnPropertyChanged("SummaryPrompt");
            }
        }
        public SpeakMode SpeakMode
        {
            get => speakMode;
            set
            {
                speakMode = value;
                OnPropertyChanged("SpeakMode");
                OnPropertyChanged("SpeakModeIndex");
            }
        }
        // For binding with ComboBox.SelectedIndex
        [System.Text.Json.Serialization.JsonIgnore]
        public int SpeakModeIndex
        {
            get => (int)speakMode;
            set => SpeakMode = (SpeakMode)Math.Clamp(value, 0, 3);
        }
        public int SpeechRate
        {
            get => speechRate;
            set
            {
                speechRate = Math.Clamp(value, -10, 10);
                OnPropertyChanged("SpeechRate");
            }
        }
        public int SpeechVolume
        {
            get => speechVolume;
            set
            {
                speechVolume = Math.Clamp(value, 0, 100);
                OnPropertyChanged("SpeechVolume");
            }
        }
        public string VoiceName
        {
            get => voiceName;
            set
            {
                voiceName = value ?? string.Empty;
                OnPropertyChanged("VoiceName");
            }
        }

        // Empty: the Windows default output device.
        public string SpeechDeviceId
        {
            get => speechDeviceId;
            set
            {
                speechDeviceId = value ?? string.Empty;
                OnPropertyChanged("SpeechDeviceId");
            }
        }
        public InputSource InputSource
        {
            get => inputSource;
            set
            {
                inputSource = value;
                OnPropertyChanged("InputSource");
            }
        }
        // Language tag of the Windows OCR engine for scanned slides; empty: chosen automatically.
        public string OcrLanguage
        {
            get => ocrLanguage;
            set
            {
                ocrLanguage = value ?? string.Empty;
                OnPropertyChanged("OcrLanguage");
            }
        }
        public bool ForceOcr
        {
            get => forceOcr;
            set
            {
                forceOcr = value;
                OnPropertyChanged("ForceOcr");
            }
        }

        // The earphone speech switch; kept across restarts.
        public bool SpeechMuted
        {
            get => speechMuted;
            set
            {
                speechMuted = value;
                OnPropertyChanged("SpeechMuted");
            }
        }

        // Every few sentences, let the LLM fix recognition errors and translate them as a paragraph.
        public bool RefineParagraphs
        {
            get => refineParagraphs;
            set
            {
                refineParagraphs = value;
                OnPropertyChanged("RefineParagraphs");
            }
        }

        public RecognitionEngine Engine
        {
            get => engine;
            set
            {
                engine = value;
                OnPropertyChanged("Engine");
            }
        }

        public string AsrApiKey
        {
            get => asrApiKey;
            set
            {
                asrApiKey = value ?? string.Empty;
                OnPropertyChanged("AsrApiKey");
            }
        }

        public string AsrModel
        {
            get => asrModel;
            set
            {
                asrModel = string.IsNullOrWhiteSpace(value) ? DEFAULT_ASR_MODEL : value.Trim();
                OnPropertyChanged("AsrModel");
            }
        }

        public List<string> AsrModels
        {
            get => asrModels;
            set
            {
                asrModels = value ?? new List<string>();
                OnPropertyChanged("AsrModels");
            }
        }

        // Language of the lecture as a hint for recognition ("en", "zh", ...); empty = detect.
        public string AsrLanguage
        {
            get => asrLanguage;
            set
            {
                asrLanguage = value ?? string.Empty;
                OnPropertyChanged("AsrLanguage");
            }
        }

        public string AsrEndpoint
        {
            get => asrEndpoint;
            set
            {
                asrEndpoint = string.IsNullOrWhiteSpace(value) ? DEFAULT_ASR_ENDPOINT : value.Trim();
                OnPropertyChanged("AsrEndpoint");
            }
        }

        // Only transcribe, don't translate.
        public bool CaptionsOnly
        {
            get => captionsOnly;
            set
            {
                captionsOnly = value;
                OnPropertyChanged("CaptionsOnly");
                Translator.Caption?.OnPropertyChanged("OriginalVisibility");
                Translator.Caption?.OnPropertyChanged("IsOriginalVisible");
            }
        }

        // 纯英文模式: nothing is translated; summaries, refined paragraphs and notes are written in English.
        public bool EnglishOnly
        {
            get => englishOnly;
            set
            {
                englishOnly = value;
                OnPropertyChanged("EnglishOnly");
                Translator.Caption?.OnPropertyChanged("OriginalVisibility");
                Translator.Caption?.OnPropertyChanged("IsOriginalVisible");
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool SkipTranslation => captionsOnly || englishOnly;

        // The language summaries, refined paragraphs and notes are written in (a name the LLM understands).
        public static string OutputLanguage()
        {
            var setting = Translator.Setting;
            if (setting == null || setting.Lecture.EnglishOnly)
                return "English";
            return OpenAIConfig.SupportedLanguages.TryGetValue(setting.TargetLanguage, out var name)
                ? name : setting.TargetLanguage;
        }

        // Added to the summary prompt in 纯英文模式, whose default asks for terms "after the translation".
        public static string EnglishOnlyHint() => Translator.Setting?.Lecture.EnglishOnly == true
            ? " The student follows the lecture in English: write in English, do not translate anything, and do " +
              "not add terms in parentheses."
            : string.Empty;

        // The subject of the class, e.g. "基因治疗 / 分子生物学". Used so the model keeps the field's terms.
        // Where each class's transcript (.md) is saved; empty = Documents\课堂同传助手.
        public string SaveFolder
        {
            get => saveFolder;
            set
            {
                saveFolder = value ?? string.Empty;
                OnPropertyChanged("SaveFolder");
            }
        }

        // After each class, write a draft of study notes (from the summaries, the transcript and the slides).
        public bool NotesDraft
        {
            get => notesDraft;
            set
            {
                notesDraft = value;
                OnPropertyChanged("NotesDraft");
            }
        }

        // Look for a new release a little after startup (at most once a day).
        public bool AutoCheckUpdate
        {
            get => autoCheckUpdate;
            set
            {
                autoCheckUpdate = value;
                OnPropertyChanged("AutoCheckUpdate");
            }
        }

        public DateTime LastUpdateCheck
        {
            get => lastUpdateCheck;
            set
            {
                lastUpdateCheck = value;
                OnPropertyChanged("LastUpdateCheck");
            }
        }

        // The newest build the user was already told about, so it is not announced at every start.
        public int LastNotifiedUpdateBuild
        {
            get => lastNotifiedUpdateBuild;
            set
            {
                lastNotifiedUpdateBuild = value;
                OnPropertyChanged("LastNotifiedUpdateBuild");
            }
        }

        // When (UTC) that build was announced; after a few days it is announced once more.
        public DateTime LastUpdateNotice
        {
            get => lastUpdateNotice;
            set
            {
                lastUpdateNotice = value;
                OnPropertyChanged("LastUpdateNotice");
            }
        }

        // The course the next recording goes into (the last one chosen when saving).
        public string CurrentCourse
        {
            get => currentCourse;
            set
            {
                currentCourse = value ?? string.Empty;
                OnPropertyChanged("CurrentCourse");
            }
        }

        // Terms of the current course, one per line, optionally "term = translation" (see Glossary).
        public string Glossary
        {
            get => glossary;
            set
            {
                glossary = value ?? string.Empty;
                OnPropertyChanged("Glossary");
            }
        }

        // The hot word list on Alibaba Cloud made from the glossary, and what it was made from.
        public string AsrVocabularyId
        {
            get => asrVocabularyId;
            set
            {
                asrVocabularyId = value ?? string.Empty;
                OnPropertyChanged("AsrVocabularyId");
            }
        }

        // The model the hot word list was made for.
        public string AsrVocabularyModel
        {
            get => asrVocabularyModel;
            set
            {
                asrVocabularyModel = value ?? string.Empty;
                OnPropertyChanged("AsrVocabularyModel");
            }
        }

        public string AsrVocabularyHash
        {
            get => asrVocabularyHash;
            set
            {
                asrVocabularyHash = value ?? string.Empty;
                OnPropertyChanged("AsrVocabularyHash");
            }
        }

        public string Subject
        {
            get => subject;
            set
            {
                subject = value ?? string.Empty;
                OnPropertyChanged("Subject");
            }
        }

        // 中外对照: keep the original sentence above its translation on the caption page.
        public bool ShowOriginal
        {
            get => showOriginal;
            set
            {
                showOriginal = value;
                OnPropertyChanged("ShowOriginal");
                Translator.Caption?.OnPropertyChanged("OriginalVisibility");
                Translator.Caption?.OnPropertyChanged("IsOriginalVisible");
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }
    }
}
