using System;
using System.Drawing;
using System.Windows.Forms;

namespace YAHCW
{
    public static class SelfTest
    {
        private static int _passedCount = 0;
        private static int _failedCount = 0;

        public static int RunAllTests()
        {
            _passedCount = 0;
            _failedCount = 0;
            RunTest("Dois cantos compartilham ações sem acumular permanência", Test_SecondaryCornerAndDwellIsolation);
            RunTest("Protocolo de brilho valida nome e faixa", Test_BrightnessProtocol);
            RunTest("Áudio preserva comunicações e restaura falha parcial", Test_AudioRolesAndRollback);

            Console.WriteLine("=================================================");
            Console.WriteLine("  YAHCW - SU\u00cdTE DE AUTO-TESTES DETERMINISTAS     ");
            Console.WriteLine("=================================================");
            Console.WriteLine();

            RunTest("Flick vs Dwell (Movimento r\u00e1pido vs espera cont\u00ednua)", Test_FlickVsDwell);
            RunTest("Bloqueio por bot\u00f5es do mouse e XButtons (Drag Safety)", Test_HeldButtonsAndXButtons);
            RunTest("Mudan\u00e7a de janela em primeiro plano cancela Dwell", Test_ForegroundIdentityChangeCancelsDwell);
            RunTest("In\u00edcio desarmado e supress\u00e3o exige rearmamento", Test_StartDisarmedAndSuppressionRequiresRearm);
            RunTest("Dist\u00e2ncia de rearmamento e Cooldown anti-repeti\u00e7\u00e3o", Test_RearmDistanceAndCooldown);
            RunTest("Geometria de tela cheia (comum vs barra auto-oculta)", Test_FullscreenGeometry_AutoHiddenTaskbar);
            RunTest("Detec\u00e7\u00e3o de aprisionamento de cursor contra tela virtual", Test_CursorClippingMath);
            RunTest("Valida\u00e7\u00e3o e clamping de configura\u00e7\u00f5es", Test_ConfigClampingAndValidation);
            RunTest("Resili\u00eancia a JSON corrompido/malformado", Test_MalformedJsonResilience);
            RunTest("Denylist de jogos e remo\u00e7\u00e3o de launchers normais", Test_ProcessDenylistMatching);
            RunTest("Decis\u00f5es de guardas de seguran\u00e7a Fail-Closed", Test_SharedGuardDecisionFailClosed);
            RunTest("Maximizada com moldura vs janela sem bordas", Test_RegularMaximized);
            RunTest("Retorno r\u00e1pido ao canto n\u00e3o exige segunda sa\u00edda", Test_QuickReturnDuringCooldown);
            RunTest("Pequeno tremor do cursor preserva perman\u00eancia", Test_DwellJitter);
            RunTest("Identidade do desktop falha de forma segura", Test_DesktopIdentity);
            RunTest("Ativa\u00e7\u00e3o pendente preserva vis\u00e3o geral quando foco \u00e9 negado", Test_PendingActivation_PreservedOnDeniedFocus);
            RunTest("Ativa\u00e7\u00e3o pendente fecha ao mudar para janela n\u00e3o relacionada", Test_PendingActivation_DismissesOnUnrelatedFocus);
            RunTest("Ativa\u00e7\u00e3o pendente transita para normal ao receber foco", Test_PendingActivation_TransitionsOnAcquiredFocus);
            RunTest("Promo\u00e7\u00e3o de estilo e Z-order nativo TopMost", Test_NativeTopMost_StyleAndPromotion);
            RunTest("Regress\u00e3o de Z-order real com duas janelas de teste", Test_NativeZOrder_TwoWindowsRegression);

            Console.WriteLine();
            Console.WriteLine("-------------------------------------------------");
            Console.WriteLine(string.Format("Resultado final: {0} aprovados, {1} falhas.", _passedCount, _failedCount));
            Console.WriteLine("=================================================");

            return _failedCount == 0 ? 0 : 1;
        }

