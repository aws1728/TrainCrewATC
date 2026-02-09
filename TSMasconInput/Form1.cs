using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Text;
using System.Windows.Forms;
using TrainCrew;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

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

        // === 3. TASC/ATC 變數 ===
        Timer tim = new Timer();
        private SimpleTASC tasc;
        private bool isTascMasterOn = true;
        private bool isAtcMasterOn = false;

        private enum TascBehaviorState { Idle, Braking, Holding }
        private TascBehaviorState currentTascBehavior = TascBehaviorState.Idle;
        private int tascStationNotch = 0;

        // === 4. 加速度計算變數 ===
        private double _lastAccelSpeed = 0.0;
        private DateTime _lastAccelTime = DateTime.Now;
        private double _currentAccel = 0.0;

        // UI 更新計數器 (降頻用)
        private int uiUpdateCounter = 0;

        private const double MY_MAX_BRAKE_DECEL_KMPHPS = 3.7;
        private const int MY_BRAKE_NOTCHES = 6;
        private const double TASC_ACTIVATION_DISTANCE_M = 800.0;
        private const double TASC_STOP_MARGIN_M = 0.18;
        private const double TASC_STOP_ADJUST_M = 0.15;
        private const double ATC_BUFFER_KMPH = 0.0;

        // === 5. 懸浮視窗變數 ===
        private SpeedMoniter speedMoniterWindow;

        public Form1()
        {
            InitializeComponent();
            TrainCrewInput.Init();
            FormClosed += Form1_FormClosed;
            this.TopMost = true;

            currentEsp32Notch = GEAR_N_INDEX;
            tasc = new SimpleTASC(MY_MAX_BRAKE_DECEL_KMPHPS, MY_BRAKE_NOTCHES);

            // 按鈕初始化
            this.btn_tasc_toggle.Click += new System.EventHandler(this.btn_tasc_toggle_Click);
            btn_tasc_toggle.Text = "TASC: ON";
            btn_tasc_toggle.BackColor = Color.LawnGreen;
            labelTascStatus.Text = "TASC: Armed";

            this.btn_atc_toggle.Click += new System.EventHandler(this.btn_atc_toggle_Click);
            btn_atc_toggle.Text = "ATC: OFF";
            btn_atc_toggle.BackColor = SystemColors.Control;

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
                this.btn_close.Enabled = true; // 預設開啟，讓使用者可以按
                this.btn_close.Text = "Refreah"; // 提示現在的功能是刷新
            }

            // === 啟動時自動顯示 SpeedMoniter ===
            speedMoniterWindow = new SpeedMoniter();
            speedMoniterWindow.Show();

            // Timer 設定
            tim.Tick += Tim_Tick;
            tim.Interval = 15;
            tim.Start();
        }

        // === 刷新 COM Port 列表的函式 ===
        private void RefreshPorts()
        {
            string lastMotor = comboBoxMotor != null ? comboBoxMotor.Text : "";
            string lastDisplay = comboBoxDisplay != null ? comboBoxDisplay.Text : "";

            if (comboBoxMotor != null) comboBoxMotor.Items.Clear();
            if (comboBoxDisplay != null) comboBoxDisplay.Items.Clear();

            // 加入 "不使用" 選項
            if (comboBoxMotor != null) comboBoxMotor.Items.Add("控制器");
            if (comboBoxDisplay != null) comboBoxDisplay.Items.Add("時速表");

            var names = SerialPort.GetPortNames();
            foreach (var nm in names)
            {
                if (comboBoxMotor != null) comboBoxMotor.Items.Add(nm);
                if (comboBoxDisplay != null) comboBoxDisplay.Items.Add(nm);
            }

            // 恢復上次選擇或設為預設
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

        // === 控制 SpeedMoniter 顯示/隱藏的按鈕事件 ===
        private void btn_monitor_toggle_Click(object sender, EventArgs e)
        {
            if (speedMoniterWindow == null || speedMoniterWindow.IsDisposed)
            {
                speedMoniterWindow = new SpeedMoniter();
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

        // === ESP32 連線 (事件驅動) ===
        private void btn_open_Click(object sender, EventArgs e)
        {
            try
            {
                // 1. 開啟接收檔位用的 Port (Input)
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
                else
                {
                    portInput = null;
                }

                // 2. 開啟顯示器用的 Port (Display)
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
                else
                {
                    portDisplay = null;
                }

                // 更新按鈕狀態
                if (btn_open != null) btn_open.Enabled = false;

                // === [修改 2] 連線成功後，設定按鈕為「斷線」功能 ===
                if (btn_close != null)
                {
                    btn_close.Enabled = true;
                    btn_close.Text = "Disconnect";
                }

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
                lock (serialBuffer)
                {
                    serialBuffer.Append(data);
                    ProcessBuffer();
                }
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
                    try
                    {
                        string valStr = line.Substring(4);
                        foundGear = int.Parse(valStr);
                    }
                    catch { }
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

        // === [修改 3] 斷線按鈕邏輯：兼具斷線與刷新功能 ===
        private void btn_close_Click(object sender, EventArgs e)
        {
            // 情況A：如果目前根本沒連線 (btn_open 是 enabled 的)，那這個按鈕就當作「刷新鍵」使用
            if (btn_open != null && btn_open.Enabled == true)
            {
                RefreshPorts();
                return;
            }

            // 情況B：如果已連線，則執行斷線動作
            try
            {
                if (portInput != null)
                {
                    portInput.DataReceived -= Port_DataReceived;
                    if (portInput.IsOpen) portInput.Close();
                    portInput.Dispose();
                }
                if (portDisplay != null)
                {
                    if (portDisplay.IsOpen) portDisplay.Close();
                    portDisplay.Dispose();
                }
            }
            catch { }
            portInput = null;
            portDisplay = null;

            // 恢復「連線」按鈕
            if (btn_open != null) btn_open.Enabled = true;

            // 確保「斷線/刷新」按鈕保持開啟，並改回文字
            if (btn_close != null)
            {
                btn_close.Enabled = true;
                btn_close.Text = "Refresh";
            }

            // 斷線後順便刷新一次
            RefreshPorts();
        }

        private void SetTascBehavior(TascBehaviorState newBehavior)
        {
            if (this.labelTascStatus == null) return;
            if (currentTascBehavior == newBehavior)
            {
                if (newBehavior == TascBehaviorState.Holding) tascStationNotch = -2;
                return;
            }
            currentTascBehavior = newBehavior;
            switch (newBehavior)
            {
                case TascBehaviorState.Idle:
                    labelTascStatus.Text = isTascMasterOn ? "TASC: Armed" : "TASC: OFF (Monitor)";
                    tascStationNotch = 0;
                    break;
                case TascBehaviorState.Braking:
                    labelTascStatus.Text = isTascMasterOn ? "TASC: Active (Braking)" : "TASC: Monitor (Braking)";
                    break;
                case TascBehaviorState.Holding:
                    labelTascStatus.Text = isTascMasterOn ? "TASC: Holding (B1)" : "TASC: Monitor (Holding)";
                    tascStationNotch = -2;
                    break;
            }
        }

        private void btn_tasc_toggle_Click(object sender, EventArgs e)
        {
            isTascMasterOn = !isTascMasterOn;
            if (isTascMasterOn)
            {
                btn_tasc_toggle.Text = "TASC: ON";
                btn_tasc_toggle.BackColor = Color.LawnGreen;
            }
            else
            {
                btn_tasc_toggle.Text = "TASC: OFF";
                btn_tasc_toggle.BackColor = SystemColors.Control;
                labelTascStatus.Text = "TASC: OFF";
                SetTascBehavior(TascBehaviorState.Idle);
            }
        }

        private void btn_atc_toggle_Click(object sender, EventArgs e)
        {
            isAtcMasterOn = !isAtcMasterOn;
            if (isAtcMasterOn)
            {
                btn_atc_toggle.Text = "ATC: ON";
                btn_atc_toggle.BackColor = Color.LawnGreen;
            }
            else
            {
                btn_atc_toggle.Text = "ATC: OFF";
                btn_atc_toggle.BackColor = SystemColors.Control;
            }
        }

        private void button1_Click(object sender, EventArgs e) { }
        private void btn_tasc_toggle_Click_1(object sender, EventArgs e) { }
        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e) { }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            TrainCrewInput.Dispose();
            if (portInput != null && portInput.IsOpen) portInput.Close();
            if (portDisplay != null && portDisplay.IsOpen) portDisplay.Close();
            if (speedMoniterWindow != null) speedMoniterWindow.Close();
        }

        StringBuilder sb = new StringBuilder();

        // === 主迴圈 ===
        private void Tim_Tick(object sender, EventArgs e)
        {
            var state = TrainCrewInput.GetTrainState();
            if (state == null) return;

            // 1. 加速度計算
            double timeDelta = (DateTime.Now - _lastAccelTime).TotalSeconds;
            if (timeDelta >= 0.25)
            {
                double speedDelta = state.Speed - _lastAccelSpeed;
                _currentAccel = speedDelta / timeDelta;
                _lastAccelSpeed = state.Speed;
                _lastAccelTime = DateTime.Now;
            }

            double currentSpeed = state.Speed;
            double distanceToStop = state.nextStaDistance + TASC_STOP_ADJUST_M;
            double currentGrade = state.gradient;
            int manualPowerNotch = state.Pnotch;

            // 2. TASC 邏輯
            bool manualPowerOverride = (manualPowerNotch > 0);

            switch (currentTascBehavior)
            {
                case TascBehaviorState.Idle:
                    bool isPassThrough = (state.nextStopType == "通過");
                    if (distanceToStop > TASC_STOP_MARGIN_M &&
                        distanceToStop <= TASC_ACTIVATION_DISTANCE_M &&
                        !isPassThrough)
                    {
                        SetTascBehavior(TascBehaviorState.Braking);
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
                        tascStationNotch = tasc.GetAtoNotch(currentSpeed, distanceToStop, currentGrade);
                    break;

                case TascBehaviorState.Holding:
                    if (manualPowerOverride)
                    {
                        SetTascBehavior(TascBehaviorState.Idle);
                        labelTascStatus.Text = "TASC: Released";
                    }
                    else
                        SetTascBehavior(TascBehaviorState.Holding);
                    break;
            }

            // 3. 計算速限與目標 (用於儀表顯示與 ATC)
            float fStopPositionOffset = TASCUtils.GetStopPositionOffset(state);
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

            // 4. ATC 邏輯
            int finalAtcNotch = 0;
            if (isAtcMasterOn && limitToShow < 999f)
            {
                finalAtcNotch = tasc.GetAtoNotchForATC(currentSpeed, limitToShow, currentGrade, state.Pnotch);
            }

            // 5. 輸出檔位
            int effectiveTascNotch = isTascMasterOn ? tascStationNotch : 0;
            int effectiveAtcNotch = isAtcMasterOn ? finalAtcNotch : 0;
            int finalOutputNotch = Math.Min(effectiveTascNotch, effectiveAtcNotch);
            TrainCrewInput.SetATO_Notch(finalOutputNotch);

            // 6. UI 更新與 ESP32 通訊 (降頻處理)
            uiUpdateCounter++;
            if (uiUpdateCounter >= 5) // 約 75ms 更新一次
            {
                uiUpdateCounter = 0;
                UpdateUI(state, limitToShow, dynamicSpeedTarget_Reason, finalOutputNotch, effectiveTascNotch, effectiveAtcNotch, safeSpeedNextGame, safeSpeedNextXml, xmlLimitSpeed, xmlLimitDistance, gaugeTargetValue);

                // --- 同步懸浮視窗數據 ---
                if (speedMoniterWindow != null && !speedMoniterWindow.IsDisposed)
                {
                    float valBlue = (currentTascBehavior == TascBehaviorState.Braking) ? (float)tasc.LastExpectedSpeedKmph : -1f;

                    float rawNextLimit = -1f;
                    if (dynamicSpeedTarget_Reason == "NextGame") rawNextLimit = state.nextSpeedLimit;
                    else if (dynamicSpeedTarget_Reason == "NextXml") rawNextLimit = xmlLimitSpeed;
                    else
                    {
                        bool isXmlValid = (xmlLimitSpeed > 0 && xmlLimitSpeed < 120f && xmlLimitDistance > 0);
                        bool isGameValid = (state.nextSpeedLimit >= 0 && state.nextSpeedLimit < 120f && state.nextSpeedLimitDistance > 0);
                        if (isXmlValid && isGameValid)
                            rawNextLimit = (xmlLimitDistance <= state.nextSpeedLimitDistance) ? xmlLimitSpeed : state.nextSpeedLimit;
                        else if (isXmlValid) rawNextLimit = xmlLimitSpeed;
                        else if (isGameValid) rawNextLimit = state.nextSpeedLimit;
                    }

                    // 判斷是否需要顯示警告
                    bool showWarning = (dynamicSpeedTarget_Reason == "NextGame" || dynamicSpeedTarget_Reason == "NextXml");

                    // 呼叫更新
                    speedMoniterWindow.UpdateData((float)state.Speed, valBlue, rawNextLimit, gaugeTargetValue, showWarning);
                }

                // ESP32 通訊
                if (portDisplay != null && portDisplay.IsOpen)
                {
                    try
                    {
                        float valSpeed = (float)state.Speed;
                        float valBlue = -1;
                        if (currentTascBehavior == TascBehaviorState.Braking)
                            valBlue = (float)tasc.LastExpectedSpeedKmph;

                        float valGreen = -1;
                        bool isXmlValid = (xmlLimitSpeed > 0 && xmlLimitSpeed < 120f && xmlLimitDistance > 0);
                        bool isGameValid = (state.nextSpeedLimit >= 0 && state.nextSpeedLimit < 120f && state.nextSpeedLimitDistance > 0);

                        if (dynamicSpeedTarget_Reason == "NextGame") valGreen = state.nextSpeedLimit;
                        else if (dynamicSpeedTarget_Reason == "NextXml") valGreen = xmlLimitSpeed;
                        else if (isXmlValid && isGameValid)
                            valGreen = (xmlLimitDistance <= state.nextSpeedLimitDistance) ? xmlLimitSpeed : state.nextSpeedLimit;
                        else if (isXmlValid) valGreen = xmlLimitSpeed;
                        else if (isGameValid) valGreen = state.nextSpeedLimit;

                        float valRed = (limitToShow >= 999f) ? -1 : limitToShow;

                        string gearStr = "N";
                        if (state.Bnotch == 8) gearStr = "EB";
                        else if (state.Bnotch > 1) gearStr = "B" + (state.Bnotch - 1);
                        else if (state.Bnotch == 1) gearStr = "HOLD";
                        else if (state.Pnotch > 0) gearStr = "P" + state.Pnotch;
                        else gearStr = "N";

                        string cmd = string.Format("G:{0:0.0},{1:0.0},{2:0.0},{3:0.0},{4}",
                            valSpeed, valBlue, valGreen, valRed, gearStr);

                        portDisplay.WriteLine(cmd);
                    }
                    catch { }
                }
            }
        }

        private void UpdateUI(TrainState state, float limitToShow, string dynamicSpeedTarget_Reason, int finalOutputNotch, int effectiveTascNotch, int effectiveAtcNotch, float safeSpeedNextGame, float safeSpeedNextXml, float xmlLimitSpeed, float xmlLimitDistance, float gaugeTargetValue)
        {
            try
            {
                this.speedGauge.Value = (float)state.Speed;
                if (currentTascBehavior == TascBehaviorState.Braking)
                {
                    this.speedGauge.TargetValue = (float)tasc.LastExpectedSpeedKmph;
                    this.speedGauge.TargetValueColor = Color.Blue;
                }
                else
                {
                    this.speedGauge.TargetValue = -1;
                }

                float rawNextLimit = -1f;
                if (dynamicSpeedTarget_Reason == "NextGame") rawNextLimit = state.nextSpeedLimit;
                else if (dynamicSpeedTarget_Reason == "NextXml") rawNextLimit = xmlLimitSpeed;
                else
                {
                    bool isXmlValid = (xmlLimitSpeed > 0 && xmlLimitSpeed < 120f && xmlLimitDistance > 0);
                    bool isGameValid = (state.nextSpeedLimit >= 0 && state.nextSpeedLimit < 120f && state.nextSpeedLimitDistance > 0);
                    if (isXmlValid && isGameValid)
                        rawNextLimit = (xmlLimitDistance <= state.nextSpeedLimitDistance) ? xmlLimitSpeed : state.nextSpeedLimit;
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
                if (state.CarStates != null && state.CarStates.Count > 0)
                {
                    this.pressureGauge.Value = state.CarStates[0].BC_Press;
                    this.pressureGauge.Value3 = state.CarStates[1].BC_Press;
                }
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
                if (currentTascBehavior == TascBehaviorState.Braking)
                    sb.AppendLine("TASC理想：" + tasc.LastExpectedSpeedKmph.ToString("0.0") + "km/h");

                if (limitToShow < 999)
                {
                    if (dynamicSpeedTarget_Reason == "NextGame" || dynamicSpeedTarget_Reason == "NextXml")
                        sb.AppendLine("建議速度：" + limitToShow.ToString("0.0") + "km/h");
                    else
                        sb.AppendLine("目前限速：" + limitToShow.ToString("0.0") + "km/h");
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
                sb.AppendLine("預告：" + state.nextSpeedLimitDistance.ToString("0.0") + "M " + state.nextSpeedLimit.ToString("0.0") + "km/h");
                sb.AppendLine("預告：" + xmlLimitDistance.ToString("0.0") + "M " + xmlLimitSpeed.ToString("0.0") + "km/h");
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
                    float limitVal = (dynamicSpeedTarget_Reason == "NextGame") ? state.nextSpeedLimit : xmlLimitSpeed;
                    sb.AppendLine("前方預告: " + limitVal);
                    label.Text = sb.ToString();
                    label.BackColor = Color.Orange;
                    bool isVisible = (Environment.TickCount % 500) < 250;
                    label.Visible = isVisible;
                }
                else
                {
                    label.Text = "";
                    label.BackColor = SystemColors.Control;
                    label.Visible = true;
                }
            }
            catch { }

            try
            {
                string atoLabel = "ATO: ";
                if (finalOutputNotch < 0)
                {
                    if (effectiveTascNotch <= effectiveAtcNotch) atoLabel = "TASC: "; else atoLabel = "ATC: ";
                }
                sb.Clear();
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
        private void SetManualNotch(int notch)
        {
            TrainCrewInput.SetNotch(notch);
        }
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
                if (match.Success && int.TryParse(match.Value, out int num) && num > 0)
                {
                    direction = num % 2 == 0 ? "上り" : "下り";
                }
            }
            return direction;
        }
    }
}