using System.IO;
using System.Net.Http;
using System.Text;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    // A first draft of study notes for a whole class, written by the LLM from the section summaries, the transcript
    // and the slides (if loaded), and saved next to the transcript as "<name> 笔记初稿.md". With translation on, the
    // notes are bilingual: each line in the lecture's language with its translation right after it.
    public static class LectureNotes
    {
        private const int MAX_TRANSCRIPT_CHARS = 24000;
        private const int MAX_SLIDE_CHARS = 16000;

        public static string DraftPath(LectureRecord lecture)
        {
            string transcript = string.IsNullOrEmpty(lecture.FilePath)
                ? Path.Combine(LectureDocument.SaveFolder, "未命名.md")
                : lecture.FilePath;
            string folder = Path.GetDirectoryName(transcript) ?? LectureDocument.SaveFolder;
            return Path.Combine(folder, Path.GetFileNameWithoutExtension(transcript) + " 笔记初稿.md");
        }

        public static async Task<string> Generate(LectureRecord lecture)
        {
            var sections = await LectureDocument.Build(lecture);
            if (sections.Count == 0)
                throw new InvalidOperationException("这节课没有识别到内容。");

            string language = LectureState.OutputLanguage();
            bool english = Translator.Setting.Lecture.EnglishOnly;
            bool bilingual = !Translator.Setting.Lecture.SkipTranslation;
            string system = bilingual ? BilingualPrompt(language) :
                $"You are a diligent university student writing study notes in {language} for one lecture. " +
                "You get the section summaries, the (speech-recognized, partly erroneous) transcript and, if available, " +
                "the text of the lecture slides. Write well-organized notes in Markdown: " +
                "follow the order of the lecture; use the slides for structure, headings and exact terminology, and " +
                "the transcript for what the lecturer actually explained. For every topic give the key points, " +
                "definitions, formulas, numbers and the examples the lecturer used. Mark points that were said in class " +
                "but are not on the slides with " + (english ? "\"[Added in class]\". " : "\"【课上补充】\". ") +
                (english ? string.Empty
                    : "Keep important technical terms in the original language in parentheses the first time. ") +
                "Never invent content; where the transcript is unclear, leave it out. " +
                "Finish with a section of 3–5 review questions. Answer with the notes only." +
                LectureState.SubjectHint();
            system += LectureState.NATURAL_NOTES_HINT;

            var user = new StringBuilder();
            user.AppendLine($"Course: {lecture.CourseName}");
            user.AppendLine($"Lecture: {lecture.Name} ({lecture.StartTime:yyyy-MM-dd})");
            user.AppendLine();

            user.AppendLine("## Section summaries");
            foreach (var section in sections)
            {
                user.AppendLine($"### {section.Header} {section.Title}");
                if (!string.IsNullOrWhiteSpace(section.Summary))
                    user.AppendLine(section.Summary);
            }
            user.AppendLine();

            string slides = SlidesText(lecture);
            if (slides.Length > 0)
            {
                user.AppendLine($"## Slides ({SlideDeck.FileName})");
                user.AppendLine(slides);
                user.AppendLine();
            }

            user.AppendLine("## Transcript");
            var transcript = new StringBuilder();
            foreach (var line in sections.SelectMany(section => section.Lines))
                transcript.AppendLine(line.SourceText);
            string text = transcript.ToString();
            if (text.Length > MAX_TRANSCRIPT_CHARS)
                text = Shorten(text, MAX_TRANSCRIPT_CHARS);
            user.AppendLine(text);

            string notes;
            try
            {
                notes = await Summarizer.Chat(system, user.ToString(), maxTokens: bilingual ? 8000 : 4000,
                    temperature: 0.3, longRequest: true);
            }
            catch (HttpRequestException ex) when (bilingual && (int?)ex.StatusCode is 400 or 422)
            {
                // Some models accept fewer output tokens than the bilingual notes ask for.
                notes = await Summarizer.Chat(system, user.ToString(), maxTokens: 4000, temperature: 0.3,
                    longRequest: true);
            }

            string path = DraftPath(lecture);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new StringBuilder();
            file.AppendLine($"<!-- 课堂同传助手自动生成的笔记初稿：{lecture.CourseName} / {lecture.Name}，请自行核对。 -->");
            file.AppendLine();
            file.AppendLine(notes.Trim());
            await File.WriteAllTextAsync(path, file.ToString(), new UTF8Encoding(false));
            return path;
        }

        // Every heading, bullet, definition and question in the original language, with its translation right after.
        private static string BilingualPrompt(string language) =>
            "You are a diligent university student writing bilingual study notes for one lecture. " +
            "You get the section summaries, the (speech-recognized, partly erroneous) transcript and, if available, " +
            "the text of the lecture slides. Write well-organized notes in Markdown: " +
            "follow the order of the lecture; use the slides for structure, headings and exact terminology, and " +
            "the transcript for what the lecturer actually explained. For every topic give the key points, " +
            "definitions, formulas, numbers and the examples the lecturer used. " +
            $"The notes are bilingual: write every heading, bullet, definition and review question first in the " +
            $"original language of the lecture (the language of the transcript and slides), and put its translation " +
            $"into {language} right after it, never the other way round and never in a separate block. Layout: " +
            "a heading is \"## Original heading / translated heading\" on one line; a bullet is \"- original text\" " +
            "followed by the translation as a nested bullet on the next line, indented by two spaces " +
            "(\"  - translated text\"); a paragraph is followed by its translation as a separate paragraph. " +
            "Do not translate formulas, numbers, code and proper names a second time, and do not repeat a line that " +
            "is the same in both languages. Example:\n" +
            "## Gradient descent / 梯度下降\n" +
            "- Update the weights against the gradient.\n" +
            "  - 沿梯度的反方向更新权重。\n" +
            "- Step size: w = w - lr * g\n" +
            "  - 步长：w = w - lr * g\n" +
            "(The example uses Chinese as the translation language.) " +
            "Mark points that were said in class but are not on the slides with \"【课上补充】\" " +
            "at the start of the original line. " +
            "Never invent content; where the transcript is unclear, leave it out. " +
            "Finish with a section of 3–5 review questions, each as a bullet with its translation nested below. " +
            "Answer with the notes only." +
            LectureState.SubjectHint();

        // The slides of the pages the class went through, or the whole deck when no page was recognized.
        private static string SlidesText(LectureRecord lecture)
        {
            if (!SlideDeck.IsLoaded)
                return string.Empty;
            var sb = new StringBuilder();
            foreach (var page in SlideDeck.Pages)
            {
                if (string.IsNullOrWhiteSpace(page.Text))
                    continue;
                sb.AppendLine($"[Page {page.Number}] {page.Text.Trim()}");
                if (sb.Length > MAX_SLIDE_CHARS)
                    break;
            }
            string text = sb.ToString();
            return text.Length > MAX_SLIDE_CHARS ? text[..MAX_SLIDE_CHARS] : text;
        }

        // Keeps the beginning and the end, and evenly spaced parts of the middle.
        private static string Shorten(string text, int maxChars)
        {
            var lines = text.Split('\n');
            int keepEvery = (int)Math.Ceiling((double)text.Length / maxChars);
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length && sb.Length < maxChars; i++)
            {
                if (i % keepEvery == 0)
                    sb.AppendLine(lines[i].TrimEnd());
            }
            return sb.ToString();
        }
    }
}
