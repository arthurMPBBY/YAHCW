using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YAHCW
{
    public sealed class DashboardControls : UserControl
    {
        private readonly FlowLayoutPanel left;
        private readonly FlowLayoutPanel brightness;
        private readonly FlowLayoutPanel right;
        private readonly Label status;
        private readonly Button refresh;
        private readonly Font headingFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        private readonly Font bodyFont = new Font("Segoe UI", 9f);
        private bool released;
        private bool loading;
        private bool writing;

        public DashboardControls()
        {
            BackColor = Color.FromArgb(22, 24, 28);
            ForeColor = Color.FromArgb(235, 238, 243);
            Font = bodyFont;
            Height = 168;
            AccessibleName = "Controles rápidos: áudio e brilho";
            TableLayoutPanel columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(14, 8, 14, 4) };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            left = CreateColumn(); brightness = CreateColumn(); right = CreateColumn();
            columns.Controls.Add(left, 0, 0); columns.Controls.Add(brightness, 1, 0); columns.Controls.Add(right, 2, 0);
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 29 };
            refresh = MakeButton("Atualizar", 88);
            refresh.Dock = DockStyle.Right;
            refresh.Click += async delegate { await RefreshAsync(); };
            status = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(165, 177, 192), Padding = new Padding(18, 5, 0, 0), AutoEllipsis = true, Text = "Carregando controles…" };
            bottom.Controls.Add(status); bottom.Controls.Add(refresh);
            Controls.Add(columns); Controls.Add(bottom);
            AddHeading(left, "SAÍDA DE ÁUDIO"); AddHeading(brightness, "BRILHO · BRIGHTTRAY"); AddHeading(right, "SAÍDA DE ÁUDIO");
        }

        private FlowLayoutPanel CreateColumn()
        {
            FlowLayoutPanel column = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(6, 0, 6, 0) };
            column.SizeChanged += delegate
            {
                foreach (Control control in column.Controls) control.Width = Math.Max(70, column.ClientSize.Width - 22);
            };
            return column;
        }
        private void AddHeading(FlowLayoutPanel column, string title)
        {
            column.Controls.Add(new Label { Text = title, Font = headingFont, Height = 25, AutoEllipsis = true, ForeColor = Color.FromArgb(155, 170, 188), Width = Math.Max(70, column.ClientSize.Width - 22) });
        }
        private void ClearColumn(FlowLayoutPanel column, string title)
        {
            while (column.Controls.Count > 0) { Control control = column.Controls[0]; column.Controls.Remove(control); control.Dispose(); }
            AddHeading(column, title);
        }
        private Button MakeButton(string title, int width)
        {
            Button button = new Button { Text = title, Width = width, Height = 43, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(35, 39, 46), ForeColor = ForeColor, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand, Font = bodyFont, AutoEllipsis = true, Margin = new Padding(0, 2, 0, 4), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = Color.FromArgb(64, 72, 85);
            return button;
        }

        public async Task RefreshAsync()
        {
            if (loading || writing || released) return;
            loading = true; refresh.Enabled = false;
            ClearColumn(left, "SAÍDA DE ÁUDIO"); ClearColumn(right, "SAÍDA DE ÁUDIO"); ClearColumn(brightness, "BRILHO · BRIGHTTRAY");
            string brightnessError = null;
            string audioError = null;
            List<BrightnessTarget> monitors = null;
            List<AudioTarget> outputs = null;
            try
            {
                await Task.WhenAll(Task.Run(delegate { try { monitors = BrightnessClient.Read(); } catch (Exception error) { brightnessError = error.GetBaseException().Message; } }),
                    Task.Run(delegate { try { outputs = AudioOutputs.Read(); } catch (Exception error) { audioError = error.GetBaseException().Message; } }));
                if (released) return;
                if (monitors != null)
                {
                    foreach (BrightnessTarget monitor in monitors) AddBrightness(monitor);
                    if (monitors.Count == 0) AddMessage(brightness, "Nenhum monitor disponível no BrightTray.");
                }
                else AddMessage(brightness, "BrightTray indisponível. Abra-o e clique em Atualizar.");
                if (outputs != null)
                {
                    for (int index = 0; index < outputs.Count; index++) AddOutput(index % 2 == 0 ? left : right, outputs[index]);
                    if (outputs.Count == 0) AddMessage(left, "Nenhuma saída conectada.");
                }
                else AddMessage(left, "Não foi possível consultar as saídas.");
                status.Text = brightnessError ?? audioError ?? "Escolha a saída ou ajuste o brilho · alterações só acontecem ao interagir";
            }
            finally
            {
                loading = false;
                if (!released) refresh.Enabled = true;
            }
        }

        private void AddMessage(FlowLayoutPanel column, string message)
        {
            column.Controls.Add(new Label { Text = message, Height = 62, Width = Math.Max(70, column.ClientSize.Width - 22), ForeColor = Color.FromArgb(173, 184, 200) });
        }
        private void SetWriting(bool value)
        {
            writing = value;
            if (released) return;
            left.Enabled = right.Enabled = brightness.Enabled = !value;
            refresh.Enabled = !value && !loading;
        }
        private void AddOutput(FlowLayoutPanel column, AudioTarget output)
        {
            Button button = MakeButton((output.IsDefault ? "●  " : "○  ") + output.Name, Math.Max(70, column.ClientSize.Width - 22));
            button.AccessibleName = "Usar saída " + output.Name;
            if (output.IsDefault) { button.BackColor = Color.FromArgb(28, 62, 79); button.FlatAppearance.BorderColor = Color.FromArgb(78, 173, 205); }
            button.Click += async delegate
            {
                if (writing || loading || released) return;
                SetWriting(true);
                status.Text = "Alterando saída de áudio…";
                string errorMessage = null;
                try { await Task.Run(delegate { AudioOutputs.Select(output.Id); }); }
                catch (Exception error) { errorMessage = error.GetBaseException().Message; }
                finally { SetWriting(false); }
                if (released) return;
                await RefreshAsync();
                if (!released) status.Text = errorMessage ?? "Saída selecionada: " + output.Name;
            };
            column.Controls.Add(button);
        }
        private void AddBrightness(BrightnessTarget monitor)
        {
            Panel row = new Panel { Width = Math.Max(70, brightness.ClientSize.Width - 22), Height = 68, Margin = new Padding(0, 0, 0, 3) };
            Label label = new Label { Text = monitor.Name + " · " + monitor.Percent + "%", Dock = DockStyle.Top, Height = 22, AutoEllipsis = true };
            TrackBar slider = new TrackBar { Minimum = 0, Maximum = 100, Value = monitor.Percent, TickStyle = TickStyle.None, Dock = DockStyle.Bottom, Height = 38, LargeChange = 10, AccessibleName = "Brilho de " + monitor.Name };
            slider.BackColor = BackColor;
            Timer debounce = new Timer { Interval = 180 };
            row.Disposed += delegate { debounce.Stop(); debounce.Dispose(); };
            slider.ValueChanged += delegate
            {
                label.Text = monitor.Name + " · " + slider.Value + "%";
                debounce.Stop(); debounce.Start();
            };
            Action commit = async delegate
            {
                if (writing || released) return;
                int value = slider.Value;
                if (value == monitor.Percent) return;
                SetWriting(true);
                try
                {
                    List<BrightnessTarget> confirmed = await Task.Run(delegate { return BrightnessClient.Set(monitor.Id, value); });
                    if (released) return;
                    BrightnessTarget result = confirmed.Find(delegate(BrightnessTarget item) { return item.Id == monitor.Id; });
                    if (result == null) throw new InvalidOperationException("Monitor mudou; atualize os controles.");
                    monitor.Percent = result.Percent;
                    slider.Value = result.Percent;
                    status.Text = "Brilho confirmado pelo BrightTray: " + result.Percent + "%";
                }
                catch (Exception error)
                {
                    if (!released) { slider.Value = monitor.Percent; status.Text = error.GetBaseException().Message; }
                }
                finally { SetWriting(false); }
            };
            debounce.Tick += delegate { debounce.Stop(); commit(); };
            slider.MouseUp += delegate { debounce.Stop(); commit(); };
            slider.KeyUp += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Left || args.KeyCode == Keys.Right || args.KeyCode == Keys.Up || args.KeyCode == Keys.Down || args.KeyCode == Keys.Home || args.KeyCode == Keys.End || args.KeyCode == Keys.PageUp || args.KeyCode == Keys.PageDown) commit();
            };
            row.Controls.Add(label); row.Controls.Add(slider); brightness.Controls.Add(row);
        }
        protected override void Dispose(bool disposing)
        {
            released = true;
            base.Dispose(disposing);
            if (disposing) { headingFont.Dispose(); bodyFont.Dispose(); }
        }
    }
}
