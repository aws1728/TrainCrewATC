using System;
using TrainCrew;
using static System.Windows.Forms.AxHost;

/// <summary>
/// 這是 bve-autopilot TASC 演算法的核心 C# 移植版。
/// </summary>
public class SimpleTASC
{
    // --- 核心常數 (Core Constants) ---
    private const double APPROACH_TIME_S = 2.0;

    // ==========================================
    // 【ATC 設定參數 / ATC Settings】
    // ==========================================

    // 1. 目標緩衝 (Target Buffer)
    // 設定 ATC 的煞車目標為 (限速 - 2.0)。例如限速 90，ATC 會努力煞到 88。
    // 這能提供安全裕度，防止 ATS 誤觸發。
    private const double ATC_TARGET_BUFFER = 2.0;

    // 2. 解除門檻 (Reset Range)
    // 只有當速度降到 (限速 - 1.0) 以下時，ATC 才會解除。
    // 必須小於 Target Buffer，確保一定能達成解除條件。
    private const double ATC_RESET_RANGE = 2.0;

    // 3. 距離提前量 (Distance Margin)
    // 在計算預告限速曲線時，假裝限速牌比實際位置近 30 公尺。
    // 這樣能強迫列車在限速牌「之前」就減速完畢，而不是「剛好」在牌下。
    private const double ATC_DISTANCE_MARGIN_M = 3.0;

    // 4. 反應靈敏度 (Response Time)
    // 數值越小煞車越猛，數值越大越柔和。2.0 秒是標準值。
    private const double ATC_RESPONSE_TIME_S = 1.0;

    // 5. 抑速緩衝時間 (Smoothing Time)
    // 當從 P 段 (加速中) 觸發 ATC 時，為了減少頓挫，
    // 在這段時間內 (1.5秒) 會限制最大煞車為 B1 (抑速)。
    private const double ATC_SMOOTHING_TIME_S = 0.5;

    // 用於 ATC 的煞車緩衝控制變數
    private int _lastFilteredBrakeNotch = 0;       // 上一次輸出的平滑後檔位
    private DateTime _lastBrakeChangeTime = DateTime.MinValue; // 上一次變換檔位的時間
    private const double BRAKE_HOLD_TIME_S = 0.2;  // 煞車檔位保持時間 (建議比加速的 0.5s 短，確保反應夠快)

    // ------------------------------------------

    // --- 物理單位轉換 ---
    private const double KMPH_TO_MPS = 1.0 / 3.6;
    private const double MPS_TO_KMPH = 3.6;
    private const double KMPHPS_TO_MPS2 = 1.0 / 3.6;
    private const double GRAVITY_MPS2 = 9.80665;

    // --- 煞車模型 ---
    private BrakeModel _brakeModel;
    private double _baseDecelerationMps2; // TASC 自動停車用的強力減速 (75%)
    private double _hintDecelerationMps2; // 預告曲線用的溫和減速 (50%)

    public double LastExpectedSpeedKmph { get; private set; }

    // ATC 狀態變數
    private bool _isAtcBraking = false;

    // ATC 介入開始時刻 (用於計算抑速緩衝)
    private DateTime _atcEngagementTime = DateTime.MinValue;

    public SimpleTASC(double maxBrakeDecel_Kmphps, int brakeNotches)
    {
        _brakeModel = new BrakeModel(maxBrakeDecel_Kmphps * KMPHPS_TO_MPS2, brakeNotches);

        _baseDecelerationMps2 = (maxBrakeDecel_Kmphps * 0.75) * KMPHPS_TO_MPS2;
        _hintDecelerationMps2 = (maxBrakeDecel_Kmphps * 0.5) * KMPHPS_TO_MPS2;

        this.LastExpectedSpeedKmph = 0.0;
        _isAtcBraking = false;
        _atcEngagementTime = DateTime.MinValue;
    }

    /// <summary>
    /// 計算儀表板紅色三角形的「理想速度」(使用溫和的 50% 煞車力)
    /// </summary>
    public double GetIdealSpeedForTarget(double distanceM, double targetSpeedKmph, double currentGradePermil)
    {
        // 關鍵：扣除距離提前量
        // 如果距離目標還有 300m，我們算成 270m，強迫提早減速
        double effectiveDistance = distanceM - ATC_DISTANCE_MARGIN_M;
        if (effectiveDistance < 0) effectiveDistance = 0;

        double targetSpeedMps = targetSpeedKmph * KMPH_TO_MPS;
        double gradeRatio = currentGradePermil / 1000.0;
        double gradeAccelerationMps2 = -0.75 * GRAVITY_MPS2 * gradeRatio;

        double correctedBaseDecelMps2 = _hintDecelerationMps2 - Math.Max(gradeAccelerationMps2, 0.0);
        if (correctedBaseDecelMps2 < 0.1) correctedBaseDecelMps2 = 0.1;

        // 物理公式: V^2 = U^2 + 2as
        double targetSpeedSquared = targetSpeedMps * targetSpeedMps;
        double decelDistanceComponent = 2.0 * correctedBaseDecelMps2 * effectiveDistance;
        double idealSpeedSquared = targetSpeedSquared + decelDistanceComponent;

        return Math.Sqrt(idealSpeedSquared) * MPS_TO_KMPH;
    }

