using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;
using 博图项目读取.Models;
using 博图项目读取.Services;

namespace 博图项目读取
{
    public partial class Form1 : Form
    {
        private TiaPortalService _tiaPortalService;
        private ProjectExportService _exportService;
        private TiaPortalProject _currentProject;

        public Form1()
        {
            InitializeComponent();
            InitializeServices();
            InitializeUI();
        }

        /// <summary>
        /// 初始化服务
        /// </summary>
        private void InitializeServices()
        {
            _tiaPortalService = new TiaPortalService(LogMessage);
            _exportService = new ProjectExportService(LogMessage);
        }

        /// <summary>
        /// 初始化界面
        /// </summary>
        private void InitializeUI()
        {
            // 设置窗体标题
            this.Text = "博图项目读取工具 - .NET Framework 4.8.1";
            
            // 禁用初始按钮
            btnSelectProject.Enabled = false;
            btnParseProject.Enabled = false;
            btnExportJson.Enabled = false;
            btnExportHtml.Enabled = false;
            btnExportCsv.Enabled = false;
            btnExportBusinessJson.Enabled = false;

            // 设置初始状态
            UpdateStatus("就绪");
            UpdateProgress(0);
        }

        /// <summary>
        /// 连接博图软件
        /// </summary>
        private async void btnConnectTiaPortal_Click(object sender, EventArgs e)
        {
            try
            {
                UpdateStatus("正在连接博图软件...");
                UpdateProgress(10);
                
                LogMessage("开始连接TIA Portal...");
                
                var success = await _tiaPortalService.ConnectAsync();
                
                if (success)
                {
                    LogMessage("TIA Portal连接成功！");
                    LogMessage("现在可以选择项目文件进行解析。");
                    UpdateStatus("博图软件连接成功");
                    btnConnectTiaPortal.Enabled = false;
                    btnSelectProject.Enabled = true;
                    UpdateProgress(100);
                }
                else
                {
                    LogMessage("TIA Portal连接失败！");
                    LogMessage("请确保：");
                    LogMessage("1. TIA Portal软件已正确安装");
                    LogMessage("2. 具有足够的系统权限");
                    LogMessage("3. 没有其他程序占用TIA Portal");
                    UpdateStatus("连接失败");
                    UpdateProgress(0);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"连接异常: {ex.Message}");
                UpdateStatus("连接异常");
                UpdateProgress(0);
            }
        }

