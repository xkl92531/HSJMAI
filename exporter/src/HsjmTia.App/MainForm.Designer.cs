using System;
using System.Windows.Forms;

namespace HsjmTia.App
{
    public partial class MainForm
    {
        /// <summary>WinForms 设计器约定的组件容器；本窗体不含 IComponent 子件，保留字段只为遵循约定。</summary>
        private System.ComponentModel.IContainer components = null;

        // 工程选择
        private TextBox txtProject;
        private Button btnPickProject;
        private Label lblVersion;

        // 导出目录
        private TextBox txtOutputRoot;
        private Button btnPickOutput;

        // 导出动作
        private Button btnExport;
        private ProgressBar progressBar;

        // PLC 列表
        private DataGridView dgvPlcs;

        // 缺口清单
        private Label lblGapSummary;
        private DataGridView dgvGaps;

        // 本地解析 / 打包结果
        private Label lblAnalyze;
        private Label lblPackages;

        // 上传区：后端连接
        private TextBox txtServer;
        private TextBox txtUser;
        private TextBox txtPassword;
        private Button btnConnectBackend;
        private Label lblBackendStatus;

        // 上传区：PLC ↔ 目标项目
        private DataGridView dgvUploadTargets;
        private ComboBox cmbTargetProject;
        private Button btnAssignTarget;
        private Label lblUploadHint;
        private CheckBox chkConfirm;
        private CheckBox chkFullProjectUpdate;
        private Button btnUpload;
        private Label lblUploadProgress;

        /// <summary>控件布局：整窗一个纵向 TableLayoutPanel，最下面的"上传"分组吃掉剩余空间。</summary>
        private void InitializeComponent()
        {
            Text = "HSJM TIA 导出工具";
            ClientSize = new System.Drawing.Size(1120, 860);
            MinimumSize = new System.Drawing.Size(900, 640);
            StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(8)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            root.Controls.Add(BuildProjectPanel(), 0, 0);
            root.Controls.Add(BuildExportActionPanel(), 0, 1);
            root.Controls.Add(BuildPlcGroup(), 0, 2);
            root.Controls.Add(BuildGapGroup(), 0, 3);
            root.Controls.Add(BuildAnalyzePanel(), 0, 4);
            root.Controls.Add(BuildUploadGroup(), 0, 5);
        }

        private Control BuildProjectPanel()
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };

            btnPickProject = new Button { Text = "选择工程...", AutoSize = true };
            btnPickProject.Click += btnPickProject_Click;