        private static void Test_SecondaryCornerAndDwellIsolation()
        {
            Screen screen = Screen.PrimaryScreen;
            Config config = new Config();
            HotCornerDetector detector = new HotCornerDetector();
            Point top = HotCornerDetector.GetCornerPoint(screen, "TopLeft");
            Point bottom = HotCornerDetector.GetCornerPoint(screen, "BottomRight");
            Assert(HotCornerDetector.GetActivationCorner(screen, config, bottom) == bottom, "Canto inferior direito deve ser ativável");
            detector.ResetToArmed();
            detector.ProcessTick(top, 1000, screen, config, true);
            HotCornerAction moved = detector.ProcessTick(bottom, 1000 + config.DwellMs, screen, config, true);
            Assert(moved.ActionType != CornerActionType.Trigger, "Permanência não pode migrar entre cantos");
            HotCornerAction held = detector.ProcessTick(bottom, 1000 + config.DwellMs * 2, screen, config, true);
            Assert(held.ActionType == CornerActionType.Trigger, "Canto inferior direito deve abrir a mesma visão geral");
            detector.NotifyOverviewClosed(2000);
            detector.ProcessTick(bottom, 3000, screen, config, true);
            Assert(detector.State == CornerState.Rearming, "Ponteiro mantido no canto não deve reabrir");
        }

        private static void Test_BrightnessProtocol()
        {
            string name = "Monitor 24L | teste";
            string wire = "OK\nabc|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(name)) + "|45\n";
            System.Collections.Generic.List<BrightnessTarget> parsed = BrightnessClient.Parse(wire);
            Assert(parsed.Count == 1 && parsed[0].Name == name && parsed[0].Percent == 45, "Resposta de brilho deve preservar nome e valor");
            bool rejected = false;
            try { BrightnessClient.Parse("OK\nabc|eA==|120\n"); } catch (System.IO.IOException) { rejected = true; }
            Assert(rejected, "Valor de brilho fora da faixa deve ser rejeitado");
        }

        private static void Test_AudioRolesAndRollback()
        {
            string[] roles = new string[] { "old-console", "old-media", "communications" };
            AudioOutputs.ApplySelection("headset", delegate(int role) { return roles[role]; },
                delegate(string id, int role) { roles[role] = id; return 0; });
            Assert(roles[0] == "headset" && roles[1] == "headset" && roles[2] == "communications", "Troca não deve alterar comunicações");
            roles[0] = "old-console"; roles[1] = "old-media";
            bool failed = false;
            try
            {
                AudioOutputs.ApplySelection("24L", delegate(int role) { return roles[role]; },
                    delegate(string id, int role)
                    {
                        if (id == "24L" && role == 1) return unchecked((int)0x80004005);
                        roles[role] = id; return 0;
                    });
            }
            catch (System.Runtime.InteropServices.COMException) { failed = true; }
            Assert(failed && roles[0] == "old-console" && roles[1] == "old-media", "Falha parcial deve restaurar a saída anterior");
        }

        private static void RunTest(string testName, Action testMethod)
        {
            try
            {
                testMethod();
                _passedCount++;
                Console.WriteLine(string.Format("[PASS] {0}", testName));
            }
            catch (Exception ex)
            {
                _failedCount++;
                Console.WriteLine(string.Format("[FAIL] {0}: {1}", testName, ex.Message));
            }
        }

        private static void Test_RegularMaximized()
        {
            Assert(SafetyGuards.IsRegularMaximizedWindow(true, 0x00CF0000), "Janela maximizada com legenda deve continuar utiliz\u00e1vel.");
            Assert(!SafetyGuards.IsRegularMaximizedWindow(true, 0x80000000), "Janela sem bordas n\u00e3o deve receber exce\u00e7\u00e3o.");
            Assert(!SafetyGuards.IsRegularMaximizedWindow(false, 0x00CF0000), "Janela n\u00e3o maximizada n\u00e3o deve receber exce\u00e7\u00e3o.");
        }

        private static void Test_QuickReturnDuringCooldown()
        {
            Config config = new Config();
            HotCornerDetector detector = new HotCornerDetector();
            Screen screen = Screen.PrimaryScreen;
            detector.ProcessTick(new Point(100, 100), 0, screen, config, true);
            detector.ProcessTick(new Point(0, 0), 10, screen, config, true);
            detector.ProcessTick(new Point(0, 0), 200, screen, config, true);
            detector.NotifyOverviewClosed(210);
            detector.ProcessTick(new Point(60, 60), 250, screen, config, true);
            HotCornerAction early = detector.ProcessTick(new Point(0, 0), 300, screen, config, true);
            Assert(early.ActionType != CornerActionType.Trigger, "Cooldown n\u00e3o pode ser ignorado.");
            detector.ProcessTick(new Point(0, 0), 1000, screen, config, true);
            HotCornerAction retry = detector.ProcessTick(new Point(0, 0), 1200, screen, config, true);
            Assert(retry.ActionType == CornerActionType.Trigger, "Uma sa\u00edda deliberada j\u00e1 deve rearmar; voltar durante cooldown n\u00e3o pode travar o canto.");
        }

