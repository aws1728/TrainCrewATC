// 檔案名：TASCUtils.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TrainCrew;

namespace TSMasconInput
{
    /// <summary>
    /// TASC 共用工具類（靜態類，任何地方都可以直接呼叫）
    /// </summary>
    public static class TASCUtils
    {
        // 建立一個簡單的 Data 工具
        /// <summary>
        /// データクラス
        /// </summary>
        private static readonly Data data = new Data();

        /// <summary>
        /// Xml 速度制限情報
        /// </summary>
        private static readonly List<SpeedLimitClass> SpeedLimitList;

        /// <summary>
        /// Xml 停止位置オフセット情報
        /// </summary>
        private static readonly List<StopPositionOffsetClass> StopPositionOffsetList;

        /// <summary>
        /// 靜態コンストラクタ（ゲーム起動時に1回だけ実行）
        /// </summary>
        static TASCUtils()
        {
            // Xmlファイル読み込み（只讀一次，超快！）
            SpeedLimitList = LoadXmlData(@"libraries\Xml\SpeedLimit.xml", element => new SpeedLimitClass
            {
                Direction = element.Element("Direction")?.Value ?? "",
                StartPos = float.TryParse(element.Element("StartPos")?.Value, out float sp) ? sp : 0f,
                EndPos = float.TryParse(element.Element("EndPos")?.Value, out float ep) ? ep : 0f,
                Limit = float.TryParse(element.Element("Limit")?.Value, out float lim) ? lim : 120f,
                BackStopPosName = element.Element("BackStopPosName")?.Value ?? "",
                NextStopPosName = element.Element("NextStopPosName")?.Value ?? ""
            });

            StopPositionOffsetList = LoadXmlData(@"libraries\Xml\StopPositionOffset.xml", element => new StopPositionOffsetClass
            {
                Direction = element.Element("Direction")?.Value ?? "",
                StationName = element.Element("StationName")?.Value ?? "",
                Offset = new List<float>
                {
                    float.TryParse(element.Element("Offset1")?.Value, out float o1) ? o1 : 0f,
                    float.TryParse(element.Element("Offset2")?.Value, out float o2) ? o2 : 0f,
                    float.TryParse(element.Element("Offset3")?.Value, out float o3) ? o3 : 0f,
                    float.TryParse(element.Element("Offset4")?.Value, out float o4) ? o4 : 0f,
                    float.TryParse(element.Element("Offset5")?.Value, out float o5) ? o5 : 0f,
                    float.TryParse(element.Element("Offset6")?.Value, out float o6) ? o6 : 0f
                }
            });
        }

        /// <summary>
        /// XmlData読み込みメソッド
        /// </summary>
        public static List<T> LoadXmlData<T>(string filePath, Func<XElement, T> selector)
        {
            try
            {
                return XElement.Load(filePath).Elements().Select(selector).ToList();
            }
            catch
            {
                return new List<T>();
            }
        }

        /// <summary>
        /// 停止位置オフセット取得メソッド
        /// </summary>
        /// <param name="_state">列車の状態</param>
        /// <returns></returns>
        public static float GetStopPositionOffset(TrainState _state)
        {
            string direction = "下り"; // 預設值，防止未載入時當機
            if (!string.IsNullOrEmpty(_state.diaName))
            {
                var match = System.Text.RegularExpressions.Regex.Match(_state.diaName, @"\d+");
                if (match.Success && int.TryParse(match.Value, out int num) && num > 0)
                {
                    direction = data.IsEven(num) ? "上り" : "下り";
                }
            }
            float offset = 0.0f;

            if (StopPositionOffsetList == null || StopPositionOffsetList.Count == 0) return offset;

            try
            {
                var str = StopPositionOffsetList
                    .Where(s => s.Direction == direction)
                    .Where(s => s.StationName == _state.nextStaName)
                    .Select(s => s.Offset[Math.Min(_state.CarStates.Count - 1, s.Offset.Count - 1)])
                    .FirstOrDefault();

                //一致したデータがあれば取得（停車する場合のみ）
                if (str > 0.0f && _state.nextStopType.Contains("停車"))
                    offset = str;
            }
            catch { }

            return offset;
        }

