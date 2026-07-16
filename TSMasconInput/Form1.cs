using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using TrainCrew;
using System.Net.Sockets; // 使用 UDP_python
using System.Text;

namespace TSMasconInput
{
    public partial class Form1 : Form
    {
        // === 1. ESP32 連線變數 ===
        private SerialPort portInput;   // 負責接收檔位
        private SerialPort portDisplay; // 負責顯示的東西
        private StringBuilder serialBuffer = new StringBuilder();

        // 儲存 ESP32 目前的檔位
        private int currentEsp32Notch = 0;
        private int lastProcessedEsp32Gear = -999;

        // === 2. 檔位配置 ===
        const int BRAKE_STEPS = 6;
        const bool HAS_EB = true;
        const bool HAS_HOLD = true;
        const int GEAR_N_INDEX = (HAS_EB ? 1 : 0) + BRAKE_STEPS + (HAS_HOLD ? 1 : 0);

        // === 3. TASC/ATO 變數 ===
        Timer tim = new Timer();
        private SimpleTASC tasc;
        private ATOController ato;

        private UdpClient udpClient = new UdpClient(); // UDP 廣播器
        private UdpClient udpReceiver; // 用來接收來自 Python 訊號的接收器 (監聽 Port 5001)

        // --- 狀態旗標 ---
        private bool isTascMasterOn = true;  // 控制：是否執行定點停車
        private bool isAtoMasterOn = true;   // 控制：是否執行自動加速
        private bool isAtcMasterOn = true;  // 控制：ATC

        private bool isAtoDepartRequest = false; // 追蹤是否按下了發車鈕

        private enum TascBehaviorState { Idle, Braking, Holding }
        private TascBehaviorState currentTascBehavior = TascBehaviorState.Idle;
        private int tascStationNotch = 0;
        private int atoRunningNotch = 0;

        // === 4. 其他變數 ===
        // 新增一個 Queue 來儲存過去 0.5 秒的速度紀錄
        private Queue<Tuple<DateTime, double>> speedHistory = new Queue<Tuple<DateTime, double>>();
        private double _currentAccel = 0.0;

        // UI 更新計數器 (降頻用)
        private int uiUpdateCounter = 0;
        private SpeedMoniter speedMoniterWindow;

        // 參數常數
        private const double MY_MAX_BRAKE_DECEL_KMPHPS = 3.7;
        private const int MY_BRAKE_NOTCHES = 6;
        private const double TASC_ACTIVATION_DISTANCE_M = 800.0;
        private const double TASC_STOP_MARGIN_M = 0.18;
        private const double TASC_STOP_ADJUST_M = 0.15; // ★ 關鍵的停車位置補償

        public Form1()
        {
            InitializeComponent();
            TrainCrewInput.Init();
            FormClosed += Form1_FormClosed;
            this.TopMost = true;

            currentEsp32Notch = GEAR_N_INDEX;
            tasc = new SimpleTASC(MY_MAX_BRAKE_DECEL_KMPHPS, MY_BRAKE_NOTCHES);
            ato = new ATOController();

            // --- 既有按鈕初始化 ---
            btn_tasc_toggle.Text = "TASC: ON";
            btn_tasc_toggle.BackColor = Color.LawnGreen;
            labelTascStatus.Text = "TASC: Armed";

            btn_atc_toggle.Text = "ATC: ON";
            btn_atc_toggle.BackColor = Color.LawnGreen;

            btn_ato_toggle.Text = "ATO: ON";
            btn_ato_toggle.BackColor = Color.Cyan;

            btn_depart.Text = "DEPART";
            btn_depart.Enabled = false;
            btn_depart.BackColor = SystemColors.Control;

            // COM Port 初始化
            RefreshPorts();
            if (this.btn_open != null)
            {
                this.btn_open.Click += new EventHandler(this.btn_open_Click);
                this.btn_open.Enabled = true;
            }
            // === [修改 1] 初始化斷線按鈕為「可用」狀態，作為刷新鍵 ===
            if (this.btn_close != null)
            {
                this.btn_close.Click += new EventHandler(this.btn_close_Click);
                this.btn_close.Enabled = true;
                this.btn_close.Text = "Refresh";
            }

            // === 啟動時自動顯示 SpeedMoniter ===
            //speedMoniterWindow = new SpeedMoniter();
            //HookSpeedMoniterEvents(); // ★ 這行一定要有！
            //speedMoniterWindow.Show();

            // Timer 設定
            tim.Tick += Tim_Tick;
            tim.Interval = 15;
            tim.Start();

            // 啟動 UDP 接收器
            StartUdpReceiver();
        }

        // [新增] 共用的綁定方法
        private void HookSpeedMoniterEvents()
        {
            if (speedMoniterWindow != null)
            {
                speedMoniterWindow.OnDepartClicked += (s, e) => {
                    if (isAtoMasterOn)
                    {
                        isAtoDepartRequest = true;
                        btn_depart.BackColor = Color.Yellow;
                    }
                };
            }
        }

        // === ATO 按鈕事件 ===
        private void Btn_ato_toggle_Click(object sender, EventArgs e)
        {
            isAtoMasterOn = !isAtoMasterOn;
            if (isAtoMasterOn)
            {
                btn_ato_toggle.Text = "ATO: ON";
                btn_ato_toggle.BackColor = Color.Cyan;

                // [連動] 當 ATO 開啟時，強制開啟 TASC
                if (!isTascMasterOn)
                {
                    isTascMasterOn = true;
                    btn_tasc_toggle.Text = "TASC: ON";
                    btn_tasc_toggle.BackColor = Color.LawnGreen;
                }

                // [連動] 當 ATO 開啟時，強制開啟 ATC
                if (!isAtcMasterOn)
                {
                    isAtcMasterOn = true;
                    btn_atc_toggle.Text = "ATC: ON";
                    btn_atc_toggle.BackColor = Color.LawnGreen;
                }
            }
            else
            {
                btn_ato_toggle.Text = "ATO: OFF";
                btn_ato_toggle.BackColor = SystemColors.Control;
                isAtoDepartRequest = false;
            }
        }