        /// <summary>
        /// 选择项目文件
        /// </summary>
        private async void btnSelectProject_Click(object sender, EventArgs e)
        {
            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Title = "选择TIA Portal项目文件";
                openFileDialog.Filter = "TIA Portal项目文件 (*.ap20;*.ap19;*.ap18;*.ap17;*.ap16;*.ap15)|*.ap20;*.ap19;*.ap18;*.ap17;*.ap16;*.ap15|所有文件 (*.*)|*.*";
                openFileDialog.FilterIndex = 1;

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    txtProjectPath.Text = openFileDialog.FileName;
                    btnParseProject.Enabled = true;
                    LogMessage($"选择项目文件: {openFileDialog.FileName}");
                    // "选择项目/解析项目"功能已合并：选完直接解析
                    await ParseCurrentProjectAsync();
                }
            }
        }

        /// <summary>
        /// 解析项目（"选择/解析"已合并，此按钮已隐藏，保留事件入口）
        /// </summary>
        private async void btnParseProject_Click(object sender, EventArgs e)
        {
            await ParseCurrentProjectAsync();
        }

        /// <summary>
        /// 打开并解析当前选中的项目（选择文件后自动触发）
        /// </summary>
        private async Task ParseCurrentProjectAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(txtProjectPath.Text))
                {
                    MessageBox.Show("请先选择项目文件！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                UpdateStatus("正在解析项目...");
                UpdateProgress(0);
                
                LogMessage("开始打开项目...");
                
                // 打开项目
                var openSuccess = await _tiaPortalService.OpenProjectAsync(txtProjectPath.Text);
                if (!openSuccess)
                {
                    LogMessage("项目打开失败！");
                    UpdateStatus("项目打开失败");
                    return;
                }
                
                UpdateProgress(30);
                LogMessage("项目打开成功，开始解析...");
                
                // 解析项目
                _currentProject = await _tiaPortalService.ParseProjectAsync();
                
                if (_currentProject != null)
                {
                    UpdateProgress(80);
                    LogMessage("项目解析成功！");
                    
                    // 更新界面显示
                    UpdateProjectDisplay();
                    
                    // 仅"导出业务系统JSON"保留可用（导出JSON/HTML/CSV符号表按钮已隐藏）
                    btnExportBusinessJson.Enabled = true;
                    
                    UpdateStatus("项目解析完成");
                    UpdateProgress(100);
                    
                    LogMessage($"解析完成 - 设备数量: {_currentProject.Devices.Count}, 符号数量: {_currentProject.Symbols.Count}, 程序块数量: {_currentProject.ProgramBlocks.Count}");
                    _tiaPortalService.CloseProj();

                    // 将 xml\{项目名} 整个文件夹打包为 zip：文件名带日期，同日期已存在则自动加序号区分（保留历史版本）
                    try
                    {
                        var xmlDir = Path.Combine(Directory.GetCurrentDirectory(), "xml");
                        var xmlRoot = Path.Combine(xmlDir, _currentProject.Name);
                        if (Directory.Exists(xmlRoot))
                        {
                            var datePart = DateTime.Now.ToString("yyyyMMdd");
                            var zipPath = Path.Combine(xmlDir, $"{_currentProject.Name}_{datePart}.zip");
                            int seq = 1;
                            while (File.Exists(zipPath))
                            {
                                zipPath = Path.Combine(xmlDir, $"{_currentProject.Name}_{datePart}_{seq}.zip");
                                seq++;
                            }
                            ZipFile.CreateFromDirectory(xmlRoot, zipPath, CompressionLevel.Optimal, true);
                            LogMessage($"项目文件夹已打包: {zipPath}");

                            // 打包成功后删除原文件夹（内容已完整保存在 zip 内；下次解析会自动重建）
                            try
                            {
                                Directory.Delete(xmlRoot, true);
                                LogMessage($"已删除原文件夹: {xmlRoot}");
                            }
                            catch (Exception delEx)
                            {
                                LogMessage($"删除原文件夹失败（zip已保留）: {delEx.Message}");
                            }
                        }
                        else
                        {
                            LogMessage($"未找到待打包目录: {xmlRoot}");
                        }
                    }
                    catch (Exception zipEx)
                    {
                        LogMessage($"打包zip失败: {zipEx.Message}");
                    }
                }
                else
                {
                    LogMessage("项目解析失败！");
                    UpdateStatus("项目解析失败");
                    UpdateProgress(0);
                    _tiaPortalService.CloseProj();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"解析异常: {ex.Message}");
                UpdateStatus("解析异常");
                UpdateProgress(0);
                _tiaPortalService.CloseProj();
            }
        }

        /// <summary>
        /// 导出JSON
        /// </summary>
        private async void btnExportJson_Click(object sender, EventArgs e)
        {
            if (_currentProject == null)
            {
                MessageBox.Show("请先解析项目！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Title = "导出JSON文件";
                saveFileDialog.Filter = "JSON文件 (*.json)|*.json";
                saveFileDialog.FileName = $"{_currentProject.Name}_项目数据.json";

                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    UpdateStatus("正在导出JSON...");
                    var success = await _exportService.ExportToJsonAsync(_currentProject, saveFileDialog.FileName);
                    
                    if (success)
                    {
                        UpdateStatus("JSON导出完成");
                        MessageBox.Show("JSON文件导出成功！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        UpdateStatus("JSON导出失败");
                        MessageBox.Show("JSON文件导出失败！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        /// <summary>
        /// 导出HTML报告
        /// </summary>
        private async void btnExportHtml_Click(object sender, EventArgs e)
        {
            if (_currentProject == null)
            {
                MessageBox.Show("请先解析项目！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Title = "导出HTML报告";
                saveFileDialog.Filter = "HTML文件 (*.html)|*.html";
                saveFileDialog.FileName = $"{_currentProject.Name}_项目报告.html";

                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    UpdateStatus("正在导出HTML报告...");
                    var success = await _exportService.ExportToHtmlReportAsync(_currentProject, saveFileDialog.FileName);
                    
                    if (success)
                    {
                        UpdateStatus("HTML报告导出完成");
                        MessageBox.Show("HTML报告导出成功！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        UpdateStatus("HTML报告导出失败");
                        MessageBox.Show("HTML报告导出失败！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        /// <summary>
        /// 导出CSV符号表
        /// </summary>
        private async void btnExportCsv_Click(object sender, EventArgs e)
        {
            if (_currentProject == null)
            {
                MessageBox.Show("请先解析项目！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Title = "导出CSV符号表";
                saveFileDialog.Filter = "CSV文件 (*.csv)|*.csv";
                saveFileDialog.FileName = $"{_currentProject.Name}_符号表.csv";

                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    UpdateStatus("正在导出CSV符号表...");
                    var success = await _exportService.ExportSymbolsToCsvAsync(_currentProject.Symbols, saveFileDialog.FileName);
                    
                    if (success)
                    {
                        UpdateStatus("CSV符号表导出完成");
                        MessageBox.Show("CSV符号表导出成功！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        UpdateStatus("CSV符号表导出失败");
                        MessageBox.Show("CSV符号表导出失败！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        /// <summary>
        /// 导出业务系统JSON
        /// </summary>
        private async void btnExportBusinessJson_Click(object sender, EventArgs e)
        {
            if (_currentProject == null)
            {
                MessageBox.Show("请先解析项目！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Title = "导出业务系统JSON";
                saveFileDialog.Filter = "JSON文件 (*.json)|*.json";
                saveFileDialog.FileName = $"{_currentProject.Name}_业务系统数据.json";

                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    UpdateStatus("正在导出业务系统JSON...");
                    var success = await _exportService.ExportBusinessSystemJsonAsync(_currentProject, saveFileDialog.FileName);
                    
                    if (success)
                    {
                        UpdateStatus("业务系统JSON导出完成");
                        MessageBox.Show("业务系统JSON导出成功！\n\n此文件包含：\n• 监控点配置\n• 报警配置\n• 通讯设置\n• 完整的项目数据\n\n可用于业务系统集成和实时监控。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        UpdateStatus("业务系统JSON导出失败");
                        MessageBox.Show("业务系统JSON导出失败！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        /// <summary>
        /// 更新项目显示
        /// </summary>
        private void UpdateProjectDisplay()
        {
            if (_currentProject == null) return;

            // 更新项目信息
            lblProjectInfo.Text = $"项目信息：{_currentProject.Name} (版本: {_currentProject.Version})";

            // 更新设备信息
            dgvDevices.Rows.Clear();
            foreach (var device in _currentProject.Devices)
            {
                dgvDevices.Rows.Add(device.Name, device.Type, device.OrderNumber, device.Blocks.Count);
            }

            // 更新符号表
            dgvSymbols.Rows.Clear();
            foreach (var symbol in _currentProject.Symbols)
            {
                dgvSymbols.Rows.Add(symbol.Name, symbol.Address, symbol.DataType, symbol.Comment);
            }

            // 更新程序块
            dgvBlocks.Rows.Clear();
            foreach (var block in _currentProject.ProgramBlocks)
            {
                dgvBlocks.Rows.Add(block.Name, block.Type, block.Number, block.Language, block.Comment);
            }
        }

        /// <summary>
        /// 更新状态
        /// </summary>
        private void UpdateStatus(string status)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(UpdateStatus), status);
                return;
            }
            
            lblStatus.Text = $"状态：{status}";
        }

        /// <summary>
        /// 更新进度
        /// </summary>
        private void UpdateProgress(int value)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<int>(UpdateProgress), value);
                return;
            }
            
            progressBar.Value = Math.Max(0, Math.Min(100, value));
        }

        /// <summary>
        /// 记录日志
        /// </summary>
        private void LogMessage(string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(LogMessage), message);
                return;
            }
            
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            txtLog.AppendText($"[{timestamp}] {message}\r\n");
            txtLog.ScrollToCaret();
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tiaPortalService?.Dispose();
                if (components != null)
                {
                    components.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
