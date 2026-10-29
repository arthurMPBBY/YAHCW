using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YAHCW;

public static class IntegrationTests
{
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static object Field(object instance, string name)
    {
        FieldInfo fi = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (fi == null) throw new Exception("Campo não encontrado: " + name);
        return fi.GetValue(instance);
    }

    private static void SetField(object instance, string name, object value)
    {
        FieldInfo fi = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (fi == null) throw new Exception("Campo não encontrado para escrita: " + name);
        fi.SetValue(instance, value);
    }

    private static object Invoke(object instance, string name, params object[] arguments)
    {
        MethodInfo mi = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (mi == null) throw new Exception("Método não encontrado: " + name);
        return mi.Invoke(instance, arguments);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void RenderLayout(OverviewForm overview, string filename)
    {
        using (Bitmap bitmap = new Bitmap(overview.ClientSize.Width, overview.ClientSize.Height))
        {
            overview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, overview.ClientSize));
            bitmap.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    [STAThread]
    public static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Config config = new Config();
            List<WindowItem> firstCatalog = null;

            Console.WriteLine("--- Iniciando Testes de Integração YAHCW (UI & Lifecycle) ---");

            // ==================== TESTE 1: CATÁLOGO, DWM E NOVOS CONTROLES ====================
            using (OverviewForm overview = new OverviewForm(Screen.PrimaryScreen, config))
            {
                overview.RefreshWindows();
                firstCatalog = (List<WindowItem>)Field(overview, "_allWindows");

                Require(firstCatalog != null, "Catálogo de janelas nulo.");
                int liveCandidates = firstCatalog.FindAll(delegate(WindowItem item) { return !item.IsMinimized; }).Count;
                int registrations = firstCatalog.FindAll(delegate(WindowItem item) { return item.ThumbnailId != IntPtr.Zero; }).Count;
                if (liveCandidates > 0) Require(registrations > 0, "Nenhuma miniatura DWM real registrada.");
                Console.WriteLine(liveCandidates > 0 ? "PASS: registros DWM reais: " + registrations : "SKIP: sem janelas não minimizadas para testar DWM.");
                Console.WriteLine("PASS: Catálogo de janelas processado (" + firstCatalog.Count + " itens identificados).");

                // Validação de novos controles da UI polida (Header, Busca estilizada, Botão fechar, Rodapé)
                Panel header = (Panel)Field(overview, "_headerPanel");
                Require(header != null && header.Height >= 64, "Header panel ausente ou altura insuficiente.");

                Label title = (Label)Field(overview, "_lblTitle");
                Require(title != null && !string.IsNullOrEmpty(title.Text), "Título do header ausente.");

                Label countLabel = (Label)Field(overview, "_lblCount");
                Require(countLabel != null, "Label de contagem de janelas ausente.");

                Panel searchPanel = (Panel)Field(overview, "_searchPanel");
                Require(searchPanel != null, "Container estilizado de busca ausente.");

                TextBox search = (TextBox)Field(overview, "_searchBox");
                Require(search != null, "TextBox de busca ausente.");

                Button btnClose = (Button)Field(overview, "_btnClose");
                Require(btnClose != null && btnClose.Text == "\u2715", "Botão visível de fechar/dismiss ausente.");

                Panel footer = (Panel)Field(overview, "_footerPanel");
                Require(footer != null && footer.Height >= 38, "Footer panel ausente.");

                Button prevBtn = (Button)Field(overview, "_previousPage");
                Button nextBtn = (Button)Field(overview, "_nextPage");
                Label pageLabel = (Label)Field(overview, "_pageLabel");
                Require(prevBtn != null && nextBtn != null && pageLabel != null, "Controles de paginação no footer ausentes.");

                Console.WriteLine("PASS: Hierarquia visual e novos componentes Win11 verificados com sucesso.");
                DashboardControls dashboard = (DashboardControls)Field(overview, "_dashboard");
                Require(dashboard.Parent == overview, "Brilho e áudio devem pertencer à mesma visão geral.");
                Invoke(dashboard, "AddBrightness", new BrightnessTarget { Id = "test-only", Name = "Generic Monitor", Percent = 45 });
                FlowLayoutPanel audioLeft = (FlowLayoutPanel)Field(dashboard, "left");
                FlowLayoutPanel audioRight = (FlowLayoutPanel)Field(dashboard, "right");
                Invoke(dashboard, "AddOutput", audioLeft, new AudioTarget { Id = "test-monitor", Name = "Generic Monitor", IsDefault = false });
                Invoke(dashboard, "AddOutput", audioRight, new AudioTarget { Id = "test-headset", Name = "Headset", IsDefault = true });
                Require(audioLeft.Controls.Count == 2 && audioRight.Controls.Count == 2, "Saídas devem aparecer dos dois lados do brilho.");
                Require(audioLeft.Controls[1] is Button && audioRight.Controls[1] is Button, "Saídas devem ser clicáveis sem outra janela.");
                Console.WriteLine("PASS: Brilho e duas saídas de áudio integrados no mesmo formulário.");

                // ==================== TESTE 2: PAGINAÇÃO, RETORNO E ÁREA ÚTIL ====================
                List<WindowItem> allWindows = (List<WindowItem>)Field(overview, "_allWindows");
                allWindows.Clear();
                for (int index = 0; index < 31; index++)
                {
                    allWindows.Add(new WindowItem
                    {
                        Title = "Teste janela " + index,
                        ProcessName = "fixture_proc",
                        IsMinimized = (index % 2 == 0)
                    });
                }

                Invoke(overview, "ApplyFilter");
                RenderLayout(overview, "ui-layout-wide.png");
                List<WindowItem> visible = (List<WindowItem>)Field(overview, "_filteredWindows");
                Require(visible.Count > 0 && visible.Count <= 12, "Paginação excedeu o limite de prévias grandes.");

                string firstTitle = visible[0].Title;
                Invoke(overview, "ChangePage", 1);
                Require(visible[0].Title != firstTitle, "Avançar página não mudou os itens.");

                Invoke(overview, "ChangePage", -1);
                Require(visible[0].Title == firstTitle, "Voltar página não restaurou os itens.");

                List<Rectangle> tiles = (List<Rectangle>)Field(overview, "_tileRects");
                foreach (Rectangle tile in tiles)
                {
                    Require(tile.Top >= 64, "Tile sobrepõe o header.");
                    Require(tile.Right <= overview.ClientSize.Width, "Tile ultrapassa margem direita.");
                    Require(tile.Bottom <= overview.ClientSize.Height - 38, "Tile ultrapassa rodapé.");
                }
                Console.WriteLine("PASS: Paginação de 31 itens, retorno e limites da grade validados.");

                // ==================== TESTE 3: BUSCA COM ESPAÇOS E ESTADO VAZIO ====================
                search.Text = "zzzz janela inexistente";
                Require(visible.Count == 0, "Busca sem resultados não exibiu estado vazio.");

                search.Text = string.Empty;
                Require(visible.Count > 0, "Limpar busca não restaurou catálogo.");
                Console.WriteLine("PASS: Busca, estado vazio e restauração de catálogo validados.");

                // ==================== TESTE 4: ROTEAMENTO DE TECLADO E ESPAÇO COMO TEXTO ====================
                int selBefore = (int)Field(overview, "_selectedIndex");

                // Simula SearchBox_KeyDown com Space: não deve acionar ativação
                KeyEventArgs spaceKey = new KeyEventArgs(Keys.Space);
                Invoke(overview, "SearchBox_KeyDown", search, spaceKey);
                Require(!spaceKey.Handled, "Espaço na caixa de busca foi indevidamente suprimido/interceptado.");

                // Simula teclas Up e Down para navegação previsível
                KeyEventArgs downKey = new KeyEventArgs(Keys.Down);
                Invoke(overview, "SearchBox_KeyDown", search, downKey);
                int selAfterDown = (int)Field(overview, "_selectedIndex");
                Require(selAfterDown >= selBefore, "Seta para baixo não navegou a seleção para frente.");

                KeyEventArgs upKey = new KeyEventArgs(Keys.Up);
                Invoke(overview, "SearchBox_KeyDown", search, upKey);
                int selAfterUp = (int)Field(overview, "_selectedIndex");
                Require(selAfterUp <= selAfterDown, "Seta para cima não retornou a seleção.");

                // Simula OverviewForm_KeyDown com Space quando SearchBox não tem foco
                int textLenBefore = search.Text.Length;
                KeyEventArgs formSpaceKey = new KeyEventArgs(Keys.Space);
                Invoke(overview, "OverviewForm_KeyDown", overview, formSpaceKey);
                Require(search.Text.Length > textLenBefore, "Espaço no formulário não foi redirecionado para a busca.");
                Console.WriteLine("PASS: Roteamento de teclado: Espaço preservado para busca e setas navegam grade.");

                // ==================== TESTE 5: LAYOUT RESPONSIVO EM TELAS ESTREITAS ====================
                Size originalSize = overview.Size;
                overview.Size = new Size(640, 480);
                Invoke(overview, "ComputeLayout");
                List<Rectangle> narrowTiles = (List<Rectangle>)Field(overview, "_tileRects");
                Require(narrowTiles.Count > 0, "Nenhum tile gerado em tela estreita.");
                foreach (Rectangle tile in narrowTiles)
                {
                    Require(tile.Width >= 120 && tile.Height >= 90, "Dimensões de tile degradadas em tela estreita.");
                    Require(tile.Right <= 640 && tile.Bottom <= 480, "Tile excedeu limites da tela estreita.");
                    Require(tile.Bottom <= dashboard.Top, "Miniaturas não podem cobrir brilho e áudio.");
                }
                Require(dashboard.Bottom <= footer.Top, "Controles não podem cobrir o rodapé.");
                RenderLayout(overview, "ui-dashboard-narrow.png");
                overview.Size = originalSize;
                RenderLayout(overview, "ui-layout-restored.png");
                Console.WriteLine("PASS: Layout responsivo validado em resolução estreita (640x480).");
            }