        private static void Test_DwellJitter()
        {
            Config config = new Config();
            HotCornerDetector detector = new HotCornerDetector();
            Screen screen = Screen.PrimaryScreen;
            detector.ProcessTick(new Point(100, 100), 0, screen, config, true);
            detector.ProcessTick(new Point(0, 0), 10, screen, config, true);
            detector.ProcessTick(new Point(4, 3), 100, screen, config, true);
            Assert(detector.State == CornerState.Dwelling, "Tremor pequeno n\u00e3o pode apagar a inten\u00e7\u00e3o de ativa\u00e7\u00e3o.");
            HotCornerAction action = detector.ProcessTick(new Point(4, 3), 200, screen, config, true);
            Assert(action.ActionType == CornerActionType.Trigger, "Dwell deve completar mesmo com tremor pequeno.");
            detector.ResetToArmed();
            detector.ProcessTick(new Point(0, 0), 300, screen, config, true);
            detector.ProcessTick(new Point(4, 3), 350, screen, config, false);
            Assert(detector.State == CornerState.Disarmed, "Toler\u00e2ncia de tremor n\u00e3o pode ignorar guardas.");
        }

        private static void Test_DesktopIdentity()
        {
            Assert(SafetyGuards.EvaluateDesktopIdentity(true, "Default", "Default").IsAllowed, "Desktop normal deve passar.");
            Assert(!SafetyGuards.EvaluateDesktopIdentity(true, "Winlogon", "Default").IsAllowed, "Desktop seguro deve bloquear.");
            Assert(!SafetyGuards.EvaluateDesktopIdentity(false, "Default", "Default").IsAllowed, "Falha de API deve bloquear.");
            Assert(!SafetyGuards.EvaluateDesktopIdentity(true, "", "Default").IsAllowed, "Nome ausente deve bloquear.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Falha na asser\u00e7\u00e3o: " + message);
            }
        }

        private static void Test_FlickVsDwell()
        {
            Config config = new Config();
            HotCornerDetector detector = new HotCornerDetector();
            config.Corner = "TopLeft";
            config.DwellMs = 180;
            config.TriggerSizePx = 24;
            config.RearmDistancePx = 24;
            Screen screen = Screen.PrimaryScreen;

            detector.ProcessTick(new Point(100, 100), 0, screen, config, true);
            Assert(detector.State == CornerState.Armed, "Ao afastar para 100px (>= 24px), deve transitar para Armed");

            HotCornerAction a1 = detector.ProcessTick(new Point(0, 0), 10, screen, config, true);
            Assert(a1.ActionType != CornerActionType.Trigger, "N\u00e3o deve disparar imediatamente em t=10");
            Assert(detector.State == CornerState.Dwelling, "Deve entrar no estado Dwelling ao tocar o canto");

            HotCornerAction a2 = detector.ProcessTick(new Point(50, 50), 30, screen, config, true);
            Assert(a2.ActionType != CornerActionType.Trigger, "Flick r\u00e1pido n\u00e3o deve disparar");
            Assert(detector.State == CornerState.Armed, "Deve abortar Dwelling e retornar para Armed ap\u00f3s flick");

            detector.ProcessTick(new Point(0, 0), 100, screen, config, true);
            Assert(detector.State == CornerState.Dwelling, "Deve reiniciar Dwelling em t=100");

            HotCornerAction a3 = detector.ProcessTick(new Point(0, 0), 200, screen, config, true);
            Assert(a3.ActionType != CornerActionType.Trigger, "N\u00e3o deve disparar em t=200ms (< 180ms decorridos)");

            HotCornerAction a4 = detector.ProcessTick(new Point(0, 0), 290, screen, config, true);
            Assert(a4.ActionType == CornerActionType.Trigger, "Deve disparar Trigger ap\u00f3s atingir 180ms de perman\u00eancia");
            Assert(detector.State == CornerState.Triggered, "Deve transitar para o estado Triggered");
        }

