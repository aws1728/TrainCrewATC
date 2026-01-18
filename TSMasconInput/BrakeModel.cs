// 檔案名稱: BrakeModel.cs
// (請將此檔案新增到您的 TSMasconInput 專案中)
using System;

/// <summary>
/// 這是一個簡化的 `制動特性.cpp` 類別，
/// 它假設車輛的 B1 到 B_max 煞車力道是「均勻分布」的。
/// 這符合 `autopilot.ini` 中只設定 `maxdeceleration` 時的預設行為。
/// </summary>
public class BrakeModel
{
    private double _maxDecelerationMps2; // B_max (例如 B7) 對應的減速度 (m/s^2)
    private int _brakeNotches;          // 常用煞車總段數 (例如 7)

    public BrakeModel(double maxDecelerationMps2, int brakeNotches)
    {
        _maxDecelerationMps2 = maxDecelerationMps2;
        _brakeNotches = brakeNotches;
    }

    /// <summary>
    /// 根據需要的減速度，計算出對應的煞車檔位 (1 ~ _brakeNotches)
    /// </summary>
    public int GetNotchForDeceleration(double requiredDecelMps2)
    {
        if (requiredDecelMps2 <= 0)
            return 0; // N 檔

        if (requiredDecelMps2 >= _maxDecelerationMps2)
            return _brakeNotches; // B_max (例如 B7)

        // 假設煞車力道是線性/均勻的
        // (移植自 `制動特性::pressure_rates::ノッチ` 的簡化邏輯)
        double brakeRatio = requiredDecelMps2 / _maxDecelerationMps2;

        // 轉換為檔位 (1 到 _brakeNotches 之間)
        // 使用 Ceiling (向上取整) 來確保煞車力道足夠 (bve-autopilot 也是這樣做的)
        int notch = (int)Math.Ceiling(brakeRatio * _brakeNotches);

        return Math.Max(1, notch); // 至少 B1
    }
}