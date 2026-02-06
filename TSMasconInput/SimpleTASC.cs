using System;
using TrainCrew;

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
    private const double ATC_SMOOTHING_TIME_S = 0.2;

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
    public int GetAtoNotchForATC(double currentSpeedKmph, double idealSpeedKmph, double currentGradePermil, int currentPnotch)
    {
        // 1. 狀態機邏輯 (State Machine)
        if (!_isAtcBraking)
        {
            // 目前沒煞車：只有「速度 > 限速」才觸發
            if (currentSpeedKmph > idealSpeedKmph)
            {
                _isAtcBraking = true;

                // 如果是從加速狀態 (P > 0) 觸發，記錄現在時間，準備進行抑速緩衝
                if (currentPnotch > 0)
                {
                    _atcEngagementTime = DateTime.Now;
                }
                else
                {
                    _atcEngagementTime = DateTime.MinValue;
                }
            }
        }
        else
        {
            // 目前煞車中：速度需低於 (限速 - 解除門檻) 才解除
            if (currentSpeedKmph <= (idealSpeedKmph - ATC_RESET_RANGE))
            {
                _isAtcBraking = false;
            }
        }

        // 如果不需要煞車，直接回傳 0
        if (!_isAtcBraking) return 0;

        // 2. 設定目標速度 (加入緩衝)
        // 目標 = 限速 - 2.0 (例如限速 90 -> 目標 88)
        double targetSpeedForCalculation = idealSpeedKmph - ATC_TARGET_BUFFER;
        if (targetSpeedForCalculation < 0.1) targetSpeedForCalculation = 0.1;

        double currentSpeedMps = currentSpeedKmph * KMPH_TO_MPS;
        double targetSpeedMps = targetSpeedForCalculation * KMPH_TO_MPS;

        // 3. 計算煞車力道 (線性 P-Control)
        double speedErrorMps = currentSpeedMps - targetSpeedMps;
        if (speedErrorMps < 0) speedErrorMps = 0;

        // 需求減速度 = 誤差 / 2.0秒
        double targetDecelMps2 = speedErrorMps / ATC_RESPONSE_TIME_S;

        // 4. 坡度補償
        double gradeRatio = currentGradePermil / 1000.0;
        double gradeAccelerationMps2 = -0.75 * GRAVITY_MPS2 * gradeRatio;

        double requiredDecelMps2 = targetDecelMps2 + gradeAccelerationMps2;

        if (requiredDecelMps2 <= 0) return 0;

        int brakeNotch = _brakeModel.GetNotchForDeceleration(requiredDecelMps2);

        // 轉換為 TrainCrew 格式 (負數表示煞車, -1=B1, -2=B2...)
        int calculatedOutput = (brakeNotch == 0) ? 0 : -(brakeNotch + 1);

        // ==========================================
        // 5. 頓挫抑制邏輯 (Anti-Jerk Smoothing)
        // ==========================================
        // 檢查是否處於 ATC 剛啟動的緩衝時間內 (1.5秒)
        if ((DateTime.Now - _atcEngagementTime).TotalSeconds < ATC_SMOOTHING_TIME_S)
        {
            // 如果計算出來的煞車力比 B1 還強 (例如 B3, B4...)
            // 我們強制限制在 B1 (抑速)，讓動力先斷開，列車緩衝一下
            if (calculatedOutput < -1)
            {
                return -1; // 強制輸出 B1
            }
        }

        return calculatedOutput;
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