        /// <summary>
        /// 【純粹】取得目前位置前方「XML 自訂的限速」與剩餘距離
        /// 不與遊戲系統比對，由主程式自行決定最終採用哪一個
        /// </summary>
        public static void GetLimitSpeed(TrainState _state, float distance, float offset,
            out float xmlLimitSpeed, out float xmlLimitDistance)
        {
            string direction = "下り"; // 預設值，防止未載入時當機
            if (!string.IsNullOrEmpty(_state.diaName))
            {
                var match = System.Text.RegularExpressions.Regex.Match(_state.diaName, @"\d+");
                if (match.Success && int.TryParse(match.Value, out int num) && num > 0)
                {
                    direction = data.IsEven(num) ? "上り" : "下り";
                }
            }
            int backStaIndex = (_state.nowStaIndex - 1 < 0) ? 0 : _state.nowStaIndex - 1;
            int nowStaIndex = _state.nowStaIndex;
            float dist = (distance < 0.0f) ? 0.0f : distance;

            // 預設值：沒抓到任何 XML 限速時
            xmlLimitSpeed = 120.0f;
            xmlLimitDistance = 0.0f;

            // 定義您想提早多少公尺抓取 (例如提早 600m)
            float lookahead = 300.0f;

            try
            {
                var str = SpeedLimitList
                    .Where(s => s.Direction == direction)
                    // 判斷車站區間 (保持不變)
                    .Where(s => s.BackStopPosName == _state.stationList[backStaIndex].StopPosName ||
                                s.NextStopPosName == _state.stationList[nowStaIndex].StopPosName)

                    // 【關鍵修改】
                    // 原始: (s.StartPos - offset) > dist
                    // 修改: 加上 lookahead，讓我們能看見前方未到達的起點
                    .Where(s => (s.StartPos - offset + lookahead) > dist && dist >= (s.EndPos - offset))

                    // 【建議新增】排序
                    // 因為提早抓取可能會同時抓到多個限速，我們應該優先取「最近的一個」(StartPos 最大的)
                    .OrderByDescending(s => s.StartPos)
                    .FirstOrDefault();

                if (str != null)
                {
                    xmlLimitSpeed = str.Limit;
                    // 計算剩餘距離 (這會是「目前位置」到「限速結束點 EndPos」的距離)
                    float endPosAdjusted = (str.EndPos - offset) > 0.0f ? (str.EndPos - offset) : 0.0f;
                    xmlLimitDistance = (dist - endPosAdjusted) > 0.0f ? (dist - endPosAdjusted) : 0.0f;
                }
                else
                {
                    xmlLimitSpeed = 120.0f;
                    xmlLimitDistance = 0.0f;
                }
            }
            catch
            {
                xmlLimitSpeed = 120.0f;
                xmlLimitDistance = 0.0f;
            }
        }
    }

    /// <summary>
    /// 速度制限クラス
    /// </summary>
    public class SpeedLimitClass
    {
        /// <summary>上下</summary>
        public string Direction { get; set; } = "";
        /// <summary>開始位置(m)</summary>
        public float StartPos { get; set; }
        /// <summary>終了位置(m)</summary>
        public float EndPos { get; set; }
        /// <summary>制限速度(km/h)</summary>
        public float Limit { get; set; }
        /// <summary>前の停止位置名</summary>
        public string BackStopPosName { get; set; } = "";
        /// <summary>次の停止位置名</summary>
        public string NextStopPosName { get; set; } = "";
    }

    /// <summary>
    /// 停止位置オフセットクラス
    /// </summary>
    public class StopPositionOffsetClass
    {
        /// <summary>上下</summary>
        public string Direction { get; set; } = "";
        /// <summary>駅名</summary>
        public string StationName { get; set; } = "";
        /// <summary>オフセット[m]</summary>
        public List<float> Offset { get; set; } = new List<float>();
    }
}