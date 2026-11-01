using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using TrainCrew;
using System.Net.Sockets;

namespace TSMasconInput
{
    public partial class Form1 : Form
    {
        const int BRAKE_STEPS = 6;
        const bool HAS_EB = true;
        const bool HAS_HOLD = true;
        const int GEAR_N_INDEX = (HAS_EB ? 1 : 0) + BRAKE_STEPS + (HAS_HOLD ? 1 : 0);

        Timer tim = new Timer();
        private SimpleTASC tasc;
        private ATOController ato;

        private UdpClient udpClient = new UdpClient();
        private UdpClient udpReceiver;

        private bool isTascMasterOn = true;
        private bool isAtoMasterOn = true;
        private bool isAtcMasterOn = true;

        private bool isAtoDepartRequest = false;

        private enum TascBehaviorState { Idle, Braking, Holding }
        private TascBehaviorState currentTascBehavior = TascBehaviorState.Idle;
        private int tascStationNotch = 0;
        private int atoRunningNotch = 0;

        // 新增一個 struct 來避免 Heap Allocation
        private struct SpeedRecord
        {
            public DateTime Time;
            public double Speed;
        }

        // 修改 Queue 的型別為 SpeedRecord
        private Queue<SpeedRecord> speedHistory = new Queue<SpeedRecord>();
        private double _currentAccel = 0.0;

        private int uiUpdateCounter = 0;

        private const double MY_MAX_BRAKE_DECEL_KMPHPS = 3.7;
        private const int MY_BRAKE_NOTCHES = 6;
        private const double TASC_ACTIVATION_DISTANCE_M = 800.0;
        private const double TASC_STOP_MARGIN_M = 0.18;
        private const double TASC_STOP_ADJUST_M = 0.15;

        public Form1()
        {
            InitializeComponent();
            TrainCrewInput.Init();
            FormClosed += Form1_FormClosed;
            this.TopMost = true;

            tasc = new SimpleTASC(MY_MAX_BRAKE_DECEL_KMPHPS, MY_BRAKE_NOTCHES);
            ato = new ATOController();

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

            tim.Tick += Tim_Tick;
            tim.Interval = 15;
            tim.Start();

            StartUdpReceiver();
        }

