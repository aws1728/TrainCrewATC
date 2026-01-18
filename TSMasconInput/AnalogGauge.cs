// 檔案名稱: AnalogGauge.cs
// (請將此檔案新增到您的 TSMasconInput 專案中)
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TSMasconInput
{
    public class AnalogGauge : UserControl
    {
        private float _value = 0;
        private float _value2 = -1; // 用於 MR 壓力 (預設 -1 為隱藏)
        private float _value3 = -1; // 第三根指針 (預設 -1 為隱藏)
        private float _targetValue = -1; // TASC 理想速度 (預設 -1 為隱藏)
        private float _maxValue = 120;
        private string _gaugeTitle = "Speed";
        private string _unit = "km/h";

        private const float StartAngle = 135f; // 儀表起始角度
        private const float SweepAngle = 270f; // 儀表總共的角度

        public Color TargetValueColor { get; set; } = Color.Blue; // 預設為藍色 (TASC)

        // TargetValue2 (用於預告速限)
        private float _targetValue2 = -1;
        public Color TargetValue2Color { get; set; } = Color.LimeGreen; // 預設為綠色

        // 【*** 新增屬性 (用於目前速限) ***】
        private float _targetValue3 = -1;
        public Color TargetValue3Color { get; set; } = Color.Red; // 預設為紅色

        public float TargetValue3
        {
            get { return _targetValue3; }
            set { _targetValue3 = value; this.Invalidate(); }
        }
        // --- ---

        public float TargetValue2
        {
            get { return _targetValue2; }
            set { _targetValue2 = value; this.Invalidate(); }
        }

        public float Value
        {
            get { return _value; }
            set { _value = value; this.Invalidate(); } // 重繪
        }

        public float Value2
        {
            get { return _value2; }
            set { _value2 = value; this.Invalidate(); }
        }

        public float Value3
        {
            get { return _value3; }
            set { _value3 = value; this.Invalidate(); }
        }

        public float TargetValue
        {
            get { return _targetValue; }
            set { _targetValue = value; this.Invalidate(); }
        }

        public float MaxValue
        {
            get { return _maxValue; }
            set { _maxValue = value; this.Invalidate(); }
        }

        public string GaugeTitle
        {
            get { return _gaugeTitle; }
            set { _gaugeTitle = value; this.Invalidate(); }
        }

        public string Unit
        {
            get { return _unit; }
            set { _unit = value; this.Invalidate(); }
        }

        public AnalogGauge()
        {
            this.DoubleBuffered = true;
            this.Resize += (s, e) => this.Invalidate(); // 改變大小時重繪
        }

        // 核心繪圖函式
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; // 高品質繪圖

            RectangleF rect = new RectangleF(
                this.ClientRectangle.X + 5,
                this.ClientRectangle.Y + 5,
                this.ClientRectangle.Width - 10,
                this.ClientRectangle.Height - 10);

            float centerX = rect.Width / 2f + rect.X;
            float centerY = rect.Height / 2f + rect.Y;
            float radius = Math.Min(rect.Width, rect.Height) / 2f - 20; // 留出邊距

            // 1. 繪製儀表背景
            using (GraphicsPath bgPath = new GraphicsPath())
            {
                bgPath.AddArc(centerX - radius, centerY - radius, radius * 2, radius * 2, StartAngle, SweepAngle);
                using (Pen bgPen = new Pen(Color.FromArgb(100, 100, 100), 10))
                {
                    g.DrawPath(bgPen, bgPath);
                }
            }

            // 2. 繪製刻度
            int majorTicks = 10;
            if (MaxValue == 120) majorTicks = 12; // 120km/h (每 10)
            if (MaxValue == 1000) majorTicks = 10; // 1000kPa (每 100)

            using (Font tickFont = new Font(this.Font.FontFamily, 8f))
            using (Pen tickPen = new Pen(Color.Black, 2))
            using (SolidBrush textBrush = new SolidBrush(Color.Black))
            {
                for (int i = 0; i <= majorTicks; i++)
                {
                    float angle = StartAngle + (SweepAngle * i / majorTicks);
                    float rad = (float)(angle * Math.PI / 180.0);

                    PointF p1 = new PointF(
                        centerX + (radius - 8) * (float)Math.Cos(rad),
                        centerY + (radius - 8) * (float)Math.Sin(rad)
                    );
                    PointF p2 = new PointF(
                        centerX + radius * (float)Math.Cos(rad),
                        centerY + radius * (float)Math.Sin(rad)
                    );
                    g.DrawLine(tickPen, p1, p2);

                    string txt = (MaxValue * i / majorTicks).ToString("0");
                    SizeF txtSize = g.MeasureString(txt, tickFont);
                    g.DrawString(txt, tickFont, textBrush, new PointF(
                        centerX + (radius - 22) * (float)Math.Cos(rad) - txtSize.Width / 2,
                        centerY + (radius - 22) * (float)Math.Sin(rad) - txtSize.Height / 2
                    ));
                }
            }

            // 3. 繪製標題和單位 (包含數位速度)
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center })
            using (Font titleFont = new Font(this.Font.FontFamily, 10, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.Black))
            {
                g.DrawString(GaugeTitle, titleFont, textBrush, centerX, centerY + 30, sf);

                string displayText = "";
                if (Unit == "km/h")
                {
                    displayText = $"{this.Value:0.0} {Unit}";
                }
                else
                {
                    displayText = $"{this.Value:0} {Unit}";
                }

                g.DrawString(displayText, this.Font, textBrush, centerX, centerY + 50, sf);
            }

            // 4a. TASC 理想速度 (TargetValue - 藍色)
            if (TargetValue >= 0)
            {
                float targetAngle = StartAngle + (SweepAngle * (TargetValue / MaxValue));
                float targetRad = (float)(targetAngle * Math.PI / 180.0);
                PointF[] triangle = new PointF[3];
                triangle[0] = new PointF(
                    centerX + (radius + 2) * (float)Math.Cos(targetRad),
                    centerY + (radius + 2) * (float)Math.Sin(targetRad)
                );
                triangle[1] = new PointF(
                    centerX + (radius + 10) * (float)Math.Cos(targetRad - 0.05f),
                    centerY + (radius + 10) * (float)Math.Sin(targetRad - 0.05f)
                );
                triangle[2] = new PointF(
                    centerX + (radius + 10) * (float)Math.Cos(targetRad + 0.05f),
                    centerY + (radius + 10) * (float)Math.Sin(targetRad + 0.05f)
                );

                using (SolidBrush targetBrush = new SolidBrush(this.TargetValueColor))
                {
                    g.FillPolygon(targetBrush, triangle);
                }
            }

            // 4b. 預告速度限制 (TargetValue2 - 綠色)
            if (TargetValue2 >= 0)
            {
                float targetAngle = StartAngle + (SweepAngle * (TargetValue2 / MaxValue));
                float targetRad = (float)(targetAngle * Math.PI / 180.0);
                PointF[] triangle = new PointF[3];
                triangle[0] = new PointF(
                    centerX + (radius + 2) * (float)Math.Cos(targetRad),
                    centerY + (radius + 2) * (float)Math.Sin(targetRad)
                );
                triangle[1] = new PointF(
                    centerX + (radius + 10) * (float)Math.Cos(targetRad - 0.05f),
                    centerY + (radius + 10) * (float)Math.Sin(targetRad - 0.05f)
                );
                triangle[2] = new PointF(
                    centerX + (radius + 10) * (float)Math.Cos(targetRad + 0.05f),
                    centerY + (radius + 10) * (float)Math.Sin(targetRad + 0.05f)
                );

                using (SolidBrush targetBrush = new SolidBrush(this.TargetValue2Color))
                {
                    g.FillPolygon(targetBrush, triangle);
                }
            }

            // 4c. 【*** 新增: 目前速度限制 (TargetValue3 - 紅色) ***】
            if (TargetValue3 >= 0)
            {
                float targetAngle = StartAngle + (SweepAngle * (TargetValue3 / MaxValue));
                float targetRad = (float)(targetAngle * Math.PI / 180.0);
                PointF[] triangle = new PointF[3];
                triangle[0] = new PointF(
                    centerX + (radius + 2) * (float)Math.Cos(targetRad),
                    centerY + (radius + 2) * (float)Math.Sin(targetRad)
                );
                triangle[1] = new PointF(
                    centerX + (radius + 10) * (float)Math.Cos(targetRad - 0.05f),
                    centerY + (radius + 10) * (float)Math.Sin(targetRad - 0.05f)
                );
                triangle[2] = new PointF(
                    centerX + (radius + 10) * (float)Math.Cos(targetRad + 0.05f),
                    centerY + (radius + 10) * (float)Math.Sin(targetRad + 0.05f)
                );

                using (SolidBrush targetBrush = new SolidBrush(this.TargetValue3Color))
                {
                    g.FillPolygon(targetBrush, triangle);
                }
            }
            // --- ---


            // 5. 繪製副指針 (Value2 - 紅色, MR 壓力)
            if (Value2 >= 0)
            {
                float value2Angle = StartAngle + (SweepAngle * (Value2 / MaxValue));
                float value2Rad = (float)(value2Angle * Math.PI / 180.0);
                PointF needleEnd = new PointF(
                    centerX + (radius - 8) * (float)Math.Cos(value2Rad),
                    centerY + (radius - 8) * (float)Math.Sin(value2Rad)
                );
                PointF needleBase = new PointF(
                    centerX - (radius / 5) * (float)Math.Cos(value2Rad),
                    centerY - (radius / 5) * (float)Math.Sin(value2Rad)
                );
                using (Pen needlePen = new Pen(Color.Red, 2))
                {
                    g.DrawLine(needlePen, needleBase, needleEnd);
                }
            }

            // 6.繪製副指針(Value3 - 灰色, BC 壓力)
            if (Value3 >= 0)
            {
                float value3Angle = StartAngle + (SweepAngle * (Value3 / MaxValue));
                float value3Rad = (float)(value3Angle * Math.PI / 180.0);
                PointF needleEnd = new PointF(
                    centerX + (radius - 8) * (float)Math.Cos(value3Rad),
                    centerY + (radius - 8) * (float)Math.Sin(value3Rad)
                );
                PointF needleBase = new PointF(
                    centerX - (radius / 5) * (float)Math.Cos(value3Rad),
                    centerY - (radius / 5) * (float)Math.Sin(value3Rad)
                );
                using (Pen needlePen = new Pen(Color.Gray, 3))
                {
                    g.DrawLine(needlePen, needleBase, needleEnd);
                }
            }

            // 7. 繪製主指針 (Value - 黑色, 速度/BC壓力)
            float valueAngle = StartAngle + (SweepAngle * (Value / MaxValue));
            float valueRad = (float)(valueAngle * Math.PI / 180.0);
            PointF needleEndMain = new PointF(
                centerX + (radius - 8) * (float)Math.Cos(valueRad),
                centerY + (radius - 8) * (float)Math.Sin(valueRad)
            );
            PointF needleBaseMain = new PointF(
                centerX - (radius / 5) * (float)Math.Cos(valueRad),
                centerY - (radius / 5) * (float)Math.Sin(valueRad)
            );
            using (Pen needlePenMain = new Pen(Color.Black, 3))
            {
                g.DrawLine(needlePenMain, needleBaseMain, needleEndMain);
            }

            // 7. 繪製中心軸
            g.FillEllipse(Brushes.Gray, centerX - 8, centerY - 8, 16, 16);
        }
    }
}