        private void Btn_depart_Click(object sender, EventArgs e)
        {
            if (isAtoMasterOn)
            {
                isAtoDepartRequest = true;
                btn_depart.BackColor = Color.Yellow;
            }
        }

        // === 既有邏輯保持不變 ===
        private void RefreshPorts()
        {
            string lastMotor = comboBoxMotor != null ? comboBoxMotor.Text : "";
            string lastDisplay = comboBoxDisplay != null ? comboBoxDisplay.Text : "";

            if (comboBoxMotor != null) comboBoxMotor.Items.Clear();
            if (comboBoxDisplay != null) comboBoxDisplay.Items.Clear();

            if (comboBoxMotor != null) comboBoxMotor.Items.Add("控制器");
            if (comboBoxDisplay != null) comboBoxDisplay.Items.Add("時速表");

            var names = SerialPort.GetPortNames();
            foreach (var nm in names)
            {
                if (comboBoxMotor != null) comboBoxMotor.Items.Add(nm);
                if (comboBoxDisplay != null) comboBoxDisplay.Items.Add(nm);
            }

            if (comboBoxMotor != null)
            {
                if (comboBoxMotor.Items.Contains(lastMotor)) comboBoxMotor.Text = lastMotor;
                else if (comboBoxMotor.Items.Count > 0) comboBoxMotor.SelectedIndex = 0;
            }

            if (comboBoxDisplay != null)
            {
                if (comboBoxDisplay.Items.Contains(lastDisplay)) comboBoxDisplay.Text = lastDisplay;
                else if (comboBoxDisplay.Items.Count > 0) comboBoxDisplay.SelectedIndex = 0;
            }
        }

        private void btn_monitor_toggle_Click(object sender, EventArgs e)
        {
            if (speedMoniterWindow == null || speedMoniterWindow.IsDisposed)
            {
                speedMoniterWindow = new SpeedMoniter();
                HookSpeedMoniterEvents(); // [新增這行]
                speedMoniterWindow.Show();
                btn_monitor_toggle.Text = "隱藏速度表";
            }
            else
            {
                speedMoniterWindow.Close();
                speedMoniterWindow = null;
                btn_monitor_toggle.Text = "顯示速度表";
            }
        }

        private void btn_open_Click(object sender, EventArgs e)
        {
            try
            {
                string portNameInput = comboBoxMotor.Text;
                if (portNameInput != "控制器" && !string.IsNullOrEmpty(portNameInput))
                {
                    portInput = new SerialPort(portNameInput, 115200);
                    portInput.DtrEnable = false;
                    portInput.RtsEnable = false;
                    portInput.DataBits = 8;
                    portInput.NewLine = "\n";
                    portInput.Parity = Parity.None;
                    portInput.DataReceived += Port_DataReceived;
                    portInput.Open();
                }
                else portInput = null;

                string portNameDisplay = comboBoxDisplay.Text;
                if (portNameDisplay != "時速表" && !string.IsNullOrEmpty(portNameDisplay))
                {
                    portDisplay = new SerialPort(portNameDisplay, 115200);
                    portDisplay.DtrEnable = false;
                    portDisplay.RtsEnable = false;
                    portDisplay.DataBits = 8;
                    portDisplay.NewLine = "\n";
                    portDisplay.Parity = Parity.None;
                    portDisplay.Open();
                }
                else portDisplay = null;

                if (btn_open != null) btn_open.Enabled = false;
                if (btn_close != null) { btn_close.Enabled = true; btn_close.Text = "Disconnect"; }

                SetManualNotch(-8);
            }
            catch (Exception ex)
            {
                if (portInput != null) { try { portInput.Close(); } catch { } portInput = null; }
                if (portDisplay != null) { try { portDisplay.Close(); } catch { } portDisplay = null; }
                MessageBox.Show("連線失敗: " + ex.Message);
            }
        }

        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (portInput == null || !portInput.IsOpen) return;
            try
            {
                string data = portInput.ReadExisting();
                lock (serialBuffer) { serialBuffer.Append(data); ProcessBuffer(); }
            }
            catch { }
        }

