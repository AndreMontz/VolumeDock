using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Win10Audio
{
    /// <summary>
    /// UI strings and the per-user language preference. "system" follows the
    /// Windows UI language; Portuguese cultures use pt-BR and all others use en.
    /// No system-wide culture or Windows setting is changed.
    /// </summary>
    public static class Localization
    {
        private static readonly object Sync = new object();
        private static bool _loaded;
        private static string _preference = "system";
        private static string _currentCultureName = "en";

        public static event Action LanguageChanged;

        public static string Preference
        {
            get { EnsureLoaded(); lock (Sync) { return _preference; } }
        }

        public static string CurrentCultureName
        {
            get { EnsureLoaded(); lock (Sync) { return _currentCultureName; } }
        }

        public static void Load()
        {
            bool changed;
            lock (Sync)
            {
                string preference = ReadPreference(SettingsFilePath());
                string currentCultureName = ResolveCulture(preference);
                changed = _loaded && (_preference != preference || _currentCultureName != currentCultureName);
                _preference = preference;
                _currentCultureName = currentCultureName;
                _loaded = true;
            }
            if (changed) NotifyLanguageChanged();
        }

        /// <summary>
        /// Saves and applies system, en, or pt-BR. Returns false when saving
        /// fails, leaving the previous preference in effect.
        /// </summary>
        public static bool SetLanguage(string preference)
        {
            string canonical = NormalizePreference(preference);
            if (canonical == null)
                throw new ArgumentException("Language must be system, en, or pt-BR.", "preference");

            EnsureLoaded();
            bool changed;
            lock (Sync)
            {
                if (!SavePreference(SettingsFilePath(), canonical)) return false;
                string cultureName = ResolveCulture(canonical);
                changed = _preference != canonical || _currentCultureName != cultureName;
                _preference = canonical;
                _currentCultureName = cultureName;
            }
            if (changed) NotifyLanguageChanged();
            return true;
        }

        /// <summary>
        /// Applies a language to this process only for previews and diagnostics.
        /// The saved preference is neither read nor changed by this method.
        /// </summary>
        public static void SetPreviewLanguage(string preference)
        {
            string canonical = NormalizePreference(preference);
            if (canonical == null)
                throw new ArgumentException("Language must be system, en, or pt-BR.", "preference");
            bool changed;
            lock (Sync)
            {
                string cultureName = ResolveCulture(canonical);
                changed = _loaded && (_preference != canonical || _currentCultureName != cultureName);
                _preference = canonical;
                _currentCultureName = cultureName;
                _loaded = true;
            }
            if (changed) NotifyLanguageChanged();
        }

        public static string Text(string key)
        {
            EnsureLoaded();
            if (key == null) return String.Empty;
            string[] translations;
            if (!Strings.TryGetValue(key, out translations)) return key;
            lock (Sync) { return translations[_currentCultureName == "pt-BR" ? 1 : 0]; }
        }

        private static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (_loaded) return;
                _preference = ReadPreference(SettingsFilePath());
                _currentCultureName = ResolveCulture(_preference);
                _loaded = true;
            }
        }

        private static string SettingsFilePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VolumeDock", "settings.json");
        }

        private static string ResolveCulture(string preference)
        {
            if (preference == "en" || preference == "pt-BR") return preference;
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase)
                ? "pt-BR" : "en";
        }

        private static string NormalizePreference(string preference)
        {
            if (String.Equals(preference, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (String.Equals(preference, "en", StringComparison.OrdinalIgnoreCase)) return "en";
            if (String.Equals(preference, "pt-BR", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
            return null;
        }

        private static Dictionary<string, object> ReadSettings(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return new Dictionary<string, object>();
                object parsed = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                Dictionary<string, object> settings = parsed as Dictionary<string, object>;
                return settings ?? new Dictionary<string, object>();
            }
            catch (Exception) { return new Dictionary<string, object>(); }
        }

        private static string ReadPreference(string path)
        {
            Dictionary<string, object> settings = ReadSettings(path);
            object value;
            return settings.TryGetValue("Language", out value) ? (NormalizePreference(value as string) ?? "system") : "system";
        }

        private static bool SavePreference(string path, string preference)
        {
            string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                Dictionary<string, object> settings = ReadSettings(path);
                settings["Language"] = preference;
                string contents = new JavaScriptSerializer().Serialize(settings);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
                // Replace existing content without truncating the settings file
                // first. On failure, keep the previous settings and selection.
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
                return true;
            }
            catch (Exception) { return false; }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (Exception) { }
            }
        }

        private static void NotifyLanguageChanged()
        {
            Action changed = LanguageChanged;
            if (changed != null) changed();
        }

        // English first, Brazilian Portuguese second. Keep keys stable so UI,
        // backend messages, render previews, and installer handoff share labels.
        private static readonly Dictionary<string, string[]> Strings = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "SoundOutput", new[] { "Sound output", "Saída de som" } },
            { "Close", new[] { "Close", "Fechar" } },
            { "InputDevice", new[] { "Input devices", "Dispositivos de entrada" } },
            { "OutputDevice", new[] { "Output device", "Dispositivo de saída" } },
            { "SpatialAudio", new[] { "Spatial audio", "Áudio espacial" } },
            { "VolumeMixer", new[] { "Volume mixer", "Mixer de volume" } },
            { "MoreVolumeSettings", new[] { "More volume settings", "Mais configurações de volume" } },
            { "MasterVolume", new[] { "System volume", "Volume do sistema" } },
            { "SystemSounds", new[] { "System sounds", "Sons do sistema" } },
            { "MuteToggle", new[] { "Mute/unmute", "Ativar/desativar som" } },
            { "SpatialConfigure", new[] { "Configure spatial audio", "Configurar áudio espacial" } },
            { "SpatialConfigureTooltip", new[] { "Configure spatial audio in Windows sound settings", "Configurar o áudio espacial nas propriedades de som do Windows" } },
            { "SpatialUnavailable", new[] { "Not available for this output", "Não disponível para esta saída" } },
            { "SpatialOff", new[] { "Off", "Desativado" } },
            { "SpatialActive", new[] { "Spatial audio enabled", "Áudio espacial ativo" } },
            { "SpatialWindowsSonic", new[] { "Windows Sonic for Headphones", "Windows Sonic para fones de ouvido" } },
            { "WindowsSonicHeadphones", new[] { "Windows Sonic for Headphones", "Windows Sonic para fones de ouvido" } },
            { "AudioDeviceFallback", new[] { "Audio device", "Dispositivo de áudio" } },
            { "ApplicationFallback", new[] { "Application", "Aplicativo" } },
            { "NoOutputs", new[] { "No audio output connected", "Nenhuma saída de áudio conectada" } },
            { "NoInputs", new[] { "No audio input connected", "Nenhuma entrada de áudio conectada" } },
            { "AudioAccessError", new[] { "Unable to access the audio device. Try opening the panel again.", "Não foi possível acessar o dispositivo de áudio. Tente abrir novamente." } },
            { "OutputFileRequired", new[] { "Specify the output file.", "Informe o arquivo de saída." } },
            { "IntegrationError", new[] { "Volume panel integration failed: ", "Falha na integração do painel de volume: " } },
            { "SelectOutputError", new[] { "Select an output device.", "Escolha um dispositivo de saída." } },
            { "SelectInputError", new[] { "Select an input device.", "Escolha um dispositivo de entrada." } },
            { "OutputDisconnectedError", new[] { "This device was disconnected. Open the panel again.", "Esse dispositivo foi desconectado. Abra o menu novamente." } },
            { "InputDisconnectedError", new[] { "This input device was disconnected. Open the panel again.", "Esse dispositivo de entrada foi desconectado. Abra o menu novamente." } },
            { "SessionEndedError", new[] { "The app's audio session ended. Open the panel again.", "O aplicativo encerrou sua sessão de áudio. Abra o menu novamente." } },
            { "NoOutputError", new[] { "No output device connected.", "Nenhum dispositivo de saída conectado." } },
            { "OutputChangedError", new[] { "The audio output changed. Refresh the panel before adjusting the volume.", "A saída de áudio mudou. Atualize o painel antes de ajustar o volume." } },
            { "VolumeRangeError", new[] { "Volume must be between 0 and 1.", "O volume deve estar entre 0 e 1." } },
            { "Language", new[] { "Language", "Idioma" } },
            { "LanguageSystem", new[] { "System language (automatic)", "Idioma do sistema (automático)" } },
            { "LanguageSystemResolved", new[] { "System language (automatic): {0}", "Idioma do sistema (automático): {0}" } },
            { "LanguageEnglish", new[] { "English", "English" } },
            { "LanguagePortugueseBrazil", new[] { "Português (Brasil)", "Português (Brasil)" } },
            { "LanguageSaveError", new[] { "Unable to save the language preference. The previous language is still selected.", "Não foi possível salvar a preferência de idioma. O idioma anterior continua selecionado." } },
            { "DemoSpeakers", new[] { "Speakers", "Alto-falantes" } },
            { "DemoHeadphones", new[] { "Headphones", "Fones de ouvido" } },
            { "DemoMicrophone", new[] { "Microphone", "Microfone" } },
            { "DemoWebcamMic", new[] { "Webcam microphone", "Microfone da webcam" } },
            { "DemoBrowser", new[] { "Browser", "Navegador" } },
            { "DemoMusic", new[] { "Music", "Música" } }
        };
    }
}
