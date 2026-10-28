using System.ComponentModel;
using System.Runtime.CompilerServices;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class MainWindowState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool topmost = true;
        private bool captionLogEnabled = false;
        private bool latencyShow = false;
        private int originalFontSize = 15;
        private int translatedFontSize = 18;
        private double sectionPanelWidth = 320;
        private string theme = "System";
        private double uiScale = 1.0;

        public string Theme
        {
            get => theme;
            set { theme = value is "Light" or "Dark" ? value : "System"; OnPropertyChanged("Theme"); }
        }
        public double UiScale
        {
            get => uiScale;
            set { uiScale = Math.Clamp(value, 0.8, 1.5); OnPropertyChanged("UiScale"); }
        }

        public bool Topmost
        {
            get => topmost;
            set
            {
                topmost = value;
                OnPropertyChanged("Topmost");
            }
        }
        // Width of the section summary column on the caption page.
        public double SectionPanelWidth
        {
            get => sectionPanelWidth;
            set
            {
                sectionPanelWidth = value;
                OnPropertyChanged("SectionPanelWidth");
            }
        }
        public bool CaptionLogEnabled
        {
            get => captionLogEnabled;
            set
            {
                captionLogEnabled = value;
                OnPropertyChanged("CaptionLogEnabled");
            }
        }
        public bool LatencyShow
        {
            get => latencyShow;
            set
            {
                latencyShow = value;
                OnPropertyChanged("LatencyShow");
            }
        }
        public int OriginalFontSize
        {
            get => originalFontSize;
            set
            {
                originalFontSize = value;
                OnPropertyChanged("OriginalFontSize");
            }
        }
        public int TranslatedFontSize
        {
            get => translatedFontSize;
            set
            {
                translatedFontSize = value;
                OnPropertyChanged("TranslatedFontSize");
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }
    }

    public class OverlayWindowState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private int fontSize = 15;
        private Color fontColor = Color.White;
        private FontBold fontBold = FontBold.None;
        private double fontStroke = 0.0;

        private Color backgroundColor = Color.Black;
        private int opacity = 150;

        public int FontSize
        {
            get => fontSize;
            set
            {
                fontSize = value;
                OnPropertyChanged("FontSize");
            }
        }
        public Color FontColor
        {
            get => fontColor;
            set
            {
                fontColor = value;
                OnPropertyChanged("FontColor");
            }
        }
        public FontBold FontBold
        {
            get => fontBold;
            set
            {
                fontBold = value;
                OnPropertyChanged("FontBold");
            }
        }
        public double FontStroke
        {
            get => fontStroke;
            set
            {
                fontStroke = value;
                OnPropertyChanged("FontStroke");
            }
        }
        public Color BackgroundColor
        {
            get => backgroundColor;
            set
            {
                backgroundColor = value;
                OnPropertyChanged("BackgroundColor");
            }
        }
        public int Opacity
        {
            get => opacity;
            set
            {
                opacity = value;
                OnPropertyChanged("Opacity");
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }
    }

    // Look of the bilingual floating window. FontSize is the size of the translation; the original is smaller.
    public class BilingualWindowState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private int fontSize = 18;
        private int opacity = 153;
        private bool topmost = true;
        private int sentences = 3;

        public int FontSize
        {
            get => fontSize;
            set
            {
                fontSize = value;
                OnPropertyChanged("FontSize");
            }
        }
        // Alpha of the black panel, 0-255.
        public int Opacity
        {
            get => opacity;
            set
            {
                opacity = value;
                OnPropertyChanged("Opacity");
            }
        }
        public bool Topmost
        {
            get => topmost;
            set
            {
                topmost = value;
                OnPropertyChanged("Topmost");
            }
        }
        // How many finished sentence pairs are shown above the one in progress.
        public int Sentences
        {
            get => sentences;
            set
            {
                sentences = value;
                OnPropertyChanged("Sentences");
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }
    }
}
