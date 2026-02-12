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
        private const float DOWNHILL_TARGET_OFFSET = 5.0f;

        // 內部狀態
        private bool _isAccelerating = false;

        // 時間與加速度計算
        private float _prevSpeed = 0f;
        private float _currentAccel = 0f;
        private DateTime _lastUpdateTime;

        // 計時器變數
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

        public int Update(TrainState state, float currentSpeed, float currentGrade, float nextStaDist, float signalLimit, bool enableTasc, bool enableAccel)
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

            int finalBrakeNotch = Math.Min(stationNotch, Math.Min(limitNotch, signalNotch));

            // 更新煞車請求時間
            if (finalBrakeNotch < 0)
            {
                _lastBrakeReqTime = DateTime.Now;
            }

            // ==========================================
            // 步驟 C：狀態機仲裁
            // ==========================================
            bool forceStopAccel = false;

            if (!enableAccel) forceStopAccel = true;
            if (enableTasc && nextStaDist < 100 && state.nextStopType.Contains("停車")) forceStopAccel = true;
            if (finalBrakeNotch <= -3) forceStopAccel = true;
            if (currentSpeed >= targetLimit) forceStopAccel = true;
            if (xmlLimitDistance > 0 && currentSpeed >= (signalLimit - 5.0f)) forceStopAccel = true;

            // [新增] 下坡保護觸發：如果正在下坡且速度超過 (紅三角 - 5)，強制停止加速並進入煞車邏輯
            if (grade < DOWNHILL_GRADE_THRESHOLD && currentSpeed > (signalLimit - DOWNHILL_TARGET_OFFSET))
            {
                forceStopAccel = true;
            }

            if (forceStopAccel)
            {
                _isAccelerating = false;
                _lastCalcPower = 0;
            }
            else
            {
                if (currentSpeed < lowerTarget)
                {
                    _isAccelerating = true;
                }
            }

            // ==========================================
            // 步驟 D：最終輸出決定
            // ==========================================

            if (_isAccelerating)
            {
                // [緩解延遲]
                double timeSinceBrake = (DateTime.Now - _lastBrakeReqTime).TotalSeconds;
                double requiredDelay = POST_BRAKE_DELAY_S;
                if (grade > 10.0f) requiredDelay = 0.5;
                else if (grade > 0.0f) requiredDelay = 1.0;

                if (timeSinceBrake < requiredDelay)
                {
                    _lastCalcPower = 0;
                    return 0;
                }

                // --- 正常的加速邏輯 ---
                float diff = upperTarget - currentSpeed;
                int basePower = 0;

                if (diff > 10.0f) basePower = 5;
                else if (diff > 5.0f) basePower = 4;
                else if (diff > 1.0f) basePower = 3;
                else basePower = 2;

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

                // 1. 計算下坡保護需求
                // 目標: 讓速度降至 (信號速限 - 5.0) 並維持
                bool downhillProtectionActive = false;

                if (grade < DOWNHILL_GRADE_THRESHOLD) // 坡度 < -5.0
                {
                    float downhillTarget = signalLimit - DOWNHILL_TARGET_OFFSET; // 目標速度

                    // 邏輯: 如果目前速度高於目標，強制介入 B1
                    // 加入 0.25km/h 的遲滯區間，避免在目標速度附近反覆跳動
                    if (currentSpeed > downhillTarget + 0.25f)
                    {
                        downhillProtectionActive = true;
                    }
                    else if (currentSpeed > downhillTarget && _currentAccel > 0)
                    {
                        // 如果速度略高於目標且正在持續加速 (重力 > 摩擦力)，也要介入
                        downhillProtectionActive = true;
                    }
                }

                // 2. 決定煞車檔位
                // 如果 ATC/TASC 計算出的煞車 (finalBrakeNotch) 已經很強 (例如 -3, -4)，就用原本的
                // 如果原本是 0 (滑行) 或 -1，但下坡保護啟動了，就強制改為 -2 (B1)
                if (downhillProtectionActive)
                {
                    if (finalBrakeNotch > -2)
                    {
                        finalBrakeNotch = -2; // 強制 B1 (假設 -2 是 B1)
                    }
                }

                // 3. 輸出處理 (含緩衝邏輯)
                // 如果不需要煞車 (0)，就回傳 0
                if (finalBrakeNotch >= 0) return 0;

                double timeSincePower = (DateTime.Now - _lastPowerTime).TotalSeconds;

                // [修改] 滑行緩衝邏輯
                // 如果是「下坡保護」觸發的煞車，我們 "不要" 等待滑行緩衝
                // 因為在陡坡上滑行 0.5 秒會讓速度衝太快
                if (!downhillProtectionActive && timeSincePower < ATC_COASTING_TIME_S)
                {
                    if (finalBrakeNotch > -6) return 0;
                }

                // 抑速緩衝 (N -> B1)
                if (timeSincePower < ATC_SMOOTHING_TIME_S)
                {
                    if (finalBrakeNotch > -6) return -2; // B1
                }

                return finalBrakeNotch;
            }
        }

        public int GetAtoNotchForATC(double currentSpeed, double limit, double grade, int pNotch)
        {
            return _simpleTASC.GetAtoNotchForATC(currentSpeed, limit, grade, pNotch);
        }

        public double GetIdealSpeedForTarget(double dist, double speed, double grade)
        {
            return _simpleTASC.GetIdealSpeedForTarget(dist, speed, grade);
        }
    }
}