        private static void Test_HeldButtonsAndXButtons()
        {
            Config config = new Config();
            HotCornerDetector detector = new HotCornerDetector();
            config.DwellMs = 180;
            config.TriggerSizePx = 24;
            config.RearmDistancePx = 24;
            Screen screen = Screen.PrimaryScreen;

            detector.ProcessTick(new Point(100, 100), 0, screen, config, true);
            Assert(detector.State == CornerState.Armed, "Detector deve estar Armed");

            detector.ProcessTick(new Point(0, 0), 10, screen, config, false);
            Assert(detector.State == CornerState.Disarmed, "Com guarda falhando no canto, deve transitar para Disarmed");

            HotCornerAction a = detector.ProcessTick(new Point(0, 0), 260, screen, config, false);
            Assert(a.ActionType != CornerActionType.Trigger, "N\u00e3o deve disparar enquanto guarda estiver bloqueada");
            Assert(detector.State == CornerState.Disarmed, "Deve permanecer Disarmed");
        }

        private static void Test_ForegroundIdentityChangeCancelsDwell()
        {
            Config config = new Config();
            HotCornerDetector detector = new HotCornerDetector();
            config.DwellMs = 180;
            config.TriggerSizePx = 24;
            config.RearmDistancePx = 24;
            Screen screen = Screen.PrimaryScreen;

            detector.ProcessTick(new Point(100, 100), 0, screen, config, true);
            Assert(detector.State == CornerState.Armed, "Detector deve estar Armed");

            IntPtr app1 = (IntPtr)0x1000;
            IntPtr app2 = (IntPtr)0x2000;

            detector.ProcessTick(new Point(0, 0), 10, screen, config, true, app1, 100);
            Assert(detector.State == CornerState.Dwelling, "Deve iniciar Dwelling no App1");
            Assert(detector.DwellHwnd == app1, "DwellHwnd deve ser App1");

            detector.ProcessTick(new Point(0, 0), 50, screen, config, true, app2, 200);
            Assert(detector.State == CornerState.Armed, "Troca de janela durante dwell deve cancelar imediatamente para Armed");
            Assert(detector.DwellStartTime == 0, "DwellStartTime deve ter sido zerado");

            HotCornerAction a = detector.ProcessTick(new Point(0, 0), 200, screen, config, true, app2, 200);
            Assert(a.ActionType != CornerActionType.Trigger, "N\u00e3o deve disparar dwell ap\u00f3s cancelamento por troca de janela");
        }

        private static void Test_StartDisarmedAndSuppressionRequiresRearm()
        {
            Config config = new Config();
            HotCornerDetector detector = new HotCornerDetector();
            config.Corner = "TopLeft";
            config.DwellMs = 180;
            config.TriggerSizePx = 24;
            config.RearmDistancePx = 24;
            Screen screen = Screen.PrimaryScreen;

            Assert(detector.State == CornerState.Disarmed, "Detector deve iniciar Disarmed");

            detector.ProcessTick(new Point(0, 0), 0, screen, config, true);
            Assert(detector.State == CornerState.Disarmed, "Cursor em (0,0) deve manter estado Disarmed at\u00e9 sair da zona de rearmamento");

            detector.ProcessTick(new Point(50, 50), 10, screen, config, true);
            Assert(detector.State == CornerState.Armed, "Afastar al\u00e9m de 24px deve armar o detector");

            detector.ProcessTick(new Point(0, 0), 20, screen, config, false);
            Assert(detector.State == CornerState.Disarmed, "Supress\u00e3o no canto deve desarmar (Disarmed)");

            detector.ProcessTick(new Point(0, 0), 100, screen, config, true);
            Assert(detector.State == CornerState.Disarmed, "Ap\u00f3s supress\u00e3o, permanecer no canto n\u00e3o deve armar");

            detector.ProcessTick(new Point(50, 50), 150, screen, config, true);
            Assert(detector.State == CornerState.Armed, "Ap\u00f3s supress\u00e3o, afastar al\u00e9m de 24px rearma para Armed");
        }

