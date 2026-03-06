using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TSMasconInput
{
    public partial class SpeedMoniter : Form
    {
        private AnalogGauge speedGauge;
        private TrackBar sliderGlobal; // 控制整體透明度
        private Button btnToggleBg;    // 切換背景透明/黑色
        private Button btnDepart;      // [新增] 發車按鈕
        private Label lblWarning; // [新增] 警告標籤
        private bool isBgTransparent = true; // 追蹤目前狀態

        // [新增] 讓主視窗(Form1)知道發車按鈕被點擊了
        public event EventHandler OnDepartClicked;

        // --- 視窗拖動 API ---
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        public SpeedMoniter()
        {
            // === 調整大小與位置 ===
            this.Size = new Size(250, 275); // 高度和寬度
            this.Location = new Point(100, 100);
            this.StartPosition = FormStartPosition.Manual;

            this.FormBorderStyle = FormBorderStyle.None;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.DoubleBuffered = true;

            // === 核心：設定去背色 ===
            // 我們設定「純黑色」為去背色。當 BackColor 為黑色時，該區域會變透明。
            Color keyColor = Color.Black;
            this.BackColor = keyColor;
            this.TransparencyKey = keyColor;

            // === [新增] 初始化警告標籤 ===
            lblWarning = new Label();
            lblWarning.AutoSize = true;
            lblWarning.Location = new Point(75, 75); // 放在左上角
            lblWarning.Font = new Font("Microsoft JhengHei UI", 12F); // 字體大一點
            
            // [關鍵修改] 改用 RGB(3,3,3) 避免被視為透明色
            lblWarning.ForeColor = Color.FromArgb(3, 3, 3); 
            
            lblWarning.BackColor = Color.Orange;
            lblWarning.Text = "前方預告";
            lblWarning.Visible = false; // 預設隱藏
            this.Controls.Add(lblWarning);
            lblWarning.BringToFront();

            // 初始化儀表
            speedGauge = new AnalogGauge();
            speedGauge.Dock = DockStyle.Fill;
            speedGauge.BackColor = Color.Black; // 預設透明
            speedGauge.ForeColor = Color.White;
            speedGauge.TargetValueColor = Color.SkyBlue;
            speedGauge.GaugeTitle = "Speed";
            speedGauge.Unit = "km/h";
            speedGauge.MaxValue = 120;

            // 點擊儀表即可拖動
            speedGauge.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(this.Handle, 0xA1, 0x2, 0);
                }
            };

            // === [修改] 使用 TableLayoutPanel 將底部空間分成兩半 ===

            TableLayoutPanel bottomPanel = new TableLayoutPanel();

            bottomPanel.Dock = DockStyle.Bottom;
            bottomPanel.Height = 25; // 稍微拉高一點比較好按
            bottomPanel.ColumnCount = 2;
            bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            bottomPanel.Margin = new Padding(0);
            bottomPanel.Padding = new Padding(0);

            // === 左側背景切換按鈕 ===
            btnToggleBg = new Button();
            btnToggleBg.Text = "BG: Transparent";
            btnToggleBg.Height = 20;           // 小按鈕高度
            btnToggleBg.Dock = DockStyle.Fill;
            btnToggleBg.FlatStyle = FlatStyle.Flat;
            btnToggleBg.FlatAppearance.BorderSize = 0;
            btnToggleBg.ForeColor = Color.White;
            btnToggleBg.BackColor = Color.FromArgb(60, 60, 60);
            btnToggleBg.Font = new Font("Arial", 8);

            btnToggleBg.Click += (s, e) => {
                isBgTransparent = !isBgTransparent;
                if (isBgTransparent)
                {
                    // 切換為全透明 (使用去背色 Black)
                    this.BackColor = Color.Black;
                    speedGauge.BackColor = Color.Black;
                    btnToggleBg.Text = "BG: Transparent";
                }
                else
                {
                    // 切換為實心黑 (使用接近黑色的 1,1,1，這不會觸發去背)
                    Color solidBlack = Color.FromArgb(1, 1, 1);
                    this.BackColor = solidBlack;
                    speedGauge.BackColor = solidBlack;
                    btnToggleBg.Text = "BG: Solid Black";
                }
            };

            // 右側：[新增] 發車按鈕
            btnDepart = new Button();
            btnDepart.Text = "DEPART";
            btnDepart.Dock = DockStyle.Fill;
            btnDepart.FlatStyle = FlatStyle.Flat;
            btnDepart.FlatAppearance.BorderSize = 0;
            btnDepart.ForeColor = Color.Black;
            btnDepart.BackColor = Color.LightGray;
            btnDepart.Font = new Font("Arial", 8, FontStyle.Bold);
            btnDepart.Enabled = false; // 預設禁用
            btnDepart.Click += (s, e) => {

                // 觸發事件給主程式

                OnDepartClicked?.Invoke(this, EventArgs.Empty);

            };



            // 將兩個按鈕放入容器

            bottomPanel.Controls.Add(btnToggleBg, 0, 0);

            bottomPanel.Controls.Add(btnDepart, 1, 0);

            // === 拉桿: 整體透明度 (控制 Opacity) ===
            sliderGlobal = CreateTinySlider(100);
            sliderGlobal.Dock = DockStyle.Bottom;
            sliderGlobal.ValueChanged += (s, e) => {
                this.Opacity = sliderGlobal.Value / 100.0;
            };

            // 將控制項加入視窗 (順序決定堆疊位置)
            this.Controls.Add(speedGauge);
            this.Controls.Add(sliderGlobal); // 位在按鈕上方
            this.Controls.Add(bottomPanel);  // 位在最底端

            this.Opacity = 1.0;
        }

        private TrackBar CreateTinySlider(int initValue)
        {
            return new TrackBar
            {
                AutoSize = false,
                Height = 12,
                Minimum = 1,
                Maximum = 100,
                Value = initValue,
                TickStyle = TickStyle.None,
                BackColor = Color.FromArgb(45, 45, 45)
            };
        }

        // === [修改] 更新數據方法，新增 showWarning 參數 ===
        public void UpdateData(float speed, float targetBlue, float nextLimitGreen, float currentLimitRed, bool showWarning = false)
        {
            if (speedGauge != null)
            {
                speedGauge.Value = speed;
                speedGauge.TargetValue = targetBlue > 0 ? targetBlue : -1;
                speedGauge.TargetValue2 = nextLimitGreen > 0 ? nextLimitGreen : -1;
                speedGauge.TargetValue3 = currentLimitRed > 0 ? currentLimitRed : -1;
            }

            // === [新增] 警告標籤邏輯 ===
            if (showWarning && nextLimitGreen > 0)
            {
                lblWarning.Text = "前方預告: " + nextLimitGreen.ToString("0");
                lblWarning.BackColor = Color.Orange;
                // 閃爍效果 (約每 0.5 秒閃一次)
                lblWarning.Visible = (Environment.TickCount % 500) < 250;
            }
            else
            {
                lblWarning.Visible = false;
            }
        }

        // === [新增] 讓 Form1 同步發車按鈕顏色及可用狀態 ===
        public void UpdateDepartState(bool enabled, bool isRequested)
        {
            if (btnDepart.InvokeRequired)
            {
                btnDepart.Invoke(new Action(() => UpdateDepartState(enabled, isRequested)));
                return;
            }

            btnDepart.Enabled = enabled;
            if (enabled)
            {
                // 如果已經點擊並送出發車請求，則亮黃色
                btnDepart.BackColor = isRequested ? Color.Yellow : Color.LightGray;
            }
            else
            {
                btnDepart.BackColor = Color.DarkGray; // 禁用狀態顏色
            }
        }
    }
}