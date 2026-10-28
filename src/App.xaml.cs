using System.IO;
using System.Linq;
using System.Windows;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        App()
        {
            // Settings and history are stored next to the program, so do not depend on how it was started.
            // If that folder is read-only (e.g. installed under Program Files without write access),
            // fall back to a per-user folder.
            Directory.SetCurrentDirectory(AppPaths.DataDirectory);
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

// Self-test mode: exercise the real translation pipeline and print the result,
// then exit. Used to verify a configured endpoint end to end.
            if (Environment.GetCommandLineArgs().Any(a => a == "--selftest"))
            {
                RunSelfTest();
                Shutdown();
                return;
            }

            Translator.Setting?.Save();

            Task.Run(() => Translator.SyncLoop());
            Task.Run(() => Translator.TranslateLoop());
            Task.Run(() => Translator.DisplayLoop());
            Task.Run(() => Summarizer.SummaryLoop());
            Task.Run(() => Refiner.RefineLoop());
            Speaker.Start();
            UpdateChecker.StartAutoCheck();
        }

        private static void RunSelfTest()
        {
            var log = new System.Text.StringBuilder();
            System.Action<string> Say = delegate(string line) { log.AppendLine(line); };

            var setting = Translator.Setting;
            string outPath = Path.Combine(AppPaths.DataDirectory, "selftest.log");

            Say($"ENGINE    = {setting.ApiName}");
            Say($"TARGET    = {setting.TargetLanguage}");
            if (setting.ApiName == "OpenAI" && setting["OpenAI"] is models.OpenAIConfig oc)
            {
                Say($"SLOT      = {oc.ConfigName}");
                Say($"URL       = {oc.ApiUrl}");
                Say($"MODEL     = {oc.ModelName}");
                Say($"STYLE     = {oc.ApiStyle}");
                Say($"SRC LANG  = {oc.SourceLanguage}");
            }

            // Verify the configuration slots and the wire format that the settings UI drives.
            try
            {
                var configs = Translator.Setting.Configs["OpenAI"];
                Say($"SLOT_COUNT     = {configs.Count}");
                for (int i = 0; i < configs.Count; i++)
                {
                    var c = configs[i] as models.OpenAIConfig;
                    string name = string.IsNullOrWhiteSpace(c.ConfigName) ? "(未命名)" : c.ConfigName;
                    Say($"SLOT_{i}         = {name} | style={c.ApiStyle} | model={c.ModelName} | url={c.ApiUrl}");
                }
                Say($"SLOT_INDEX     = {Translator.Setting.ConfigIndices["OpenAI"]}");
            }
            catch (Exception ex)
            {
                Say($"SLOT_CHECK_EXCEPTION: {ex.Message}");
            }
            File.WriteAllText(outPath, log.ToString());

            // Load the settings window and confirm the new controls exist and are wired up.
            try
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
                var win = new SettingWindow();
                win.Show();
                PumpMessages(win);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                PumpMessages(win);

                var combo = win.FindName("OpenAIConfigComboBox") as System.Windows.Controls.ComboBox;
                Say($"UI_PICKER      = {(combo != null ? "FOUND" : "MISSING")}");
                if (combo != null)
                {
                    Say($"UI_PICKER_ITEMS = {combo.Items.Count}");
                    foreach (var it in combo.Items)
                        Say($"UI_PICKER_ITEM  = {it}");
                    Say($"UI_PICKER_SEL   = {(combo.SelectedItem as SettingWindow.ConfigSlotItem)?.Label}");
                }

                var styleCombo = win.FindName("OpenAIApiStyleComboBox") as System.Windows.Controls.ComboBox;
                Say($"UI_STYLE       = {(styleCombo != null ? "FOUND" : "MISSING")}");
                if (styleCombo != null)
                    Say($"UI_STYLE_SEL   = {(styleCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content}");

                var index = win.FindName("OpenAIIndex") as System.Windows.Controls.TextBlock;
                Say($"UI_INDEX_LABEL = {index?.Text}");
                win.Close();
            }
            catch (Exception ex)
            {
                Say($"UI_CHECK_EXCEPTION: {ex.Message}");
            }
            File.WriteAllText(outPath, log.ToString());

            string[] samples =
            {
                "Today I will talk about the basic concepts of machine learning.",
                "Let us look at the results on the next slide.",
                "The gradient descent algorithm iteratively updates the model parameters."
            };

            // The summary slot can use a conversational model independently from translation.
            try
            {
                var summaryConfig = Summarizer.Config;
                Say($"SUMMARY_SLOT  = {summaryConfig?.ConfigName ?? "(跟随翻译档位)"}");
                Say($"SUMMARY_MODEL = {summaryConfig?.ModelName}");
                Say($"SUMMARY_STYLE = {summaryConfig?.ApiStyle}");
                string probe = Summarizer.TestConnection().GetAwaiter().GetResult();
                Say($"SUMMARY_TEST  = {probe.Replace("\n", " | ")}");
            }
            catch (Exception ex)
            {
                Say($"SUMMARY_TEST_EXCEPTION: {ex.Message}");
            }
            Say("");
            File.WriteAllText(outPath, log.ToString());

            foreach (string sample in samples)
            {
                string result;
                try
                {
                    result = TranslateAPI.OpenAI(sample).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    result = $"EXCEPTION: {ex.Message}";
                }
                Say($"EN: {sample}");
                Say($"ZH: {result}");
                Say("");
                File.WriteAllText(outPath, log.ToString());
            }

            File.WriteAllText(outPath, log.ToString());
            Console.WriteLine(log.ToString());
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            if (Translator.Window != null)
            {
                LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                LiveCaptionsHandler.KillLiveCaptions(Translator.Window);
            }
        }

        /// <summary>
        /// Lets the WPF dispatcher process pending work, so a freshly shown window can lay
        /// out before its visual tree is inspected.
        /// </summary>
        private static void PumpMessages(Window window, int rounds = 12)
        {
            for (int i = 0; i < rounds; i++)
            {
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                System.Windows.Threading.DispatcherFrame frame = new();
                window.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
        }

    }
}