        private void ProcessBuffer()
        {
            string content = serialBuffer.ToString();
            if (content.IndexOf('\n') == -1) return;
            string[] lines = content.Split('\n');
            int validLinesCount = lines.Length - 1;
            int foundGear = -999;
            for (int i = 0; i < validLinesCount; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("CMD:"))
                {
                    try { foundGear = int.Parse(line.Substring(4)); } catch { }
                }
            }
            serialBuffer.Remove(0, content.LastIndexOf('\n') + 1);
            if (foundGear != -999)
            {
                int gameNotch = ConvertEsp32ToGameNotch(foundGear);
                if (lastProcessedEsp32Gear != foundGear)
                {
                    currentEsp32Notch = gameNotch;
                    lastProcessedEsp32Gear = foundGear;
                    SetManualNotch(gameNotch);
                }
            }
        }

        private void btn_close_Click(object sender, EventArgs e)
        {
            if (btn_open != null && btn_open.Enabled == true) { RefreshPorts(); return; }
            try
            {
                if (portInput != null) { portInput.DataReceived -= Port_DataReceived; if (portInput.IsOpen) portInput.Close(); portInput.Dispose(); }
                if (portDisplay != null) { if (portDisplay.IsOpen) portDisplay.Close(); portDisplay.Dispose(); }
            }
            catch { }
            portInput = null; portDisplay = null;
            if (btn_open != null) btn_open.Enabled = true;
            if (btn_close != null) { btn_close.Enabled = true; btn_close.Text = "Refresh"; }
            RefreshPorts();
        }

        private void SetTascBehavior(TascBehaviorState newBehavior)
        {
            if (this.labelTascStatus == null) return;
            if (currentTascBehavior == newBehavior) { if (newBehavior == TascBehaviorState.Holding) tascStationNotch = -2; return; }
            currentTascBehavior = newBehavior;
            switch (newBehavior)
            {
                case TascBehaviorState.Idle: labelTascStatus.Text = isTascMasterOn ? "TASC: Armed" : "TASC: OFF (Monitor)"; tascStationNotch = 0; break;
                case TascBehaviorState.Braking: labelTascStatus.Text = isTascMasterOn ? "TASC: Active (Braking)" : "TASC: Monitor (Braking)"; break;
                case TascBehaviorState.Holding: labelTascStatus.Text = isTascMasterOn ? "TASC: Holding (B1)" : "TASC: Monitor (Holding)"; tascStationNotch = -2; break;
            }
        }

        private void btn_tasc_toggle_Click(object sender, EventArgs e)
        {
            isTascMasterOn = !isTascMasterOn;
            if (isTascMasterOn) { btn_tasc_toggle.Text = "TASC: ON"; btn_tasc_toggle.BackColor = Color.LawnGreen; }
            else { btn_tasc_toggle.Text = "TASC: OFF"; btn_tasc_toggle.BackColor = SystemColors.Control; labelTascStatus.Text = "TASC: OFF"; SetTascBehavior(TascBehaviorState.Idle); }
        }

        private void btn_tasc_toggle_Click_1(object sender, EventArgs e) { btn_tasc_toggle_Click(sender, e); }

        private void btn_atc_toggle_Click(object sender, EventArgs e)
        {
            isAtcMasterOn = !isAtcMasterOn;
            if (isAtcMasterOn) { btn_atc_toggle.Text = "ATC: ON"; btn_atc_toggle.BackColor = Color.LawnGreen; }
            else { btn_atc_toggle.Text = "ATC: OFF"; btn_atc_toggle.BackColor = SystemColors.Control; }
        }

        private void button1_Click(object sender, EventArgs e) { }
        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e) { }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            TrainCrewInput.Dispose();
            if (portInput != null && portInput.IsOpen) portInput.Close();
            if (portDisplay != null && portDisplay.IsOpen) portDisplay.Close();
            if (speedMoniterWindow != null) speedMoniterWindow.Close();
            // 關閉 UDP
            if (udpClient != null) { udpClient.Close(); }
            // 關閉 UDP 接收器
            if (udpReceiver != null) { udpReceiver.Close(); }
        }

        StringBuilder sb = new StringBuilder();

        // === 主迴圈 (核心邏輯修正) ===
        private void Tim_Tick(object sender, EventArgs e)
        {
            var state = TrainCrewInput.GetTrainState();
            if (state == null) return;

            // 1. 加速度計算
            speedHistory.Enqueue(new Tuple<DateTime, double>(DateTime.Now, state.Speed));

            // 清除超過 0.5 秒前的舊資料
            while (speedHistory.Count > 0 && (DateTime.Now - speedHistory.Peek().Item1).TotalSeconds > 0.5)
            {
                speedHistory.Dequeue();
            }

            // 確保有足夠的歷史資料來計算
            if (speedHistory.Count > 1)
            {
                var oldest = speedHistory.Peek();
                double tDelta = (DateTime.Now - oldest.Item1).TotalSeconds;

                if (tDelta > 0)
                {
                    // 加速度 = (現在速度 - 0.5秒前的速度) / 經過時間
                    _currentAccel = (state.Speed - oldest.Item2) / tDelta;
                }
            }

            double currentSpeed = state.Speed;
            // ★ 計算包含補償的停車距離 (這是解決停車偏差的關鍵)
            double distanceToStop = state.nextStaDistance + TASC_STOP_ADJUST_M;
            double currentGrade = state.gradient;
            int manualPowerNotch = state.Pnotch;

            // UI 邏輯，動態限速計算
            float fStopPositionOffset = TASCUtils.GetStopPositionOffset(state);
            // 這裡也建議使用補償後的距離來計算 UI，不過原版似乎是用 nextStaDistance，維持原樣避免 UI 混亂
            float remainigDistance = state.nextStaDistance;
            float dist = remainigDistance > 0.0f ? remainigDistance : 0.0f;
            float xmlLimitSpeed = 0.0f;
            float xmlLimitDistance = 0.0f;
            TASCUtils.GetLimitSpeed(state, dist, fStopPositionOffset, out xmlLimitSpeed, out xmlLimitDistance);

            float safeSpeedCurrent = (state.speedLimit >= 0) ? state.speedLimit : 999f;
            float safeSpeedNextGame = 999f;
            float safeSpeedNextXml = 999f;

            if (state.nextSpeedLimit >= 0.0f && state.nextSpeedLimitDistance > 0)
                safeSpeedNextGame = (float)tasc.GetIdealSpeedForTarget(state.nextSpeedLimitDistance, state.nextSpeedLimit, state.gradient);

            if (xmlLimitSpeed < 120f && xmlLimitDistance > 0)
                safeSpeedNextXml = (float)tasc.GetIdealSpeedForTarget(xmlLimitDistance, xmlLimitSpeed, state.gradient);

            float limitToShow = safeSpeedCurrent;
            string dynamicSpeedTarget_Reason = "CurrentLimit";

            if (safeSpeedNextGame < limitToShow) { limitToShow = safeSpeedNextGame; dynamicSpeedTarget_Reason = "NextGame"; }
            if (safeSpeedNextXml < limitToShow) { limitToShow = safeSpeedNextXml; dynamicSpeedTarget_Reason = "NextXml"; }

            float gaugeTargetValue = (limitToShow >= 999f) ? -1f : limitToShow;

            // ATC
            int finalAtcNotch = 0;
            if (isAtcMasterOn && (limitToShow < 999f || state.nextSpeedLimit == 0))
            {
                finalAtcNotch = tasc.GetAtoNotchForATC(state, currentSpeed, limitToShow, currentGrade, state.Pnotch);
            }

            // 發車按鈕狀態管理
            bool departEnabled = false; // [新增變數追蹤是否可用]
            if (isAtoMasterOn)
            {
                if (currentSpeed > 1.0)
                {
                    if (isAtoDepartRequest) { isAtoDepartRequest = false; btn_depart.BackColor = Color.LightGray; }
                    btn_depart.Enabled = false;
                    departEnabled = false;
                }
                else 
                { 
                    btn_depart.Enabled = true;
                    departEnabled = true;
                }
            }
            else
            {
                btn_depart.Enabled = false;
                btn_depart.BackColor = SystemColors.Control;
                isAtoDepartRequest = false;
                departEnabled = false;
            }

            

            // 狀態機 (TASC)
            bool manualPowerOverride = (manualPowerNotch > 0);
            switch (currentTascBehavior)
            {
                case TascBehaviorState.Idle:
                    bool isPassThrough = (state.nextStopType == "通過");

                    // ★ 修正重點：恢復「以距離為準」的判斷 (解決藍色三角形消失問題)
                    // 只要進入距離範圍，強制切換到 Braking 狀態，這樣藍色三角形就會顯示
                    if (distanceToStop > TASC_STOP_MARGIN_M && distanceToStop <= TASC_ACTIVATION_DISTANCE_M && !isPassThrough)
                    {
                        SetTascBehavior(TascBehaviorState.Braking);
                        //tascStationNotch = atoRecommendedNotch;
                    }
                    break;

                case TascBehaviorState.Braking:
                    if (currentSpeed < 0.1 && distanceToStop < TASC_STOP_MARGIN_M)
                        SetTascBehavior(TascBehaviorState.Holding);
                    else if (distanceToStop > TASC_ACTIVATION_DISTANCE_M || distanceToStop < -5.0)
                        SetTascBehavior(TascBehaviorState.Idle);
                    else if (state.nextStopType == "通過")
                        SetTascBehavior(TascBehaviorState.Idle);
                    else
                    {
                        tascStationNotch = tasc.GetAtoNotch(currentSpeed, distanceToStop, currentGrade);
                    }
                    break;

                case TascBehaviorState.Holding:
                    if (manualPowerOverride)
                    {
                        SetTascBehavior(TascBehaviorState.Idle);
                        labelTascStatus.Text = "TASC: Released";
                    }
                    else if (isAtoMasterOn && isAtoDepartRequest)
                    {
                        SetTascBehavior(TascBehaviorState.Idle);
                    }
                    else
                        SetTascBehavior(TascBehaviorState.Holding);
                    break;
            }
            float tascExpectedSpeed = (currentTascBehavior == TascBehaviorState.Braking) ? (float)tasc.LastExpectedSpeedKmph : -1f;

            // ATO 邏輯整合
            bool doorsClosed = state.Lamps[PanelLamp.DoorClose];
            bool allowAccel = isAtoMasterOn && (currentSpeed > 0.5 || isAtoDepartRequest) && doorsClosed;
            int atoRecommendedNotch = ato.Update(state, (float)currentSpeed, (float)currentGrade, (float)distanceToStop, limitToShow,
                isTascMasterOn, allowAccel, tascExpectedSpeed, finalAtcNotch);
            atoRunningNotch = atoRecommendedNotch;

            // 輸出檔位
            int finalOutputNotch = 0;
            if (tascStationNotch < 0 || finalAtcNotch < 0)
            {
                // TASC 或是 ATC 要求煞車
                finalOutputNotch = Math.Min(tascStationNotch, finalAtcNotch);
            }
            else if (isAtoMasterOn)
            {
                // TASC 或是 ATC  沒有動作，交給 ATO 控制加速或下坡保護
                finalOutputNotch = atoRecommendedNotch;
            }
            else
            {
                // 兩者都沒開或沒有動作，維持 N 檔
                finalOutputNotch = 0;
            }
            
            TrainCrewInput.SetATO_Notch(finalOutputNotch);

            // UI 更新
            uiUpdateCounter++;
            if (uiUpdateCounter >= 5)
            {
                uiUpdateCounter = 0;
                UpdateUI(state, limitToShow, dynamicSpeedTarget_Reason, finalOutputNotch, tascStationNotch, finalAtcNotch, safeSpeedNextGame, safeSpeedNextXml, xmlLimitSpeed, xmlLimitDistance, gaugeTargetValue);

                float valBlue = (currentTascBehavior == TascBehaviorState.Braking) ? tascExpectedSpeed : -1f;
                float rawNextLimit = -1f;
                if (dynamicSpeedTarget_Reason == "NextGame") rawNextLimit = state.nextSpeedLimit;
                else if (dynamicSpeedTarget_Reason == "NextXml") rawNextLimit = xmlLimitSpeed;
                else
                {
                    bool isXmlValid = (xmlLimitSpeed > 0 && xmlLimitSpeed < 120f && xmlLimitDistance > 0);
                    bool isGameValid = (state.nextSpeedLimit >= 0 && state.nextSpeedLimit < 120f && state.nextSpeedLimitDistance > 0);
                    if (isXmlValid && isGameValid) rawNextLimit = (xmlLimitDistance <= state.nextSpeedLimitDistance) ? xmlLimitSpeed : state.nextSpeedLimit;
                    else if (isXmlValid) rawNextLimit = xmlLimitSpeed;
                    else if (isGameValid) rawNextLimit = state.nextSpeedLimit;
                }
                bool showWarning = (dynamicSpeedTarget_Reason == "NextGame" || dynamicSpeedTarget_Reason == "NextXml");

                // --- 同步懸浮視窗數據 ---
                if (speedMoniterWindow != null && !speedMoniterWindow.IsDisposed)
                {
                    //float valBlue = (currentTascBehavior == TascBehaviorState.Braking) ? tascExpectedSpeed : -1f;
                    //float rawNextLimit = -1f;
                    //if (dynamicSpeedTarget_Reason == "NextGame") rawNextLimit = state.nextSpeedLimit;
                    //else if (dynamicSpeedTarget_Reason == "NextXml") rawNextLimit = xmlLimitSpeed;
                    //else
                    //{
                    //    bool isXmlValid = (xmlLimitSpeed > 0 && xmlLimitSpeed < 120f && xmlLimitDistance > 0);
                    //    bool isGameValid = (state.nextSpeedLimit >= 0 && state.nextSpeedLimit < 120f && state.nextSpeedLimitDistance > 0);
                    //    if (isXmlValid && isGameValid) rawNextLimit = (xmlLimitDistance <= state.nextSpeedLimitDistance) ? xmlLimitSpeed : state.nextSpeedLimit;
                    //    else if (isXmlValid) rawNextLimit = xmlLimitSpeed;
                    //    else if (isGameValid) rawNextLimit = state.nextSpeedLimit;
                    //}
                    //bool showWarning = (dynamicSpeedTarget_Reason == "NextGame" || dynamicSpeedTarget_Reason == "NextXml");
                    speedMoniterWindow.UpdateData((float)state.Speed, valBlue, rawNextLimit, gaugeTargetValue, showWarning);
                    speedMoniterWindow.UpdateDepartState(departEnabled, isAtoDepartRequest);
                    //UpdateUI(state, limitToShow, dynamicSpeedTarget_Reason, finalOutputNotch, tascStationNotch, finalAtcNotch, safeSpeedNextGame, safeSpeedNextXml, xmlLimitSpeed, xmlLimitDistance, gaugeTargetValue);
                }

                // ★ 新增：發送 UDP 資料給 Python 儀表板 ★
                try
                {
                    // 1. 取得計算好的各項速度標記數值
                    // 取得計算好的各項數值 (如果沒有數值，我們用 -1 代表)
                    float udpcurrentSpeed = (float)state.Speed;
                    float tascBlueLimit = (currentTascBehavior == TascBehaviorState.Braking) ? tascExpectedSpeed : -1f;

                    // rawNextLimit 在上面你已經算好了，代表綠色的前方預告限速
                    float greenNextLimit = rawNextLimit;

                    // gaugeTargetValue 代表紅色的當前限速
                    float redCurrentLimit = gaugeTargetValue;

                    // === 2. 計算目前玩家的「手動檔位」數值 (-8 ~ 5) ===
                    int manualNotch = 0;
                    if (state.Bnotch > 0)
                    {
                        // Bnotch 對應: 8=非常(-8), 2=B1(-2), 1=抑速(-1)
                        manualNotch = -state.Bnotch;
                    }
                    else if (state.Pnotch > 0)
                    {
                        // Pnotch 對應: 1=P1(1) ~ 5=P5(5)
                        manualNotch = state.Pnotch;
                    }

                    // === 3. 根據遊戲規則，計算「最終實際作用 (Effective)」的檔位 ===
                    // finalOutputNotch 是你的 ATO/TASC 算出來要給電腦輸出的檔位
                    int effectiveNotch = 0;

                    if (finalOutputNotch > 0)
                    {
                        // ATO 出力 (Powering)
                        // 規則：只有在手動檔位置於 N 檔時，ATO 的出力才會生效
                        if (manualNotch == 0)
                        {
                            effectiveNotch = finalOutputNotch;
                        }
                        else
                        {
                            effectiveNotch = manualNotch;
                        }
                    }
                    else if (finalOutputNotch < 0)
                    {
                        // ATO 煞車 (Braking)
                        // 規則：套用手動與 ATO 之中「煞車力道較大」的那一個
                        // 由於煞車是以負數表示 (-8 最強，-1 最弱)，數值越小代表煞車越強，因此使用 Math.Min
                        effectiveNotch = Math.Min(manualNotch, finalOutputNotch);
                    }
                    else
                    {
                        // ATO 為 N 檔 (0) 時，完全聽從玩家手動把手
                        effectiveNotch = manualNotch;
                    }

                    // === 4. 將計算出的數值，轉換為 Python 儀表板認得的字串 ===
                    string appliedNotchStr = "N";
                    if (effectiveNotch == -8)
                        appliedNotchStr = "非常";
                    else if (effectiveNotch <= -2)
                        appliedNotchStr = "B" + (-effectiveNotch - 1);
                    else if (effectiveNotch == -1)
                        appliedNotchStr = "抑速";
                    else if (effectiveNotch > 0)
                        appliedNotchStr = "P" + effectiveNotch;

                    // 計算按鈕狀態碼 (0=禁用/暗灰, 1=可按/淺灰, 2=已按下/黃色)
                    int departStateCode = 0;
                    if (!departEnabled) departStateCode = 0;
                    else if (isAtoDepartRequest) departStateCode = 2;
                    else departStateCode = 1;

                    // 格式化為字串："速度,藍色TASC,綠色預告,紅色限速" (例如："120.5,-1,60,110")
                    string udpMsg = $"{udpcurrentSpeed:F1}," +
                        $"{tascBlueLimit:F1}," +
                        $"{greenNextLimit:F1}," +
                        $"{redCurrentLimit:F1}," +
                        $"{appliedNotchStr}," +
                        $"{_currentAccel:F2}," +
                        $"{currentGrade:F1}," +     // 第 7 項：坡度
                        $"{state.CarStates[0].BC_Press.ToString("0"):F1}," +          // 第 8 項：BC
                        $"{state.CarStates[1].BC_Press.ToString("0"):F1}," +
                        $"{state.MR_Press.ToString("0"):F1}," +     // 第 10 項：MR
                        $"{state.CarStates[0].Ampare.ToString("0"):F1}," +
                        $"{state.CarStates[1].Ampare.ToString("0"):F1}," +
                        $"{departStateCode}";            

                    byte[] sendBytes = Encoding.UTF8.GetBytes(udpMsg);
                    // 傳送到本機的 5000 埠號
                    udpClient.Send(sendBytes, sendBytes.Length, "127.0.0.1", 5000);
                }
                catch { }


                // ESP32 通訊
                if (portDisplay != null && portDisplay.IsOpen)
                {
                    try
                    {
                        float valSpeed = (float)state.Speed;
                        //float valBlue = -1;
                        if (currentTascBehavior == TascBehaviorState.Braking) valBlue = tascExpectedSpeed;
                        float valGreen = -1;
                        // ESP32 Logic
                        bool isXmlValid = (xmlLimitSpeed > 0 && xmlLimitSpeed < 120f && xmlLimitDistance > 0);
                        bool isGameValid = (state.nextSpeedLimit >= 0 && state.nextSpeedLimit < 120f && state.nextSpeedLimitDistance > 0);
                        if (dynamicSpeedTarget_Reason == "NextGame") valGreen = state.nextSpeedLimit;
                        else if (dynamicSpeedTarget_Reason == "NextXml") valGreen = xmlLimitSpeed;
                        else if (isXmlValid && isGameValid) valGreen = (xmlLimitDistance <= state.nextSpeedLimitDistance) ? xmlLimitSpeed : state.nextSpeedLimit;
                        else if (isXmlValid) valGreen = xmlLimitSpeed;
                        else if (isGameValid) valGreen = state.nextSpeedLimit;

                        float valRed = (limitToShow >= 999f) ? -1 : limitToShow;
                        string gearStr = "N";
                        if (state.Bnotch == 8) gearStr = "EB";
                        else if (state.Bnotch > 1) gearStr = "B" + (state.Bnotch - 1);
                        else if (state.Bnotch == 1) gearStr = "HOLD";
                        else if (state.Pnotch > 0) gearStr = "P" + state.Pnotch;
                        string cmd = string.Format("G:{0:0.0},{1:0.0},{2:0.0},{3:0.0},{4}", valSpeed, valBlue, valGreen, valRed, gearStr);
                        portDisplay.WriteLine(cmd);
                    }
                    catch { }
                }
            }
        }

        private async void StartUdpReceiver()
        {
            try
            {
                // 綁定 Port 5001 進行監聽 (需與 Python 設定的 target_address 一致)
                udpReceiver = new UdpClient(5001);

                while (true)
                {
                    // 非同步等待接收封包，不會卡死 UI
                    UdpReceiveResult result = await udpReceiver.ReceiveAsync();
                    string message = Encoding.UTF8.GetString(result.Buffer);

                    // 如果收到的字串是 "DEPART"
                    if (message == "DEPART")
                    {
                        // 因為 UDP 接收是在背景執行緒，要修改 UI 與主程式狀態必須透過 Invoke 切回主執行緒
                        this.Invoke(new Action(() =>
                        {
                            // 執行與按下 C# 實體按鈕相同的邏輯
                            if (isAtoMasterOn)
                            {
                                isAtoDepartRequest = true;
                                btn_depart.BackColor = Color.Yellow;
                                Console.WriteLine("收到 Python 發車訊號！");
                            }
                        }));
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // 當程式關閉，udpReceiver 被 Close() 時會觸發此例外，正常忽略即可
            }
            catch (Exception ex)
            {
                Console.WriteLine("UDP 接收發生錯誤: " + ex.Message);
            }
        }

        private void UpdateUI(TrainState state, float limitToShow, string dynamicSpeedTarget_Reason, int finalOutputNotch, int effectiveTascNotch, int effectiveAtcNotch, float safeSpeedNextGame, float safeSpeedNextXml, float xmlLimitSpeed, float xmlLimitDistance, float gaugeTargetValue)
        {
            try
            {
                this.speedGauge.Value = (float)state.Speed;
                if (currentTascBehavior == TascBehaviorState.Braking) { this.speedGauge.TargetValue = (float)tasc.LastExpectedSpeedKmph; this.speedGauge.TargetValueColor = Color.Blue; }
                else { this.speedGauge.TargetValue = -1; }

                float rawNextLimit = -1f;
                if (dynamicSpeedTarget_Reason == "NextGame") rawNextLimit = state.nextSpeedLimit;
                else if (dynamicSpeedTarget_Reason == "NextXml") rawNextLimit = xmlLimitSpeed; 
                else
                {
                    bool isXmlValid = (xmlLimitSpeed > 0 && xmlLimitSpeed < 120f && xmlLimitDistance > 0);
                    bool isGameValid = (state.nextSpeedLimit >= 0 && state.nextSpeedLimit < 120f && state.nextSpeedLimitDistance > 0);
                    if (isXmlValid && isGameValid) rawNextLimit = (xmlLimitDistance <= state.nextSpeedLimitDistance) ? xmlLimitSpeed : state.nextSpeedLimit;
                    else if (isXmlValid) rawNextLimit = xmlLimitSpeed;
                    else if (isGameValid) rawNextLimit = state.nextSpeedLimit;
                }
                this.speedGauge.TargetValue2 = rawNextLimit;
                this.speedGauge.TargetValue2Color = Color.LimeGreen;
                this.speedGauge.TargetValue3 = gaugeTargetValue;
                this.speedGauge.TargetValue3Color = Color.Red;
                this.speedGauge.Value2 = -1;
            }
            catch { }

            try
            {
                if (state.CarStates != null && state.CarStates.Count > 0) { this.pressureGauge.Value = state.CarStates[0].BC_Press; this.pressureGauge.Value3 = state.CarStates[1].BC_Press; }
                this.pressureGauge.Value2 = state.MR_Press;
                this.pressureGauge.TargetValue = -1; this.pressureGauge.TargetValue2 = -1; this.pressureGauge.TargetValue3 = -1;
            }
            catch { }

            try
            {
                sb.Clear();
                sb.AppendLine("速度：" + state.Speed.ToString("0.0") + "km/h");
                string accelSign = _currentAccel > 0 ? "+" : "";
                sb.AppendLine("加速度：" + accelSign + _currentAccel.ToString("0.00") + "km/h/s");
                if (currentTascBehavior == TascBehaviorState.Braking) sb.AppendLine("TASC理想：" + tasc.LastExpectedSpeedKmph.ToString("0.0") + "km/h");
                if (limitToShow < 999)
                {
                    if (dynamicSpeedTarget_Reason == "NextGame" || dynamicSpeedTarget_Reason == "NextXml") sb.AppendLine("建議速度：" + limitToShow.ToString("0.0") + "km/h");
                    else sb.AppendLine("目前限速：" + limitToShow.ToString("0.0") + "km/h");
                }
                sb.AppendLine("勾配：" + state.gradient.ToString("0.0") + "‰");
                sb.AppendLine("MR圧力：" + state.MR_Press.ToString("0") + "kPa");
                sb.AppendLine("殘距離：" + state.nextStaDistance.ToString("0.0") + "M " + state.nextStopType);
                if (state.CarStates != null && state.CarStates.Count > 0)
                {
                    sb.AppendLine("電流：" + state.CarStates[0].Ampare.ToString("0") + "A");
                    sb.AppendLine("電流：" + state.CarStates[1].Ampare.ToString("0") + "A");
                    sb.AppendLine("BC壓力：" + state.CarStates[0].BC_Press.ToString("0") + "kPa");
                    sb.AppendLine("BC壓力：" + state.CarStates[1].BC_Press.ToString("0") + "kPa");
                }
                label1.Text = sb.ToString();
            }
            catch { label1.Text = "Info Error"; }

            try
            {
                sb.Clear();
                if (dynamicSpeedTarget_Reason == "NextGame")
                {
                    sb.AppendLine("●預告：" + state.nextSpeedLimitDistance.ToString("0.0") + "M " + state.nextSpeedLimit.ToString("0.0") + "km/h");
                }
                else
                {
                    sb.AppendLine("預告：" + state.nextSpeedLimitDistance.ToString("0.0") + "M " + state.nextSpeedLimit.ToString("0.0") + "km/h");
                }
                if (dynamicSpeedTarget_Reason == "NextXml")
                {
                    sb.AppendLine("●預告：" + xmlLimitDistance.ToString("0.0") + "M " + xmlLimitSpeed.ToString("0.0") + "km/h");
                }
                else
                {
                    sb.AppendLine("預告：" + xmlLimitDistance.ToString("0.0") + "M " + xmlLimitSpeed.ToString("0.0") + "km/h");
                }

                sb.AppendLine("曲線G：" + (safeSpeedNextGame < 999 ? safeSpeedNextGame.ToString("0.0") : "---"));
                sb.AppendLine("曲線X：" + (safeSpeedNextXml < 999 ? safeSpeedNextXml.ToString("0.0") : "---"));
                sb.AppendLine((state.Lamps[PanelLamp.DoorClose] ? "●" : "○") + "戶　閉");
                sb.AppendLine((state.Lamps[PanelLamp.ATS_Ready] ? "●" : "○") + "ATS正常");
                sb.AppendLine((state.Lamps[PanelLamp.ATS_BrakeApply] ? "●" : "○") + "ATS動作");
                sb.AppendLine((state.Lamps[PanelLamp.ATS_Open] ? "●" : "○") + "ATS開放");
                sb.AppendLine((state.Lamps[PanelLamp.RegenerativeBrake] ? "●" : "○") + "回　生");
                sb.AppendLine((state.Lamps[PanelLamp.EB_Timer] ? "●" : "○") + "E　B");
                sb.AppendLine((state.Lamps[PanelLamp.EmagencyBrake] ? "●" : "○") + "非常ﾌﾞﾚｰｷ");
                sb.AppendLine((state.Lamps[PanelLamp.Overload] ? "●" : "○") + "過負荷");
                labelPnl.Text = sb.ToString();
            }
            catch { labelPnl.Text = "Panel Error"; }

            try
            {
                sb.Clear();
                sb.AppendLine(state.ATS_Class);
                bool show = (Environment.TickCount % 1000 >= 500);
                if (state.ATS_State.Contains("接近") || state.ATS_State.Contains("動作") || state.ATS_State == "EB") { } else { show = true; }
                sb.AppendLine(state.ATS_Speed);
                sb.AppendLine(show ? state.ATS_State : "");
                if (state.CarStates != null && state.CarStates.Count > 0)
                {
                    for (int i = 0; i < state.CarStates.Count; i++)
                        sb.AppendLine((state.CarStates[i].DoorClose ? "●" : "○") + (i + 1) + "號車 戶閉");
                }
                labelATS.Text = sb.ToString();
            }
            catch { labelATS.Text = "ATS Error"; }

            try
            {
                sb.Clear();
                sb.AppendLine(state.NowTime.ToString());
                sb.AppendLine("殘距離：" + state.nextStaDistance.ToString("0.0") + "M " + state.nextStopType);
                if (state.stationList != null && state.stationList.Count > 0)
                {
                    for (int i = 0; i < state.stationList.Count; i++)
                    {
                        if (state.nextStaName == state.stationList[i].Name)
                        {
                            sb.AppendLine("誤　　差：" + (state.stationList[i].ArvTime - state.NowTime));
                            sb.AppendLine(state.stationList[i].Name + ":" + state.stationList[i].ArvTime + " " + state.stationList[i].stopType);
                        }
                    }
                }
                sb.AppendLine("列車編號：" + state.diaName + GetDirection(state));
                sb.AppendLine("種　　別：" + state.Class);
                sb.AppendLine("行　　先：" + state.BoundFor);
                label2.Text = sb.ToString();
            }
            catch { label2.Text = "Drive Info Error"; }

            try
            {
                sb.Clear();
                if (dynamicSpeedTarget_Reason == "NextGame" || dynamicSpeedTarget_Reason == "NextXml")
                {
                    // 預告狀態
                    float limitVal = (dynamicSpeedTarget_Reason == "NextGame") ? state.nextSpeedLimit : xmlLimitSpeed;
                    sb.AppendLine("前方預告: " + limitVal);
                    label.Text = sb.ToString();
                    label.Visible = true;

                    // 判斷預告狀態下是否超速
                    if (gaugeTargetValue > 0 && state.Speed > gaugeTargetValue)
                    {
                        // 超速時：背景在「紅色」與「橘色」之間交替閃爍
                        if ((Environment.TickCount % 500) < 250)
                        {
                            label.BackColor = Color.Red;
                            label.ForeColor = Color.White;
                        }
                        else
                        {
                            label.BackColor = Color.Orange;
                            label.ForeColor = Color.Black;
                        }
                    }
                    else
                    {
                        // 未超速單純顯示預告：保持橘色閃爍
                        label.BackColor = Color.Orange;
                        label.ForeColor = Color.Black;
                        bool isVisible = (Environment.TickCount % 500) < 250;
                        label.Visible = isVisible;
                    }
                }
                else
                {
                    // 正常狀態
                    if (gaugeTargetValue > 0 && state.Speed > gaugeTargetValue)
                    {
                        // 正常狀態下且超速時：顯示「速度限制: XX」並讓背景變為紅色
                        float limitVal = gaugeTargetValue; // 紅色三角形代表的目前速限
                        sb.AppendLine("速度限制: " + limitVal);
                        label.Text = sb.ToString();
                        label.BackColor = Color.Red;
                        label.ForeColor = Color.White;
                        bool isVisible = (Environment.TickCount % 500) < 250;
                        label.Visible = isVisible;
                    }
                    else
                    {
                        // 正常狀態下且沒有超速：維持空白及預設背景色
                        label.Text = "";
                        label.BackColor = SystemColors.Control;
                        label.Visible = true; // 將這行移進來這裡
                    }
                    //label.Visible = true;
                }
            }
            catch { }

            try
            {
                string atoLabel = "ATO:";

                // 判斷最終輸出的來源標籤 (邏輯保持不變，用於顯示 OUT)
                if (effectiveTascNotch < 0 && effectiveTascNotch < effectiveAtcNotch)
                {
                    atoLabel = "TASC:";
                }
                else if (effectiveAtcNotch < 0 && effectiveAtcNotch < effectiveTascNotch)
                {
                    atoLabel = "ATC:";
                }

                sb.Clear();

                // === 修改開始：顯示詳細的除錯資訊 ===
                // 格式：ATO:P5 ATC:N TASC:B2
                // 這樣你可以一眼看出三者目前的狀態
                sb.Append("ATO:" + ConvertNotchToString(atoRunningNotch) + " ");
                sb.Append("ATC:" + ConvertNotchToString(effectiveAtcNotch) + " ");
                sb.Append("TASC:" + ConvertNotchToString(effectiveTascNotch) + " ");

                // 顯示最終輸出 (Winner)
                sb.AppendLine(atoLabel + ConvertNotchToString(finalOutputNotch));
                sb.AppendLine("Notch:" + CalNotch(state.Pnotch, state.Bnotch));
                sb.AppendLine("Handle:" + ConvertNotchToString(currentEsp32Notch));
                labelNotch.Text = sb.ToString();
            }
            catch { }
        }

        private int ConvertEsp32ToGameNotch(int rawGear)
        {
            if (rawGear > GEAR_N_INDEX) return rawGear - GEAR_N_INDEX;
            else if (rawGear == GEAR_N_INDEX) return 0;
            else if (HAS_HOLD && rawGear == (GEAR_N_INDEX - 1)) return -1;
            else if (rawGear == 0) return -8;
            else
            {
                int notch = rawGear - GEAR_N_INDEX;
                if (notch <= -8) notch = -7;
                return notch;
            }
        }
        private void SetManualNotch(int notch) { TrainCrewInput.SetNotch(notch); }
        private string ConvertNotchToString(int notch)
        {
            if (notch == -8) return "EB";
            if (notch == -1) return "抑速";
            if (notch == 0) return "N";
            if (notch > 0) return "P" + notch;
            if (notch < -1) return "B" + (-notch - 1);
            return "---";
        }
        private string CalNotch(int P, int B)
        {
            if (B == 8) return "EB";
            if (B > 1) return "B" + (B - 1);
            if (B == 1) return "抑速";
            if (P == 0 && B == 0) return "N";
            if (P > 0) return "P" + P;
            return "---";
        }
        private string GetDirection(TrainState _state)
        {
            string direction = "下り";
            if (!string.IsNullOrEmpty(_state.diaName))
            {
                var match = System.Text.RegularExpressions.Regex.Match(_state.diaName, @"\d+");
                if (match.Success && int.TryParse(match.Value, out int num) && num > 0) { direction = num % 2 == 0 ? "上り" : "下り"; }
            }
            return direction;
        }
    }
}