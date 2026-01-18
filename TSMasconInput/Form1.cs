// 檔案名稱: Form1.cs
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
        SerialPort port;
        // 移除 lastStr，改用 StringBuilder 緩衝區來處理斷裂的資料
        private StringBuilder serialBuffer = new StringBuilder();

        // 新增變數來儲存 ESP32 目前的檔位，供全域存取
        private int currentEsp32Notch = 0;

        // 新增：用來記錄最後一次有效讀取到的 Raw Gear，防止重複更新
        private int lastProcessedEsp32Gear = -999;

        // === 2. ESP32 檔位配置 (必須與 Arduino 一致) ===
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

        // 效能優化：UI 更新計數器
        private int uiUpdateCounter = 0;

        private const double MY_MAX_BRAKE_DECEL_KMPHPS = 3.7;
        private const int MY_BRAKE_NOTCHES = 6;
        private const double TASC_ACTIVATION_DISTANCE_M = 800.0;
        private const double TASC_STOP_MARGIN_M = 0.18;
        private const double TASC_STOP_ADJUST_M = 0.15;
        private const double ATC_BUFFER_KMPH = 0.0;

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
            if (this.comboBox1 != null)
            {
                var names = SerialPort.GetPortNames();
                foreach (var nm in names)
                {
                    comboBox1.Items.Add(nm);
                    if (comboBox1.Text == "") comboBox1.Text = nm;
                }
            }

            if (this.btn_open != null)
            {
                this.btn_open.Click += new EventHandler(this.btn_open_Click);
                this.btn_open.Enabled = true;
            }
            if (this.btn_close != null)
            {
                this.btn_close.Click += new EventHandler(this.btn_close_Click);
                this.btn_close.Enabled = false;
            }

            // Timer 設定
            tim.Tick += Tim_Tick;
            tim.Interval = 15; // 保持快速，用於計算 TASC 和發送指令給遊戲
            tim.Start();
        }

        // === ESP32 連線 (修改為事件驅動) ===
        private void btn_open_Click(object sender, EventArgs e)
        {
            try
            {
                string portName = comboBox1.Text;
                port = new SerialPort(portName, 115200);
                port.DtrEnable = false;
                port.RtsEnable = false;
                port.DataBits = 8;
                port.NewLine = "\n";
                port.Parity = Parity.None;

                // [關鍵修改] 註冊資料接收事件，不再依賴 Timer 讀取
                port.DataReceived += Port_DataReceived;

                port.Open();

                if (btn_open != null) btn_open.Enabled = false;
                if (btn_close != null) btn_close.Enabled = true;

                SetManualNotch(-8);
            }
            catch (Exception ex)
            {
                MessageBox.Show("連線失敗: " + ex.Message);
            }
        }

        // [關鍵修改] 這是背景執行緒，專門負責收資料，不會漏接
        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (port == null || !port.IsOpen) return;

            try
            {
                string data = port.ReadExisting();
                // 因為這是背景執行緒，我們不能直接操作 UI 或某些變數
                // 但 StringBuilder 是可以簡單操作的，或者我們用 lock
                lock (serialBuffer)
                {
                    serialBuffer.Append(data);
                    ProcessBuffer();
                }
            }
            catch { }
        }

        // [關鍵修改] 解析緩衝區資料
        private void ProcessBuffer()
        {
            string content = serialBuffer.ToString();

            // 如果沒有換行符號，表示指令還沒傳完，先不動
            if (content.IndexOf('\n') == -1) return;

            string[] lines = content.Split('\n');

            // 處理所有完整的行
            // 最後一行可能是殘缺的，要留給下次
            int validLinesCount = lines.Length - 1;

            // 我們只需要「最後一個有效」的 CMD 指令
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

            // 更新緩衝區：只保留最後那段殘缺的 (或者如果是空的就清空)
            serialBuffer.Remove(0, content.LastIndexOf('\n') + 1);

            // 如果有找到新的檔位，更新變數
            if (foundGear != -999)
            {
                // 因為我們只更新變數，不操作 UI，所以這裡不需要 Invoke，很高效
                // 計算對應的遊戲檔位
                int gameNotch = ConvertEsp32ToGameNotch(foundGear);

                // 如果跟上次不一樣，才更新 (防止重複觸發)
                if (lastProcessedEsp32Gear != foundGear)
                {
                    currentEsp32Notch = gameNotch;
                    lastProcessedEsp32Gear = foundGear;

                    // 這裡可以選擇：
                    // 1. 直接發送給遊戲 (反應最快)
                    // 2. 等 Timer 來發送 (最安全)
                    // 為了反應速度，我們直接發送，但要小心執行緒安全，TrainCrewInput 應該是安全的
                    SetManualNotch(gameNotch);
                }
            }
        }

        private void btn_close_Click(object sender, EventArgs e)
        {
            if (port != null)
            {
                try
                {
                    // 移除事件監聽
                    port.DataReceived -= Port_DataReceived;
                    if (port.IsOpen) port.Close();
                    port.Dispose();
                }
                catch { }
                port = null;
            }
            if (btn_open != null) btn_open.Enabled = true;
            if (btn_close != null) btn_close.Enabled = false;
        }

        // ... (中間的 TASC 按鈕邏輯 SetTascBehavior, btn_tasc_toggle... 保持不變) ...
        // 為了節省篇幅，這裡省略中間沒改動的按鈕程式碼，請直接保留你原本的
        // ...
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
        }

        StringBuilder sb = new StringBuilder();

        private void Tim_Tick(object sender, EventArgs e)
        {
            // [修改] 1. ESP32 輸入已經移到 DataReceived 事件處理了，這裡不需要再 ReadExisting

            // 2. 獲取遊戲狀態
            var state = TrainCrewInput.GetTrainState();
            if (state == null) return;

            double currentSpeed = state.Speed;
            double distanceToStop = state.nextStaDistance + TASC_STOP_ADJUST_M;
            double currentGrade = state.gradient;
            int manualPowerNotch = state.Pnotch;

            // 3. TASC 邏輯 (保持你修改後的版本)
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

            // 4. 計算限速與儀表 (ATC 準備)
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
            {
                safeSpeedNextGame = (float)tasc.GetIdealSpeedForTarget(state.nextSpeedLimitDistance, state.nextSpeedLimit, state.gradient);
            }

            if (xmlLimitSpeed < 120f && xmlLimitDistance > 0)
            {
                safeSpeedNextXml = (float)tasc.GetIdealSpeedForTarget(xmlLimitDistance, xmlLimitSpeed, state.gradient);
            }

            float limitToShow = safeSpeedCurrent;
            string dynamicSpeedTarget_Reason = "CurrentLimit";

            if (safeSpeedNextGame < limitToShow) { limitToShow = safeSpeedNextGame; dynamicSpeedTarget_Reason = "NextGame"; }
            if (safeSpeedNextXml < limitToShow) { limitToShow = safeSpeedNextXml; dynamicSpeedTarget_Reason = "NextXml"; }

            float gaugeTargetValue = (limitToShow >= 999f) ? -1f : limitToShow;

            // 5. ATC 煞車計算
            int finalAtcNotch = 0;
            if (isAtcMasterOn && limitToShow < 999f)
            {
                if (currentSpeed > (limitToShow + ATC_BUFFER_KMPH))
                {
                    finalAtcNotch = tasc.GetAtoNotchForATC(currentSpeed, limitToShow, currentGrade);
                }
            }

            // 6. 最終決策 (ATO Output)
            int effectiveTascNotch = isTascMasterOn ? tascStationNotch : 0;
            int effectiveAtcNotch = isAtcMasterOn ? finalAtcNotch : 0;
            int finalOutputNotch = Math.Min(effectiveTascNotch, effectiveAtcNotch);
            TrainCrewInput.SetATO_Notch(finalOutputNotch);

            // [關鍵修改] 7. 限制 UI 更新頻率 (提升輸入流暢度)
            // 這裡設定為每 5 個 Tick 更新一次 UI (約 75ms~100ms 一次)，人眼感覺不到延遲，但能大幅減輕 CPU 負擔
            uiUpdateCounter++;
            if (uiUpdateCounter >= 5)
            {
                uiUpdateCounter = 0;
                UpdateUI(state, limitToShow, dynamicSpeedTarget_Reason, finalOutputNotch, effectiveTascNotch, effectiveAtcNotch, safeSpeedNextGame, safeSpeedNextXml, xmlLimitSpeed, xmlLimitDistance, gaugeTargetValue);
            }
        }

        // 把 UI 更新邏輯抽離出來，保持 Tim_Tick 乾淨
        private void UpdateUI(TrainState state, float limitToShow, string dynamicSpeedTarget_Reason, int finalOutputNotch, int effectiveTascNotch, int effectiveAtcNotch, float safeSpeedNextGame, float safeSpeedNextXml, float xmlLimitSpeed, float xmlLimitDistance, float gaugeTargetValue)
        {
            // 儀表更新
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

            // 文字更新 1
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

            // 文字更新 2 (Label1)
            try
            {
                sb.Clear();
                sb.AppendLine("速度：" + state.Speed.ToString("0.0") + "km/h");
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
                    sb.AppendLine("BC圧力：" + state.CarStates[0].BC_Press.ToString("0") + "kPa");
                    sb.AppendLine("BC圧力：" + state.CarStates[1].BC_Press.ToString("0") + "kPa");
                }
                label1.Text = sb.ToString();
            }
            catch { label1.Text = "Info Error"; }

            // 文字更新 3 (LabelPnl)
            try
            {
                sb.Clear();
                sb.AppendLine("預告：" + state.nextSpeedLimitDistance.ToString("0.0") + "M " + state.nextSpeedLimit.ToString("0.0") + "km/h");
                sb.AppendLine("預告：" + xmlLimitDistance.ToString("0.0") + "M " + xmlLimitSpeed.ToString("0.0") + "km/h");
                sb.AppendLine("曲線G：" + (safeSpeedNextGame < 999 ? safeSpeedNextGame.ToString("0.0") : "---"));
                sb.AppendLine("曲線X：" + (safeSpeedNextXml < 999 ? safeSpeedNextXml.ToString("0.0") : "---"));
                sb.AppendLine((state.Lamps[PanelLamp.DoorClose] ? "●" : "○") + "戸　閉");
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

            // 文字更新 4 (LabelATS)
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
                        sb.AppendLine((state.CarStates[i].DoorClose ? "●" : "○") + (i + 1) + "号車 戸閉");
                }
                labelATS.Text = sb.ToString();
            }
            catch { labelATS.Text = "ATS Error"; }

            // 文字更新 5 (Label2)
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
                sb.AppendLine("列車番号：" + state.diaName + GetDirection(state));
                sb.AppendLine("種　　別：" + state.Class);
                sb.AppendLine("行　　先：" + state.BoundFor);
                label2.Text = sb.ToString();
            }
            catch { label2.Text = "Drive Info Error"; }

            // 文字更新 6 (Label 建議速度)
            try
            {
                sb.Clear();
                if (dynamicSpeedTarget_Reason == "NextGame" || dynamicSpeedTarget_Reason == "NextXml")
                {
                    float limitVal = (dynamicSpeedTarget_Reason == "NextGame") ? state.nextSpeedLimit : xmlLimitSpeed;
                    sb.AppendLine("前方予告: " + limitVal);
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

            // 文字更新 7 (LabelNotch)
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

        // === 輔助函式保持不變 ===
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