        private static void Test_RearmDistanceAndCooldown()
        {
            Config config = new Config();
            HotCornerDetector detector = new HotCornerDetector();
            config.Corner = "TopLeft";
            config.DwellMs = 180;
            config.RearmDistancePx = 24;
            config.CooldownMs = 700;
            Screen screen = Screen.PrimaryScreen;

            detector.ProcessTick(new Point(100, 100), 0, screen, config, true);
            detector.ProcessTick(new Point(0, 0), 10, screen, config, true);
            detector.ProcessTick(new Point(0, 0), 200, screen, config, true);
            Assert(detector.State == CornerState.Triggered, "Deve estar no estado Triggered");

            detector.ProcessTick(new Point(10, 10), 210, screen, config, true);
            Assert(detector.State == CornerState.Triggered, "Dentro da zona de 24px, deve manter Triggered");

            detector.ProcessTick(new Point(50, 50), 220, screen, config, true);
            Assert(detector.State == CornerState.Cooldown, "Ap\u00f3s passar de 24px, deve entrar em Cooldown");

            HotCornerAction a = detector.ProcessTick(new Point(0, 0), 500, screen, config, true);
            Assert(a.ActionType != CornerActionType.Trigger, "N\u00e3o deve disparar durante cooldown de 700ms");
            Assert(detector.State == CornerState.Cooldown, "Deve permanecer em Cooldown");

            detector.ProcessTick(new Point(50, 50), 1000, screen, config, true);
            Assert(detector.State == CornerState.Armed, "Ap\u00f3s 700ms de cooldown e fora do canto, deve rearmar para Armed");
        }

        private static void Test_FullscreenGeometry_AutoHiddenTaskbar()
        {
            RECT mon = new RECT(0, 0, 1920, 1080);

            // Case A: Ordinary window taking work area (1920x1040) - NOT fullscreen
            RECT rcWorkWindow = new RECT(0, 0, 1920, 1040);
            RECT rcWorkAreaNormal = new RECT(0, 0, 1920, 1040);
            bool fsA = SafetyGuards.IsFullscreenGeometryTest(rcWorkWindow, mon, rcWorkAreaNormal);
            Assert(!fsA, "Janela confinada \u00e0 \u00e1rea de trabalho comum n\u00e3o \u00e9 tela cheia");

            // Case B: Window covering entire monitor (1920x1080) when taskbar is visible - Fullscreen!
            RECT rcFull = new RECT(0, 0, 1920, 1080);
            bool fsB = SafetyGuards.IsFullscreenGeometryTest(rcFull, mon, rcWorkAreaNormal);
            Assert(fsB, "Janela cobrindo todo o monitor sobre barra vis\u00edvel \u00e9 tela cheia");

            // Case C: Auto-hidden taskbar where WorkArea == MonitorBounds (1920x1080).
            RECT rcWorkAreaAutoHidden = new RECT(0, 0, 1920, 1080);
            bool fsC = SafetyGuards.IsFullscreenGeometryTest(rcFull, mon, rcWorkAreaAutoHidden);
            Assert(fsC, "Janela cobrindo todo o monitor com barra auto-oculta \u00e9 detectada como tela cheia na geometria");
        }

        private static void Test_CursorClippingMath()
        {
            RECT clipFull = new RECT(0, 0, 1920, 1080);
            Rectangle vsFull = new Rectangle(0, 0, 1920, 1080);
            Assert(!SafetyGuards.IsCursorClippedAgainstVirtualScreen(clipFull, vsFull), "Cursor sem recorte cobre toda a tela virtual e n\u00e3o deve ser considerado clipped");

            RECT clipMulti = new RECT(0, 0, 1920, 1080);
            Rectangle vsMulti = new Rectangle(0, 0, 3840, 1080);
            Assert(SafetyGuards.IsCursorClippedAgainstVirtualScreen(clipMulti, vsMulti), "Cursor confinado a um \u00fanico monitor em setup multi-monitor deve ser detectado como clipped");

            RECT clipWindow = new RECT(100, 100, 800, 600);
            Assert(SafetyGuards.IsCursorClippedAgainstVirtualScreen(clipWindow, vsFull), "Cursor confinado a janela deve ser detectado como clipped");
        }