            Require(firstCatalog.Count == 0, "Dispose não liberou catálogo.");
            Console.WriteLine("PASS: Dispose liberou o catálogo completamente.");

            // ==================== TESTE 6: CICLO DE FOCO (ONDEACTIVATE GUARD) ====================
            using (OverviewForm lifecycleForm = new OverviewForm(Screen.PrimaryScreen, config))
            {
                // Antes de ser foregrounded, OnDeactivate NÃO pode fechar o formulário (bugfix da transição Show)
                SetField(lifecycleForm, "_hasBeenForegrounded", false);
                SetField(lifecycleForm, "_closing", false);

                Invoke(lifecycleForm, "OnDeactivate", EventArgs.Empty);
                bool closingDuringInitialShow = (bool)Field(lifecycleForm, "_closing");
                Require(!closingDuringInitialShow, "OverviewForm fechou indevidamente durante transição inicial antes de atingir foreground!");

                // Após ser efetivamente foregrounded, OnDeactivate DEVE fechar o formulário ao perder foco para janela externa
                SetField(lifecycleForm, "_hasBeenForegrounded", true);
                Invoke(lifecycleForm, "OnDeactivate", EventArgs.Empty);
                bool closingAfterExternalFocus = (bool)Field(lifecycleForm, "_closing");
                Require(closingAfterExternalFocus, "OverviewForm não iniciou fechamento ao ser desativado após estar foregrounded.");
                Console.WriteLine("PASS: Lifecycle de foco corrigido: OnDeactivate protegido na transição e ativo no foco externo.");
            }

