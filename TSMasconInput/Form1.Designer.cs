namespace TSMasconInput
{
    partial class Form1
    {
        /// <summary>
        /// 必要なデザイナー変数です。
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// 使用中のリソースをすべてクリーンアップします。
        /// </summary>
        /// <param name="disposing">マネージド リソースを破棄する場合は true を指定し、その他の場合は false を指定します。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows フォーム デザイナーで生成されたコード

        /// <summary>
        /// デザイナー サポートに必要なメソッドです。このメソッドの内容を
        /// コード エディターで変更しないでください。
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            this.label1 = new System.Windows.Forms.Label();
            this.labelATS = new System.Windows.Forms.Label();
            this.labelPnl = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.btn_tasc_toggle = new System.Windows.Forms.Button();
            this.labelNotch = new System.Windows.Forms.Label();
            this.labelTascStatus = new System.Windows.Forms.Label();
            this.btn_atc_toggle = new System.Windows.Forms.Button();
            this.label = new System.Windows.Forms.Label();
            this.btn_ato_toggle = new System.Windows.Forms.Button();
            this.btn_depart = new System.Windows.Forms.Button();
            this.pressureGauge = new TSMasconInput.AnalogGauge();
            this.speedGauge = new TSMasconInput.AnalogGauge();
            this.SuspendLayout();
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(18, 331);
            this.label1.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(50, 18);
            this.label1.TabIndex = 2;
            this.label1.Text = "label1";
            // 
            // labelATS
            // 
            this.labelATS.AutoSize = true;
            this.labelATS.Location = new System.Drawing.Point(426, 331);
            this.labelATS.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelATS.Name = "labelATS";
            this.labelATS.Size = new System.Drawing.Size(73, 18);
            this.labelATS.TabIndex = 2;
            this.labelATS.Text = "labelATS";
            // 
            // labelPnl
            // 
            this.labelPnl.AutoSize = true;
            this.labelPnl.Location = new System.Drawing.Point(212, 331);
            this.labelPnl.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelPnl.Name = "labelPnl";
            this.labelPnl.Size = new System.Drawing.Size(67, 18);
            this.labelPnl.TabIndex = 2;
            this.labelPnl.Text = "labelPn1";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(543, 331);
            this.label2.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(50, 18);
            this.label2.TabIndex = 2;
            this.label2.Text = "label2";
            // 
            // btn_tasc_toggle
            // 
            this.btn_tasc_toggle.Location = new System.Drawing.Point(319, 491);
            this.btn_tasc_toggle.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.btn_tasc_toggle.Name = "btn_tasc_toggle";
            this.btn_tasc_toggle.Size = new System.Drawing.Size(118, 35);
            this.btn_tasc_toggle.TabIndex = 4;
            this.btn_tasc_toggle.Text = "TASC: OFF";
            this.btn_tasc_toggle.UseVisualStyleBackColor = true;
            this.btn_tasc_toggle.Click += new System.EventHandler(this.btn_tasc_toggle_Click);
            // 
            // labelNotch
            // 
            this.labelNotch.AutoSize = true;
            this.labelNotch.BackColor = System.Drawing.Color.Transparent;
            this.labelNotch.Location = new System.Drawing.Point(15, 26);
            this.labelNotch.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelNotch.Name = "labelNotch";
            this.labelNotch.Size = new System.Drawing.Size(98, 54);
            this.labelNotch.TabIndex = 5;
            this.labelNotch.Text = "ATO: N\nNotch:抑速\nHandle:抑速";
            // 
            // labelTascStatus
            // 
            this.labelTascStatus.AutoSize = true;
            this.labelTascStatus.Location = new System.Drawing.Point(332, 17);
            this.labelTascStatus.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelTascStatus.Name = "labelTascStatus";
            this.labelTascStatus.Size = new System.Drawing.Size(90, 18);
            this.labelTascStatus.TabIndex = 6;
            this.labelTascStatus.Text = "TASC: OFF";
            // 
            // btn_atc_toggle
            // 
            this.btn_atc_toggle.Location = new System.Drawing.Point(437, 491);
            this.btn_atc_toggle.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.btn_atc_toggle.Name = "btn_atc_toggle";
            this.btn_atc_toggle.Size = new System.Drawing.Size(118, 35);
            this.btn_atc_toggle.TabIndex = 9;
            this.btn_atc_toggle.Text = "ATC: OFF";
            this.btn_atc_toggle.UseVisualStyleBackColor = true;
            this.btn_atc_toggle.Click += new System.EventHandler(this.btn_atc_toggle_Click);
            // 
            // label
            // 
            this.label.AutoSize = true;
            this.label.Font = new System.Drawing.Font("Microsoft JhengHei UI", 12F);
            this.label.Location = new System.Drawing.Point(518, 103);
            this.label.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label.Name = "label";
            this.label.Size = new System.Drawing.Size(163, 30);
            this.label.TabIndex = 2;
            this.label.Text = "前方予告: 120";
            // 
            // btn_ato_toggle
            // 
            this.btn_ato_toggle.Location = new System.Drawing.Point(555, 491);
            this.btn_ato_toggle.Name = "btn_ato_toggle";
            this.btn_ato_toggle.Size = new System.Drawing.Size(118, 35);
            this.btn_ato_toggle.TabIndex = 11;
            this.btn_ato_toggle.Text = "ATO: OFF";
            this.btn_ato_toggle.UseVisualStyleBackColor = true;
            this.btn_ato_toggle.Click += new System.EventHandler(this.Btn_ato_toggle_Click);
            // 
            // btn_depart
            // 
            this.btn_depart.BackColor = System.Drawing.Color.LightGray;
            this.btn_depart.Enabled = false;
            this.btn_depart.Font = new System.Drawing.Font("Arial", 9F, System.Drawing.FontStyle.Bold);
            this.btn_depart.Location = new System.Drawing.Point(673, 491);
            this.btn_depart.Name = "btn_depart";
            this.btn_depart.Size = new System.Drawing.Size(118, 35);
            this.btn_depart.TabIndex = 12;
            this.btn_depart.Text = "DEPART";
            this.btn_depart.UseVisualStyleBackColor = false;
            this.btn_depart.Click += new System.EventHandler(this.Btn_depart_Click);
            // 
            // pressureGauge
            // 
            this.pressureGauge.GaugeTitle = "壓力 (黑:BC/紅:MR)";
            this.pressureGauge.Location = new System.Drawing.Point(-11, -18);
            this.pressureGauge.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.pressureGauge.MaxValue = 1000F;
            this.pressureGauge.Name = "pressureGauge";
            this.pressureGauge.Size = new System.Drawing.Size(405, 432);
            this.pressureGauge.TabIndex = 8;
            this.pressureGauge.TargetValue = -1F;
            this.pressureGauge.TargetValue2 = -1F;
            this.pressureGauge.TargetValue2Color = System.Drawing.Color.LimeGreen;
            this.pressureGauge.TargetValue3 = -1F;
            this.pressureGauge.TargetValue3Color = System.Drawing.Color.Red;
            this.pressureGauge.TargetValueColor = System.Drawing.Color.Blue;
            this.pressureGauge.Unit = "kPa";
            this.pressureGauge.Value = 0F;
            this.pressureGauge.Value2 = 0F;
            this.pressureGauge.Value3 = 0F;
            // 
            // speedGauge
            // 
            this.speedGauge.GaugeTitle = "速度";
            this.speedGauge.Location = new System.Drawing.Point(395, -18);
            this.speedGauge.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.speedGauge.MaxValue = 120F;
            this.speedGauge.Name = "speedGauge";
            this.speedGauge.Size = new System.Drawing.Size(405, 432);
            this.speedGauge.TabIndex = 7;
            this.speedGauge.TargetValue = -1F;
            this.speedGauge.TargetValue2 = -1F;
            this.speedGauge.TargetValue2Color = System.Drawing.Color.Green;
            this.speedGauge.TargetValue3 = -1F;
            this.speedGauge.TargetValue3Color = System.Drawing.Color.Red;
            this.speedGauge.TargetValueColor = System.Drawing.Color.Blue;
            this.speedGauge.Unit = "km/h";
            this.speedGauge.Value = 0F;
            this.speedGauge.Value2 = -1F;
            this.speedGauge.Value3 = -1F;
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 18F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(796, 563);
            this.Controls.Add(this.btn_depart);
            this.Controls.Add(this.btn_ato_toggle);
            this.Controls.Add(this.btn_atc_toggle);
            this.Controls.Add(this.labelTascStatus);
            this.Controls.Add(this.labelNotch);
            this.Controls.Add(this.btn_tasc_toggle);
            this.Controls.Add(this.labelPnl);
            this.Controls.Add(this.label);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.labelATS);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.pressureGauge);
            this.pressureGauge.Controls.Add(this.labelNotch);
            this.Controls.Add(this.speedGauge);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.MaximizeBox = false;
            this.Name = "Form1";
            this.Text = "TrainCrewマスコン入力";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label label;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label labelATS;
        private System.Windows.Forms.Label labelPnl;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Button btn_tasc_toggle;
        private System.Windows.Forms.Label labelNotch;
        private System.Windows.Forms.Label labelTascStatus;
        private AnalogGauge speedGauge;
        private AnalogGauge pressureGauge;
        private System.Windows.Forms.Button btn_atc_toggle;
        private System.Windows.Forms.ComboBox comboBoxMotor;
        private System.Windows.Forms.Button btn_open;
        private System.Windows.Forms.Button btn_close;
        private System.Windows.Forms.ComboBox comboBoxDisplay;
        private System.Windows.Forms.Button btn_monitor_toggle;
        private System.Windows.Forms.Button btn_ato_toggle;
        private System.Windows.Forms.Button btn_depart;
    }
}