        private static void Test_ConfigClampingAndValidation()
        {
            Config cfg = new Config();
            cfg.DwellMs = -50;
            cfg.TriggerSizePx = 999;
            cfg.RearmDistancePx = 2;
            cfg.CooldownMs = 10;
            cfg.MaxThumbnails = 100;
            cfg.Corner = "InvalidCorner";

            cfg.ClampAndValidate();

            Assert(cfg.DwellMs == 50, "DwellMs negativo deve ser clampeado em 50");
            Assert(cfg.TriggerSizePx == 10, "TriggerSizePx excessivo deve ser clampeado em 10");
            Assert(cfg.RearmDistancePx == 10, "RearmDistancePx insuficiente deve ser clampeado em 10");
            Assert(cfg.CooldownMs == 100, "CooldownMs baixo deve ser clampeado em 100");
            Assert(cfg.MaxThumbnails == 64, "MaxThumbnails excessivo deve ser clampeado em 64");
            Assert(cfg.Corner == "TopLeft", "Corner inv\u00e1lido deve reverter para TopLeft");
        }

        private static void Test_MalformedJsonResilience()
        {
            string corruptJson = "{ \"dwellMs\": -999, \"triggerSizePx\": invalid_number, \"blockedProcesses\": [ \"foo\", ";
            Config cfg = Config.ParseJson(corruptJson);

            Assert(cfg != null, "ParseJson n\u00e3o deve retornar nulo");
            Assert(cfg.DwellMs >= 50, "Configura\u00e7\u00f5es de JSON corrompido devem ser v\u00e1lidas e clampeadas");
        }

        private static void Test_ProcessDenylistMatching()
        {
            Config cfg = new Config();
            cfg.BlockedProcesses.Add("cyberpunk2077");
            cfg.BlockedProcesses.Add("bloodborne.exe");
            cfg.ClampAndValidate();

            Assert(cfg.IsProcessBlocked("cyberpunk2077") == true, "cyberpunk2077 deve estar bloqueado");
            Assert(cfg.IsProcessBlocked("Cyberpunk2077.exe") == true, "Cyberpunk2077.exe (mai\u00fasculas + .exe) deve estar bloqueado");
            Assert(cfg.IsProcessBlocked("bloodborne") == true, "bloodborne sem .exe deve estar bloqueado");
            Assert(cfg.IsProcessBlocked("notepad.exe") == false, "notepad n\u00e3o deve estar bloqueado");

            // Steam and EpicGamesLauncher should NOT be blocked by default
            Config defaultCfg = new Config();
            Assert(defaultCfg.BlockedProcesses.Count == 0, "Nenhum aplicativo bloqueado automaticamente");
            Assert(!defaultCfg.SuspendOnGameProcessRunning, "Varredura de jogos desativada por padr\u00e3o");
            Assert(defaultCfg.IsProcessBlocked("steam") == false, "steam launcher N\u00c3O deve estar bloqueado por padr\u00e3o");
            Assert(defaultCfg.IsProcessBlocked("epicgameslauncher") == false, "epicgameslauncher N\u00c3O deve estar bloqueado por padr\u00e3o");
        }