            // ==================== TESTE 7: ESTABILIDADE DE RECURSOS GDI/USER (25 CICLOS) ====================
            using (Process current = Process.GetCurrentProcess())
            {
                for (int warmupCycle = 0; warmupCycle < 10; warmupCycle++)
                {
                    using (OverviewForm warmup = new OverviewForm(Screen.PrimaryScreen, config))
                    {
                        warmup.RefreshWindows();
                    }
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                uint beforeGdi = GetGuiResources(current.Handle, 0);
                uint beforeUser = GetGuiResources(current.Handle, 1);
                for (int cycle = 0; cycle < 25; cycle++)
                {
                    using (OverviewForm overview = new OverviewForm(Screen.PrimaryScreen, config))
                    {
                        overview.RefreshWindows();
                    }
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                uint afterGdi = GetGuiResources(current.Handle, 0);
                uint afterUser = GetGuiResources(current.Handle, 1);
                Console.WriteLine("RESOURCE SNAPSHOT: GDI " + beforeGdi + " -> " + afterGdi + "; USER " + beforeUser + " -> " + afterUser);
                Require(afterGdi <= beforeGdi + 8 && afterUser <= beforeUser + 8,
                    string.Format("Crescimento excessivo de handles: GDI {0}->{1}, USER {2}->{3}", beforeGdi, afterGdi, beforeUser, afterUser));
                Console.WriteLine("PASS: 25 ciclos ocultos; GDI " + beforeGdi + " -> " + afterGdi + "; USER " + beforeUser + " -> " + afterUser);
            }

            // ==================== TESTE 8: ATIVAÇÃO PENDENTE & DISMISSAL ====================
            IntPtr mockInitialFg = (IntPtr)0x1234;
            using (OverviewForm pendingForm = new OverviewForm(Screen.PrimaryScreen, config, mockInitialFg))
            {
                pendingForm.StartPendingActivation(mockInitialFg);
                TextBox pendingSearch = (TextBox)Field(pendingForm, "_searchBox");
                typeof(Control).GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(pendingSearch, new object[] { EventArgs.Empty });
                Require(!pendingForm.HasBeenForegrounded, "Entrada lógica na busca não comprova foco do Windows.");
                Require(pendingForm.IsPendingActivation, "OverviewForm deveria estar no estado pending activation.");
                Require(!pendingForm.HasBeenForegrounded, "OverviewForm não deveria estar marcado como foregrounded.");

                // Durante ativação pendente, OnDeactivate NÃO fecha o formulário
                Invoke(pendingForm, "OnDeactivate", EventArgs.Empty);
                bool closingOnDeact = (bool)Field(pendingForm, "_closing");
                Require(!closingOnDeact, "OverviewForm não deve fechar no OnDeactivate durante ativação pendente.");

                // Foco continuando na janela original mantém a visão geral aberta
                bool keptOpen = pendingForm.EvaluatePendingState(mockInitialFg, true, false);
                Require(keptOpen, "OverviewForm deve permanecer aberto enquanto foco continua na janela original.");
                Require(!(bool)Field(pendingForm, "_closing"), "OverviewForm não deve fechar para a janela original.");

                // Mudança de foco para janela não relacionada fecha a visão geral
                IntPtr unrelatedFg = (IntPtr)0x9999;
                bool unrelatedResult = pendingForm.EvaluatePendingState(unrelatedFg, true, false);
                Require(!unrelatedResult, "CheckPendingState deve retornar falso para janela não relacionada.");
                Require((bool)Field(pendingForm, "_closing"), "OverviewForm deve fechar ao detectar foco em janela não relacionada.");
                Console.WriteLine("PASS: Ativação pendente: mantida com foco original e fechada ao trocar para janela não relacionada.");
            }

            // ==================== TESTE 9: ATIVAÇÃO RETARDADA (TRANSIÇÃO PARA CICLO NORMAL) ====================
            using (OverviewForm delayedForm = new OverviewForm(Screen.PrimaryScreen, config, mockInitialFg))
            {
                bool eventFired = false;
                delayedForm.ForegroundAcquired += delegate { eventFired = true; };
                delayedForm.StartPendingActivation(mockInitialFg);

                // Simula aquisição retardada de foco pelo próprio OverviewForm
                bool acquiredResult = delayedForm.EvaluatePendingState(delayedForm.Handle, true, false);
                Require(acquiredResult, "CheckPendingState deve retornar verdadeiro quando overview adquire foco.");
                Require(delayedForm.HasBeenForegrounded, "OverviewForm deve registrar HasBeenForegrounded = true.");
                Require(!delayedForm.IsPendingActivation, "OverviewForm não deve mais estar em ativação pendente.");
                Require(eventFired, "Evento ForegroundAcquired deve ter sido disparado.");

                // Após ter sido ativado, perda de foco subsequente deve fechar
                Invoke(delayedForm, "OnDeactivate", EventArgs.Empty);
                Require((bool)Field(delayedForm, "_closing"), "OverviewForm deve fechar ao perder foco após ter sido ativado.");
                Console.WriteLine("PASS: Ativação retardada: transição para ciclo normal e disparo de ForegroundAcquired validados.");
            }

            // ==================== TESTE 10: REGRESSÃO NATIVA DE Z-ORDER (DOIS FORMULÁRIOS PRÓPRIOS) ====================
            // Simula janela de usuário em tela cheia com TopMost e valida promoção de Z-order do OverviewForm
            using (Form mockFullscreen = new Form())
            using (OverviewForm zOverview = new OverviewForm(Screen.PrimaryScreen, config))
            {
                mockFullscreen.Text = "Mock_Fullscreen_TopMost";
                mockFullscreen.FormBorderStyle = FormBorderStyle.None;
                mockFullscreen.StartPosition = FormStartPosition.Manual;
                mockFullscreen.Bounds = new Rectangle(50, 50, 500, 400);
                mockFullscreen.TopMost = true;
                mockFullscreen.Show();

                zOverview.Bounds = new Rectangle(50, 50, 500, 400);
                zOverview.Show();
                Application.DoEvents();

                // 1. Promove a janela mock de tela cheia para o topo do z-order topmost
                int err;
                bool fsPromoted = NativeMethods.PromoteToTopMost(mockFullscreen.Handle, out err);
                Require(fsPromoted, "Falha ao promover mockFullscreen.");
                Require(NativeMethods.HasTopMostStyle(mockFullscreen.Handle), "mockFullscreen deve possuir estilo WS_EX_TOPMOST.");
                Require(zOverview.IsNativeTopMost(), "zOverview deve possuir estilo WS_EX_TOPMOST.");

                // Verifica Z-order nativo real: mockFullscreen deve estar ACIMA de zOverview
                bool fsAbove = NativeMethods.IsWindowAbove(mockFullscreen.Handle, zOverview.Handle);
                Require(fsAbove, "mockFullscreen deveria estar acima de zOverview antes da promoção do overview.");

                // 2. Executa a promoção explícita da visão geral (SetWindowPos com HWND_TOPMOST e SWP_NOACTIVATE)
                bool overviewPromoted = zOverview.PromoteTopMost(out err);
                Require(overviewPromoted, "Falha ao executar PromoteTopMost na visão geral.");
                Require(zOverview.IsNativeTopMost(), "zOverview deve manter estilo WS_EX_TOPMOST após promoção.");

                // 3. Valida que a ordem física Z-order foi invertida: zOverview agora está ACIMA de mockFullscreen
                bool ovAbove = NativeMethods.IsWindowAbove(zOverview.Handle, mockFullscreen.Handle);
                Require(ovAbove, "zOverview deve estar fisicamente acima de mockFullscreen no Z-order nativo.");
                Require(!NativeMethods.IsWindowAbove(mockFullscreen.Handle, zOverview.Handle), "mockFullscreen não pode estar acima de zOverview após promoção.");

                // 4. Valida a flag de reasserção diferida (bounded deferred promotion)
                Require(zOverview.HasDeferredPromoted, "Reasserção diferida de z-order não foi executada.");

                mockFullscreen.Close();
                zOverview.Close();
                Console.WriteLine("PASS: Regressão de Z-order nativo: estilo TopMost vs ordem física e promoção sem ativação validadas.");
            }

            Console.WriteLine("SUCESSO: Testes de integração, UI e sobreposição de janelas próprias passaram.");
            using (DashboardControls preview = new DashboardControls())
            {
                preview.Size = new Size(960, 168);
                preview.CreateControl();
                Invoke(preview, "AddBrightness", new BrightnessTarget { Id = "preview", Name = "Monitor 24L", Percent = 45 });
                Invoke(preview, "AddOutput", (FlowLayoutPanel)Field(preview, "left"), new AudioTarget { Id = "monitor", Name = "Display - HDMI" });
                Invoke(preview, "AddOutput", (FlowLayoutPanel)Field(preview, "right"), new AudioTarget { Id = "Headset - Audio Device", IsDefault = true });
                using (Bitmap image = new Bitmap(preview.Width, preview.Height))
                {
                    preview.DrawToBitmap(image, preview.ClientRectangle);
                    image.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dashboard-controls-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
