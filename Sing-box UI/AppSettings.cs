using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Sing_box_UI
{
    internal sealed class AppSettings
    {
        private const string SectionName = "SingBoxUI";
        private const string ConfigFileKey = "ConfigFile";
        private const string CheckUpdatesTimeoutSecondsKey = "CheckUpdatesTimeoutSeconds";
        private const string DownloadTimeoutSecondsKey = "DownloadTimeoutSeconds";
        private const int DefaultTimeoutSeconds = 30;

        private readonly string _settingsPath;

        public AppSettings(string settingsPath)
        {
            _settingsPath = settingsPath;
            CheckUpdatesTimeoutSeconds = DefaultTimeoutSeconds;
            DownloadTimeoutSeconds = DefaultTimeoutSeconds;
        }

        public string ConfigFileName { get; set; }

        public int CheckUpdatesTimeoutSeconds { get; set; }

        public int DownloadTimeoutSeconds { get; set; }

        public void Load()
        {
            ConfigFileName = null;
            CheckUpdatesTimeoutSeconds = DefaultTimeoutSeconds;
            DownloadTimeoutSeconds = DefaultTimeoutSeconds;

            if (!File.Exists(_settingsPath))
            {
                return;
            }

            var inTargetSection = false;
            foreach (var rawLine in File.ReadAllLines(_settingsPath, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    inTargetSection = string.Equals(line.Substring(1, line.Length - 2), SectionName, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inTargetSection)
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim();

                if (string.Equals(key, ConfigFileKey, StringComparison.OrdinalIgnoreCase))
                {
                    ConfigFileName = value;
                }
                else if (string.Equals(key, CheckUpdatesTimeoutSecondsKey, StringComparison.OrdinalIgnoreCase))
                {
                    CheckUpdatesTimeoutSeconds = ParseTimeout(value);
                }
                else if (string.Equals(key, DownloadTimeoutSecondsKey, StringComparison.OrdinalIgnoreCase))
                {
                    DownloadTimeoutSeconds = ParseTimeout(value);
                }
            }
        }

        public void Save()
        {
            var lines = new List<string>
            {
                "[" + SectionName + "]",
                ConfigFileKey + "=" + (ConfigFileName ?? string.Empty),
                CheckUpdatesTimeoutSecondsKey + "=" + CheckUpdatesTimeoutSeconds,
                DownloadTimeoutSecondsKey + "=" + DownloadTimeoutSeconds
            };

            var directoryPath = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            File.WriteAllLines(_settingsPath, lines, Encoding.UTF8);
        }

        private static int ParseTimeout(string value)
        {
            int parsedValue;
            if (!int.TryParse(value, out parsedValue) || parsedValue <= 0)
            {
                return DefaultTimeoutSeconds;
            }

            return parsedValue;
        }
    }
}
