// 檔案名：TASCUtils.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        private static readonly Data data = new Data();
        private static readonly List<SpeedLimitClass> SpeedLimitList;
        private static readonly List<StopPositionOffsetClass> StopPositionOffsetList;

        static TASCUtils()
        {
            SpeedLimitList = LoadXmlData("TSMasconInput.libraries.Xml.SpeedLimit.xml", element => new SpeedLimitClass
            {
                Direction = element.Element("Direction")?.Value ?? "",
                StartPos = float.TryParse(element.Element("StartPos")?.Value, out float sp) ? sp : 0f,
                EndPos = float.TryParse(element.Element("EndPos")?.Value, out float ep) ? ep : 0f,
                Limit = float.TryParse(element.Element("Limit")?.Value, out float lim) ? lim : 120f,
                BackStopPosName = element.Element("BackStopPosName")?.Value ?? "",
                NextStopPosName = element.Element("NextStopPosName")?.Value ?? ""
            });

            StopPositionOffsetList = LoadXmlData("TSMasconInput.libraries.Xml.StopPositionOffset.xml", element => new StopPositionOffsetClass
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

        public static List<T> LoadXmlData<T>(string resourceName, Func<XElement, T> selector)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        return new List<T>();
                    }
                    return XElement.Load(stream).Elements().Select(selector).ToList();
                }
            }
            catch
            {
                return new List<T>();
            }
        }

        public static float GetStopPositionOffset(TrainState _state)
        {
            string direction = "下り";
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
                if (_state.nextStopType.Contains("停車"))
                {
                    // 改用 foreach 避免 GC Allocation
                    foreach (var s in StopPositionOffsetList)
                    {
                        if (s.Direction == direction && s.StationName == _state.nextStaName)
                        {
                            int offsetIndex = Math.Min(_state.CarStates.Count - 1, s.Offset.Count - 1);
                            if (offsetIndex >= 0)
                            {
                                float val = s.Offset[offsetIndex];
                                if (val > 0.0f)
                                {
                                    offset = val;
                                }
                            }
                            break; // 找到對應車站即跳出
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("GetStopPositionOffset Error: " + ex.Message); }

            return offset;
        }

        public static void GetLimitSpeed(TrainState _state, float distance, float offset,
            out float xmlLimitSpeed, out float xmlLimitDistance)
        {
            string direction = "下り";
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

            xmlLimitSpeed = 120.0f;
            xmlLimitDistance = 0.0f;
            float lookahead = 300.0f;

            try
            {
                SpeedLimitClass bestMatch = null;
                string backStopPosName = _state.stationList[backStaIndex].StopPosName;
                string nextStopPosName = _state.stationList[nowStaIndex].StopPosName;

                // 改用 foreach 取代 Where 與 OrderByDescending
                foreach (var s in SpeedLimitList)
                {
                    if (s.Direction != direction) continue;
                    if (s.BackStopPosName != backStopPosName && s.NextStopPosName != nextStopPosName) continue;

                    if ((s.StartPos - offset + lookahead) > dist && dist >= (s.EndPos - offset))
                    {
                        if (bestMatch == null || s.StartPos > bestMatch.StartPos)
                        {
                            bestMatch = s;
                        }
                    }
                }

                if (bestMatch != null)
                {
                    xmlLimitSpeed = bestMatch.Limit;
                    float endPosAdjusted = (bestMatch.EndPos - offset) > 0.0f ? (bestMatch.EndPos - offset) : 0.0f;
                    xmlLimitDistance = (dist - endPosAdjusted) > 0.0f ? (dist - endPosAdjusted) : 0.0f;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("GetLimitSpeed Error: " + ex.Message);
                xmlLimitSpeed = 120.0f;
                xmlLimitDistance = 0.0f;
            }
        }
    }

    public class SpeedLimitClass
    {
        public string Direction { get; set; } = "";
        public float StartPos { get; set; }
        public float EndPos { get; set; }
        public float Limit { get; set; }
        public string BackStopPosName { get; set; } = "";
        public string NextStopPosName { get; set; } = "";
    }

    public class StopPositionOffsetClass
    {
        public string Direction { get; set; } = "";
        public string StationName { get; set; } = "";
        public List<float> Offset { get; set; } = new List<float>();
    }
}