            txtProject = new TextBox { ReadOnly = true, Width = 420, Margin = new Padding(6, 6, 6, 6) };
            lblVersion = new Label
            {
                AutoSize = true,
                Margin = new Padding(12, 10, 3, 3),
                Text = "请先选择工程文件",
                Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold)
            };

            var pickPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            pickPanel.Controls.Add(btnPickProject);
            pickPanel.Controls.Add(txtProject);

            flow.Controls.Add(pickPanel);
            flow.Controls.Add(lblVersion);

            var outputRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            btnPickOutput = new Button { Text = "选择导出目录...", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            btnPickOutput.Click += btnPickOutput_Click;
            txtOutputRoot = new TextBox { ReadOnly = true, Width = 420, Margin = new Padding(6, 6, 6, 6) };
            outputRow.Controls.Add(btnPickOutput);
            outputRow.Controls.Add(txtOutputRoot);

            var container = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2 };
            container.Controls.Add(flow, 0, 0);
            container.Controls.Add(outputRow, 0, 1);
            return container;
        }

        private Control BuildExportActionPanel()
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };

            btnExport = new Button { Text = "开始导出", AutoSize = true, Margin = new Padding(3, 6, 12, 6) };
            btnExport.Click += btnExport_Click;

            progressBar = new ProgressBar { Width = 400, Height = 22, Margin = new Padding(3, 9, 3, 3) };

            flow.Controls.Add(btnExport);
            flow.Controls.Add(progressBar);
            return flow;
        }

        private Control BuildPlcGroup()
        {
            var group = new GroupBox { Text = "PLC 列表（勾选决定是否进入下方上传列表）", Dock = DockStyle.Fill };

            dgvPlcs = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            dgvPlcs.Columns.Add(new DataGridViewCheckBoxColumn
            {
                DataPropertyName = nameof(PlcRow.Selected),
                HeaderText = "导出/上传",
                Width = 80,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None
            });
            dgvPlcs.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(PlcRow.Name),
                HeaderText = "名称",
                ReadOnly = true
            });
            dgvPlcs.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(PlcRow.Ip),
                HeaderText = "IP",
                ReadOnly = true
            });
            dgvPlcs.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(PlcRow.OrderNumber),
                HeaderText = "订货号",
                ReadOnly = true
            });
            dgvPlcs.CurrentCellDirtyStateChanged += dgvPlcs_CurrentCellDirtyStateChanged;
            dgvPlcs.CellValueChanged += dgvPlcs_CellValueChanged;

            group.Controls.Add(dgvPlcs);
            return group;
        }

        private Control BuildGapGroup()
        {
            var group = new GroupBox { Text = "缺口清单（受保护块 / 名称被清洗 / 无法读取的值等）", Dock = DockStyle.Fill };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            lblGapSummary = new Label { AutoSize = true, Margin = new Padding(3) };

            dgvGaps = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            dgvGaps.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(HsjmTia.Core.Manifest.ExportGap.Kind),
                HeaderText = "类别"
            });
            dgvGaps.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(HsjmTia.Core.Manifest.ExportGap.Target),
                HeaderText = "对象"
            });
            dgvGaps.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(HsjmTia.Core.Manifest.ExportGap.Detail),
                HeaderText = "详情",
                FillWeight = 200
            });

            layout.Controls.Add(lblGapSummary, 0, 0);
            layout.Controls.Add(dgvGaps, 0, 1);
            group.Controls.Add(layout);
            return group;
        }

        private Control BuildAnalyzePanel()
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            lblAnalyze = new Label { AutoSize = true, Margin = new Padding(3, 6, 24, 3) };
            lblPackages = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            flow.Controls.Add(lblAnalyze);
            flow.Controls.Add(lblPackages);
            return flow;
        }

        private Control BuildUploadGroup()
        {
            var group = new GroupBox { Text = "上传：PLC ↔ 目标项目", Dock = DockStyle.Fill };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            layout.Controls.Add(BuildBackendConnectRow(), 0, 0);
            layout.Controls.Add(BuildUploadTargetsGrid(), 0, 1);
            layout.Controls.Add(BuildAssignRow(), 0, 2);
            layout.Controls.Add(BuildConfirmRow(), 0, 3);

            lblUploadProgress = new Label { AutoSize = true, Margin = new Padding(3) };
            layout.Controls.Add(lblUploadProgress, 0, 4);

            group.Controls.Add(layout);
            return group;
        }

        private Control BuildBackendConnectRow()
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };

            flow.Controls.Add(new Label { Text = "后端地址：", AutoSize = true, Margin = new Padding(3, 6, 0, 3) });
            txtServer = new TextBox { Width = 220, Margin = new Padding(3, 3, 12, 3) };
            flow.Controls.Add(txtServer);

            flow.Controls.Add(new Label { Text = "账号：", AutoSize = true, Margin = new Padding(3, 6, 0, 3) });
            txtUser = new TextBox { Width = 120, Margin = new Padding(3, 3, 12, 3) };
            flow.Controls.Add(txtUser);

            flow.Controls.Add(new Label { Text = "密码：", AutoSize = true, Margin = new Padding(3, 6, 0, 3) });
            txtPassword = new TextBox { Width = 120, PasswordChar = '*', Margin = new Padding(3, 3, 12, 3) };
            flow.Controls.Add(txtPassword);

            btnConnectBackend = new Button { Text = "连接后端", AutoSize = true, Margin = new Padding(3) };
            btnConnectBackend.Click += btnConnectBackend_Click;
            flow.Controls.Add(btnConnectBackend);

            lblBackendStatus = new Label { AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
            flow.Controls.Add(lblBackendStatus);

            return flow;
        }

        private Control BuildUploadTargetsGrid()
        {
            dgvUploadTargets = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            dgvUploadTargets.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(UploadRow.PlcDisplay),
                HeaderText = "PLC（名称 / IP / 订货号）"
            });
            dgvUploadTargets.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(UploadRow.ProjectDisplay),
                HeaderText = "目标项目（名称 / PlcIp / PlcCode）"
            });
            dgvUploadTargets.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(UploadRow.VerdictDisplay),
                HeaderText = "状态",
                FillWeight = 80
            });
            dgvUploadTargets.SelectionChanged += dgvUploadTargets_SelectionChanged;
            dgvUploadTargets.CellFormatting += dgvUploadTargets_CellFormatting;

            return dgvUploadTargets;
        }

        private Control BuildAssignRow()
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };

            flow.Controls.Add(new Label { Text = "目标项目：", AutoSize = true, Margin = new Padding(3, 6, 0, 3) });
            cmbTargetProject = new ComboBox
            {
                Width = 320,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(3, 3, 12, 3)
            };
            flow.Controls.Add(cmbTargetProject);

            btnAssignTarget = new Button { Text = "分配到选中行", AutoSize = true, Margin = new Padding(3) };
            btnAssignTarget.Click += btnAssignTarget_Click;
            flow.Controls.Add(btnAssignTarget);

            return flow;
        }

        private Control BuildConfirmRow()
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };

            lblUploadHint = new Label
            {
                AutoSize = true,
                Margin = new Padding(3, 6, 12, 3),
                MaximumSize = new System.Drawing.Size(600, 0)
            };
            flow.Controls.Add(lblUploadHint);

            chkConfirm = new CheckBox { Text = "我确认", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
            chkConfirm.CheckedChanged += chkConfirm_CheckedChanged;
            flow.Controls.Add(chkConfirm);

            chkFullProjectUpdate = new CheckBox
            {
                Text = "整工程覆盖更新（--full）",
                AutoSize = true,
                Margin = new Padding(3, 6, 12, 3)
            };
            chkFullProjectUpdate.CheckedChanged += chkFullProjectUpdate_CheckedChanged;
            flow.Controls.Add(chkFullProjectUpdate);

            btnUpload = new Button { Text = "上传选中 PLC", AutoSize = true, Margin = new Padding(3) };
            btnUpload.Click += btnUpload_Click;
            flow.Controls.Add(btnUpload);

            return flow;
        }
    }
}
