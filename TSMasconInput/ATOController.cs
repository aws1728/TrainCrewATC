using System;
using TrainCrew;

namespace TSMasconInput
{
    public class ATOController
    {
        private SimpleTASC _simpleTASC;
        public SimpleTASC Tasc => _simpleTASC;

        // --- [參數設定] ---
        // 上限: 限速 - 0.5 (ex: 25 - 0.5 = 24.5)
        private const float CRUISE_SPEED_MARGIN = 0.5f;

        // 下限: 上限 - 2.0 (ex: 24.5 - 2.0 = 22.5)
        private const float ACCEL_RESUME_DIFF = 2.0f;

        // 滑行緩衝時間 (秒): 動力切斷後，先維持 N 檔的時間
        private const double ATC_COASTING_TIME_S = 0.5;

        // 抑速緩衝時間 (秒)
        private const double ATC_SMOOTHING_TIME_S = 2.0;

        // 最小檔位保持時間 (秒): 防止檔位跳動 & 控制升檔節奏
        private const double MIN_NOTCH_HOLD_TIME = 0.5;

        // 模擬等待空氣煞車排氣，避免「動力與殘餘煞車力打架」造成的頓挫
        private const double POST_BRAKE_DELAY_S = 1.0;

        // [新增] 下坡控制參數
        // 當坡度小於 -5.0 (大於 5 度下坡)
        private const float DOWNHILL_GRADE_THRESHOLD = -5.0f;
        // 下坡時的目標緩衝區 (紅色三角形 - 5km/h)
        private const float DOWNHILL_TARGET_OFFSET = 3.0f;

        // 您的指定參數：
        // 1. 上限觸發點 (紅色三角形 - 0.5km) -> 觸發 B1
        private const float DOWNHILL_TRIGGER_OFFSET = 0.5f;

        // --- [新增] 煞車平滑化變數 (Step-by-Step Buffering) ---
        private int _lastFilteredBrakeNotch = 0;       // 上一次輸出的平滑後檔位
        private DateTime _lastBrakeChangeTime = DateTime.MinValue; // 上一次變換檔位的時間
        private const double BRAKE_HOLD_TIME_S = 0.2;  // 檔位保持時間

        // 內部狀態
        private bool _isAccelerating = false;

        // [新增] 下坡狀態機
        // 0: 一般/滑行 (Normal)
        // 1: 抑速維持 (Holding, -1)
        // 2: 強制減速 (Braking, -2)
        private int _downhillState = 0;

        // 時間與物理量
        private float _prevSpeed = 0f;
        private float _currentAccel = 0f;
        private DateTime _lastUpdateTime;

        // 計時器
        private DateTime _lastPowerTime;
        private DateTime _lastNotchChangeTime;
        private DateTime _lastBrakeReqTime;

        private int _lastCalcPower = 0;

        public ATOController()
        {
            _simpleTASC = new SimpleTASC(3.7, 6);
            _lastUpdateTime = DateTime.Now;
            _lastPowerTime = DateTime.MinValue;
            _lastNotchChangeTime = DateTime.MinValue;
            _lastBrakeReqTime = DateTime.Now;
        }

