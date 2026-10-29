using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YAHCW
{
    public class Config
    {
        public string Corner { get; set; }
        public int DwellMs { get; set; }
        public int TriggerSizePx { get; set; }
        public int RearmDistancePx { get; set; }
        public int CooldownMs { get; set; }
        public bool HotkeyEnabled { get; set; }
        public string HotkeyModifiers { get; set; }
        public string HotkeyKey { get; set; }
        public int MaxThumbnails { get; set; }
        public List<string> BlockedProcesses { get; set; }
        public bool SuspendOnGameProcessRunning { get; set; }
        public int CheckRunningGamesIntervalMs { get; set; }
        public bool IsPaused { get; set; }

        public Config()
        {
            Corner = "TopLeft";
            DwellMs = 180;
            TriggerSizePx = 2;
            RearmDistancePx = 24;
            CooldownMs = 700;
            HotkeyEnabled = true;
            HotkeyModifiers = "Ctrl+Alt";
            HotkeyKey = "Space";
            MaxThumbnails = 32;
            BlockedProcesses = new List<string>();
            SuspendOnGameProcessRunning = false;
            CheckRunningGamesIntervalMs = 5000;
            IsPaused = false;
        }

        public void ClampAndValidate()
        {
            if (string.IsNullOrEmpty(Corner))
            {
                Corner = "TopLeft";
            }
            else
            {
                string c = Corner.Trim().ToLowerInvariant();
                if (c == "topleft" || c == "top-left" || c == "upperleft" || c == "tl")
                    Corner = "TopLeft";
                else if (c == "topright" || c == "top-right" || c == "upperright" || c == "tr")
                    Corner = "TopRight";
                else if (c == "bottomleft" || c == "bottom-left" || c == "lowerleft" || c == "bl")
                    Corner = "BottomLeft";
                else if (c == "bottomright" || c == "bottom-right" || c == "lowerright" || c == "br")
                    Corner = "BottomRight";
                else
                    Corner = "TopLeft";
            }

            if (DwellMs < 50) DwellMs = 50;
            if (DwellMs > 2000) DwellMs = 2000;

            if (TriggerSizePx < 1) TriggerSizePx = 1;
            if (TriggerSizePx > 10) TriggerSizePx = 10;

            if (RearmDistancePx < 10) RearmDistancePx = 10;
            if (RearmDistancePx > 200) RearmDistancePx = 200;

            if (CooldownMs < 100) CooldownMs = 100;
            if (CooldownMs > 5000) CooldownMs = 5000;

            if (MaxThumbnails < 4) MaxThumbnails = 4;
            if (MaxThumbnails > 64) MaxThumbnails = 64;

            if (CheckRunningGamesIntervalMs < 1000) CheckRunningGamesIntervalMs = 1000;
            if (CheckRunningGamesIntervalMs > 30000) CheckRunningGamesIntervalMs = 30000;

            if (BlockedProcesses == null)
            {
                BlockedProcesses = new List<string>();
            }

            // Normalise blocked processes to lower-case and strip trailing .exe for consistent comparison
            List<string> cleaned = new List<string>();
            for (int i = 0; i < BlockedProcesses.Count; i++)
            {
                string item = BlockedProcesses[i];
                if (!string.IsNullOrWhiteSpace(item))
                {
                    string norm = item.Trim().ToLowerInvariant();
                    if (!cleaned.Contains(norm))
                    {
                        cleaned.Add(norm);
                    }
                    if (norm.EndsWith(".exe"))
                    {
                        string withoutExe = norm.Substring(0, norm.Length - 4);
                        if (!cleaned.Contains(withoutExe))
                        {
                            cleaned.Add(withoutExe);
                        }
                    }
                }
            }
            BlockedProcesses = cleaned;

            if (string.IsNullOrEmpty(HotkeyModifiers)) HotkeyModifiers = "Ctrl+Alt";
            if (string.IsNullOrEmpty(HotkeyKey)) HotkeyKey = "Space";
        }

        public bool IsProcessBlocked(string processName)
        {
            if (string.IsNullOrEmpty(processName) || BlockedProcesses == null)
                return false;

            string p = processName.Trim().ToLowerInvariant();
            if (p.EndsWith(".exe"))
            {
                p = p.Substring(0, p.Length - 4);
            }

            for (int i = 0; i < BlockedProcesses.Count; i++)
            {
                string bp = BlockedProcesses[i];
                if (bp.EndsWith(".exe"))
                {
                    bp = bp.Substring(0, bp.Length - 4);
                }
                if (string.Equals(p, bp, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public bool AddBlockedProcess(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            string norm = processName.Trim().ToLowerInvariant();
            if (norm.EndsWith(".exe"))
            {
                norm = norm.Substring(0, norm.Length - 4);
            }
            if (!BlockedProcesses.Contains(norm))
            {
                BlockedProcesses.Add(norm);
                return Save();
            }
            return false;
        }

        public static string GetConfigFilePath()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string localFile = Path.Combine(appDir, "yahcw.config.json");
            if (File.Exists(localFile))
            {
                return localFile;
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userDir = Path.Combine(appData, "YAHCW");
            string userFile = Path.Combine(userDir, "yahcw.config.json");
            if (File.Exists(userFile))
            {
                return userFile;
            }

            return localFile;
        }

        public static Config Load()
        {
            Config cfg = new Config();
            string path = GetConfigFilePath();

            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    cfg = ParseJson(json);
                }
                catch
                {
                    // If reading or parsing fails, fallback to defaults
                    cfg = new Config();
                }
            }

            cfg.ClampAndValidate();
            return cfg;
        }

        public bool Save()
        {
            ClampAndValidate();
            string json = ToJson();

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string primaryPath = Path.Combine(appDir, "yahcw.config.json");

            try
            {
                File.WriteAllText(primaryPath, json, Encoding.UTF8);
                return true;
            }
            catch
            {
                try
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string userDir = Path.Combine(appData, "YAHCW");
                    if (!Directory.Exists(userDir))
                    {
                        Directory.CreateDirectory(userDir);
                    }
                    string userPath = Path.Combine(userDir, "yahcw.config.json");
                    File.WriteAllText(userPath, json, Encoding.UTF8);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendFormat("  \"corner\": \"{0}\",\r\n", Corner);
            sb.AppendFormat("  \"dwellMs\": {0},\r\n", DwellMs);
            sb.AppendFormat("  \"triggerSizePx\": {0},\r\n", TriggerSizePx);
            sb.AppendFormat("  \"rearmDistancePx\": {0},\r\n", RearmDistancePx);
            sb.AppendFormat("  \"cooldownMs\": {0},\r\n", CooldownMs);
            sb.AppendFormat("  \"hotkeyEnabled\": {0},\r\n", HotkeyEnabled ? "true" : "false");
            sb.AppendFormat("  \"hotkeyModifiers\": \"{0}\",\r\n", HotkeyModifiers);
            sb.AppendFormat("  \"hotkeyKey\": \"{0}\",\r\n", HotkeyKey);
            sb.AppendFormat("  \"maxThumbnails\": {0},\r\n", MaxThumbnails);
            sb.AppendFormat("  \"suspendOnGameProcessRunning\": {0},\r\n", SuspendOnGameProcessRunning ? "true" : "false");
            sb.AppendFormat("  \"checkRunningGamesIntervalMs\": {0},\r\n", CheckRunningGamesIntervalMs);
            sb.AppendFormat("  \"isPaused\": {0},\r\n", IsPaused ? "true" : "false");
            sb.AppendLine("  \"blockedProcesses\": [");
            for (int i = 0; i < BlockedProcesses.Count; i++)
            {
                sb.AppendFormat("    \"{0}\"{1}\r\n", BlockedProcesses[i], (i == BlockedProcesses.Count - 1) ? "" : ",");
            }
            sb.AppendLine("  ]");
            sb.Append("}");
            return sb.ToString();
        }

        public static Config ParseJson(string json)
        {
            Config cfg = new Config();
            if (string.IsNullOrWhiteSpace(json)) return cfg;

            try
            {
                // Simple token-based parser for our known fields
                string[] lines = json.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                bool inBlockedList = false;
                List<string> parsedBlocked = new List<string>();

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith("#"))
                        continue;

                    if (inBlockedList)
                    {
                        if (line.Contains("]"))
                        {
                            inBlockedList = false;
                        }
                        else
                        {
                            string item = ExtractStringValue(line);
                            if (!string.IsNullOrEmpty(item))
                            {
                                parsedBlocked.Add(item);
                            }
                        }
                        continue;
                    }

                    if (line.StartsWith("\"blockedProcesses\"", StringComparison.OrdinalIgnoreCase))
                    {
                        inBlockedList = true;
                        if (line.Contains("["))
                        {
                            // If closed on same line: "blockedProcesses": ["a", "b"]
                            if (line.Contains("]"))
                            {
                                inBlockedList = false;
                                int startBracket = line.IndexOf('[');
                                int endBracket = line.LastIndexOf(']');
                                string inner = line.Substring(startBracket + 1, endBracket - startBracket - 1);
                                string[] parts = inner.Split(',');
                                for (int p = 0; p < parts.Length; p++)
                                {
                                    string s = ExtractStringValue(parts[p]);
                                    if (!string.IsNullOrEmpty(s)) parsedBlocked.Add(s);
                                }
                            }
                        }
                        continue;
                    }

                    int colonIndex = line.IndexOf(':');
                    if (colonIndex <= 0) continue;

                    string key = line.Substring(0, colonIndex).Trim().Trim('"');
                    string val = line.Substring(colonIndex + 1).Trim().TrimEnd(',');

                    if (key.Equals("corner", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.Corner = ExtractStringValue(val);
                    }
                    else if (key.Equals("dwellMs", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(ExtractNumber(val), out n)) cfg.DwellMs = n;
                    }
                    else if (key.Equals("triggerSizePx", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(ExtractNumber(val), out n)) cfg.TriggerSizePx = n;
                    }
                    else if (key.Equals("rearmDistancePx", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(ExtractNumber(val), out n)) cfg.RearmDistancePx = n;
                    }
                    else if (key.Equals("cooldownMs", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(ExtractNumber(val), out n)) cfg.CooldownMs = n;
                    }
                    else if (key.Equals("hotkeyEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.HotkeyEnabled = val.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    else if (key.Equals("hotkeyModifiers", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.HotkeyModifiers = ExtractStringValue(val);
                    }
                    else if (key.Equals("hotkeyKey", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.HotkeyKey = ExtractStringValue(val);
                    }
                    else if (key.Equals("maxThumbnails", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(ExtractNumber(val), out n)) cfg.MaxThumbnails = n;
                    }
                    else if (key.Equals("suspendOnGameProcessRunning", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.SuspendOnGameProcessRunning = val.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    else if (key.Equals("checkRunningGamesIntervalMs", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(ExtractNumber(val), out n)) cfg.CheckRunningGamesIntervalMs = n;
                    }
                    else if (key.Equals("isPaused", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.IsPaused = val.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }

                if (parsedBlocked.Count > 0)
                {
                    cfg.BlockedProcesses = parsedBlocked;
                }
            }
            catch
            {
                // Return default on parsing exception
                return new Config();
            }

            cfg.ClampAndValidate();
            return cfg;
        }

        private static string ExtractStringValue(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            int firstQuote = raw.IndexOf('"');
            if (firstQuote >= 0)
            {
                int secondQuote = raw.IndexOf('"', firstQuote + 1);
                if (secondQuote > firstQuote)
                {
                    return raw.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                }
            }
            return raw.Trim().Trim('"', '\'', ',');
        }

        private static string ExtractNumber(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "0";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsDigit(c) || c == '-')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