        private static void Test_SharedGuardDecisionFailClosed()
        {
            // 1. Manual pause
            GuardResult rPaused = SafetyGuards.CheckManualPause(true);
            Assert(!rPaused.IsAllowed, "Pausa manual deve bloquear");
            GuardResult rUnpaused = SafetyGuards.CheckManualPause(false);
            Assert(rUnpaused.IsAllowed, "Pausa manual desligada deve permitir");

            // 2. Shell Notification State QUNS evaluation
            GuardResult rD3D = SafetyGuards.EvaluateShellNotificationState(0, SafetyGuards.QUNS_RUNNING_D3D_FULL_SCREEN);
            Assert(rD3D.IsAllowed, "QUNS_RUNNING_D3D_FULL_SCREEN n\u00e3o deve bloquear por estado de notifica\u00e7\u00e3o");

            GuardResult rBusy = SafetyGuards.EvaluateShellNotificationState(0, SafetyGuards.QUNS_BUSY);
            Assert(rBusy.IsAllowed, "QUNS_BUSY n\u00e3o deve bloquear por estado de notifica\u00e7\u00e3o");

            GuardResult rPres = SafetyGuards.EvaluateShellNotificationState(0, SafetyGuards.QUNS_PRESENTATION_MODE);
            Assert(rPres.IsAllowed, "QUNS_PRESENTATION_MODE n\u00e3o deve bloquear por estado de notifica\u00e7\u00e3o");

            GuardResult rNotPres = SafetyGuards.EvaluateShellNotificationState(0, SafetyGuards.QUNS_NOT_PRESENT);
            Assert(rNotPres.IsAllowed, "QUNS_NOT_PRESENT n\u00e3o deve bloquear por estado de notifica\u00e7\u00e3o");

            GuardResult rApp = SafetyGuards.EvaluateShellNotificationState(0, SafetyGuards.QUNS_APP);
            Assert(rApp.IsAllowed, "QUNS_APP n\u00e3o deve bloquear por estado de notifica\u00e7\u00e3o");

            GuardResult rOk = SafetyGuards.EvaluateShellNotificationState(0, SafetyGuards.QUNS_ACCEPTS_NOTIFICATIONS);
            Assert(rOk.IsAllowed, "QUNS_ACCEPTS_NOTIFICATIONS deve permitir");

            GuardResult rQuiet = SafetyGuards.EvaluateShellNotificationState(0, SafetyGuards.QUNS_QUIET_HOURS);
            Assert(rQuiet.IsAllowed, "QUNS_QUIET_HOURS deve permitir");

            // Unknown QUNS code MUST fail-closed
            GuardResult rUnknownQuns = SafetyGuards.EvaluateShellNotificationState(0, 999);
            Assert(!rUnknownQuns.IsAllowed, "C\u00f3digo QUNS desconhecido deve falhar fechado (bloquear)");

            // Shell API failure HRESULT MUST fail-closed
            GuardResult rApiFail = SafetyGuards.EvaluateShellNotificationState(unchecked((int)0x80004005), 0);
            Assert(!rApiFail.IsAllowed, "Falha na chamada de API SHQueryUserNotificationState deve falhar fechado");

            // 3. RunningGameCache enumeration failure MUST fail-closed
            RunningGameCache cache = new RunningGameCache();
            Config cfg = new Config();
            cfg.SuspendOnGameProcessRunning = true;
            cfg.BlockedProcesses.Add("test-game");
            cache.SetSimulatedError("Acesso negado simulado na enumera\u00e7\u00e3o");
            GuardResult rCacheFail = SafetyGuards.CheckRunningGames(cfg, cache, false);
            Assert(!rCacheFail.IsAllowed, "Falha na enumera\u00e7\u00e3o de processos de jogos deve falhar fechado (bloquear)");
        }

        private static void Test_PendingActivation_PreservedOnDeniedFocus()
        {
            Config config = new Config();
            using (OverviewForm form = new OverviewForm(Screen.PrimaryScreen, config, (IntPtr)0x1111))
            {
                form.StartPendingActivation((IntPtr)0x1111);
                Assert(form.IsPendingActivation, "OverviewForm deve estar no estado IsPendingActivation");
                Assert(!form.HasBeenForegrounded, "OverviewForm n\u00e3o deve ter HasBeenForegrounded");

                bool keptOpen = form.EvaluatePendingState((IntPtr)0x1111, true, false);
                Assert(keptOpen, "Vis\u00e3o geral deve permanecer aberta com foco na janela original");
                Assert(!form.EvaluatePendingState((IntPtr)0x1111, true, true), "Confinamento deve fechar a vis\u00e3o geral pendente");
            }
            using (OverviewForm form = new OverviewForm(Screen.PrimaryScreen, config, (IntPtr)0x1111))
            {
                form.StartPendingActivation((IntPtr)0x1111);
                Assert(!form.EvaluatePendingState(form.Handle, false, false), "Desktop inseguro deve bloquear at\u00e9 aquisi\u00e7\u00e3o de foco");
            }
        }

        private static void Test_PendingActivation_DismissesOnUnrelatedFocus()
        {
            Config config = new Config();
            using (OverviewForm form = new OverviewForm(Screen.PrimaryScreen, config, (IntPtr)0x1111))
            {
                form.StartPendingActivation((IntPtr)0x1111);
                bool keptOpen = form.EvaluatePendingState((IntPtr)0x2222, true, false);
                Assert(!keptOpen, "Vis\u00e3o geral deve ser dispensada se foco mudar para janela externa n\u00e3o relacionada");
            }
        }