        public int Update(TrainState state, float currentSpeed, float currentGrade, float nextStaDist, float signalLimit, 
            bool enableTasc, bool enableAccel, int overrideBrakeNotch = 0)
        {
            // 0. 基本物理量計算
            DateTime now = DateTime.Now;
            double dt = (now - _lastUpdateTime).TotalSeconds;
            if (dt > 0.001)
            {
                float speedDiff = currentSpeed - _prevSpeed;
                _currentAccel = (float)(speedDiff / dt);
                _prevSpeed = currentSpeed;
                _lastUpdateTime = now;
            }

            // 1. 取得 XML 速限
            float xmlLimitSpeed, xmlLimitDistance;
            float dist = nextStaDist > 0 ? nextStaDist : 0;
            TASCUtils.GetLimitSpeed(state, dist, 0, out xmlLimitSpeed, out xmlLimitDistance);

            float grade = currentGrade;

            // ==========================================
            // 步驟 A：計算目標速度區間
            // ==========================================
            float targetLimit = signalLimit;
            float upperTarget = targetLimit - CRUISE_SPEED_MARGIN;
            if (upperTarget < 0) upperTarget = 0;
            float lowerTarget = upperTarget - ACCEL_RESUME_DIFF;

            // ==========================================
            // 步驟 B：計算煞車需求 (TASC & ATC)
            // ==========================================
            bool isPass = state.nextStopType == "通過";
            int stationNotch = (enableTasc && !isPass) ? _simpleTASC.GetAtoNotch(currentSpeed, nextStaDist, grade) : 0;

            int limitNotch = 0;
            if (xmlLimitDistance > 0)
                limitNotch = _simpleTASC.GetAtoNotchToTargetSpeed(currentSpeed, xmlLimitDistance, xmlLimitSpeed, grade);

            int signalNotch = 0;
            if (currentSpeed > signalLimit + 1.0f)
                signalNotch = _simpleTASC.GetAtoNotchToTargetSpeed(currentSpeed, 10, signalLimit, grade);

            //int finalBrakeNotch = Math.Min(stationNotch, Math.Min(limitNotch, signalNotch));
            int finalBrakeNotch = 0; //關閉ATO煞車，讓下坡保護獨立運作

            // 更新煞車請求時間
            if (finalBrakeNotch < 0 || overrideBrakeNotch < 0) _lastBrakeReqTime = DateTime.Now;

            // ==========================================
            // 步驟 C：狀態機仲裁
            // ==========================================
            bool forceStopAccel = false;

            if (!enableAccel) forceStopAccel = true;
            if (enableTasc && nextStaDist < 100 && state.nextStopType.Contains("停車")) forceStopAccel = true;
            if (state.nextSpeedLimit == 0.0 && state.nextSpeedLimitDistance < 100 ) forceStopAccel = true;
            if (finalBrakeNotch <= -1 || overrideBrakeNotch < 0) forceStopAccel = true;
            if (currentSpeed >= targetLimit) forceStopAccel = true;
            if (xmlLimitDistance > 0 && currentSpeed >= (signalLimit - 5.0f)) forceStopAccel = true;

            // 如果下坡保護正在運作 (State 1 或 2)，絕對不能加速
            if (_downhillState != 0) forceStopAccel = true;

            // 如果是大下坡且速度快超速，也強制停止加速
            if (grade < DOWNHILL_GRADE_THRESHOLD && currentSpeed > (signalLimit - DOWNHILL_TARGET_OFFSET)) forceStopAccel = true;

            if (forceStopAccel)
            {
                _isAccelerating = false;
                _lastCalcPower = 0;
            }
            else
            {
                if (currentSpeed < lowerTarget) _isAccelerating = true;
            }

            // ==========================================
            // 步驟 D：最終輸出決定
            // ==========================================

            if (_isAccelerating)
            {
                // 加速模式時，重置下坡保護狀態
                _downhillState = 0;

                // [緩解延遲]
                double timeSinceBrake = (DateTime.Now - _lastBrakeReqTime).TotalSeconds;
                double requiredDelay = POST_BRAKE_DELAY_S;
                if (grade > 10.0f) requiredDelay = 0.5;
                else if (grade > 0.0f) requiredDelay = 1.0;
                else requiredDelay = 1.0;

                if (timeSinceBrake < requiredDelay) { _lastCalcPower = 0; return 0; }

                // --- 正常的加速邏輯 ---
                float diff = upperTarget - currentSpeed;
                int basePower = 0;

                if (signalLimit <= 25.0f)
                {
                    if (diff > 10.0f) basePower = 4;
                    else if (diff > 5.0f) basePower = 3;
                    else if (diff > 1.0f) basePower = 2;
                    else basePower = 1;
                }
                else
                {
                    if (diff > 10.0f) basePower = 5;
                    else if (diff > 5.0f) basePower = 4;
                    else if (diff > 1.0f) basePower = 3;
                    else basePower = 2;
                }

                // 坡度補償
                if (grade > 5 && basePower < 4 && basePower > 0) basePower += 1;
                if (grade > 15) basePower = 5;
                if (grade < -5 && basePower > 0) basePower -= 1;

                if (basePower > 5) basePower = 5;
                if (basePower < 0) basePower = 0;

                // [升檔緩衝]
                if (basePower > _lastCalcPower + 1) basePower = _lastCalcPower + 1;

                // [防抖動]
                if (basePower != _lastCalcPower)
                {
                    double timeSinceChange = (DateTime.Now - _lastNotchChangeTime).TotalSeconds;
                    if (timeSinceChange < MIN_NOTCH_HOLD_TIME) basePower = _lastCalcPower;
                    else { _lastCalcPower = basePower; _lastNotchChangeTime = DateTime.Now; }
                }

                if (basePower > 0) _lastPowerTime = DateTime.Now;
                return basePower;
            }
            else
            {
                // --- 煞車/滑行模式 ---

                // ====================================================
                // [核心邏輯] 下坡循環狀態機 (Downhill State Machine)
                // ====================================================

                // 如果不是大下坡，重置狀態，走一般邏輯
                if (grade >= DOWNHILL_GRADE_THRESHOLD)
                {
                    _downhillState = 0;
                }
                else
                {
                    // 定義觸發點
                    // Top: 紅色三角形 - 0.5 (例如 60 - 0.5 = 59.5)
                    float triggerSpeed = signalLimit - DOWNHILL_TRIGGER_OFFSET;
                    // Bottom: 紅色三角形 - 5.0 (例如 60 - 5.0 = 55.0)
                    float targetSpeed = signalLimit - DOWNHILL_TARGET_OFFSET;

                    // 狀態機轉換邏輯
                    switch (_downhillState)
                    {
                        case 0: // 狀態 0: N檔滑行中
                            if (currentSpeed >= triggerSpeed)
                            {
                                // 觸發條件：速度快超越紅三角 (-0.5)
                                // 動作：進入狀態 2 (B1 強制減速)
                                _downhillState = 2;
                            }
                            break;

                        case 2: // 狀態 2: B1 減速中
                            if (currentSpeed <= targetSpeed)
                            {
                                // 轉換條件：速度已降至目標 (-5.0)
                                // 動作：進入狀態 1 (抑速維持)
                                // 關鍵點：這裡不回 N，而是切換到 -1 試圖維持速度
                                _downhillState = 1;
                            }
                            break;

                        case 1: // 狀態 1: 抑速 (-1) 維持中
                            if (currentSpeed >= triggerSpeed)
                            {
                                // 失效條件：重力太強，速度又衝上去了
                                // 動作：回去狀態 2 (B1) 再壓下來
                                _downhillState = 2;
                            }
                            else if (currentSpeed < targetSpeed - 0.2f)
                            {
                                // 解除條件：速度低於目標 (多給 0.2 緩衝避免跳動)
                                // 動作：解除保護，回 N 滑行
                                _downhillState = 0;
                            }
                            break;
                    }
                }

                // ====================================================
                // [輸出判斷]
                // ====================================================

                // [修改] 融合 TASC 與 下坡保護 的指令
                int downhillReq = 0;
                if (_downhillState == 2) downhillReq = -2;      // 強制 B1
                else if (_downhillState == 1) downhillReq = -1; // 強制 抑速

                // 取兩者中「煞車力較大」的 (數值較小的)
                // 這樣如果 TASC 需要急煞 (-5)，就不會被下坡保護 (-2) 蓋掉
                finalBrakeNotch = Math.Min(finalBrakeNotch, downhillReq);

                // [關鍵修正] 在這裡再次更新煞車時間！
                // 這樣才能確保「下坡保護 (-2/-1)」也被視為煞車，讓延遲邏輯生效
                if (finalBrakeNotch < 0)
                {
                    _lastBrakeReqTime = DateTime.Now;
                }

                // --- 以下為一般 ATO/TASC 煞車邏輯 (當 _downhillState == 0 時) ---

                if (finalBrakeNotch >= 0) return 0;

                double timeSincePower = (DateTime.Now - _lastPowerTime).TotalSeconds;

                // [滑行緩衝]
                if (timeSincePower < ATC_COASTING_TIME_S)
                {
                    if (finalBrakeNotch > -6) return 0;
                }

                // [抑速緩衝]
                if (timeSincePower < ATC_SMOOTHING_TIME_S)
                {
                    if (finalBrakeNotch > -6) return -1; // 用 -1 (抑速) 作為過渡
                }

                return finalBrakeNotch;
            }
        }

        public int GetAtoNotchForATC(TrainState state, double currentSpeed, double limit, double grade, int pNotch)
        {
            return _simpleTASC.GetAtoNotchForATC(state, currentSpeed, limit, grade, pNotch);
        }

        public double GetIdealSpeedForTarget(double dist, double speed, double grade)
        {
            return _simpleTASC.GetIdealSpeedForTarget(dist, speed, grade);
        }
    }
}