    /// <summary>
    /// 計算「車站停車」需要的 ATO 煞車檔位
    /// </summary>
    public int GetAtoNotch(double currentSpeedKmph, double distanceToStopM, double currentGradePermil)
    {
        return CalculateTascNotch(currentSpeedKmph, distanceToStopM, 0.0, currentGradePermil, true);
    }

    /// <summary>
    /// 計算「到達限速點」需要的 ATO 煞車檔位
    /// </summary>
    public int GetAtoNotchToTargetSpeed(double currentSpeedKmph, double distanceToTargetM, double targetSpeedKmph, double currentGradePermil)
    {
        if (distanceToTargetM <= 0) return 0;
        return CalculateTascNotch(currentSpeedKmph, distanceToTargetM, targetSpeedKmph, currentGradePermil, false);
    }

    /// <summary>
    /// 【ATC 專用計算邏輯】(包含防抖動、目標緩衝、頓挫抑制)
    /// </summary>
    /// <param name="currentPnotch">目前的加速檔位 (P1~P5)，用於判斷是否需要抑速緩衝</param>
    public int GetAtoNotchForATC(TrainState state, double currentSpeedKmph, double idealSpeedKmph, double currentGradePermil, int currentPnotch)
    {
        // 1. 狀態機邏輯 (保持原有觸發判定)
        if (!_isAtcBraking)
        {
            if (currentSpeedKmph > idealSpeedKmph || (idealSpeedKmph == 0 && currentSpeedKmph > 0.1))
            {
                _isAtcBraking = true;
                _atcEngagementTime = (currentPnotch > 0) ? DateTime.Now : DateTime.MinValue;
            }
        }
        else
        {
            // 只有在目標速度不為 0 時才檢查解除門檻；若目標是停車，則直到完全停穩前都不解除
            if (idealSpeedKmph > 0 && currentSpeedKmph <= (idealSpeedKmph - ATC_RESET_RANGE))
            {
                _isAtcBraking = false;
            }
        }

        // 若未觸發 ATC，重置緩衝狀態並回傳 0
        if (!_isAtcBraking)
        {
            _lastFilteredBrakeNotch = 0; // 重置
            return 0;
        }

        // 物理計算：算出「理想的目標檔位」 (Raw Target)
        int rawTargetNotch = 0;

        // 2. 核心計算：目標減速度 (targetDecelMps2)
        double currentSpeedMps = currentSpeedKmph * KMPH_TO_MPS;
        double targetDecelMps2 = 0.0;

        // --- [優化部分] ---
        if (currentSpeedKmph <= 15.0)
        {
            // 直接調用 TASC 停車邏輯
            // 假設距離目標剩餘 5 公尺（模擬紅燈停車的緩衝距離），或直接傳入 0 觸發時間收斂
            rawTargetNotch = CalculateTascNotch(currentSpeedKmph, state.nextSpeedLimitDistance - ATC_DISTANCE_MARGIN_M, 0.0, currentGradePermil, false);
        }
        else // 一般限速行駛
        {
            double targetSpeedMps = (idealSpeedKmph - ATC_TARGET_BUFFER) * KMPH_TO_MPS;
            double speedErrorMps = Math.Max(0, currentSpeedMps - targetSpeedMps);
            targetDecelMps2 = speedErrorMps / ATC_RESPONSE_TIME_S;
        
        // 3. 坡度補償與最終輸出
            double gradeRatio = currentGradePermil / 1000.0;
            double gradeAccelerationMps2 = -0.75 * GRAVITY_MPS2 * gradeRatio;
            double requiredDecelMps2 = targetDecelMps2 + gradeAccelerationMps2;

            if (requiredDecelMps2 <= 0)
            {
                rawTargetNotch = 0;
            }
            else
            {
                int brakeNotch = _brakeModel.GetNotchForDeceleration(requiredDecelMps2);
                rawTargetNotch = (brakeNotch == 0) ? 0 : -(brakeNotch + 1);
            }
        }

        // --- (新增：ATC 煞車平滑緩衝邏輯) ---

        // 3. 逐級升降檔 (Step-by-Step Buffering)
        // 比較「計算出的目標」與「上一次實際輸出」
        // 注意：煞車檔位是負數 (0, -1, -2... -8)
        // 更小的數字代表更強的煞車 (例如 -5 比 -2 強)

        int finalOutput = rawTargetNotch;

        // [加強煞車]：如果目標比上次更強 (例如上次 -2, 目標 -5)
        // 限制一次只能加一級 (變成 -3)
        if (finalOutput < _lastFilteredBrakeNotch - 1)
        {
            finalOutput = _lastFilteredBrakeNotch - 1;
        }
        // [緩解煞車]：如果目標比上次更弱 (例如上次 -5, 目標 -2)
        // 限制一次只能放一級 (變成 -4)
        else if (finalOutput > _lastFilteredBrakeNotch + 1)
        {
            finalOutput = _lastFilteredBrakeNotch + 1;
        }

        // 4. 防抖動時間鎖定 (Time Delay)
        // 只有當檔位發生變化時才檢查
        if (finalOutput != _lastFilteredBrakeNotch)
        {
            double timeSinceChange = (DateTime.Now - _lastBrakeChangeTime).TotalSeconds;

            // 如果距離上次變動時間太短，則忽略這次改變，維持原檔位
            // (除非是緊急情況：若目標是緊急煞車或極大落差，可視情況略過，但這裡為了平滑先強制鎖定)
            if (timeSinceChange < BRAKE_HOLD_TIME_S)
            {
                finalOutput = _lastFilteredBrakeNotch;
            }
            else
            {
                // 時間足夠，允許變動，更新時間與狀態
                _lastFilteredBrakeNotch = finalOutput;
                _lastBrakeChangeTime = DateTime.Now;
            }
        }
        /*
        // 4. 防頓挫平滑緩衝 (保持原有功能)
        if ((DateTime.Now - _atcEngagementTime).TotalSeconds < ATC_SMOOTHING_TIME_S)
        {
            if (finalOutput < -1) finalOutput = -1; // 剛開始只允許 B1
        }
        */
        // 更新記錄並回傳
        _lastFilteredBrakeNotch = finalOutput;
        return finalOutput;
    }

