namespace 博图项目读取
{
    partial class Form1
    {
        /// <summary>
        /// 必需的设计器变量。
        /// </summary>
        private System.ComponentModel.IContainer components = null;

       

        #region Windows 窗体设计器生成的代码

        /// <summary>
        /// 设计器支持所需的方法 - 不要修改
        /// 使用代码编辑器修改此方法的内容。
        /// </summary>
        private void InitializeComponent()
        {
            this.btnConnectTiaPortal = new System.Windows.Forms.Button();
            this.btnSelectProject = new System.Windows.Forms.Button();
            this.btnParseProject = new System.Windows.Forms.Button();
            this.btnExportJson = new System.Windows.Forms.Button();
            this.btnExportHtml = new System.Windows.Forms.Button();
            this.btnExportCsv = new System.Windows.Forms.Button();
            this.btnExportBusinessJson = new System.Windows.Forms.Button();
            this.txtProjectPath = new System.Windows.Forms.TextBox();
            this.txtLog = new System.Windows.Forms.TextBox();
            this.lblProjectPath = new System.Windows.Forms.Label();
            this.lblLog = new System.Windows.Forms.Label();
            this.progressBar = new System.Windows.Forms.ProgressBar();
            this.lblStatus = new System.Windows.Forms.Label();
            this.tabControl = new System.Windows.Forms.TabControl();
            this.tabProject = new System.Windows.Forms.TabPage();
            this.dgvDevices = new System.Windows.Forms.DataGridView();
            this.colDeviceName = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colDeviceType = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colOrderNumber = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colBlockCount = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.tabSymbols = new System.Windows.Forms.TabPage();
            this.dgvSymbols = new System.Windows.Forms.DataGridView();
            this.colSymbolName = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colAddress = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colDataType = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colComment = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.tabBlocks = new System.Windows.Forms.TabPage();
            this.dgvBlocks = new System.Windows.Forms.DataGridView();
            this.colBlockName = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colBlockType = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colBlockNumber = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colLanguage = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colBlockComment = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.lblProjectInfo = new System.Windows.Forms.Label();
            this.tabControl.SuspendLayout();
            this.tabProject.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvDevices)).BeginInit();
            this.tabSymbols.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvSymbols)).BeginInit();
            this.tabBlocks.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvBlocks)).BeginInit();
            this.SuspendLayout();
            // 
            // btnConnectTiaPortal
            // 
            this.btnConnectTiaPortal.Location = new System.Drawing.Point(12, 12);
            this.btnConnectTiaPortal.Name = "btnConnectTiaPortal";
            this.btnConnectTiaPortal.Size = new System.Drawing.Size(120, 30);
            this.btnConnectTiaPortal.TabIndex = 0;
            this.btnConnectTiaPortal.Text = "连接博图软件";
            this.btnConnectTiaPortal.UseVisualStyleBackColor = true;
            this.btnConnectTiaPortal.Click += new System.EventHandler(this.btnConnectTiaPortal_Click);
            // 
            // btnSelectProject
            // 
            this.btnSelectProject.Location = new System.Drawing.Point(138, 12);
            this.btnSelectProject.Name = "btnSelectProject";
            this.btnSelectProject.Size = new System.Drawing.Size(140, 30);
            this.btnSelectProject.TabIndex = 1;
            this.btnSelectProject.Text = "选择并解析项目";
            this.btnSelectProject.UseVisualStyleBackColor = true;
            this.btnSelectProject.Click += new System.EventHandler(this.btnSelectProject_Click);
            // 
            // btnParseProject
            // 
            this.btnParseProject.Location = new System.Drawing.Point(244, 12);
            this.btnParseProject.Name = "btnParseProject";
            this.btnParseProject.Size = new System.Drawing.Size(100, 30);
            this.btnParseProject.TabIndex = 2;
            this.btnParseProject.Text = "解析项目";
            this.btnParseProject.Visible = false;
            this.btnParseProject.UseVisualStyleBackColor = true;
            this.btnParseProject.Click += new System.EventHandler(this.btnParseProject_Click);
            // 
            // btnExportJson
            // 
            this.btnExportJson.Location = new System.Drawing.Point(350, 12);
            this.btnExportJson.Name = "btnExportJson";
            this.btnExportJson.Size = new System.Drawing.Size(100, 30);
            this.btnExportJson.TabIndex = 3;
            this.btnExportJson.Text = "导出JSON";
            this.btnExportJson.Visible = false;
            this.btnExportJson.UseVisualStyleBackColor = true;
            this.btnExportJson.Click += new System.EventHandler(this.btnExportJson_Click);
            // 
            // btnExportHtml
            // 
            this.btnExportHtml.Location = new System.Drawing.Point(456, 12);
            this.btnExportHtml.Name = "btnExportHtml";
            this.btnExportHtml.Size = new System.Drawing.Size(100, 30);
            this.btnExportHtml.TabIndex = 4;
            this.btnExportHtml.Text = "导出HTML";
            this.btnExportHtml.Visible = false;
            this.btnExportHtml.UseVisualStyleBackColor = true;
            this.btnExportHtml.Click += new System.EventHandler(this.btnExportHtml_Click);
            // 
            // btnExportCsv
            // 
            this.btnExportCsv.Enabled = false;
            this.btnExportCsv.Location = new System.Drawing.Point(650, 70);
            this.btnExportCsv.Name = "btnExportCsv";
            this.btnExportCsv.Size = new System.Drawing.Size(120, 30);
            this.btnExportCsv.TabIndex = 8;
            this.btnExportCsv.Text = "导出CSV符号表";
            this.btnExportCsv.Visible = false;
            this.btnExportCsv.UseVisualStyleBackColor = true;
            this.btnExportCsv.Click += new System.EventHandler(this.btnExportCsv_Click);
            // 
            // btnExportBusinessJson
            // 
            this.btnExportBusinessJson.Enabled = false;
            this.btnExportBusinessJson.Location = new System.Drawing.Point(780, 70);
            this.btnExportBusinessJson.Name = "btnExportBusinessJson";
            this.btnExportBusinessJson.Size = new System.Drawing.Size(140, 30);
            this.btnExportBusinessJson.TabIndex = 9;
            this.btnExportBusinessJson.Text = "导出业务系统JSON";
            this.btnExportBusinessJson.UseVisualStyleBackColor = true;
            this.btnExportBusinessJson.Click += new System.EventHandler(this.btnExportBusinessJson_Click);
            // 
            // txtProjectPath
            // 
            this.txtProjectPath.Location = new System.Drawing.Point(80, 50);
            this.txtProjectPath.Name = "txtProjectPath";
            this.txtProjectPath.Size = new System.Drawing.Size(600, 21);
            this.txtProjectPath.TabIndex = 6;
            // 
            // txtLog
            // 
            this.txtLog.Location = new System.Drawing.Point(12, 400);
            this.txtLog.Multiline = true;
            this.txtLog.Name = "txtLog";
            this.txtLog.ReadOnly = true;
            this.txtLog.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtLog.Size = new System.Drawing.Size(760, 120);
            this.txtLog.TabIndex = 7;
            // 
            // lblProjectPath
            // 
            this.lblProjectPath.AutoSize = true;
            this.lblProjectPath.Location = new System.Drawing.Point(12, 53);
            this.lblProjectPath.Name = "lblProjectPath";
            this.lblProjectPath.Size = new System.Drawing.Size(65, 12);
            this.lblProjectPath.TabIndex = 8;
            this.lblProjectPath.Text = "项目路径：";
            // 
            // lblLog
            // 
            this.lblLog.AutoSize = true;
            this.lblLog.Location = new System.Drawing.Point(12, 380);
            this.lblLog.Name = "lblLog";
            this.lblLog.Size = new System.Drawing.Size(41, 12);
            this.lblLog.TabIndex = 9;
            this.lblLog.Text = "日志：";
            // 
            // progressBar
            // 
            this.progressBar.Location = new System.Drawing.Point(12, 350);
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new System.Drawing.Size(760, 20);
            this.progressBar.TabIndex = 10;
            // 
            // lblStatus
            // 
            this.lblStatus.AutoSize = true;
            this.lblStatus.Location = new System.Drawing.Point(12, 330);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(65, 12);
            this.lblStatus.TabIndex = 11;
            this.lblStatus.Text = "状态：就绪";
            // 
            // tabControl
            // 
            this.tabControl.Controls.Add(this.tabProject);
            this.tabControl.Controls.Add(this.tabSymbols);
            this.tabControl.Controls.Add(this.tabBlocks);
            this.tabControl.Location = new System.Drawing.Point(12, 110);
            this.tabControl.Name = "tabControl";
            this.tabControl.SelectedIndex = 0;
            this.tabControl.Size = new System.Drawing.Size(760, 200);
            this.tabControl.TabIndex = 12;
            // 
            // tabProject
            // 
            this.tabProject.Controls.Add(this.dgvDevices);
            this.tabProject.Location = new System.Drawing.Point(4, 22);
            this.tabProject.Name = "tabProject";
            this.tabProject.Padding = new System.Windows.Forms.Padding(3);
            this.tabProject.Size = new System.Drawing.Size(752, 174);
            this.tabProject.TabIndex = 0;
            this.tabProject.Text = "设备信息";
            this.tabProject.UseVisualStyleBackColor = true;
            // 
            // dgvDevices
            // 
            this.dgvDevices.AllowUserToAddRows = false;
            this.dgvDevices.AllowUserToDeleteRows = false;
            this.dgvDevices.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvDevices.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colDeviceName,
            this.colDeviceType,
            this.colOrderNumber,
            this.colBlockCount});
            this.dgvDevices.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvDevices.Location = new System.Drawing.Point(3, 3);
            this.dgvDevices.Name = "dgvDevices";
            this.dgvDevices.ReadOnly = true;
            this.dgvDevices.RowTemplate.Height = 23;
            this.dgvDevices.Size = new System.Drawing.Size(746, 168);
            this.dgvDevices.TabIndex = 0;
            // 
            // colDeviceName
            // 
            this.colDeviceName.HeaderText = "设备名称";
            this.colDeviceName.Name = "colDeviceName";
            this.colDeviceName.ReadOnly = true;
            this.colDeviceName.Width = 150;
            // 
            // colDeviceType
            // 
            this.colDeviceType.HeaderText = "设备类型";
            this.colDeviceType.Name = "colDeviceType";
            this.colDeviceType.ReadOnly = true;
            this.colDeviceType.Width = 200;
            // 
            // colOrderNumber
            // 
            this.colOrderNumber.HeaderText = "订货号";
            this.colOrderNumber.Name = "colOrderNumber";
            this.colOrderNumber.ReadOnly = true;
            this.colOrderNumber.Width = 200;
            // 
            // colBlockCount
            // 
            this.colBlockCount.HeaderText = "程序块数量";
            this.colBlockCount.Name = "colBlockCount";
            this.colBlockCount.ReadOnly = true;
            this.colBlockCount.Width = 100;
            // 
            // tabSymbols
            // 
            this.tabSymbols.Controls.Add(this.dgvSymbols);
            this.tabSymbols.Location = new System.Drawing.Point(4, 22);
            this.tabSymbols.Name = "tabSymbols";
            this.tabSymbols.Padding = new System.Windows.Forms.Padding(3);
            this.tabSymbols.Size = new System.Drawing.Size(752, 174);
            this.tabSymbols.TabIndex = 1;
            this.tabSymbols.Text = "符号表";
            this.tabSymbols.UseVisualStyleBackColor = true;
            // 
            // dgvSymbols
            // 
            this.dgvSymbols.AllowUserToAddRows = false;
            this.dgvSymbols.AllowUserToDeleteRows = false;
            this.dgvSymbols.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvSymbols.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colSymbolName,
            this.colAddress,
            this.colDataType,
            this.colComment});
            this.dgvSymbols.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvSymbols.Location = new System.Drawing.Point(3, 3);
            this.dgvSymbols.Name = "dgvSymbols";
            this.dgvSymbols.ReadOnly = true;
            this.dgvSymbols.RowTemplate.Height = 23;
            this.dgvSymbols.Size = new System.Drawing.Size(746, 168);
            this.dgvSymbols.TabIndex = 0;
            // 
            // colSymbolName
            // 
            this.colSymbolName.HeaderText = "符号名称";
            this.colSymbolName.Name = "colSymbolName";
            this.colSymbolName.ReadOnly = true;
            this.colSymbolName.Width = 150;
            // 
            // colAddress
            // 
            this.colAddress.HeaderText = "地址";
            this.colAddress.Name = "colAddress";
            this.colAddress.ReadOnly = true;
            this.colAddress.Width = 100;
            // 
            // colDataType
            // 
            this.colDataType.HeaderText = "数据类型";
            this.colDataType.Name = "colDataType";
            this.colDataType.ReadOnly = true;
            this.colDataType.Width = 100;
            // 
            // colComment
            // 
            this.colComment.HeaderText = "注释";
            this.colComment.Name = "colComment";
            this.colComment.ReadOnly = true;
            this.colComment.Width = 300;
            // 
            // tabBlocks
            // 
            this.tabBlocks.Controls.Add(this.dgvBlocks);
            this.tabBlocks.Location = new System.Drawing.Point(4, 22);
            this.tabBlocks.Name = "tabBlocks";
            this.tabBlocks.Padding = new System.Windows.Forms.Padding(3);
            this.tabBlocks.Size = new System.Drawing.Size(752, 174);
            this.tabBlocks.TabIndex = 2;
            this.tabBlocks.Text = "程序块";
            this.tabBlocks.UseVisualStyleBackColor = true;
            // 
            // dgvBlocks
            // 
            this.dgvBlocks.AllowUserToAddRows = false;
            this.dgvBlocks.AllowUserToDeleteRows = false;
            this.dgvBlocks.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvBlocks.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colBlockName,
            this.colBlockType,
            this.colBlockNumber,
            this.colLanguage,
            this.colBlockComment});
            this.dgvBlocks.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvBlocks.Location = new System.Drawing.Point(3, 3);
            this.dgvBlocks.Name = "dgvBlocks";
            this.dgvBlocks.ReadOnly = true;
            this.dgvBlocks.RowTemplate.Height = 23;
            this.dgvBlocks.Size = new System.Drawing.Size(746, 168);
            this.dgvBlocks.TabIndex = 0;
            // 
            // colBlockName
            // 
            this.colBlockName.HeaderText = "程序块名称";
            this.colBlockName.Name = "colBlockName";
            this.colBlockName.ReadOnly = true;
            this.colBlockName.Width = 150;
            // 
            // colBlockType
            // 
            this.colBlockType.HeaderText = "类型";
            this.colBlockType.Name = "colBlockType";
            this.colBlockType.ReadOnly = true;
            this.colBlockType.Width = 80;
            // 
            // colBlockNumber
            // 
            this.colBlockNumber.HeaderText = "编号";
            this.colBlockNumber.Name = "colBlockNumber";
            this.colBlockNumber.ReadOnly = true;
            this.colBlockNumber.Width = 80;
            // 
            // colLanguage
            // 
            this.colLanguage.HeaderText = "语言";
            this.colLanguage.Name = "colLanguage";
            this.colLanguage.ReadOnly = true;
            this.colLanguage.Width = 80;
            // 
            // colBlockComment
            // 
            this.colBlockComment.HeaderText = "注释";
            this.colBlockComment.Name = "colBlockComment";
            this.colBlockComment.ReadOnly = true;
            this.colBlockComment.Width = 300;
            // 
            // lblProjectInfo
            // 
            this.lblProjectInfo.AutoSize = true;
            this.lblProjectInfo.Location = new System.Drawing.Point(12, 80);
            this.lblProjectInfo.Name = "lblProjectInfo";
            this.lblProjectInfo.Size = new System.Drawing.Size(65, 12);
            this.lblProjectInfo.TabIndex = 13;
            this.lblProjectInfo.Text = "项目信息：";
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(784, 532);
            this.Controls.AddRange(new System.Windows.Forms.Control[] {
            this.tabControl,
            this.btnConnectTiaPortal,
            this.btnSelectProject,
            this.btnParseProject,
            this.btnExportJson,
            this.btnExportHtml,
            this.btnExportCsv,
            this.btnExportBusinessJson,
            this.txtProjectPath,
            this.txtLog,
            this.lblProjectInfo,
            this.lblStatus,
            this.progressBar});
            this.Name = "Form1";
            this.Text = "博图项目读取工具";
            this.tabControl.ResumeLayout(false);
            this.tabProject.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvDevices)).EndInit();
            this.tabSymbols.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvSymbols)).EndInit();
            this.tabBlocks.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvBlocks)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnConnectTiaPortal;
        private System.Windows.Forms.Button btnSelectProject;
        private System.Windows.Forms.Button btnParseProject;
        private System.Windows.Forms.Button btnExportJson;
        private System.Windows.Forms.Button btnExportHtml;
        private System.Windows.Forms.Button btnExportCsv;
        private System.Windows.Forms.Button btnExportBusinessJson;
        private System.Windows.Forms.TextBox txtProjectPath;
        private System.Windows.Forms.TextBox txtLog;
        private System.Windows.Forms.Label lblProjectPath;
        private System.Windows.Forms.Label lblLog;
        private System.Windows.Forms.ProgressBar progressBar;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.TabControl tabControl;
        private System.Windows.Forms.TabPage tabProject;
        private System.Windows.Forms.DataGridView dgvDevices;
        private System.Windows.Forms.TabPage tabSymbols;
        private System.Windows.Forms.DataGridView dgvSymbols;
        private System.Windows.Forms.TabPage tabBlocks;
        private System.Windows.Forms.DataGridView dgvBlocks;
        private System.Windows.Forms.Label lblProjectInfo;
        private System.Windows.Forms.DataGridViewTextBoxColumn colDeviceName;
        private System.Windows.Forms.DataGridViewTextBoxColumn colDeviceType;
        private System.Windows.Forms.DataGridViewTextBoxColumn colOrderNumber;
        private System.Windows.Forms.DataGridViewTextBoxColumn colBlockCount;
        private System.Windows.Forms.DataGridViewTextBoxColumn colSymbolName;
        private System.Windows.Forms.DataGridViewTextBoxColumn colAddress;
        private System.Windows.Forms.DataGridViewTextBoxColumn colDataType;
        private System.Windows.Forms.DataGridViewTextBoxColumn colComment;
        private System.Windows.Forms.DataGridViewTextBoxColumn colBlockName;
        private System.Windows.Forms.DataGridViewTextBoxColumn colBlockType;
        private System.Windows.Forms.DataGridViewTextBoxColumn colBlockNumber;
        private System.Windows.Forms.DataGridViewTextBoxColumn colLanguage;
        private System.Windows.Forms.DataGridViewTextBoxColumn colBlockComment;
    }
}

