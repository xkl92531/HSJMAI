using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using HsjmTia.Core.Discovery;
using HsjmTia.Core.Manifest;
using HsjmTia.Core.Model;
using HsjmTia.Core.Upload;
using HsjmTia.Openness;

namespace HsjmTia.App
{
    /// <summary>
    /// 现场工程师用的导出/上传界面。三件事必须让人一眼看到（旧工具全部藏起来了）：
    /// 导出的缺口（红色区域，直接绑 manifest.Gaps）、这个工程需要哪个 TIA 版本 vs 本机装了什么
    /// （选工程后立即判定，不等到导出失败）、PLC 与目标后端项目的 IP 不一致
    /// （整行标红，必须显式勾选"我确认"才能解锁上传）。
    ///
    /// 关键约束：所有 Openness/TIA 调用都在后台线程（Task.Run）执行，绝不放在 UI 线程——
    /// 旧工具把 SDK 调用放在 UI 线程上，导出几千个块时界面会冻结几分钟。
    /// </summary>
    public partial class MainForm : Form
    {
        private OpennessInstallation _installation;
        private string _installedApiVersion;
        private ExportRunResult _lastResult;

        private HttpClient _backendHttp;
        private BackendClient _backendClient;
        private IReadOnlyList<BackendProject> _backendProjects = new List<BackendProject>();

        private readonly BindingSource _plcs = new BindingSource();
        private readonly BindingSource _gaps = new BindingSource();
        private readonly BindingSource _uploadRows = new BindingSource();

        public MainForm()
        {
            InitializeComponent();

            dgvPlcs.AutoGenerateColumns = false;
            dgvPlcs.DataSource = _plcs;

            dgvGaps.AutoGenerateColumns = false;
            dgvGaps.DataSource = _gaps;
            dgvGaps.DefaultCellStyle.ForeColor = Color.Firebrick;

            dgvUploadTargets.AutoGenerateColumns = false;
            dgvUploadTargets.DataSource = _uploadRows;

            btnExport.Enabled = false;
            btnUpload.Enabled = false;
            chkConfirm.Visible = false;

            txtOutputRoot.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xml");
        }

        // ---------------------------------------------------------------
        // 步骤 1：选工程 → 立即判定 TIA 版本
        // ---------------------------------------------------------------

        /// <summary>选工程后立即给出版本判定，不等到导出才失败。</summary>
        private void btnPickProject_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "选择 TIA Portal 工程文件",
                Filter = "TIA Portal 工程 (*.ap19;*.ap18;*.ap17;*.ap16;*.ap15)|*.ap19;*.ap18;*.ap17;*.ap16;*.ap15"
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                txtProject.Text = dialog.FileName;
                _installation = OpennessProbe.Select(
                    OpennessProbe.FindInstallations(new WindowsRegistryReader()),
                    dialog.FileName, out var error);

                if (_installation == null)
                {
                    lblVersion.ForeColor = Color.Firebrick;
                    lblVersion.Text = error;
                    btnExport.Enabled = false;
                    return;
                }

                // OpennessAssemblyResolver.Install 全进程只生效一次（.NET Framework 也没法在同一个
                // AppDomain 里加载两个不同版本的同名程序集）。这个窗体是长生命周期的，用户完全可能
                // 在同一次运行里先后选中两个需要不同 TIA 版本的工程——如果第二次选择悄悄复用第一次
                // 装好的钩子，界面会显示"将使用 V19"但实际解析出来的仍是 V16 的程序集，是个比"报错"
                // 更危险的沉默错误。这里显式拒绝并提示重启，而不是让它悄悄错下去。
                if (_installedApiVersion != null &&
                    !string.Equals(_installedApiVersion, _installation.ApiVersion, StringComparison.OrdinalIgnoreCase))
                {
                    lblVersion.ForeColor = Color.Firebrick;
                    lblVersion.Text =
                        $"本次运行已经装载过 TIA {_installedApiVersion}，同一进程内无法切换到 {_installation.ApiVersion}。" +
                        "请重启本工具后再打开这个工程。";
                    _installation = null;
                    btnExport.Enabled = false;
                    return;
                }

                // 必须在任何触碰 Siemens.Engineering 类型的方法被 JIT 之前完成；
                // 此调用本身不引用任何 Siemens 类型（只是挂 AssemblyResolve 钩子），
                // 真正触碰 SDK 的调用全部在 AppExportRunner.Run 里，且只有点了"导出"才会走到。
                OpennessAssemblyResolver.Install(_installation);
                _installedApiVersion = _installation.ApiVersion;