    // --- TASC 共用計算邏輯 (保持不變) ---
    private int CalculateTascNotch(double currentSpeedKmph, double distanceM, double targetSpeedKmph, double currentGradePermil, bool isForStation)
    {
        double currentSpeedMps = currentSpeedKmph * KMPH_TO_MPS;
        double targetSpeedMps = targetSpeedKmph * KMPH_TO_MPS;
        double gradeRatio = currentGradePermil / 1000.0;
        double gradeAccelerationMps2 = -0.75 * GRAVITY_MPS2 * gradeRatio;

        double correctedBaseDecelMps2 = _baseDecelerationMps2 - Math.Max(gradeAccelerationMps2, 0.0);
        if (correctedBaseDecelMps2 < 0.1) correctedBaseDecelMps2 = 0.1;

        double targetSq = targetSpeedMps * targetSpeedMps;
        double expectedSpeedSquared = targetSq + 2.0 * correctedBaseDecelMps2 * distanceM;
        double expectedSpeedMps = (expectedSpeedSquared > 0) ? Math.Sqrt(expectedSpeedSquared) : 0.0;

        if (isForStation)
        {
            this.LastExpectedSpeedKmph = expectedSpeedMps * MPS_TO_KMPH;
        }

        // TASC 仍然使用前饋控制，因為定點停車需要精確度
        double targetDecelMps2 = 0.0;
        if (expectedSpeedMps > 0.01)
        {
            double expectedDecel = correctedBaseDecelMps2;
            double speedRatio = currentSpeedMps / expectedSpeedMps;
            targetDecelMps2 = expectedDecel * speedRatio + (currentSpeedMps - expectedSpeedMps) / APPROACH_TIME_S;
        }
        else if (currentSpeedMps > 0.1)
        {
            targetDecelMps2 = currentSpeedMps / APPROACH_TIME_S;
        }

        double requiredDecelMps2 = targetDecelMps2 + gradeAccelerationMps2;

        if (requiredDecelMps2 <= 0) return 0;
        int brakeNotch = _brakeModel.GetNotchForDeceleration(requiredDecelMps2);
        if (brakeNotch == 0) return 0;
        return -(brakeNotch + 1);
    }
}