        private static void Test_PendingActivation_TransitionsOnAcquiredFocus()
        {
            Config config = new Config();
            using (OverviewForm form = new OverviewForm(Screen.PrimaryScreen, config, (IntPtr)0x1111))
            {
                bool fired = false;
                form.ForegroundAcquired += delegate { fired = true; };
                form.StartPendingActivation((IntPtr)0x1111);

                bool result = form.EvaluatePendingState(form.Handle, true, false);
                Assert(result, "CheckPendingState deve retornar true ao obter foco");
                Assert(form.HasBeenForegrounded, "Deve registrar HasBeenForegrounded = true");
                Assert(!form.IsPendingActivation, "IsPendingActivation deve ser false ap\u00f3s obter foco");
                Assert(fired, "ForegroundAcquired deve ser disparado");
            }
        }

        private static void Test_NativeTopMost_StyleAndPromotion()
        {
            Config config = new Config();
            using (OverviewForm form = new OverviewForm(Screen.PrimaryScreen, config))
            {
                Assert(form.TopMost, "OverviewForm deve ter TopMost = true configurado");
                Assert(form.IsNativeTopMost(), "OverviewForm deve possuir estilo estendido WS_EX_TOPMOST nativo");

                int err;
                bool ok = form.PromoteTopMost(out err);
                Assert(ok, "PromoteTopMost deve retornar sucesso na janela criada");
                Assert(form.IsNativeTopMost(), "Estilo TopMost deve persistir ap\u00f3s PromoteTopMost");
            }
        }

        private static void Test_NativeZOrder_TwoWindowsRegression()
        {
            using (Form mockFullscreen = new Form())
            using (Form mockOverview = new Form())
            {
                mockFullscreen.Text = "SelfTest_MockFullscreen";
                mockFullscreen.FormBorderStyle = FormBorderStyle.None;
                mockFullscreen.StartPosition = FormStartPosition.Manual;
                mockFullscreen.Bounds = new Rectangle(10, 10, 400, 300);
                mockFullscreen.TopMost = true;
                mockFullscreen.Show();

                mockOverview.Text = "SelfTest_MockOverview";
                mockOverview.FormBorderStyle = FormBorderStyle.None;
                mockOverview.StartPosition = FormStartPosition.Manual;
                mockOverview.Bounds = new Rectangle(10, 10, 400, 300);
                mockOverview.TopMost = true;
                mockOverview.Show();
                Application.DoEvents();

                // 1. Promove mockFullscreen
                int err;
                bool fsPromoted = NativeMethods.PromoteToTopMost(mockFullscreen.Handle, out err);
                Assert(fsPromoted, "mockFullscreen deve ser promovido com sucesso");

                // Valida que ambos possuem estilo TopMost, mas a ordem física coloca mockFullscreen acima
                Assert(NativeMethods.HasTopMostStyle(mockFullscreen.Handle), "mockFullscreen deve ter WS_EX_TOPMOST");
                Assert(NativeMethods.HasTopMostStyle(mockOverview.Handle), "mockOverview deve ter WS_EX_TOPMOST");
                Assert(NativeMethods.IsWindowAbove(mockFullscreen.Handle, mockOverview.Handle), "mockFullscreen deve estar acima de mockOverview ap\u00f3s promo\u00e7\u00e3o");

                // 2. Promove mockOverview sem ativar (SWP_NOACTIVATE | HWND_TOPMOST)
                bool ovPromoted = NativeMethods.PromoteToTopMost(mockOverview.Handle, out err);
                Assert(ovPromoted, "mockOverview deve ser promovido com sucesso");

                // 3. Valida invers\u00e3o da ordem física no Z-order
                Assert(NativeMethods.IsWindowAbove(mockOverview.Handle, mockFullscreen.Handle), "mockOverview DEVE estar fisicamente acima de mockFullscreen");
                Assert(!NativeMethods.IsWindowAbove(mockFullscreen.Handle, mockOverview.Handle), "mockFullscreen N\u00c3O pode estar acima de mockOverview ap\u00f3s promo\u00e7\u00e3o");

                mockFullscreen.Close();
                mockOverview.Close();
            }
        }
    }
}
