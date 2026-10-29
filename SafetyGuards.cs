using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace YAHCW
{
    public static class MonotonicTime
    {
        private static readonly Stopwatch _watch = Stopwatch.StartNew();

        public static long NowMs
        {
            get { return _watch.ElapsedMilliseconds; }
        }
    }

    public class GuardResult
    {
        public bool IsAllowed { get; private set; }
        public string Reason { get; private set; }

        private GuardResult(bool isAllowed, string reason)
        {
            IsAllowed = isAllowed;
            Reason = reason ?? string.Empty;
        }

        public static GuardResult Allow()
        {
            return new GuardResult(true, "Acesso permitido.");
        }

        public static GuardResult Block(string reason)
        {
            return new GuardResult(false, reason);
        }

        public override string ToString()
        {
            return IsAllowed ? "PERMITIDO: " + Reason : "BLOQUEADO: " + Reason;
        }
    }

    public class RunningGameCache
    {
        private long _lastCheckMs = 0;
        private string _lastDetectedGame = null;
        private string _lastError = null;
        private string _simulatedError = null;
        private readonly object _lock = new object();

        public string LastError
        {
            get { lock (_lock) { return _lastError; } }
        }

        public string LastDetectedGame
        {
            get { lock (_lock) { return _lastDetectedGame; } }
        }

        public void SetSimulatedError(string error)
        {
            lock (_lock)
            {
                _simulatedError = error;
                _lastError = error;
            }
        }

        public string CheckRunningGameProcesses(Config config, bool forceRefresh)
        {
            if (config == null || !config.SuspendOnGameProcessRunning || config.BlockedProcesses == null || config.BlockedProcesses.Count == 0)
            {
                lock (_lock)
                {
                    _lastError = null;
                    _lastDetectedGame = null;
                }
                return null;
            }

            lock (_lock)
            {
                if (!string.IsNullOrEmpty(_simulatedError))
                {
                    _lastError = _simulatedError;
                    return null;
                }

                long now = MonotonicTime.NowMs;
                if (!forceRefresh && _lastCheckMs != 0 && (now - _lastCheckMs) < config.CheckRunningGamesIntervalMs)
                {
                    return _lastDetectedGame;
                }

                _lastCheckMs = now;
                _lastDetectedGame = null;
                _lastError = null;

                Process[] procs = null;
                try
                {
                    procs = Process.GetProcesses();
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    return null;
                }

                if (procs != null)
                {
                    try
                    {
                        for (int i = 0; i < procs.Length; i++)
                        {
                            Process p = procs[i];
                            if (p == null) continue;

                            try
                            {
                                if (_lastDetectedGame == null)
                                {
                                    string pName = p.ProcessName;
                                    if (config.IsProcessBlocked(pName))
                                    {
                                        _lastDetectedGame = pName;
                                    }
                                }
                            }
                            catch
                            {
                                // Inaccessible process name (system/protected), continue inspecting
                            }
                            finally
                            {
                                try { p.Dispose(); } catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.Message;
                    }
                    finally
                    {
                        // Ensure any remaining processes in the array are disposed
                        for (int i = 0; i < procs.Length; i++)
                        {
                            try
                            {
                                if (procs[i] != null)
                                {
                                    procs[i].Dispose();
                                }
                            }
                            catch { }
                        }
                    }
                }

                return _lastDetectedGame;
            }
        }
    }

    public static class SafetyGuards
    {
        public const int QUNS_NOT_PRESENT = 1;
        public const int QUNS_BUSY = 2;
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        public const int QUNS_PRESENTATION_MODE = 4;
        public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
        public const int QUNS_QUIET_HOURS = 6;
        public const int QUNS_APP = 7;

        public static GuardResult EvaluateAllGuards(
            Screen screen,
            Config config,
            bool isPaused,
            IntPtr ownFormHandle,
            RunningGameCache cache,
            bool forceFreshGameScan = false)
        {
            try
            {
                // Guard 1: Manual Pause Backstop
                GuardResult rPause = CheckManualPause(isPaused);
                if (!rPause.IsAllowed) return rPause;

                // Guard 2: Input Desktop / Session Lock
                GuardResult rInputDesk = CheckInputDesktop();
                if (!rInputDesk.IsAllowed) return rInputDesk;

                // Guard 3: Mouse Buttons Held / Dragging (including XButtons)
                GuardResult rMouse = CheckMouseButtons();
                if (!rMouse.IsAllowed) return rMouse;

                // Guard 4: Shell User Notification State (Direct3D, Busy, Presentation, App, etc.)
                GuardResult rQuns = CheckShellNotificationState();
                if (!rQuns.IsAllowed) return rQuns;

                // Guard 5: Running game processes denylist (with forced fresh scan if requested)
                GuardResult rRunningGame = CheckRunningGames(config, cache, forceFreshGameScan);
                if (!rRunningGame.IsAllowed) return rRunningGame;

                // Guard 6: Foreground Process Denylist
                GuardResult rFgProc = CheckForegroundProcess(config);
                if (!rFgProc.IsAllowed) return rFgProc;



                // Guard 8: Cursor clipping (against virtual screen)
                GuardResult rClip = CheckCursorClipping(screen);
                if (!rClip.IsAllowed) return rClip;

                return GuardResult.Allow();
            }
            catch (Exception ex)
            {
                // Fail-closed guarantee
                return GuardResult.Block("Exceção na validação de segurança (fail-closed): " + ex.Message);
            }
        }

        public static GuardResult CheckManualPause(bool isPaused)
        {
            if (isPaused)
            {
                return GuardResult.Block("YAHCW está pausado manualmente via ícone da bandeja.");
            }
            return GuardResult.Allow();
        }

        public static GuardResult CheckInputDesktop()
        {
            try
            {
                IntPtr hDesktop = NativeMethods.OpenInputDesktop(0, false, 0x0001);
                if (hDesktop == IntPtr.Zero)
                {
                    return GuardResult.Block("Área de trabalho de entrada inacessível ou sessão bloqueada/UAC (OpenInputDesktop falhou) (fail-closed).");
                }

                try
                {
                    IntPtr threadDesktop = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
                    StringBuilder inputName = new StringBuilder(256);
                    StringBuilder threadName = new StringBuilder(256);
                    uint needed;
                    bool inputRead = NativeMethods.GetUserObjectInformationW(hDesktop, 2, inputName, (uint)inputName.Capacity * 2, out needed);
                    bool threadRead = threadDesktop != IntPtr.Zero && NativeMethods.GetUserObjectInformationW(threadDesktop, 2, threadName, (uint)threadName.Capacity * 2, out needed);
                    return EvaluateDesktopIdentity(inputRead && threadRead, inputName.ToString(), threadName.ToString());
                }
                finally
                {
                    NativeMethods.CloseDesktop(hDesktop);
                }
            }
            catch (Exception ex)
            {
                return GuardResult.Block("Exceção ao validar área de trabalho de entrada (fail-closed): " + ex.Message);
            }
        }

        public static GuardResult CheckMouseButtons()
        {
            try
            {
                short l = NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);
                short r = NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON);
                short m = NativeMethods.GetAsyncKeyState(NativeMethods.VK_MBUTTON);
                short x1 = NativeMethods.GetAsyncKeyState(NativeMethods.VK_XBUTTON1);
                short x2 = NativeMethods.GetAsyncKeyState(NativeMethods.VK_XBUTTON2);

                if ((l & 0x8000) != 0 || (r & 0x8000) != 0 || (m & 0x8000) != 0 ||
                    (x1 & 0x8000) != 0 || (x2 & 0x8000) != 0)
                {
                    return GuardResult.Block("Botão do mouse pressionado ou operação de arrasto em andamento.");
                }
                return GuardResult.Allow();
            }
            catch (Exception ex)
            {
                return GuardResult.Block("Falha ao checar botões do mouse (fail-closed): " + ex.Message);
            }
        }

        public static GuardResult EvaluateDesktopIdentity(bool readSucceeded, string inputName, string threadName)
        {
            if (!readSucceeded || string.IsNullOrEmpty(inputName) || string.IsNullOrEmpty(threadName))
                return GuardResult.Block("Não foi possível confirmar o desktop de entrada (fail-closed).");
            if (!string.Equals(inputName, threadName, StringComparison.OrdinalIgnoreCase))
                return GuardResult.Block("Desktop de entrada diferente: sessão bloqueada, UAC ou troca de usuário.");
            return GuardResult.Allow();
        }

        public static GuardResult EvaluateShellNotificationState(int hr, int qState)
        {
            if (hr != 0)
            {
                return GuardResult.Block(string.Format("Falha ao consultar SHQueryUserNotificationState (HRESULT 0x{0:X8}) (fail-closed).", hr));
            }

            switch (qState)
            {
                case QUNS_RUNNING_D3D_FULL_SCREEN:
                case QUNS_BUSY:
                case QUNS_PRESENTATION_MODE:
                case QUNS_NOT_PRESENT:
                case QUNS_APP:
                case QUNS_ACCEPTS_NOTIFICATIONS:
                case QUNS_QUIET_HOURS:
                    return GuardResult.Allow();
                default:
                    return GuardResult.Block(string.Format("Estado de notificação shell desconhecido ou restritivo ({0}) (fail-closed).", qState));
            }
        }

        public static GuardResult CheckShellNotificationState()
        {
            try
            {
                int qState;
                int hr = NativeMethods.SHQueryUserNotificationState(out qState);
                return EvaluateShellNotificationState(hr, qState);
            }
            catch (Exception ex)
            {
                return GuardResult.Block("Exceção ao consultar estado de notificação shell (fail-closed): " + ex.Message);
            }
        }

        public static GuardResult CheckRunningGames(Config config, RunningGameCache cache, bool forceRefresh = false)
        {
            if (config == null || cache == null || !config.SuspendOnGameProcessRunning)
            {
                return GuardResult.Allow();
            }

            try
            {
                string game = cache.CheckRunningGameProcesses(config, forceRefresh);
                if (!string.IsNullOrEmpty(cache.LastError))
                {
                    return GuardResult.Block("Falha ao enumerar processos em execução (fail-closed): " + cache.LastError);
                }
                if (!string.IsNullOrEmpty(game))
                {
                    return GuardResult.Block(string.Format("Jogo bloqueado em execução no sistema: {0}.", game));
                }
                return GuardResult.Allow();
            }
            catch (Exception ex)
            {
                return GuardResult.Block("Exceção ao checar lista de processos de jogos (fail-closed): " + ex.Message);
            }
        }

        public static GuardResult CheckForegroundProcess(Config config)
        {
            try
            {
                IntPtr hFg = NativeMethods.GetForegroundWindow();
                if (hFg == IntPtr.Zero)
                {
                    return GuardResult.Block("Nenhuma janela em primeiro plano identificada ou área de trabalho inacessível (fail-closed).");
                }

                uint pid;
                NativeMethods.GetWindowThreadProcessId(hFg, out pid);
                if (pid == 0)
                {
                    return GuardResult.Block("Identificador de processo da janela em primeiro plano é inválido (PID 0) (fail-closed).");
                }

                if (config == null || config.BlockedProcesses == null || config.BlockedProcesses.Count == 0)
                {
                    return GuardResult.Allow();
                }

                using (Process proc = Process.GetProcessById((int)pid))
                {
                    string name = proc.ProcessName;
                    if (config.IsProcessBlocked(name))
                    {
                        return GuardResult.Block(string.Format("Janela em primeiro plano pertence a processo de jogo bloqueado: {0}.", name));
                    }
                }
                return GuardResult.Allow();
            }
            catch (Exception ex)
            {
                return GuardResult.Block("Exceção ao verificar processo em primeiro plano (fail-closed): " + ex.Message);
            }
        }

        public static bool IsFullscreenGeometry(RECT rcWin, RECT rcMonitor, RECT rcWork)
        {
            bool coversMonitor = (rcWin.Left <= rcMonitor.Left &&
                                  rcWin.Top <= rcMonitor.Top &&
                                  rcWin.Right >= rcMonitor.Right &&
                                  rcWin.Bottom >= rcMonitor.Bottom);

            if (!coversMonitor)
            {
                return false;
            }

            bool isWorkAreaSameAsMonitor = (rcWork.Left == rcMonitor.Left &&
                                            rcWork.Top == rcMonitor.Top &&
                                            rcWork.Right == rcMonitor.Right &&
                                            rcWork.Bottom == rcMonitor.Bottom);

            // Suppress fullscreen even when workarea == monitor (e.g. auto-hidden taskbar or borderless screen)
            if (isWorkAreaSameAsMonitor)
            {
                return true;
            }

            // When workarea != monitor (docked taskbar), window must extend into or cover the non-workarea region
            bool largerThanWorkArea = (rcWin.Height > rcWork.Height || rcWin.Width > rcWork.Width);
            return largerThanWorkArea;
        }

        public static bool IsFullscreenGeometryTest(RECT rcWin, RECT rcMonitor, RECT rcWork)
        {
            return IsFullscreenGeometry(rcWin, rcMonitor, rcWork);
        }

        public static bool IsRegularMaximizedWindow(bool zoomed, long style)
        {
            return zoomed && (style & 0x00C00000L) == 0x00C00000L;
        }

        public static GuardResult CheckFullscreenGeometry(Screen targetScreen, IntPtr ownFormHandle)
        {
            try
            {
                IntPtr hFg = NativeMethods.GetForegroundWindow();
                if (hFg == IntPtr.Zero)
                {
                    return GuardResult.Block("Nenhuma janela em primeiro plano identificada para teste de tela cheia (fail-closed).");
                }

                if (ownFormHandle != IntPtr.Zero && hFg == ownFormHandle)
                {
                    return GuardResult.Block("A visão geral já está aberta.");
                }

                IntPtr hShell = NativeMethods.GetShellWindow();
                IntPtr hDesktop = NativeMethods.GetDesktopWindow();
                if (hFg == hShell || hFg == hDesktop)
                {
                    return GuardResult.Allow();
                }

                StringBuilder sbClass = new StringBuilder(256);
                NativeMethods.GetClassName(hFg, sbClass, 256);
                string cls = sbClass.ToString();
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                {
                    return GuardResult.Allow();
                }

                // Get monitor of foreground window
                IntPtr hMon = NativeMethods.MonitorFromWindow(hFg, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (hMon == IntPtr.Zero)
                {
                    return GuardResult.Block("Falha ao identificar monitor da janela em primeiro plano (MonitorFromWindow) (fail-closed).");
                }

                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!NativeMethods.GetMonitorInfo(hMon, ref mi))
                {
                    return GuardResult.Block("Falha ao consultar informações do monitor (GetMonitorInfo) (fail-closed).");
                }

                RECT rcWin;
                if (!NativeMethods.GetWindowRect(hFg, out rcWin))
                {
                    return GuardResult.Block("Falha ao obter dimensões da janela em primeiro plano (GetWindowRect) (fail-closed).");
                }

                long style = NativeMethods.GetWindowLongPtr(hFg, NativeMethods.GWL_STYLE).ToInt64();
                if (!IsRegularMaximizedWindow(NativeMethods.IsZoomed(hFg), style) && IsFullscreenGeometry(rcWin, mi.rcMonitor, mi.rcWork))
                {
                    return GuardResult.Block(string.Format("Janela em primeiro plano ({0}) cobre totalmente o monitor em tela cheia.", cls));
                }

                return GuardResult.Allow();
            }
            catch (Exception ex)
            {
                return GuardResult.Block("Exceção ao avaliar geometria de tela cheia (fail-closed): " + ex.Message);
            }
        }

        public static bool IsCursorClippedAgainstVirtualScreen(RECT clip, Rectangle vs)
        {
            return (clip.Left > vs.Left ||
                    clip.Top > vs.Top ||
                    clip.Right < vs.Right ||
                    clip.Bottom < vs.Bottom);
        }

        public static GuardResult CheckCursorClipping(Screen screen)
        {
            try
            {
                RECT clip;
                if (!NativeMethods.GetClipCursor(out clip))
                {
                    return GuardResult.Block("Falha ao consultar recorte do cursor (GetClipCursor) (fail-closed).");
                }

                Rectangle virtualScreen = SystemInformation.VirtualScreen;

                // An unconfined cursor in Windows has a clip rectangle covering the entire virtual screen.
                // If the clip rectangle is restricted against the virtual screen, the cursor is trapped/clipped.
                if (IsCursorClippedAgainstVirtualScreen(clip, virtualScreen))
                {
                    return GuardResult.Block(string.Format("Cursor aprisionado em área restrita ({0},{1} - {2}x{3}) menor que a tela virtual ({4},{5} - {6}x{7}).",
                        clip.Left, clip.Top, clip.Width, clip.Height,
                        virtualScreen.Left, virtualScreen.Top, virtualScreen.Width, virtualScreen.Height));
                }

                return GuardResult.Allow();
            }
            catch (Exception ex)
            {
                return GuardResult.Block("Exceção ao checar aprisionamento de cursor (fail-closed): " + ex.Message);
            }
        }
    }
}
