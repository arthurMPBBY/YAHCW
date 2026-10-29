using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace YAHCW
{
    public static class Diagnostics
    {
        public static void Run()
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("      YAHCW - DIAGNÓSTICO DO SISTEMA E GUARDAS   ");
            Console.WriteLine("=================================================");
            Console.WriteLine();

            Config cfg = Config.Load();
            Console.WriteLine("[Última tentativa da instância na bandeja]");
            Console.WriteLine(ActivationStatus.ReadLast());
            Console.WriteLine();
            RunningGameCache cache = new RunningGameCache();

            // 1. Environment & OS
            Console.WriteLine("[Ambiente]");
            Console.WriteLine(string.Format("  OS: {0} ({1})", Environment.OSVersion, Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
            Console.WriteLine(string.Format("  CLR: {0}", Environment.Version));
            Console.WriteLine(string.Format("  Monitores detectados: {0}", Screen.AllScreens.Length));
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                Screen s = Screen.AllScreens[i];
                Console.WriteLine(string.Format("    Monitor {0}: {1}x{2} em ({3},{4}) | Área útil: {5}x{6} | Principal: {7}",
                    i, s.Bounds.Width, s.Bounds.Height, s.Bounds.Left, s.Bounds.Top,
                    s.WorkingArea.Width, s.WorkingArea.Height, s.Primary));
            }
            Console.WriteLine();

            // 2. Shell Notification State
            Console.WriteLine("[Estado Shell (SHQueryUserNotificationState)]");
            int qState;
            int hr = NativeMethods.SHQueryUserNotificationState(out qState);
            string qStateDesc;
            switch (qState)
            {
                case SafetyGuards.QUNS_NOT_PRESENT: qStateDesc = "QUNS_NOT_PRESENT (Ausente / Bloqueado)"; break;
                case SafetyGuards.QUNS_BUSY: qStateDesc = "QUNS_BUSY (Ocupado / Tela cheia exclusiva)"; break;
                case SafetyGuards.QUNS_RUNNING_D3D_FULL_SCREEN: qStateDesc = "QUNS_RUNNING_D3D_FULL_SCREEN (Jogo 3D / DirectX tela cheia)"; break;
                case SafetyGuards.QUNS_PRESENTATION_MODE: qStateDesc = "QUNS_PRESENTATION_MODE (Modo apresentação)"; break;
                case SafetyGuards.QUNS_ACCEPTS_NOTIFICATIONS: qStateDesc = "QUNS_ACCEPTS_NOTIFICATIONS (Normal / Notificações ativas)"; break;
                case SafetyGuards.QUNS_QUIET_HOURS: qStateDesc = "QUNS_QUIET_HOURS (Foco assistido)"; break;
                case SafetyGuards.QUNS_APP: qStateDesc = "QUNS_APP (App imersivo tela cheia)"; break;
                default: qStateDesc = "Desconhecido (" + qState + ")"; break;
            }
            Console.WriteLine(string.Format("  HRESULT: 0x{0:X8} | Código: {1} ({2})", hr, qState, qStateDesc));
            Console.WriteLine();

            // 3. Foreground Window
            Console.WriteLine("[Janela em Primeiro Plano]");
            IntPtr hFg = NativeMethods.GetForegroundWindow();
            if (hFg == IntPtr.Zero)
            {
                Console.WriteLine("  Nenhuma janela em primeiro plano encontrada.");
            }
            else
            {
                StringBuilder sbTitle = new StringBuilder(256);
                NativeMethods.GetWindowText(hFg, sbTitle, 256);
                StringBuilder sbClass = new StringBuilder(256);
                NativeMethods.GetClassName(hFg, sbClass, 256);

                uint pid;
                NativeMethods.GetWindowThreadProcessId(hFg, out pid);
                string procName = "Desconhecido";
                if (pid != 0)
                {
                    try
                    {
                        using (Process p = Process.GetProcessById((int)pid))
                        {
                            procName = p.ProcessName;
                        }
                    }
                    catch { }
                }

                RECT rcWin;
                NativeMethods.GetWindowRect(hFg, out rcWin);

                Screen fgScreen = Screen.FromHandle(hFg);
                bool isFs = SafetyGuards.IsFullscreenGeometryTest(rcWin,
                    new RECT(fgScreen.Bounds.Left, fgScreen.Bounds.Top, fgScreen.Bounds.Right, fgScreen.Bounds.Bottom),
                    new RECT(fgScreen.WorkingArea.Left, fgScreen.WorkingArea.Top, fgScreen.WorkingArea.Right, fgScreen.WorkingArea.Bottom));

                Console.WriteLine(string.Format("  HWND: 0x{0:X8}", hFg.ToInt64()));
                Console.WriteLine(string.Format("  Título: \"{0}\"", sbTitle));
                Console.WriteLine(string.Format("  Classe: \"{0}\"", sbClass));
                Console.WriteLine(string.Format("  Processo: \"{0}\" (PID {1})", procName, pid));
                Console.WriteLine(string.Format("  Dimensões: {0}x{1} em ({2},{3})", rcWin.Width, rcWin.Height, rcWin.Left, rcWin.Top));
                Console.WriteLine(string.Format("  Cobre monitor totalmente (Tela cheia): {0}", isFs));
            }
            Console.WriteLine();

            // 4. Mouse and Cursor Status
            Console.WriteLine("[Mouse e Cursor]");
            POINT ptCursor;
            NativeMethods.GetCursorPos(out ptCursor);
            Screen curScreen = Screen.FromPoint(ptCursor.ToPoint());
            Point cornerPt = HotCornerDetector.GetCornerPoint(curScreen, cfg.Corner);
            double dist = HotCornerDetector.CalculateDistance(ptCursor.ToPoint(), cornerPt);

            short lBtn = NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);
            short rBtn = NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON);
            short mBtn = NativeMethods.GetAsyncKeyState(NativeMethods.VK_MBUTTON);
            bool btnsPressed = ((lBtn & 0x8000) != 0 || (rBtn & 0x8000) != 0 || (mBtn & 0x8000) != 0);

            RECT clip;
            NativeMethods.GetClipCursor(out clip);

            Console.WriteLine(string.Format("  Posição do cursor: ({0}, {1}) no Monitor {2}", ptCursor.X, ptCursor.Y, curScreen.DeviceName));
            Console.WriteLine(string.Format("  Canto configurado ({0}): ({1}, {2})", cfg.Corner, cornerPt.X, cornerPt.Y));
            Console.WriteLine(string.Format("  Distância até o canto: {0:F1}px", dist));
            Console.WriteLine(string.Format("  Botões pressionados: {0}", btnsPressed ? "SIM (arrasto/clique detectado)" : "Não"));
            Console.WriteLine(string.Format("  Área do ClipCursor: {0}x{1} em ({2},{3})", clip.Width, clip.Height, clip.Left, clip.Top));
            Console.WriteLine();

            // 5. Game Scanner
            Console.WriteLine("[Varredura de Jogos Bloqueados]");
            string detectedRunningGame = cache.CheckRunningGameProcesses(cfg, true);
            if (!string.IsNullOrEmpty(detectedRunningGame))
            {
                Console.WriteLine(string.Format("  ALERTA: Processo de jogo detectado em execução: {0}", detectedRunningGame));
            }
            else
            {
                Console.WriteLine("  Nenhum processo da lista de jogos detectado em execução no momento.");
            }
            Console.WriteLine(string.Format("  Total de processos na denylist: {0}", cfg.BlockedProcesses.Count));
            Console.WriteLine();

            // 6. Guards Evaluation Summary
            Console.WriteLine("[Avaliação Geral das Guardas]");
            GuardResult overallGuard = SafetyGuards.EvaluateAllGuards(curScreen, cfg, cfg.IsPaused, IntPtr.Zero, cache);
            Console.WriteLine(string.Format("  Status: {0}", overallGuard.IsAllowed ? "ATIVO / PERMITIDO" : "BLOQUEADO"));
            Console.WriteLine(string.Format("  Motivo: {0}", overallGuard.Reason));
            Console.WriteLine();

            // 7. AltTab Eligible Windows List
            Console.WriteLine("[Janelas Elegíveis para Visão Geral (AltTab)]");
            List<WindowItem> windows = WindowEnumerator.EnumerateEligibleWindows(IntPtr.Zero, cfg.MaxThumbnails);
            Console.WriteLine(string.Format("  Total elegíveis: {0}", windows.Count));
            for (int i = 0; i < windows.Count; i++)
            {
                WindowItem w = windows[i];
                Console.WriteLine(string.Format("  [{0:D2}] HWND: 0x{1:X8} | Proc: {2,-18} | Min: {3,-5} | \"{4}\"",
                    i + 1, w.Handle.ToInt64(), w.ProcessName, w.IsMinimized, w.Title));
                w.Dispose();
            }
            Console.WriteLine();

            // 8. JSON Export summary
            Console.WriteLine("[Resumo JSON]");
            StringBuilder json = new StringBuilder();
            json.AppendLine("{");
            json.AppendFormat("  \"guardsAllowed\": {0},\r\n", overallGuard.IsAllowed ? "true" : "false");
            json.AppendFormat("  \"guardsReason\": \"{0}\",\r\n", overallGuard.Reason.Replace("\"", "\\\""));
            json.AppendFormat("  \"qunsState\": {0},\r\n", qState);
            json.AppendFormat("  \"qunsDescription\": \"{0}\",\r\n", qStateDesc);
            json.AppendFormat("  \"mouseButtonsHeld\": {0},\r\n", btnsPressed ? "true" : "false");
            json.AppendFormat("  \"detectedRunningGame\": {0},\r\n", string.IsNullOrEmpty(detectedRunningGame) ? "null" : "\"" + detectedRunningGame + "\"");
            json.AppendFormat("  \"eligibleWindowsCount\": {0},\r\n", windows.Count);
            json.AppendFormat("  \"corner\": \"{0}\",\r\n", cfg.Corner);
            json.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "  \"distanceToCornerPx\": {0:F1}\r\n", dist);
            json.Append("}");
            Console.WriteLine(json.ToString());
            Console.WriteLine("=================================================");
        }
    }
}