        private void Btn_ato_toggle_Click(object sender, EventArgs e)
        {
            isAtoMasterOn = !isAtoMasterOn;
            if (isAtoMasterOn)
            {
                btn_ato_toggle.Text = "ATO: ON";
                btn_ato_toggle.BackColor = Color.Cyan;

                if (!isTascMasterOn)
                {
                    isTascMasterOn = true;
                    btn_tasc_toggle.Text = "TASC: ON";
                    btn_tasc_toggle.BackColor = Color.LawnGreen;
                }

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
            if (udpClient != null) { udpClient.Close(); }
            if (udpReceiver != null) { udpReceiver.Close(); }
        }

        StringBuilder sb = new StringBuilder();

        // === 主迴圈 (核心邏輯修正) ===
        private void Tim_Tick(object sender, EventArgs e)
        {
            var state = TrainCrewInput.GetTrainState();
            if (state == null) return;

            DateTime currentTime = DateTime.Now;

            // 1. 加速度計算 (使用 struct 避免 GC Allocation)
            speedHistory.Enqueue(new SpeedRecord { Time = currentTime, Speed = state.Speed });

            // 清除超過 0.5 秒前的舊資料
            while (speedHistory.Count > 0 && (currentTime - speedHistory.Peek().Time).TotalSeconds > 0.5)
            {
                speedHistory.Dequeue();
            }

            // 確保有足夠的歷史資料來計算
            if (speedHistory.Count > 1)
            {
                var oldest = speedHistory.Peek();
                double tDelta = (currentTime - oldest.Time).TotalSeconds;

                if (tDelta > 0)
                {
                    _currentAccel = (state.Speed - oldest.Speed) / tDelta;
                }
            }

            double currentSpeed = state.Speed;
            double distanceToStop = state.nextStaDistance + TASC_STOP_ADJUST_M;
            double currentGrade = state.gradient;
            int manualPowerNotch = state.Pnotch;

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

            // ATC
            int finalAtcNotch = 0;
            if (isAtcMasterOn && (limitToShow < 999f || state.nextSpeedLimit == 0))
            {
                finalAtcNotch = tasc.GetAtoNotchForATC(state, currentSpeed, limitToShow, currentGrade, state.Pnotch);
            }

            if (isAtoMasterOn)
            {
                if (currentSpeed > 1.0)
                {
                    if (isAtoDepartRequest) { isAtoDepartRequest = false; btn_depart.BackColor = Color.LightGray; }
                    btn_depart.Enabled = false;
                }
                else { btn_depart.Enabled = true; }
            }
            else
            {
                btn_depart.Enabled = false;
                btn_depart.BackColor = SystemColors.Control;
                isAtoDepartRequest = false;
            }

            bool manualPowerOverride = (manualPowerNotch > 0);
            switch (currentTascBehavior)
            {
                case TascBehaviorState.Idle:
                    bool isPassThrough = (state.nextStopType == "通過");

                    if (distanceToStop > TASC_STOP_MARGIN_M && distanceToStop <= TASC_ACTIVATION_DISTANCE_M && !isPassThrough)
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

            bool doorsClosed = state.Lamps[PanelLamp.DoorClose];
            bool allowAccel = isAtoMasterOn && (currentSpeed > 0.5 || isAtoDepartRequest) && doorsClosed;
            int atoRecommendedNotch = ato.Update(state, (float)currentSpeed, (float)currentGrade, (float)distanceToStop, limitToShow,
                isTascMasterOn, allowAccel, tascExpectedSpeed, finalAtcNotch);
            atoRunningNotch = atoRecommendedNotch;

            int effectiveTascNotch = isTascMasterOn ? tascStationNotch : 0;

            int finalOutputNotch = 0;
            if (effectiveTascNotch < 0 || finalAtcNotch < 0)
            {
                finalOutputNotch = Math.Min(effectiveTascNotch, finalAtcNotch);
            }
            else if (isAtoMasterOn)
            {
                finalOutputNotch = atoRecommendedNotch;
            }
            else
            {
                finalOutputNotch = 0;
            }

            TrainCrewInput.SetATO_Notch(finalOutputNotch);

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

                try
                {
                    float udpcurrentSpeed = (float)state.Speed;
                    float tascBlueLimit = (currentTascBehavior == TascBehaviorState.Braking) ? tascExpectedSpeed : -1f;
                    float greenNextLimit = rawNextLimit;
                    float redCurrentLimit = gaugeTargetValue;

                    int manualNotch = 0;
                    if (state.Bnotch > 0)
                    {
                        manualNotch = -state.Bnotch;
                    }
                    else if (state.Pnotch > 0)
                    {
                        manualNotch = state.Pnotch;
                    }

                    int effectiveNotch = 0;
                    if (finalOutputNotch > 0)
                    {
                        if (manualNotch == 0)
                            effectiveNotch = finalOutputNotch;
                        else
                            effectiveNotch = manualNotch;
                    }
                    else if (finalOutputNotch < 0)
                    {
                        effectiveNotch = Math.Min(manualNotch, finalOutputNotch);
                    }
                    else
                    {
                        effectiveNotch = manualNotch;
                    }

                    string appliedNotchStr = "N";
                    if (effectiveNotch == -8) appliedNotchStr = "非常";
                    else if (effectiveNotch <= -2) appliedNotchStr = "B" + (-effectiveNotch - 1);
                    else if (effectiveNotch == -1) appliedNotchStr = "抑速";
                    else if (effectiveNotch > 0) appliedNotchStr = "P" + effectiveNotch;

                    int departStateCode = 0;
                    if (!btn_depart.Enabled) departStateCode = 0;
                    else if (isAtoDepartRequest) departStateCode = 2;
                    else departStateCode = 1;

                    string udpMsg = $"{udpcurrentSpeed:F1}," +
                        $"{tascBlueLimit:F1}," +
                        $"{greenNextLimit:F1}," +
                        $"{redCurrentLimit:F1}," +
                        $"{appliedNotchStr}," +
                        $"{_currentAccel:F2}," +
                        $"{currentGrade:F1}," +
                        $"{state.CarStates[0].BC_Press.ToString("0"):F1}," +
                        $"{state.CarStates[1].BC_Press.ToString("0"):F1}," +
                        $"{state.MR_Press.ToString("0"):F1}," +
                        $"{state.CarStates[0].Ampare.ToString("0"):F1}," +
                        $"{state.CarStates[1].Ampare.ToString("0"):F1}," +
                        $"{departStateCode}";

                    byte[] sendBytes = Encoding.UTF8.GetBytes(udpMsg);
                    // 傳送到本機的 5000 埠號 (改用 SendAsync)
                    udpClient.SendAsync(sendBytes, sendBytes.Length, "127.0.0.1", 5000);
                }
                catch (Exception ex) { Console.WriteLine("UDP 發送錯誤: " + ex.Message); }
            }
        }

        private async void StartUdpReceiver()
        {
            try
            {
                udpReceiver = new UdpClient(5001);
                while (true)
                {
                    UdpReceiveResult result = await udpReceiver.ReceiveAsync();
                    string message = Encoding.UTF8.GetString(result.Buffer);

                    if (message == "DEPART")
                    {
                        this.Invoke(new Action(() =>
                        {
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
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Console.WriteLine("UDP 接收發生錯誤: " + ex.Message); }
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
            catch (Exception ex) { Console.WriteLine("UI Error - Gauge Update: " + ex.Message); }

            try
            {
                if (state.CarStates != null && state.CarStates.Count > 0) { this.pressureGauge.Value = state.CarStates[0].BC_Press; this.pressureGauge.Value3 = state.CarStates[1].BC_Press; }
                this.pressureGauge.Value2 = state.MR_Press;
                this.pressureGauge.TargetValue = -1; this.pressureGauge.TargetValue2 = -1; this.pressureGauge.TargetValue3 = -1;
            }
            catch (Exception ex) { Console.WriteLine("UI Error - Pressure: " + ex.Message); }

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
            catch (Exception ex) { label1.Text = "Info Error"; Console.WriteLine("UI Error - Info: " + ex.Message); }

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
                sb.AppendLine((state.Lamps[PanelLamp.DoorClose] ? "●" : "○") + "戶 閉");
                sb.AppendLine((state.Lamps[PanelLamp.ATS_Ready] ? "●" : "○") + "ATS正常");
                sb.AppendLine((state.Lamps[PanelLamp.ATS_BrakeApply] ? "●" : "○") + "ATS動作");
                sb.AppendLine((state.Lamps[PanelLamp.ATS_Open] ? "●" : "○") + "ATS開放");
                sb.AppendLine((state.Lamps[PanelLamp.RegenerativeBrake] ? "●" : "○") + "回 生");
                sb.AppendLine((state.Lamps[PanelLamp.EB_Timer] ? "●" : "○") + "E B");
                sb.AppendLine((state.Lamps[PanelLamp.EmagencyBrake] ? "●" : "○") + "非常ﾌﾞﾚｰｷ");
                sb.AppendLine((state.Lamps[PanelLamp.Overload] ? "●" : "○") + "過負荷");
                labelPnl.Text = sb.ToString();
            }
            catch (Exception ex) { labelPnl.Text = "Panel Error"; Console.WriteLine("UI Error - Panel: " + ex.Message); }

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
            catch (Exception ex) { labelATS.Text = "ATS Error"; Console.WriteLine("UI Error - ATS: " + ex.Message); }

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
                            sb.AppendLine("誤  差：" + (state.stationList[i].ArvTime - state.NowTime));
                            sb.AppendLine(state.stationList[i].Name + ":" + state.stationList[i].ArvTime + " " + state.stationList[i].stopType);
                        }
                    }
                }
                sb.AppendLine("列車編號：" + state.diaName + GetDirection(state));
                sb.AppendLine("種  別：" + state.Class);
                sb.AppendLine("行  先：" + state.BoundFor);
                label2.Text = sb.ToString();
            }
            catch (Exception ex) { label2.Text = "Drive Info Error"; Console.WriteLine("UI Error - Drive Info: " + ex.Message); }

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
            catch (Exception ex) { Console.WriteLine("UI Error - Limit Label: " + ex.Message); }

            try
            {
                string atoLabel = "ATO:";

                if (effectiveTascNotch < 0 && effectiveTascNotch < effectiveAtcNotch)
                {
                    atoLabel = "TASC:";
                }
                else if (effectiveAtcNotch < 0 && effectiveAtcNotch < effectiveTascNotch)
                {
                    atoLabel = "ATC:";
                }

                sb.Clear();

                sb.Append("ATO:" + ConvertNotchToString(atoRunningNotch) + " ");
                sb.Append("ATC:" + ConvertNotchToString(effectiveAtcNotch) + " ");
                sb.Append("TASC:" + ConvertNotchToString(effectiveTascNotch) + " ");

                sb.AppendLine(atoLabel + ConvertNotchToString(finalOutputNotch));
                sb.AppendLine("Notch:" + CalNotch(state.Pnotch, state.Bnotch));
                labelNotch.Text = sb.ToString();
            }
            catch (Exception ex) { Console.WriteLine("UI Error - Notch Label: " + ex.Message); }
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
                if (match.Success && int.TryParse(match.Value, out int num) && num > 0) { direction = num % 2 == 0 ? "上り" : "下り"; }
            }
            return direction;
        }
    }
}