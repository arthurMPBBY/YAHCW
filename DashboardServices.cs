using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace YAHCW
{
    public static class DashboardDiagnostics
    {
        public static int Run()
        {
            StringBuilder report = new StringBuilder("Diagnóstico somente leitura: " + DateTimeOffset.Now.ToString("o") + Environment.NewLine);
            int failures = 0;
            try
            {
                foreach (AudioTarget output in AudioOutputs.Read()) report.AppendLine("Áudio: " + output.Name + (output.IsDefault ? " [padrão multimídia]" : ""));
            }
            catch (Exception error) { failures++; report.AppendLine("Áudio indisponível: " + error.GetBaseException().Message); }
            try
            {
                foreach (BrightnessTarget monitor in BrightnessClient.Read()) report.AppendLine("Brilho via BrightTray: " + monitor.Name + " = " + monitor.Percent + "%");
            }
            catch (Exception error) { failures++; report.AppendLine("BrightTray indisponível: " + error.GetBaseException().Message); }
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YAHCW");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "controls-diagnose.txt"), report.ToString(), Encoding.UTF8);
            Console.WriteLine(report.ToString());
            return failures == 0 ? 0 : 1;
        }
    }
    public sealed class BrightnessTarget
    {
        public string Id;
        public string Name;
        public int Percent;
    }

    public static class BrightnessClient
    {
        public static List<BrightnessTarget> Read() { return Request(null, 0); }
        public static List<BrightnessTarget> Set(string id, int percent)
        {
            if (string.IsNullOrEmpty(id) || percent < 0 || percent > 100)
                throw new ArgumentException("Valor de brilho inválido.");
            return Request(id, percent);
        }

        public static List<BrightnessTarget> Parse(string response)
        {
            if (response == null || !response.StartsWith("OK\n", StringComparison.Ordinal))
                throw new IOException(response == null ? "BrightTray sem resposta." : response.Replace("ERR\n", ""));
            List<BrightnessTarget> targets = new List<BrightnessTarget>();
            foreach (string line in response.Substring(3).Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] fields = line.Split('|');
                int percent;
                if (fields.Length != 3 || !int.TryParse(fields[2], out percent) || percent < 0 || percent > 100)
                    throw new IOException("Resposta inválida do BrightTray.");
                targets.Add(new BrightnessTarget { Id = fields[0], Name = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1])), Percent = percent });
            }
            return targets;
        }

        private static List<BrightnessTarget> Request(string id, int percent)
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", "BrightTray.Dashboard.v1", PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(800);
                using (BinaryWriter writer = new BinaryWriter(pipe, Encoding.UTF8, true))
                using (BinaryReader reader = new BinaryReader(pipe, Encoding.UTF8, true))
                {
                    writer.Write(id == null ? 0 : 1);
                    if (id != null) { writer.Write(id); writer.Write(percent); }
                    writer.Flush();
                    Task<string> pending = Task.Run(delegate { return reader.ReadString(); });
                    if (!pending.Wait(2000)) throw new TimeoutException("BrightTray demorou para responder; atualize para conferir o resultado.");
                    return Parse(pending.Result);
                }
            }
        }
    }

    public sealed class AudioTarget
    {
        public string Id;
        public string Name;
        public bool IsDefault;
    }

    public static class AudioOutputs
    {
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Enumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out Collection collection);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out Device device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out Device device);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Collection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out Device device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Device
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, out IntPtr instance);
            [PreserveSig] int OpenPropertyStore(uint access, out Properties properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Properties
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropertyValue { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Text; }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue value);

        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Policy
        {
            void Reserved00(); void Reserved01(); void Reserved02(); void Reserved03(); void Reserved04();
            void Reserved05(); void Reserved06(); void Reserved07(); void Reserved08(); void Reserved09();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        }

        private static Enumerator CreateEnumerator()
        {
            return (Enumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
        }
        private static void Release(object instance)
        {
            if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
        private static string DefaultId(Enumerator enumerator, int role)
        {
            Device device = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0, role, out device) < 0) return null;
                string id;
                Marshal.ThrowExceptionForHR(device.GetId(out id));
                return id;
            }
            finally { Release(device); }
        }

        public static List<AudioTarget> Read()
        {
            Enumerator enumerator = CreateEnumerator();
            Collection collection = null;
            try
            {
                string current = DefaultId(enumerator, 1);
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out collection));
                uint count;
                Marshal.ThrowExceptionForHR(collection.GetCount(out count));
                List<AudioTarget> targets = new List<AudioTarget>();
                for (uint index = 0; index < count; index++)
                {
                    Device device = null;
                    Properties properties = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(collection.Item(index, out device));
                        string id;
                        Marshal.ThrowExceptionForHR(device.GetId(out id));
                        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out properties));
                        PropertyKey key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                        PropertyValue value;
                        Marshal.ThrowExceptionForHR(properties.GetValue(ref key, out value));
                        string name;
                        try { name = value.Type == 31 ? Marshal.PtrToStringUni(value.Text) : null; }
                        finally { PropVariantClear(ref value); }
                        targets.Add(new AudioTarget { Id = id, Name = name ?? "Saída de áudio", IsDefault = id == current });
                    }
                    finally { Release(properties); Release(device); }
                }
                return targets;
            }
            finally { Release(collection); Release(enumerator); }
        }

        internal static void ApplySelection(string id, Func<int, string> readDefault, Func<string, int, int> setDefault)
        {
            string console = readDefault(0);
            string media = readDefault(1);
            if (console == null || media == null)
                throw new InvalidOperationException("Não foi possível consultar as saídas anteriores; nenhuma alteração feita.");
            bool consoleChanged = false;
            bool mediaChanged = false;
            try
            {
                Marshal.ThrowExceptionForHR(setDefault(id, 0));
                consoleChanged = true;
                Marshal.ThrowExceptionForHR(setDefault(id, 1));
                mediaChanged = true;
                if (readDefault(0) != id || readDefault(1) != id)
                    throw new InvalidOperationException("O Windows não confirmou a saída selecionada.");
            }
            catch (Exception failure)
            {
                bool rollbackFailed = false;
                if (mediaChanged) rollbackFailed = setDefault(media, 1) < 0;
                if (consoleChanged) rollbackFailed = setDefault(console, 0) < 0 || rollbackFailed;
                if (rollbackFailed) throw new InvalidOperationException("Falha na troca e na restauração; confira a saída atual no Windows.", failure);
                throw;
            }
        }

        public static void Select(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Saída inválida.");
            Enumerator enumerator = CreateEnumerator();
            Policy policy = null;
            try
            {
                Device target = null;
                try
                {
                    Marshal.ThrowExceptionForHR(enumerator.GetDevice(id, out target));
                    uint state;
                    Marshal.ThrowExceptionForHR(target.GetState(out state));
                    if ((state & 1) == 0) throw new InvalidOperationException("Saída desconectada; atualize a lista.");
                }
                finally { Release(target); }
                policy = (Policy)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")));
                ApplySelection(id, delegate(int role) { return DefaultId(enumerator, role); },
                    delegate(string deviceId, int role) { return policy.SetDefaultEndpoint(deviceId, role); });
            }
            finally { Release(policy); Release(enumerator); }
        }
    }
}
