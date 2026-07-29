#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ZombieBar.Utilities;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ZombieBar
{
    /// <summary>
    /// Feedback form opened from the tray menu ("Report a problem or suggestion"). The user types a
    /// description and may attach screenshots (existing image files or a fresh screen capture). On
    /// send it opens the project's GitHub "new issue" page pre-filled with the text and environment
    /// info, and - because GitHub can't pre-attach images via a URL - opens a folder with the
    /// screenshots so the user can drag them into the issue editor before submitting.
    /// </summary>
    public partial class FeedbackWindow : Window
    {
        /// <summary>An attached image: its source path and a small thumbnail for the list.</summary>
        public sealed class Attachment
        {
            public string Path { get; init; } = "";
            public BitmapImage? Thumbnail { get; init; }
        }

        private readonly ObservableCollection<Attachment> _attachments = new();
        private int _captureCount;

        // Working folder for this session's screenshots (captures + copies handed to the user).
        private static readonly string FeedbackTempDir =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Ssz.TaskBar.feedback");

        public FeedbackWindow()
        {
            InitializeComponent();
            AppUi.ApplyDialogTheme(this);
            AppIcon.Source = AppUi.LoadAppIcon();

            AttachmentsList.ItemsSource = _attachments;
            _attachments.CollectionChanged += (_, _) => UpdateHint();
            UpdateHint();
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void UpdateHint()
        {
            NoShotsHint.Visibility = _attachments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string Loc(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;

        private void AddFile_OnClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = Loc("feedback_image_filter", "Images") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp|" +
                         Loc("feedback_all_files", "All files") + "|*.*"
            };
            if (dlg.ShowDialog(this) == true)
            {
                foreach (string path in dlg.FileNames)
                {
                    AddAttachment(path);
                }
            }
        }

        private async void Capture_OnClick(object sender, RoutedEventArgs e)
        {
            // Hide the form so it isn't in the shot, then capture the whole (multi-monitor) screen.
            Hide();
            try
            {
                await Task.Delay(250);

                System.Drawing.Rectangle bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
                using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                }

                Directory.CreateDirectory(FeedbackTempDir);
                string path = System.IO.Path.Combine(FeedbackTempDir, $"capture-{++_captureCount}.png");
                bmp.Save(path, ImageFormat.Png);
                AddAttachment(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Loc("feedback_title", "Feedback"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Show();
                Activate();
            }
        }

        private void AddAttachment(string path)
        {
            BitmapImage? thumb = null;
            try
            {
                thumb = new BitmapImage();
                thumb.BeginInit();
                thumb.CacheOption = BitmapCacheOption.OnLoad;            // load now so the file isn't locked
                thumb.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                thumb.DecodePixelWidth = 208;                           // small thumbnail, low memory
                thumb.UriSource = new Uri(path);
                thumb.EndInit();
                thumb.Freeze();
            }
            catch
            {
                thumb = null; // not a readable image - still attach the file, just without a preview
            }

            _attachments.Add(new Attachment { Path = path, Thumbnail = thumb });
        }

        private void RemoveAttachment_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { Tag: Attachment a })
            {
                _attachments.Remove(a);
            }
        }

        private void Cancel_OnClick(object sender, RoutedEventArgs e) => Close();

        private void Send_OnClick(object sender, RoutedEventArgs e)
        {
            string text = DescriptionBox.Text.Trim();
            if (text.Length == 0 && _attachments.Count == 0)
            {
                MessageBox.Show(this, Loc("feedback_empty", "Please describe the problem or suggestion."),
                    Loc("feedback_title", "Feedback"), MessageBoxButton.OK, MessageBoxImage.Information);
                DescriptionBox.Focus();
                return;
            }

            try
            {
                string? shotsDir = PrepareScreenshots();
                OpenIssue(text, hasScreenshots: shotsDir != null);

                if (shotsDir != null)
                {
                    // GitHub can't pre-attach images via the URL, so open the folder and let the
                    // user drag the screenshots into the issue editor.
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{shotsDir}\"") { UseShellExecute = true });
                    MessageBox.Show(this,
                        Loc("feedback_screenshot_hint",
                            "Drag the screenshots from the opened folder into the GitHub issue, then submit it."),
                        Loc("feedback_title", "Feedback"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Loc("feedback_title", "Feedback"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Close();
        }

        // Copies the attached screenshots into a clean folder for the user to drag from. Returns its
        // path, or null when nothing is attached.
        private string? PrepareScreenshots()
        {
            if (_attachments.Count == 0)
            {
                return null;
            }

            string dir = System.IO.Path.Combine(FeedbackTempDir, "attached");
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
            Directory.CreateDirectory(dir);

            int i = 0;
            foreach (Attachment a in _attachments)
            {
                try
                {
                    string dest = System.IO.Path.Combine(dir, $"{++i:00}-{System.IO.Path.GetFileName(a.Path)}");
                    File.Copy(a.Path, dest, true);
                }
                catch { /* skip a file we can't copy */ }
            }
            return dir;
        }

        // Opens the GitHub "new issue" page pre-filled with the title and body. If the resulting URL
        // would be too long, copies the body to the clipboard and opens a title-only issue instead.
        private void OpenIssue(string text, bool hasScreenshots)
        {
            string title = BuildTitle(text);
            string body = BuildBody(text, hasScreenshots);

            string url = AppLinks.NewIssueUrl +
                         "?title=" + Uri.EscapeDataString(title) +
                         "&body=" + Uri.EscapeDataString(body);

            if (url.Length <= 7500)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }

            try { System.Windows.Clipboard.SetText(body); } catch { /* clipboard may be busy */ }
            string shortUrl = AppLinks.NewIssueUrl + "?title=" + Uri.EscapeDataString(title);
            Process.Start(new ProcessStartInfo(shortUrl) { UseShellExecute = true });
            MessageBox.Show(this,
                Loc("feedback_clipboard_hint", "The text was copied to the clipboard - paste it (Ctrl+V) into the issue."),
                Loc("feedback_title", "Feedback"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string BuildTitle(string text)
        {
            string firstLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (firstLine.Length == 0)
            {
                return "[Feedback]";
            }
            if (firstLine.Length > 80)
            {
                firstLine = firstLine.Substring(0, 80).TrimEnd() + "…";
            }
            return firstLine;
        }

        private static string BuildBody(string text, bool hasScreenshots)
        {
            var sb = new StringBuilder();
            sb.AppendLine(text);
            sb.AppendLine();
            if (hasScreenshots)
            {
                sb.AppendLine(Loc("feedback_body_screenshots",
                    "_Attach the screenshots here (drag them from the opened folder)._"));
                sb.AppendLine();
            }
            sb.AppendLine("---");
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            sb.AppendLine($"- ZombieBar: {version}");
            sb.AppendLine($"- OS: {RuntimeInformation.OSDescription}");
            sb.AppendLine($"- .NET: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"- Culture: {CultureInfo.CurrentUICulture.Name}");
            return sb.ToString();
        }
    }
}
