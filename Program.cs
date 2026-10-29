using System;
using System.Threading;
using System.Windows.Forms;

namespace YAHCW
{
    static class Program
    {
        private const string MUTEX_NAME = "Global\\YAHCW_SingleInstance_A91B4F82";

        [STAThread]
        static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                string first = args[0].Trim().ToLowerInvariant();
                if (first == "--controls-diagnose")
                {
                    AttachParentConsole();
                    return DashboardDiagnostics.Run();
                }

                if (first == "--self-test" || first == "-t")
                {
                    AttachParentConsole();
                    int exitCode = SelfTest.RunAllTests();
                    return exitCode;
                }

                if (first == "--diagnose" || first == "-d")
                {
                    AttachParentConsole();
                    Diagnostics.Run();
                    return 0;
                }

                if (first == "--overview" || first == "-o")
                {
                    uint showMsg = NativeMethods.RegisterWindowMessage("YAHCW_SHOW_OVERVIEW_MSG");

                    // Check if an existing tray instance is already running
                    bool createdNew;
                    using (Mutex mutex = new Mutex(true, MUTEX_NAME, out createdNew))
                    {
                        if (!createdNew)
                        {
                            // Tray instance is running: route request via tray message window
                            IntPtr receiver = NativeMethods.FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, "YAHCW_TrayMessageReceiver");
                            if (receiver != IntPtr.Zero) NativeMethods.PostMessage(receiver, showMsg, IntPtr.Zero, IntPtr.Zero);
                            return 0;
                        }
                    // No existing instance: start TrayAppContext which evaluates guards in ShowOverview
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Config cfg = Config.Load();
                    using (TrayAppContext trayContext = new TrayAppContext(cfg, true))
                    {
                        Application.Run(trayContext);
                    }
                    return 0;
                    }
                }

                if (first == "--help" || first == "-h" || first == "/?")
                {
                    AttachParentConsole();
                    PrintHelp();
                    return 0;
                }
            }

            // Normal background tray application
            bool createdNewApp;
            using (Mutex mutex = new Mutex(true, MUTEX_NAME, out createdNewApp))
            {
                if (!createdNewApp)
                {
                    MessageBox.Show(
                        "O YAHCW já está em execução na bandeja do sistema.\nVerifique os ícones próximos ao relógio.",
                        "YAHCW",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Config config = Config.Load();
                using (TrayAppContext trayContext = new TrayAppContext(config))
                {
                    Application.Run(trayContext);
                }

                return 0;
            }
        }

        private static void AttachParentConsole()
        {
            if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
            {
                NativeMethods.AllocConsole();
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("  YAHCW - Yet Another Hot Corners for Windows     ");
            Console.WriteLine("=================================================");
            Console.WriteLine("Uso:");
            Console.WriteLine("  YAHCW.exe                Inicia minimizado na bandeja (modo padrão)");
            Console.WriteLine("  YAHCW.exe --overview     Abre visão geral via bandeja com validação de guardas");
            Console.WriteLine("  YAHCW.exe --diagnose     Exibe relatório diagnóstico do sistema/guardas");
            Console.WriteLine("  YAHCW.exe --self-test    Executa bateria de auto-testes deterministas");
            Console.WriteLine("  YAHCW.exe --help         Exibe esta mensagem de ajuda");
            Console.WriteLine();
            Console.WriteLine("Atalho global padrão: Ctrl + Alt + Espaço");
            Console.WriteLine("Configuração: yahcw.config.json");
            Console.WriteLine("=================================================");
        }
    }
}