                lblVersion.ForeColor = Color.ForestGreen;
                lblVersion.Text = $"将使用本机 TIA {_installation.ApiVersion}";
                btnExport.Enabled = true;
            }
        }

        private void btnPickOutput_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择导出目录" })
            {
                if (!string.IsNullOrWhiteSpace(txtOutputRoot.Text) && Directory.Exists(txtOutputRoot.Text))
                {
                    dialog.SelectedPath = txtOutputRoot.Text;
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtOutputRoot.Text = dialog.SelectedPath;
                }
            }
        }

        // ---------------------------------------------------------------
        // 步骤 2~4：导出（后台线程）→ PLC 列表 / 缺口清单 / 本地解析结果
        // ---------------------------------------------------------------

        /// <summary>
        /// 所有 Openness 调用都在后台线程。旧工具把它放在 UI 线程上，
        /// 导出几千个块时界面完全冻结；这里用 Task.Run 让 UI 线程只负责展示结果。
        /// </summary>
        private async void btnExport_Click(object sender, EventArgs e)
        {
            if (_installation == null)
            {
                return;
            }

            var projectPath = txtProject.Text;
            var outputRoot = txtOutputRoot.Text;
            var installation = _installation;

            SetBusy(true);
            lblGapSummary.Text = string.Empty;
            lblAnalyze.Text = string.Empty;
            lblPackages.Text = string.Empty;
            try
            {
                var result = await Task.Run(() => RunExport(projectPath, installation, outputRoot));
                _lastResult = result;

                _plcs.DataSource = result.Plcs
                    .Select(p => new PlcRow { Plc = p })
                    .ToList();

                _gaps.DataSource = result.Gaps.ToList();
                lblGapSummary.Text = result.Gaps.Count == 0
                    ? "本次导出无缺口"
                    : $"本次导出有 {result.Gaps.Count} 条缺口，请逐条确认";
                lblGapSummary.ForeColor = result.Gaps.Count == 0 ? Color.ForestGreen : Color.Firebrick;

                lblAnalyze.Text = $"本地解析 {result.AnalyzeSucceeded}/{result.AnalyzeTotal} 成功";
                lblPackages.Text = $"成功打包 {result.Packages.Count}/{result.Plcs.Count} 个 PLC";
                lblPackages.ForeColor = result.Packages.Count == result.Plcs.Count
                    ? Color.ForestGreen
                    : Color.Firebrick;

                RebuildUploadRows();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ExportRunResult RunExport(string projectPath, OpennessInstallation installation, string outputRoot)
            => AppExportRunner.Run(projectPath, installation, outputRoot);

        private void SetBusy(bool busy)
        {
            btnPickProject.Enabled = !busy;
            btnPickOutput.Enabled = !busy;
            btnExport.Enabled = !busy && _installation != null;
            progressBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        }

        /// <summary>PLC 列表里的勾选框改变时，立即重建上传行——只给勾选的 PLC 生成上传目标行。</summary>
        private void dgvPlcs_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (dgvPlcs.IsCurrentCellDirty)
            {
                // 复选框点击后立即提交，CellValueChanged 才会马上触发，而不是等失焦。
                dgvPlcs.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void dgvPlcs_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            RebuildUploadRows();
        }

        // ---------------------------------------------------------------
        // 步骤 5：上传区 —— 逐 PLC 一行，PLC↔目标项目 配对，IP 不一致必须显式确认
        // ---------------------------------------------------------------

        /// <summary>只给勾选的、且导出打包成功（通过校验、可上传）的 PLC 生成上传行。</summary>
        private void RebuildUploadRows()
        {
            var selected = new HashSet<PlcIdentity>(_plcs.List.Cast<PlcRow>().Where(r => r.Selected).Select(r => r.Plc));

            var existingByPlc = _uploadRows.List.Cast<UploadRow>().ToDictionary(r => r.Plc);

            var rows = (_lastResult?.Packages ?? new List<ExportPackage>())
                .Where(p => selected.Contains(p.Plc))
                .Select(p =>
                {
                    // 已经分配过目标项目的行保留其分配（Project），避免用户重新勾选时前功尽弃。
                    // 但 Confirmed 必须清空：Gaps 可能已经变化——上次导出没问题的块这次可能
                    // 知产保护/导出失败，产生新的不安全缺口；而 IP 不一致这类旧风险不会消失。
                    // 旧的"已确认"是对上一份 Gaps 做出的判断，不能被当成对新 Gaps 的确认，
                    // 否则新出现的不安全缺口会被一个过时的勾选悄悄放行（C2 的确认机制形同虚设）。
                    if (existingByPlc.TryGetValue(p.Plc, out var existing))
                    {
                        existing.ZipPath = p.ZipPath;
                        existing.Gaps = p.Gaps;
                        existing.Confirmed = false;
                        return existing;
                    }
                    return new UploadRow { Plc = p.Plc, ZipPath = p.ZipPath, Gaps = p.Gaps };
                })
                .ToList();

            _uploadRows.DataSource = rows;
            dgvUploadTargets_SelectionChanged(dgvUploadTargets, EventArgs.Empty);
        }

        /// <summary>
        /// 重新计算一行的风险判定：IP 不一致（UploadTargetValidator，老逻辑不变）与
        /// C2 的"整工程覆盖更新会不会误删数据"（只在勾了「整工程覆盖更新」时才生效）。
        /// 两者共用同一套"必须显式确认"UI（chkConfirm），不新造第二套确认机制。
        /// 只计算，不动 row.Confirmed —— 是否需要清空确认状态由调用方决定
        /// （见 chkFullProjectUpdate_CheckedChanged 的说明）。
        /// </summary>
        private void ApplyRowVerdict(UploadRow row)
        {
            if (row.Project == null)
            {
                row.RequiresConfirmation = false;
                row.FullUpdateUnsafeGaps = new List<ExportGap>();
                row.Message = null;
                return;
            }

            var ipCheck = UploadTargetValidator.Validate(row.Plc, row.Project);
            var unsafeGaps = chkFullProjectUpdate.Checked
                ? FullUpdateSafety.FindUnsafeGaps(row.Gaps)
                : new List<ExportGap>();

            row.Verdict = ipCheck.Verdict;
            row.FullUpdateUnsafeGaps = unsafeGaps;
            row.RequiresConfirmation = ipCheck.RequiresConfirmation || unsafeGaps.Count > 0;

            var messages = new List<string>();
            if (ipCheck.Message != null)
            {
                messages.Add(ipCheck.Message);
            }
            if (unsafeGaps.Count > 0)
            {
                var names = string.Join("、", unsafeGaps.Select(g => $"{g.Target}（{g.Kind}）"));
                messages.Add(
                    $"整工程覆盖更新会把以下 {unsafeGaps.Count} 个本次未导出的块当成已删除并硬删其全部历史数据：{names}");
            }
            row.Message = messages.Count == 0 ? null : string.Join(" ", messages);
        }

        /// <summary>「整工程覆盖更新」勾选状态变化时，所有已分配目标的行都要重新判定——
        /// 不只是当前选中行，因为这个复选框是全局的，任何行的风险都可能因此改变。</summary>
        private void chkFullProjectUpdate_CheckedChanged(object sender, EventArgs e)
        {
            foreach (var row in _uploadRows.List.Cast<UploadRow>())
            {
                // 这个开关一变，之前"已确认"的含义就不成立了（可能是确认了 IP 不一致，
                // 现在又多了全量更新会删数据的风险，或者反过来）——强制重新确认，
                // 不能让旧的一次勾选悄悄覆盖新出现的风险。
                row.Confirmed = false;
                ApplyRowVerdict(row);
            }

            dgvUploadTargets.Refresh();
            dgvUploadTargets_SelectionChanged(dgvUploadTargets, EventArgs.Empty);
        }

        private async void btnConnectBackend_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtServer.Text))
            {
                MessageBox.Show("请先填写后端地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnConnectBackend.Enabled = false;
            lblBackendStatus.ForeColor = SystemColors.ControlText;
            lblBackendStatus.Text = "连接中…";

            // 后端只是普通 HTTP 调用（非 Openness/COM），HttpClient 本身是异步的，
            // 直接 await 即可保持界面响应，不需要 Task.Run。
            var http = new HttpClient { BaseAddress = new Uri(txtServer.Text.Trim().TrimEnd('/') + "/") };
            try
            {
                var client = new BackendClient(http);
                await client.LoginAsync(txtUser.Text, txtPassword.Text);
                var projects = await client.GetProjectsAsync();

                _backendHttp?.Dispose();
                _backendHttp = http;
                _backendClient = client;
                _backendProjects = projects;

                cmbTargetProject.DataSource = null;
                cmbTargetProject.DisplayMember = nameof(BackendProject.ProjectName);
                cmbTargetProject.DataSource = projects.ToList();

                lblBackendStatus.ForeColor = Color.ForestGreen;
                lblBackendStatus.Text = $"已连接，读到 {projects.Count} 个后端项目";
            }
            catch (Exception ex)
            {
                http.Dispose();
                lblBackendStatus.ForeColor = Color.Firebrick;
                lblBackendStatus.Text = $"连接失败：{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                btnConnectBackend.Enabled = true;
            }
        }

        /// <summary>把下拉框里选中的后端项目分配给上传列表当前选中的那一行，并立即做一次 IP 校验。</summary>
        private void btnAssignTarget_Click(object sender, EventArgs e)
        {
            var row = CurrentUploadRow();
            if (row == null)
            {
                MessageBox.Show("请先在上传列表里选中一行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var project = cmbTargetProject.SelectedItem as BackendProject;
            if (project == null)
            {
                MessageBox.Show("请先连接后端并在下拉框中选择目标项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            row.Project = project;
            row.Confirmed = false;

            ApplyRowVerdict(row);

            dgvUploadTargets.Refresh();
            dgvUploadTargets_SelectionChanged(dgvUploadTargets, EventArgs.Empty);
        }

        /// <summary>IP 不一致或整工程覆盖更新会误删数据时整行标红并锁住上传按钮，
        /// 勾选"我确认"才解禁——两类风险共用同一个确认开关（见 ApplyRowVerdict）。</summary>
        private void dgvUploadTargets_SelectionChanged(object sender, EventArgs e)
        {
            var row = CurrentUploadRow();
            if (row == null)
            {
                lblUploadHint.Text = string.Empty;
                chkConfirm.Visible = false;
                btnUpload.Enabled = false;
                return;
            }

            if (row.Project == null)
            {
                lblUploadHint.Text = "请先在下拉框选择目标项目，再点击「分配到选中行」。";
                lblUploadHint.ForeColor = SystemColors.ControlText;
                chkConfirm.Visible = false;
                btnUpload.Enabled = false;
                return;
            }

            ApplyRowVerdict(row);

            lblUploadHint.Text = row.Message ?? "IP 一致，可直接上传";
            lblUploadHint.ForeColor = row.RequiresConfirmation ? Color.Firebrick : Color.ForestGreen;

            chkConfirm.Visible = row.RequiresConfirmation;
            chkConfirm.Checked = row.RequiresConfirmation && row.Confirmed;
            btnUpload.Enabled = !row.RequiresConfirmation || row.Confirmed;

            dgvUploadTargets.Refresh();
        }

        private void chkConfirm_CheckedChanged(object sender, EventArgs e)
        {
            var row = CurrentUploadRow();
            if (row != null)
            {
                row.Confirmed = chkConfirm.Checked;
            }

            btnUpload.Enabled = !chkConfirm.Visible || chkConfirm.Checked;
            dgvUploadTargets.Refresh();
        }

        /// <summary>缺口/上传目标未一致的行整行标红，与列表里其它行区分开。</summary>
        private void dgvUploadTargets_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= dgvUploadTargets.Rows.Count)
            {
                return;
            }

            var row = dgvUploadTargets.Rows[e.RowIndex].DataBoundItem as UploadRow;
            if (row == null)
            {
                return;
            }

            if (row.Project != null && row.RequiresConfirmation && !row.Confirmed)
            {
                e.CellStyle.BackColor = Color.MistyRose;
                e.CellStyle.ForeColor = Color.Firebrick;
            }
        }

        private async void btnUpload_Click(object sender, EventArgs e)
        {
            var row = CurrentUploadRow();
            if (row == null || row.Project == null)
            {
                MessageBox.Show("请先选中一行并分配目标项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (row.RequiresConfirmation && !row.Confirmed)
            {
                // 双重防线：即便按钮意外可点，这里也再挡一次——IP 不一致 / 整工程覆盖更新
                // 会误删数据，两种情况都绝不能无声上传。
                MessageBox.Show(
                    row.Message ?? "存在需要确认的风险，请先勾选「我确认」。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_backendClient == null)
            {
                MessageBox.Show("请先连接后端。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetUploadBusy(true);
            try
            {
                var taskId = await _backendClient.StartUploadAsync(
                    row.ZipPath, row.Project.Id, chkFullProjectUpdate.Checked);
                lblUploadProgress.Text = $"[{row.Plc.Name}] 已提交，taskId={taskId}";

                var completed = await PollUploadUntilDoneAsync(taskId, row.Plc.Name);
                lblUploadProgress.ForeColor = completed ? Color.ForestGreen : Color.Firebrick;
            }
            catch (Exception ex)
            {
                lblUploadProgress.ForeColor = Color.Firebrick;
                lblUploadProgress.Text = $"[{row.Plc.Name}] 上传失败：{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                SetUploadBusy(false);
            }
        }

        /// <summary>
        /// 轮询上传进度直到完成。返回 true 表示成功完成（UploadStatus.Completed）。
        /// Failed 必须算作失败，不能因为轮询循环正常结束就当成功处理。
        /// </summary>
        private async Task<bool> PollUploadUntilDoneAsync(string taskId, string plcName)
        {
            while (true)
            {
                await Task.Delay(2000);
                var progress = await _backendClient.GetProgressAsync(taskId);
                if (progress == null)
                {
                    lblUploadProgress.Text = $"[{plcName}] 任务不存在或已过期";
                    return false;
                }

                lblUploadProgress.Text = $"[{plcName}] {progress.Percentage}% {progress.Status} {progress.Message}";
                if (!progress.IsCompleted)
                {
                    continue;
                }

                if (progress.Status == UploadStatus.Failed)
                {
                    lblUploadProgress.Text = $"[{plcName}] 上传失败：{progress.ErrorDetails ?? progress.Message}";
                    return false;
                }

                lblUploadProgress.Text = $"[{plcName}] 上传完成";
                return true;
            }
        }

        private void SetUploadBusy(bool busy)
        {
            btnUpload.Enabled = !busy && IsUploadAllowedByConfirmation();
            btnAssignTarget.Enabled = !busy;
            btnConnectBackend.Enabled = !busy;
        }

        private bool IsUploadAllowedByConfirmation()
            => !chkConfirm.Visible || chkConfirm.Checked;

        private UploadRow CurrentUploadRow()
            => dgvUploadTargets.CurrentRow?.DataBoundItem as UploadRow;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _backendHttp?.Dispose();
                components?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>PLC 列表行：勾选框决定该 PLC 是否进入下方的上传目标列表。</summary>
    public class PlcRow
    {
        public bool Selected { get; set; } = true;
        public PlcIdentity Plc { get; set; }

        public string Name => Plc.Name;
        public string Ip => Plc.Ip ?? "(未组态)";
        public string OrderNumber => Plc.OrderNumber;
    }

    /// <summary>上传区一行：一个已导出打包的 PLC，配上一个（可能尚未选定的）后端目标项目。</summary>
    public class UploadRow
    {
        public PlcIdentity Plc { get; set; }
        public string ZipPath { get; set; }
        public BackendProject Project { get; set; }
        public UploadVerdict? Verdict { get; set; }
        public bool RequiresConfirmation { get; set; }
        public bool Confirmed { get; set; }

        /// <summary>这个 PLC 自己的缺口清单，来自 ExportPackage.Gaps；C2 的全项目更新安全
        /// 判定（FullUpdateSafety）就是在这份数据上跑的。</summary>
        public IReadOnlyList<ExportGap> Gaps { get; set; } = new List<ExportGap>();

        /// <summary>只在勾了「整工程覆盖更新」时才非空——本次上传会让后端把这些块当成
        /// 已删除并硬删的那些缺口，由 MainForm.ApplyRowVerdict 计算。</summary>
        public IReadOnlyList<ExportGap> FullUpdateUnsafeGaps { get; set; } = new List<ExportGap>();

        /// <summary>IP 不一致 + 整工程覆盖更新风险的合并提示文案，由 ApplyRowVerdict 计算。</summary>
        public string Message { get; set; }

        public string PlcDisplay => $"{Plc.Name} / {Plc.Ip ?? "(未组态)"} / {Plc.OrderNumber}";

        public string ProjectDisplay => Project == null
            ? "(未分配)"
            : $"{Project.ProjectName} / {Project.PlcIp} / {Project.PlcCode}";

        public string VerdictDisplay
        {
            get
            {
                if (Project == null)
                {
                    return "未分配目标项目";
                }
                if (!RequiresConfirmation)
                {
                    return "IP 一致";
                }

                string reason;
                if (FullUpdateUnsafeGaps.Count == 0)
                {
                    reason = Verdict.ToString();
                }
                else if (Verdict == UploadVerdict.Match)
                {
                    reason = $"全量更新会删数据（{FullUpdateUnsafeGaps.Count} 块）";
                }
                else
                {
                    reason = $"{Verdict}+全量更新会删数据（{FullUpdateUnsafeGaps.Count} 块）";
                }
                return Confirmed ? $"{reason}（已确认）" : $"{reason}（待确认）";
            }
        }
    }
}
