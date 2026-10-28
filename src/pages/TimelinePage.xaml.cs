using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class TimelinePage : Page
    {
        public const int MIN_HEIGHT = 360;

        private List<SectionEntry> sections = new();
        private bool selecting = false;

        public TimelinePage()
        {
            InitializeComponent();
            MainWindow.ApplySavedTheme();

            ScrollHelper.UseOwnScrollViewer(this, SectionScroll);

            Loaded += async (s, e) =>
            {
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(minHeight: MIN_HEIGHT);
                Summarizer.SectionSummarized += OnSectionSummarized;
                Summarizer.CurrentPageChanged += OnCurrentPageChanged;
                ClassSession.StateChanged += OnSessionChanged;
                ShowSlidesInfo();
                await SelectCurrent();
            };
            Unloaded += (s, e) =>
            {
                Summarizer.SectionSummarized -= OnSectionSummarized;
                Summarizer.CurrentPageChanged -= OnCurrentPageChanged;
                ClassSession.StateChanged -= OnSessionChanged;
            };
        }

        private LectureRecord? SelectedLecture => LectureBox.SelectedItem as LectureRecord;

        private void OnSectionSummarized(SectionEntry section)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (SelectedLecture is LectureRecord lecture && section.FirstHistoryId > lecture.FirstHistoryId &&
                    (lecture.LastHistoryId == null || section.FirstHistoryId <= lecture.LastHistoryId))
                    await LoadSections(scrollToEnd: true);
            });
        }

        // A class was started: follow it.
        private void OnSessionChanged()
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (ClassSession.IsRunning && SelectedLecture?.Id != ClassSession.CurrentLectureId)
                    await SelectCurrent();
            });
        }

        // Shows the running (or most recent) class of the current course.
        private async Task SelectCurrent()
        {
            List<CourseEntry> courses;
            LectureRecord? current = null;
            try
            {
                courses = await LectureStore.LoadCourses();
                if (ClassSession.CurrentLectureId >= 0)
                    current = await LectureStore.GetLecture(ClassSession.CurrentLectureId);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 读取课程失败。", ex.Message, SnackbarType.Error, timeout: 2, closeButton: true);
                return;
            }

            selecting = true;
            CourseBox.ItemsSource = courses;
            CourseBox.SelectedItem = courses.FirstOrDefault(c => c.Id == current?.CourseId)
                                     ?? courses.FirstOrDefault(c => c.Name == Translator.Setting.Lecture.CurrentCourse)
                                     ?? courses.FirstOrDefault();
            selecting = false;
            await LoadLectures(current?.Id);
        }

        private async Task LoadLectures(long? selectId = null)
        {
            List<LectureRecord> lectures = new();
            if (CourseBox.SelectedItem is CourseEntry course)
            {
                try
                {
                    lectures = await LectureStore.LoadLectures(course.Id);
                }
                catch (Exception)
                {
                    // Best effort: show an empty lecture list if loading fails.
                }
            }
            selecting = true;
            LectureBox.ItemsSource = lectures;
            LectureBox.SelectedItem = lectures.FirstOrDefault(l => l.Id == selectId) ?? lectures.FirstOrDefault();
            selecting = false;
            await LoadSections(scrollToEnd: true);
        }

        private async void CourseBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!selecting && IsLoaded)
                await LoadLectures();
        }

        private async void LectureBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!selecting && IsLoaded)
                await LoadSections();
        }

        private void OnCurrentPageChanged(int? page)
        {
            Dispatcher.InvokeAsync(ShowSlidesInfo);
        }

        private void ShowSlidesInfo()
        {
            if (!SlideDeck.IsLoaded)
            {
                SlidesInfo.Text = "未载入课件：按语义分节";
                ClearSlides.Visibility = Visibility.Collapsed;
                return;
            }
            string page = Summarizer.CurrentPage == null ? "识别中" : $"第 {Summarizer.CurrentPage} 页";
            SlidesInfo.Text = $"{SlideDeck.FileName}（共 {SlideDeck.Pages.Count} 页，当前{page}）";
            ClearSlides.Visibility = Visibility.Visible;
        }

        private async void LoadSlides_click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = SlideDeck.FileFilter,
                RestoreDirectory = true,
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                await SlideDeck.Load(dialog.FileName);
                SnackbarHost.Show("课件已载入。", $"共 {SlideDeck.Pages.Count} 页，之后按页码分节。",
                    SnackbarType.Success, timeout: 2);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 课件载入失败。", ex.Message, SnackbarType.Error,
                    timeout: 3, closeButton: true);
            }
            ShowSlidesInfo();
        }

        private void ClearSlides_click(object sender, RoutedEventArgs e)
        {
            SlideDeck.Clear();
            ShowSlidesInfo();
        }

        private async Task LoadSections(bool scrollToEnd = false)
        {
            try
            {
                if (SelectedLecture is LectureRecord lecture)
                {
                    long last = await LectureStore.EffectiveLastHistoryId(lecture);
                    sections = await SectionLogger.LoadSectionsInRange(lecture.FirstHistoryId, last);
                }
                else
                    sections = new List<SectionEntry>();
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 读取时间线失败。", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
                return;
            }
            SectionList.ItemsSource = sections;
            EmptyHint.Visibility = sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (scrollToEnd)
                SectionScroll.ScrollToEnd();
        }

        private void EndSection_click(object sender, RoutedEventArgs e)
        {
            MainWindow.EndSection();
        }

        private async void Refresh_click(object sender, RoutedEventArgs e)
        {
            long? selected = SelectedLecture?.Id;
            await LoadLectures(selected);
        }

        private async void Export_click(object sender, RoutedEventArgs e)
        {
            if (SelectedLecture is not LectureRecord selected)
            {
                SnackbarHost.Show("还没有可导出的课。", "", SnackbarType.Warning, timeout: 2);
                return;
            }

            try
            {
                var lecture = await LectureStore.GetLecture(selected.Id) ?? selected;
                string path = await LectureDocument.Save(lecture);
                SnackbarHost.Show("已导出。", path, SnackbarType.Success, timeout: 2);
                LectureDialogs.ShowInFolder(path);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 导出失败。", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }
    }
}
