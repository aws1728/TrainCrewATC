// 檔案名稱: SimpleTASC.cs
// (請將此檔案新增到您的 TSMasconInput 專案中)
using System;
using TrainCrew;

/// <summary>
/// 這是 bve-autopilot TASC 演算法的核心 C# 移植版。
/// </summary>
public class SimpleTASC
{
    // --- 核心常數 ---
    private const double APPROACH_TIME_S = 2.0;

    // --- 物理單位轉換 ---
    private const double KMPH_TO_MPS = 1.0 / 3.6;
    private const double MPS_TO_KMPH = 3.6;
    private const double KMPHPS_TO_MPS2 = 1.0 / 3.6;
    private const double GRAVITY_MPS2 = 9.80665;

    // --- 煞車模型 ---
    private BrakeModel _brakeModel;
    private double _baseDecelerationMps2; // TASC 自動停車/硬性限速煞車 (75% 力道)
    private double _hintDecelerationMps2; // 視覺提示用的溫和減速度 (50% 力道)

    public double LastExpectedSpeedKmph { get; private set; }

    public SimpleTASC(double maxBrakeDecel_Kmphps, int brakeNotches)
    {
        _brakeModel = new BrakeModel(maxBrakeDecel_Kmphps * KMPHPS_TO_MPS2, brakeNotches);

        // 75% 力道：用於實際的 TASC 停車和 ATC 強制減速
        _baseDecelerationMps2 = (maxBrakeDecel_Kmphps * 0.75) * KMPHPS_TO_MPS2;

        // 50% 力道：用於計算儀表板上的紅色三角形 (提早提示)
        _hintDecelerationMps2 = (maxBrakeDecel_Kmphps * 0.5) * KMPHPS_TO_MPS2;

        this.LastExpectedSpeedKmph = 0.0;
    }

    /// <summary>
    /// 計算儀表板紅色三角形的「理想速度」(使用溫和的 50% 煞車力)
    /// </summary>
    public double GetIdealSpeedForTarget(double distanceM, double targetSpeedKmph, double currentGradePermil)
    {
        double targetSpeedMps = targetSpeedKmph * KMPH_TO_MPS;
        double gradeRatio = currentGradePermil / 1000.0;
        double gradeAccelerationMps2 = -0.75 * GRAVITY_MPS2 * gradeRatio;

        // 使用溫和的 _hintDecelerationMps2
        double correctedBaseDecelMps2 = _hintDecelerationMps2 - Math.Max(gradeAccelerationMps2, 0.0);
        if (correctedBaseDecelMps2 < 0.1) correctedBaseDecelMps2 = 0.1;

        // 物理公式: V_ideal^2 = V_target^2 + 2 * a * s
        double targetSpeedSquared = targetSpeedMps * targetSpeedMps;
        double decelDistanceComponent = 2.0 * correctedBaseDecelMps2 * distanceM;
        double idealSpeedSquared = targetSpeedSquared + decelDistanceComponent;

        return Math.Sqrt(idealSpeedSquared) * MPS_TO_KMPH;
    }

    /// <summary>
    /// 計算「車站停車」需要的 ATO 煞車檔位 (目標速度為 0)
    /// </summary>
    public int GetAtoNotch(double currentSpeedKmph, double distanceToStopM, double currentGradePermil)
    {
        return CalculateTascNotch(currentSpeedKmph, distanceToStopM, 0.0, currentGradePermil, true);
    }

    /// <summary>
    /// 【新增】計算「到達限速點」需要的 ATO 煞車檔位 (目標速度為 targetSpeedKmph)
    /// 這會使用強力的 80% 煞車曲線，確保在檢查點前降速
    /// </summary>
    public int GetAtoNotchToTargetSpeed(double currentSpeedKmph, double distanceToTargetM, double targetSpeedKmph, double currentGradePermil)
    {
        // 如果已經比目標速度慢了，就不需要為了「預告」而煞車
        // (但如果距離很短且速度很快，還是需要計算，這裡交給核心邏輯判斷)
        if (distanceToTargetM <= 0) return 0;

        // 呼叫核心計算，不更新 LastExpectedSpeedKmph (那是給車站用的)
        return CalculateTascNotch(currentSpeedKmph, distanceToTargetM, targetSpeedKmph, currentGradePermil, false);
    }

    /// <summary>
    /// 計算「目前超速」需要的 ATO 煞車檔位 (單純維持速度)
    /// </summary>
    public int GetAtoNotchForATC(double currentSpeedKmph, double idealSpeedKmph, double currentGradePermil)
    {
        if (currentSpeedKmph <= idealSpeedKmph) return 0;

        double currentSpeedMps = currentSpeedKmph * KMPH_TO_MPS;
        double idealSpeedMps = idealSpeedKmph * KMPH_TO_MPS;

        double gradeRatio = currentGradePermil / 1000.0;
        double gradeAccelerationMps2 = -0.75 * GRAVITY_MPS2 * gradeRatio;

        // 簡單的回饋控制：消除速度差
        double speedErrorMps = currentSpeedMps - idealSpeedMps;
        double targetDecelMps2 = speedErrorMps / APPROACH_TIME_S;

        double requiredDecelMps2 = targetDecelMps2 + gradeAccelerationMps2;

        if (requiredDecelMps2 <= 0) return 0;

        int brakeNotch = _brakeModel.GetNotchForDeceleration(requiredDecelMps2);
        if (brakeNotch == 0) return 0;
        return -(brakeNotch + 1);
    }

    // --- 內部共用的 TASC 計算邏輯 ---
    private int CalculateTascNotch(double currentSpeedKmph, double distanceM, double targetSpeedKmph, double currentGradePermil, bool isForStation)
    {
        double currentSpeedMps = currentSpeedKmph * KMPH_TO_MPS;
        double targetSpeedMps = targetSpeedKmph * KMPH_TO_MPS;

        double gradeRatio = currentGradePermil / 1000.0;
        double gradeAccelerationMps2 = -0.75 * GRAVITY_MPS2 * gradeRatio;

        // 使用標準的 80% 煞車力道
        double correctedBaseDecelMps2 = _baseDecelerationMps2 - Math.Max(gradeAccelerationMps2, 0.0);
        if (correctedBaseDecelMps2 < 0.1) correctedBaseDecelMps2 = 0.1;

        // 計算理想速度 (期待速度)
        // V_expected^2 = V_target^2 + 2 * a * s
        double targetSq = targetSpeedMps * targetSpeedMps;
        double expectedSpeedSquared = targetSq + 2.0 * correctedBaseDecelMps2 * distanceM;
        double expectedSpeedMps = (expectedSpeedSquared > 0) ? Math.Sqrt(expectedSpeedSquared) : 0.0;

        // 如果是車站停車，我們更新此屬性供儀表顯示
        if (isForStation)
        {
            this.LastExpectedSpeedKmph = expectedSpeedMps * MPS_TO_KMPH;
        }

        // TASC 核心公式 (2秒法則)
        double targetDecelMps2 = 0.0;
        // 防止除以零，且只有在當前速度大於目標速度時才需積極減速
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

        // 坡度補償
        double requiredDecelMps2 = targetDecelMps2 + gradeAccelerationMps2;

        if (requiredDecelMps2 <= 0) return 0;

        int brakeNotch = _brakeModel.GetNotchForDeceleration(requiredDecelMps2);
        if (brakeNotch == 0) return 0;
        return -(brakeNotch + 1);
    }
}