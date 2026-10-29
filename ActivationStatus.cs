using System;
using System.IO;
using System.Text;

namespace YAHCW
{
    public static class ActivationStatus
    {
        private static string _last;

        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YAHCW", "activation-status.txt"); }
        }

        public static void Record(string status)
        {
            if (string.Equals(status, _last, StringComparison.Ordinal)) return;
            _last = status;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, DateTimeOffset.Now.ToString("o") + Environment.NewLine + status, Encoding.UTF8);
            }
            catch { }
        }

        public static string ReadLast()
        {
            try { return File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : "Nenhuma tentativa registrada."; }
            catch { return "Não foi possível ler o último estado."; }
        }
    }
}
