using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Win10Audio
{
    public static class Program
    {
        // Keep the original names so an upgrade can stop the previous local build.
        private const string MutexName = "Local\\Win10AudioPanel.Instance";
        private const string ToggleName = "Local\\Win10AudioPanel.Toggle";
        private const string QuitName = "Local\\Win10AudioPanel.Quit";
        public static bool IsExiting { get; private set; }
        public static string DataDirectory
        {
            get
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VolumeDock");
                Directory.CreateDirectory(path);
                return path;
            }
        }
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                Localization.Load();
                string languageArgument = args.FirstOrDefault(delegate(string value) { return value.StartsWith("--language=", StringComparison.Ordinal); });
                if (languageArgument != null)
                {
                    string preference = languageArgument.Substring("--language=".Length);
                    if (preference != "system" && preference != "en" && preference != "pt-BR") throw new ArgumentException("Supported languages: system, en, pt-BR.");
                    if (!Localization.SetLanguage(preference)) throw new IOException(Localization.Text("LanguageSaveError"));
                }
                string previewLanguage = args.FirstOrDefault(delegate(string value) { return value.StartsWith("--preview-language=", StringComparison.Ordinal); });
                if (args.Contains("--render-demo") && previewLanguage != null) Localization.SetPreviewLanguage(previewLanguage.Substring("--preview-language=".Length));
                if (args.Contains("--disable")) { DisablePanel(); return 0; }
                if (args.Length == 1 && (args[0] == "--diagnostics" || args[0] == "--render" || args[0] == "--render-demo")) throw new ArgumentException(Localization.Text("OutputFileRequired"));
                if (args.Length >= 2 && args[0] == "--diagnostics")
                {
                    var state = new { Language = Localization.CurrentCultureName, LanguagePreference = Localization.Preference, Devices = AudioBackend.GetDevices(), InputDevices = AudioBackend.GetInputDevices(), Master = AudioBackend.GetMaster(), Sessions = AudioBackend.GetSessions(), VolumeIcon = TrayVolumeHook.GetVolumeIconBounds() };
                    File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(state));
                    return 0;
                }
                if (args.Length >= 2 && (args[0] == "--render" || args[0] == "--render-demo"))
                {
                    Application renderApp = new Application();
                    AudioPanel preview = new AudioPanel();
                    if (args[0] == "--render-demo") preview.LoadDemo();
                    else preview.RefreshAudio(true);
                    preview.RenderToFile(args[1]);
                    renderApp.Shutdown();
                    return 0;
                }
                if (args.Contains("--quit")) { Signal(QuitName); return 0; }
                bool first;
                using (Mutex mutex = new Mutex(true, MutexName, out first))
                {
                    if (!first) { if (args.Contains("--toggle") || args.Contains("--show")) Signal(ToggleName); return 0; }
                    using (EventWaitHandle toggle = new EventWaitHandle(false, EventResetMode.AutoReset, ToggleName))
                    using (EventWaitHandle quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName))
                    {
                        Application app = new Application();
                        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { Log(e.Exception); e.Handled = true; };
                        AudioPanel panel = new AudioPanel();
                        app.MainWindow = panel;
                        using (TrayVolumeHook hook = new TrayVolumeHook(delegate { app.Dispatcher.BeginInvoke(new Action(panel.Toggle)); }, delegate { app.Dispatcher.BeginInvoke(new Action(panel.Dismiss)); }))
                        {
                            panel.VisibleBoundsChanged += hook.SetPanelBounds;
                            if (!hook.IsInstalled || !hook.IsKeyboardInstalled) throw new InvalidOperationException(Localization.Text("IntegrationError") + " " + hook.InstallationError);
                            try { File.WriteAllText(Path.Combine(DataDirectory, "status.json"), new JavaScriptSerializer().Serialize(new { Running = true, Language = Localization.CurrentCultureName, LanguagePreference = Localization.Preference, NativeIconHook = hook.IsInstalled, KeyboardShortcutHook = hook.IsKeyboardInstalled, NativeIconBounds = TrayVolumeHook.GetVolumeIconBounds(), Started = DateTime.Now.ToString("s") })); } catch { }
                            ThreadPool.QueueUserWorkItem(delegate {
                                try { while (true) { toggle.WaitOne(); app.Dispatcher.BeginInvoke(new Action(panel.Toggle)); } } catch (ObjectDisposedException) { }
                            });
                            ThreadPool.QueueUserWorkItem(delegate {
                                try { quit.WaitOne(); app.Dispatcher.BeginInvoke(new Action(delegate { IsExiting = true; app.Shutdown(); })); } catch (ObjectDisposedException) { }
                            });
                            if (args.Contains("--toggle") || args.Contains("--show")) app.Dispatcher.BeginInvoke(new Action(panel.Toggle));
                            app.Run();
                        }
                        mutex.ReleaseMutex();
                    }
                }
                return 0;
            }
            catch (Exception error) { Log(error); return 1; }
        }

        private static void Signal(string name)
        {
            for (int i = 0; i < 20; i++)
            {
                try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting(name)) signal.Set(); return; }
                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(25); }
            }
        }
        private static void DisablePanel()
        {
            string startupLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "VolumeDock.lnk");
            if (File.Exists(startupLink)) File.Delete(startupLink);
            Signal(QuitName);
        }
        public static void Log(Exception error)
        {
            try { File.AppendAllText(Path.Combine(DataDirectory, "error.log"), DateTime.Now.ToString("s") + " " + error + Environment.NewLine); } catch { }
        }
    }

    public sealed class AudioPanel : Window
    {
        private readonly Border _root;
        private readonly StackPanel _body;
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _previewTimer;
        private readonly DispatcherTimer _closeTimer;
        private VolumeRow _pendingPreview;
        private readonly List<Button> _openingHeaders = new List<Button>();
        private TranslateTransform _panelTranslation;
        private bool _collectOpeningHeaders;
        private bool _closing;
        private string _closeAction;
        private DateTime _lastPreviewStarted = DateTime.MinValue;
        private readonly Dictionary<string, VolumeRow> _rows = new Dictionary<string, VolumeRow>();
        private readonly Dictionary<string, bool> _expandedSections = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, ImageSource> _icons = new Dictionary<string, ImageSource>();
        private VolumeRow _master;
        private string _signature;
        private string _defaultDeviceId;
        private bool _synchronizing;
        private bool _dark;
        private bool _transparency;
        private string _error;
        private Brush _accent;
        private Brush _selected;
        private TextBlock _spatialLabel;
        private readonly TextBlock _headerTitle;
        private readonly TextBlock _moreText;
        private readonly Button _backButton;
        private readonly Button _languageButton;
        private readonly Border _languagePicker;
        private readonly StackPanel _languageChoices;
        private bool _demo;
        private DateTime _openingUntil;
        public event Action<System.Drawing.Rectangle> VisibleBoundsChanged;

        public AudioPanel()
        {
            Title = Localization.Text("SoundOutput");
            Width = 360;
            Height = 560;
            MinHeight = 240;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 14;
            LoadTheme();
            using (Stream styleStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AudioStyles"))
                Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(styleStream));
            Foreground = (Brush)Resources["TextBrush"];
            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
            _root = new Border { Background = (Brush)Resources["PanelBrush"], BorderBrush = (Brush)Resources["BorderBrush"], BorderThickness = new Thickness(1), Child = layout };
            _panelTranslation = new TranslateTransform();
            _root.RenderTransform = _panelTranslation;
            Content = _root;
            Grid header = new Grid { Margin = new Thickness(14, 0, 20, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Button back = new Button { Content = Glyph("\uE72B", 18), Padding = new Thickness(5), ToolTip = Localization.Text("Close") };
            _backButton = back;
            back.Click += delegate { CloseAnimated("hidden"); };
            header.Children.Add(back);
            TextBlock title = new TextBlock { Text = Localization.Text("SoundOutput"), FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            _headerTitle = title;
            Grid.SetColumn(title, 1); header.Children.Add(title);
            StackPanel keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            keys.Children.Add(KeyBadge("\uE782", true)); keys.Children.Add(KeyBadge("CTRL", false)); keys.Children.Add(KeyBadge("V", false));
            Grid.SetColumn(keys, 2); header.Children.Add(keys);
            layout.Children.Add(header);
            _body = new StackPanel { Margin = new Thickness(20, 0, 20, 16) };
            ScrollViewer scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
            Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
            Border footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = (Brush)Resources["BorderBrush"], Background = (Brush)Resources["FooterBrush"] };
            _moreText = new TextBlock { Text = Localization.Text("MoreVolumeSettings"), FontSize = 13, Foreground = (Brush)Resources["SecondaryBrush"] };
            Button more = new Button { Content = _moreText, Padding = new Thickness(20, 8, 8, 8) };
            more.Click += delegate { OpenSettings("ms-settings:apps-volume"); };
            Grid footerLayout = new Grid();
            footerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            footerLayout.Children.Add(more);
            _languageButton = new Button { Content = Glyph("\uE774", 18), ToolTip = Localization.Text("Language"), Padding = new Thickness(6) };
            _languageButton.Click += delegate { BuildLanguageChoices(); _languagePicker.Visibility = _languagePicker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
            Grid.SetColumn(_languageButton, 1); footerLayout.Children.Add(_languageButton);
            footer.Child = footerLayout; Grid.SetRow(footer, 2); layout.Children.Add(footer);
            // Keep language choices inside this window so the outside-click hook
            // cannot dismiss a separate popup before its selection is delivered.
            _languageChoices = new StackPanel { Margin = new Thickness(12) };
            _languagePicker = new Border { Child = _languageChoices, Background = (Brush)Resources["PanelBrush"], BorderBrush = (Brush)Resources["BorderBrush"], BorderThickness = new Thickness(1), Margin = new Thickness(12, 0, 12, 52), VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
            Grid.SetRowSpan(_languagePicker, 3); layout.Children.Add(_languagePicker);
            PreviewMouseLeftButtonDown += delegate { if (_languagePicker.Visibility == Visibility.Visible && !_languagePicker.IsMouseOver && !_languageButton.IsMouseOver) _languagePicker.Visibility = Visibility.Collapsed; };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _timer.Tick += delegate { if (IsVisible) RefreshAudio(false); };
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
            _previewTimer.Tick += delegate { _previewTimer.Stop(); PlayPendingPreview(); };
            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _closeTimer.Tick += delegate
            {
                _closeTimer.Stop();
                if (!_closing) return;
                Hide();
                _closing = false;
                _root.BeginAnimation(UIElement.OpacityProperty, null);
                _panelTranslation.BeginAnimation(TranslateTransform.YProperty, null);
                _root.Opacity = 1;
                _panelTranslation.Y = 0;
                _timer.Stop();
                SaveUiStatus(_closeAction ?? "hidden");
            };
            SourceInitialized += delegate { ApplyBackdrop(); };
            IsVisibleChanged += delegate { PublishVisibleBounds(); };
            LocationChanged += delegate { PublishVisibleBounds(); };
            SizeChanged += delegate { PublishVisibleBounds(); };
            Deactivated += delegate { if (DateTime.UtcNow < _openingUntil) return; CloseAnimated("deactivated"); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { CloseAnimated("escape"); e.Handled = true; } };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!Program.IsExiting) { e.Cancel = true; Dismiss(); } };
        }

        private void LoadTheme()
        {
            _dark = true; _transparency = true;
            uint accent = 0xffd77800;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
            {
                if (key != null) { _dark = Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 0)) == 0; _transparency = Convert.ToInt32(key.GetValue("EnableTransparency", 1)) != 0; }
            }
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\DWM"))
                if (key != null) accent = unchecked((uint)Convert.ToInt32(key.GetValue("AccentColor", unchecked((int)accent))));
            _accent = new SolidColorBrush(Color.FromRgb((byte)accent, (byte)(accent >> 8), (byte)(accent >> 16)));
            _selected = new SolidColorBrush(_dark ? Color.FromRgb(48, 48, 48) : Color.FromRgb(225, 225, 225));
            Resources["TextBrush"] = new SolidColorBrush(_dark ? Color.FromRgb(245, 245, 245) : Color.FromRgb(25, 25, 25));
            Resources["SecondaryBrush"] = new SolidColorBrush(_dark ? Color.FromRgb(180, 180, 180) : Color.FromRgb(90, 90, 90));
            Resources["PanelBrush"] = new SolidColorBrush(_dark ? Color.FromRgb(31, 31, 31) : Color.FromRgb(243, 243, 243));
            Resources["FooterBrush"] = new SolidColorBrush(_dark ? Color.FromRgb(27, 27, 27) : Color.FromRgb(235, 235, 235));
            Resources["BorderBrush"] = new SolidColorBrush(_dark ? Color.FromRgb(66, 66, 66) : Color.FromRgb(190, 190, 190));
            Resources["HoverBrush"] = new SolidColorBrush(_dark ? Color.FromRgb(57, 57, 57) : Color.FromRgb(217, 217, 217));
            Resources["PressedBrush"] = new SolidColorBrush(_dark ? Color.FromRgb(72, 72, 72) : Color.FromRgb(200, 200, 200));
            Resources["TrackBrush"] = new SolidColorBrush(_dark ? Color.FromRgb(120, 120, 120) : Color.FromRgb(130, 130, 130));
            Resources["AccentBrush"] = _accent;
        }

        public void Toggle()
        {
            _languagePicker.Visibility = Visibility.Collapsed;
            if (_closing)
            {
                _closeTimer.Stop();
                _closing = false;
                _openingUntil = DateTime.UtcNow.AddMilliseconds(350);
                AnimateOpen();
                _timer.Start();
                SaveUiStatus("shown");
                return;
            }
            if (IsVisible) { CloseAnimated("hidden"); return; }
            _openingUntil = DateTime.UtcNow.AddMilliseconds(350);
            _expandedSections.Clear();
            _openingHeaders.Clear();
            _collectOpeningHeaders = true;
            try { RefreshAudio(true); }
            finally { _collectOpeningHeaders = false; }
            _root.Opacity = 0;
            _panelTranslation.Y = 10;
            Show();
            PositionAtTray();
            Activate();
            AnimateOpen();
            _timer.Start();
            SaveUiStatus("shown");
        }

        public void Dismiss()
        {
            if (!IsVisible) return;
            _languagePicker.Visibility = Visibility.Collapsed;
            CloseAnimated("outside-click");
        }

        private void AnimateOpen()
        {
            CubicEase easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            _root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(_root.Opacity, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
            _panelTranslation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_panelTranslation.Y, 0, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing });
            for (int i = 0; i < _openingHeaders.Count; i++)
            {
                Button header = _openingHeaders[i];
                header.Opacity = 0;
                TranslateTransform movement = header.RenderTransform as TranslateTransform;
                if (movement == null) { movement = new TranslateTransform(); header.RenderTransform = movement; }
                movement.Y = 6;
                TimeSpan delay = TimeSpan.FromMilliseconds(i * 28);
                header.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(135))) { BeginTime = delay, EasingFunction = easing });
                movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, new Duration(TimeSpan.FromMilliseconds(150))) { BeginTime = delay, EasingFunction = easing });
            }
            _openingHeaders.Clear();
        }

        private void CloseAnimated(string action)
        {
            if (!IsVisible || _closing) return;
            _closing = true;
            _closeAction = action;
            _timer.Stop();
            _previewTimer.Stop();
            _pendingPreview = null;
            CubicEase easing = new CubicEase { EasingMode = EasingMode.EaseIn };
            _root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(_root.Opacity, 0, TimeSpan.FromMilliseconds(130)) { EasingFunction = easing });
            _panelTranslation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_panelTranslation.Y, 8, TimeSpan.FromMilliseconds(140)) { EasingFunction = easing });
            _closeTimer.Stop();
            _closeTimer.Start();
        }

        private void SchedulePreview(VolumeRow row)
        {
            if (row.Session != null && !row.Session.IsSystem) { _previewTimer.Stop(); _pendingPreview = null; return; }
            _pendingPreview = row;
            _previewTimer.Stop();
            _previewTimer.Interval = TimeSpan.FromMilliseconds(240);
            if (!row.Slider.IsMouseCaptureWithin) _previewTimer.Start();
        }

        private void PlayPendingPreview()
        {
            VolumeRow row = _pendingPreview;
            if (row == null) return;
            // A slider can briefly lose capture while the pointer is still down.
            // Keep the latest value pending until the interaction actually ends.
            if (row.MouseAdjusting) return;
            // Windows Default.wav is a short 410 ms sample. Keep enough space
            // between previews for it to finish instead of restarting its tail.
            TimeSpan playbackSpacing = TimeSpan.FromMilliseconds(470);
            if (_lastPreviewStarted != DateTime.MinValue)
            {
                TimeSpan remaining = playbackSpacing - (DateTime.UtcNow - _lastPreviewStarted);
                if (remaining > TimeSpan.Zero)
                {
                    _previewTimer.Stop();
                    _previewTimer.Interval = remaining;
                    _previewTimer.Start();
                    return;
                }
            }
            _pendingPreview = null;
            // SND_SYSTEM assigns the sample to "Sons do sistema", matching
            // Windows' volume feedback without a long, easily interrupted chime.
            string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            string sample = Path.Combine(media, "Windows Default.wav");
            if (!File.Exists(sample)) sample = Path.Combine(media, "ding.wav");
            bool played = File.Exists(sample) && PlaySound(sample, IntPtr.Zero, 0x00200000 | 0x00020000 | 0x00000001 | 0x00000002);
            if (!played) played = PlaySound("SystemDefault", IntPtr.Zero, 0x00200000 | 0x00010000 | 0x00000001 | 0x00000002);
            if (played) _lastPreviewStarted = DateTime.UtcNow;
        }

        private void PublishVisibleBounds()
        {
            Action<System.Drawing.Rectangle> publish = VisibleBoundsChanged;
            if (publish == null) return;
            if (!IsVisible) { publish(System.Drawing.Rectangle.Empty); return; }
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            NativeRect rect;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect))
                publish(System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
        }

        private void SaveUiStatus(string action)
        {
            try { File.WriteAllText(Path.Combine(Program.DataDirectory, "ui-status.json"), new JavaScriptSerializer().Serialize(new { Action = action, Language = Localization.CurrentCultureName, LanguagePreference = Localization.Preference, Visible = IsVisible, Active = IsActive, Width = Width, Height = Height, Time = DateTime.Now.ToString("s") })); } catch { }
        }

        private void BuildLanguageChoices()
        {
            _languageChoices.Children.Clear();
            _languageChoices.Children.Add(Heading(Localization.Text("Language"), 0));
            string[] preferences = { "system", "en", "pt-BR" };
            string[] labels = { Localization.Text("LanguageSystem"), Localization.Text("LanguageEnglish"), Localization.Text("LanguagePortugueseBrazil") };
            for (int i = 0; i < preferences.Length; i++)
            {
                string preference = preferences[i];
                Button choice = new Button { Content = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center }, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6), MinHeight = 34, Background = Localization.Preference == preference ? _selected : Brushes.Transparent };
                choice.Click += delegate {
                    if (!Localization.SetLanguage(preference)) { MessageBox.Show(this, Localization.Text("LanguageSaveError"), "VolumeDock", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                    _languagePicker.Visibility = Visibility.Collapsed;
                    ApplyLanguage();
                };
                _languageChoices.Children.Add(choice);
            }
        }

        public void ApplyLanguage()
        {
            Title = _headerTitle.Text = Localization.Text("SoundOutput");
            _backButton.ToolTip = Localization.Text("Close");
            _moreText.Text = Localization.Text("MoreVolumeSettings");
            _languageButton.ToolTip = Localization.Text("Language");
            _signature = null;
            if (_demo) LoadDemo(); else RefreshAudio(true);
            SaveUiStatus("language-changed");
        }

        // Render a representative preview without reading or changing this PC's audio.
        public void LoadDemo()
        {
            _demo = true;
            _synchronizing = true;
            try
            {
                BuildBody(new List<AudioDevice> {
                    new AudioDevice { Id = "demo-microphone", Name = Localization.Text("DemoMicrophone"), IsDefault = true, IsInput = true },
                    new AudioDevice { Id = "demo-webcam-mic", Name = Localization.Text("DemoWebcamMic"), IsInput = true }
                }, new List<AudioDevice> {
                    new AudioDevice { Id = "demo-speakers", Name = Localization.Text("DemoSpeakers"), IsDefault = true },
                    new AudioDevice { Id = "demo-headphones", Name = Localization.Text("DemoHeadphones") }
                }, new List<AudioSession> {
                    new AudioSession { Id = "demo-system", Name = Localization.Text("SystemSounds"), IsSystem = true },
                    new AudioSession { Id = "demo-browser", Name = Localization.Text("DemoBrowser"), ProcessId = 1 },
                    new AudioSession { Id = "demo-music", Name = Localization.Text("DemoMusic"), ProcessId = 2 }
                });
                _spatialLabel.Text = Localization.Text("SpatialOff");
                UpdateRow(_master, 0.60f, false);
                UpdateRow(_rows["system"], 0.50f, false);
                UpdateRow(_rows["1"], 0.80f, false);
                UpdateRow(_rows["2"], 0.35f, false);
            }
            finally { _synchronizing = false; }
        }

        public void RefreshAudio(bool force)
        {
            try
            {
                bool previousSync = _synchronizing;
                _synchronizing = true;
                try
                {
                List<AudioDevice> inputs = AudioBackend.GetInputDevices();
                List<AudioDevice> devices = AudioBackend.GetDevices();
                AudioDevice selected = devices.FirstOrDefault(delegate(AudioDevice d) { return d.IsDefault; });
                _defaultDeviceId = selected == null ? null : selected.Id;
                List<AudioSession> sessions = selected == null ? new List<AudioSession>() : AudioBackend.GetSessions();
                string signature = String.Join("|", inputs.Select(delegate(AudioDevice d) { return d.Id + ":" + d.IsDefault; })) + "#" + String.Join("|", devices.Select(delegate(AudioDevice d) { return d.Id + ":" + d.IsDefault; })) + "#" + String.Join("|", sessions.Select(SessionKey));
                if (force || signature != _signature) { BuildBody(inputs, devices, sessions); _signature = signature; }
                    if (selected != null)
                    {
                        if (_spatialLabel != null) _spatialLabel.Text = SpatialAudioState.Read(selected.Id).Label;
                        AudioVolume volume = AudioBackend.GetMaster();
                        if (_master != null) UpdateRow(_master, volume.Volume, volume.IsMuted);
                        foreach (AudioSession session in sessions)
                        {
                            VolumeRow row;
                            if (_rows.TryGetValue(SessionKey(session), out row)) { row.Session = session; UpdateRow(row, session.Volume, session.IsMuted); }
                        }
                    }
                _error = null;
                }
                finally { _synchronizing = previousSync; }
            }
            catch (Exception error)
            {
                Program.Log(error);
                if (force)
                {
                    _error = Localization.Text("AudioAccessError");
                    bool previousSync = _synchronizing;
                    _synchronizing = true;
                    try { BuildBody(new List<AudioDevice>(), new List<AudioDevice>(), new List<AudioSession>()); }
                    finally { _synchronizing = previousSync; }
                }
            }
        }

        private void BuildBody(List<AudioDevice> inputs, List<AudioDevice> devices, List<AudioSession> sessions)
        {
            _body.Children.Clear(); _rows.Clear(); _master = null;
            StackPanel inputList = new StackPanel();
            AddDeviceChoices(inputList, inputs, true);
            _body.Children.Add(Section("InputDevice", Localization.Text("InputDevice"), inputList));

            StackPanel outputList = new StackPanel();
            AddDeviceChoices(outputList, devices, false);
            _body.Children.Add(Section("OutputDevice", Localization.Text("OutputDevice"), outputList));

            StackPanel spatialContent = new StackPanel();
            string spatialText = Localization.Text("SpatialConfigure");
            if (_defaultDeviceId != null) spatialText = SpatialAudioState.Read(_defaultDeviceId).Label;
            Grid spatial = new Grid();
            spatial.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            spatial.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            _spatialLabel = new TextBlock { Text = spatialText, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            spatial.Children.Add(_spatialLabel);
            TextBlock spatialArrow = Glyph("\uE8A7", 12); Grid.SetColumn(spatialArrow, 1); spatial.Children.Add(spatialArrow);
            Button spatialButton = new Button { Content = spatial, ToolTip = Localization.Text("SpatialConfigureTooltip"), MinHeight = 40 };
            spatialButton.Click += delegate { OpenSettings("ms-settings:sound"); };
            spatialContent.Children.Add(spatialButton);
            _body.Children.Add(Section("SpatialAudio", Localization.Text("SpatialAudio"), spatialContent));

            StackPanel mixerContent = new StackPanel();
            if (devices.Any(delegate(AudioDevice d) { return d.IsDefault; }))
            {
                _master = BuildVolumeRow(null, Localization.Text("MasterVolume"), null);
                mixerContent.Children.Add(_master.Container);
                foreach (AudioSession session in sessions)
                {
                    VolumeRow row = BuildVolumeRow(session, session.Name, GetAppIcon(session));
                    _rows[SessionKey(session)] = row;
                    mixerContent.Children.Add(row.Container);
                }
            }
            else mixerContent.Children.Add(new TextBlock { Text = _error ?? Localization.Text("NoOutputs"), Foreground = (Brush)Resources["SecondaryBrush"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 12, 0, 8) });
            _body.Children.Add(Section("VolumeMixer", Localization.Text("VolumeMixer"), mixerContent));
            _body.Measure(new Size(318, Double.PositiveInfinity));
            Height = Math.Min(700, Math.Max(270, _body.DesiredSize.Height + 111));
            if (IsVisible) PositionAtTray();
        }

        private void AddDeviceChoices(StackPanel destination, List<AudioDevice> devices, bool input)
        {
            foreach (AudioDevice device in devices)
            {
                AudioDevice captured = device;
                Button choice = new Button { Padding = new Thickness(0), Background = device.IsDefault ? _selected : Brushes.Transparent, ToolTip = device.Name, MinHeight = 40, Margin = new Thickness(0, 2, 0, 0) };
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Border bar = new Border { Width = 3, Height = 17, Background = device.IsDefault ? _accent : Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center }; row.Children.Add(bar);
                TextBlock icon = Glyph(input ? "\uE720" : (IsHeadphone(device.Name) ? "\uE7F6" : "\uE7F5"), 18); Grid.SetColumn(icon, 1); row.Children.Add(icon);
                TextBlock label = new TextBlock { Text = device.Name, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(3, 8, 10, 8), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 2); row.Children.Add(label); choice.Content = row;
                choice.Click += delegate { TryAudio(delegate { if (input) AudioBackend.SetDefaultInputDevice(captured.Id); else AudioBackend.SetDefaultDevice(captured.Id); }); RefreshAudio(true); PositionAtTray(); };
                destination.Children.Add(choice);
            }
            if (devices.Count == 0)
            {
                string emptyText = input ? Localization.Text("NoInputs") : (_error ?? Localization.Text("NoOutputs"));
                destination.Children.Add(new TextBlock { Text = emptyText, Foreground = (Brush)Resources["SecondaryBrush"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 10, 0, 8) });
            }
        }

        private UIElement Section(string key, string title, UIElement content)
        {
            bool expanded;
            if (!_expandedSections.TryGetValue(key, out expanded)) expanded = false;
            StackPanel section = new StackPanel { Margin = new Thickness(0, 0, 0, 3) };
            Grid header = new Grid { MinHeight = 42 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock arrow = Glyph(expanded ? "\uE70D" : "\uE76C", 12);
            arrow.Foreground = (Brush)Resources["SecondaryBrush"];
            header.Children.Add(arrow);
            TextBlock label = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); header.Children.Add(label);
            Border contentHost = new Border { Child = content, Visibility = expanded ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(24, 0, 0, 4) };
            Button toggle = new Button { Content = header, Padding = new Thickness(3, 2, 3, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 42, ToolTip = title };
            if (_collectOpeningHeaders) _openingHeaders.Add(toggle);
            toggle.Click += delegate
            {
                bool expand = contentHost.Visibility != Visibility.Visible;
                _expandedSections[key] = expand;
                contentHost.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
                arrow.Text = expand ? "\uE70D" : "\uE76C";
                if (expand)
                {
                    contentHost.Opacity = 0;
                    TranslateTransform movement = contentHost.RenderTransform as TranslateTransform;
                    if (movement == null) { movement = new TranslateTransform(); contentHost.RenderTransform = movement; }
                    movement.Y = -4;
                    CubicEase easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                    contentHost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(135)) { EasingFunction = easing });
                    movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, 0, TimeSpan.FromMilliseconds(155)) { EasingFunction = easing });
                }
                _body.Measure(new Size(318, Double.PositiveInfinity));
                Height = Math.Min(700, Math.Max(270, _body.DesiredSize.Height + 111));
                if (IsVisible) PositionAtTray();
            };
            section.Children.Add(toggle);
            section.Children.Add(contentHost);
            return section;
        }

        private VolumeRow BuildVolumeRow(AudioSession session, string name, ImageSource icon)
        {
            VolumeRow row = new VolumeRow { Session = session, Name = name, DeviceId = _defaultDeviceId };
            Grid container = new Grid { Height = 46, Margin = new Thickness(0, 0, 0, 0) };
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid iconGrid = new Grid { Width = 27, Height = 29 };
            if (icon != null) iconGrid.Children.Add(new Image { Source = icon, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
            else { row.Glyph = Glyph(session == null ? "\uE767" : (session.IsSystem ? "\uE770" : "\uE7F4"), 23); iconGrid.Children.Add(row.Glyph); }
            row.MuteBadge = Glyph("\uE74F", 11); row.MuteBadge.HorizontalAlignment = HorizontalAlignment.Right; row.MuteBadge.VerticalAlignment = VerticalAlignment.Bottom; row.MuteBadge.Background = (Brush)Resources["PanelBrush"]; row.MuteBadge.Visibility = Visibility.Collapsed; iconGrid.Children.Add(row.MuteBadge);
            Button mute = new Button { Content = iconGrid, Padding = new Thickness(2), ToolTip = name + " — " + Localization.Text("MuteToggle") };
            mute.Click += delegate { TryAudio(delegate { if (row.Session == null) AudioBackend.SetMasterMute(!row.IsMuted, row.DeviceId); else AudioBackend.SetSessionMute(row.Session.ProcessId, row.Session.Id, !row.IsMuted, row.DeviceId); }); RefreshAudio(false); };
            container.Children.Add(mute);
            Slider slider = new Slider { Minimum = 0, Maximum = 100, Margin = new Thickness(8, 0, 4, 0), ToolTip = name, AutoToolTipPlacement = AutoToolTipPlacement.TopLeft, AutoToolTipPrecision = 0 };
            slider.ValueChanged += delegate(object sender, RoutedPropertyChangedEventArgs<double> e) {
                if (_synchronizing) return;
                TryAudio(delegate { if (row.Session == null) AudioBackend.SetMasterVolume((float)(slider.Value / 100), row.DeviceId); else AudioBackend.SetSessionVolume(row.Session.ProcessId, row.Session.Id, (float)(slider.Value / 100), row.DeviceId); });
                if (row.MouseAdjusting && !row.SuppressPreview) SchedulePreview(row);
            };
            slider.PreviewMouseLeftButtonDown += delegate { row.MouseAdjusting = true; };
            slider.PreviewMouseLeftButtonUp += delegate { row.MouseAdjusting = false; _previewTimer.Stop(); PlayPendingPreview(); };
            slider.LostMouseCapture += delegate { if (row.MouseAdjusting && Mouse.LeftButton == MouseButtonState.Released) { row.MouseAdjusting = false; _previewTimer.Stop(); PlayPendingPreview(); } };
            slider.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) { row.SuppressPreview = true; try { slider.Value = Math.Max(0, Math.Min(100, slider.Value + Math.Sign(e.Delta) * 2)); } finally { row.SuppressPreview = false; } e.Handled = true; };
            Grid.SetColumn(slider, 1); container.Children.Add(slider);
            row.Slider = slider; row.Container = container;
            return row;
        }

        private void UpdateRow(VolumeRow row, float volume, bool muted)
        {
            if (!row.Slider.IsMouseCaptureWithin) row.Slider.Value = Math.Round(volume * 100);
            row.IsMuted = muted;
            row.MuteBadge.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            row.Slider.ToolTip = row.Name + " — " + Math.Round(volume * 100) + "%";
        }

        private static string SessionKey(AudioSession session) { return session.IsSystem ? "system" : (session.ProcessId != 0 ? session.ProcessId.ToString() : session.Id); }
        private static bool IsHeadphone(string name) { string n = name.ToLowerInvariant(); return n.Contains("head") || n.Contains("fone") || n.Contains("ear"); }
        private TextBlock Heading(string text, double margin) { return new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, margin, 0, 8) }; }
        private static TextBlock Glyph(string glyph, double size) { return new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
        private Border KeyBadge(string text, bool icon)
        {
            UIElement label;
            if (icon)
            {
                Canvas logo = new Canvas { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
                for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++)
                {
                    Border square = new Border { Width = 4, Height = 4, Background = (Brush)Resources["SecondaryBrush"] };
                    Canvas.SetLeft(square, x * 6); Canvas.SetTop(square, y * 6); logo.Children.Add(square);
                }
                label = logo;
            }
            else label = new TextBlock { Text = text, FontSize = 10, Foreground = (Brush)Resources["SecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
            return new Border { BorderBrush = (Brush)Resources["BorderBrush"], BorderThickness = new Thickness(1), Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(3, 0, 0, 0), Height = 19, Child = label };
        }
        private ImageSource GetAppIcon(AudioSession session)
        {
            string path = session.ProcessPath;
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            ImageSource cached;
            if (_icons.TryGetValue(path, out cached)) return cached;
            try
            {
                using (System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (icon == null) return null;
                    BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    source.Freeze(); _icons[path] = source; return source;
                }
            }
            catch { return null; }
        }
        private void TryAudio(Action action) { try { action(); } catch (Exception error) { Program.Log(error); _signature = null; } }
        private void OpenSettings(string uri) { CloseAnimated("settings"); try { Process.Start(uri); } catch (Exception error) { Program.Log(error); } }

        private void PositionAtTray()
        {
            if (!IsVisible) return;
            System.Drawing.Rectangle icon = TrayVolumeHook.GetVolumeIconBounds();
            Forms.Screen screen = icon.IsEmpty ? Forms.Screen.PrimaryScreen : Forms.Screen.FromRectangle(icon);
            System.Drawing.Rectangle area = screen.WorkingArea;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            double scale = 1;
            try { scale = GetDpiForWindow(hwnd) / 96.0; } catch (EntryPointNotFoundException) { }
            int width = (int)Math.Round(Width * scale);
            int height = Math.Min((int)Math.Round(Height * scale), area.Height - 16);
            int x = area.Right - width;
            int y = area.Bottom - height;
            if (!icon.IsEmpty)
            {
                x = Math.Max(area.Left, Math.Min(icon.Right - width + 4, area.Right - width));
                if (icon.Bottom <= area.Top + 4) y = area.Top;
                else if (icon.Right <= area.Left + 4) { x = area.Left; y = Math.Max(area.Top, Math.Min(icon.Bottom - height, area.Bottom - height)); }
                else if (icon.Left >= area.Right - 4) { x = area.Right - width; y = Math.Max(area.Top, Math.Min(icon.Bottom - height, area.Bottom - height)); }
            }
            SetWindowPos(hwnd, new IntPtr(-1), x, y, width, height, 0x0010);
            PublishVisibleBounds();
        }

        private void ApplyBackdrop()
        {
            if (!_transparency) return;
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                HwndSource source = HwndSource.FromHwnd(handle);
                if (source != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
                AccentPolicy policy = new AccentPolicy { AccentState = 4, AccentFlags = 2, GradientColor = _dark ? unchecked((int)0xD01F1F1F) : unchecked((int)0xD0F3F3F3) };
                int size = Marshal.SizeOf(typeof(AccentPolicy));
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try { Marshal.StructureToPtr(policy, ptr, false); WindowCompositionAttributeData data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size }; SetWindowCompositionAttribute(handle, ref data); }
                finally { Marshal.FreeHGlobal(ptr); }
                _root.Background = new SolidColorBrush(_dark ? Color.FromArgb(80, 31, 31, 31) : Color.FromArgb(80, 243, 243, 243));
            }
            catch (EntryPointNotFoundException) { }
        }

        public void RenderToFile(string path)
        {
            _root.Measure(new Size(Width, Height));
            _root.Arrange(new Rect(0, 0, Width, Height));
            _root.UpdateLayout();
            RenderTargetBitmap image = new RenderTargetBitmap((int)(Width * 1.5), (int)(Height * 1.5), 144, 144, PixelFormats.Pbgra32);
            image.Render(_root);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
        }

        private sealed class VolumeRow
        {
            public AudioSession Session;
            public string Name;
            public string DeviceId;
            public bool IsMuted;
            public bool MouseAdjusting;
            public bool SuppressPreview;
            public Grid Container;
            public Slider Slider;
            public TextBlock Glyph;
            public TextBlock MuteBadge;
        }
        [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr window, ref WindowCompositionAttributeData data);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern bool PlaySound(string sound, IntPtr module, uint flags);
    }
}
