// Program.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PDFtoImage;
using SkiaSharp;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace InvoiceSummary
{
    // ==================== 数据模型 ====================
    public class Invoice
    {
        public string FilePath = "";
        public string Number = "";
        public DateTime Date;
        public string BuyerName = "";
        public string BuyerTaxId = "";
        public string SellerName = "";
        public string SellerTaxId = "";
        public string ItemName = "";
        public string Description = "";
        public string Category = "其他";
        public decimal Rate;
        public decimal Amount;
        public decimal Tax;
        public decimal Total;
    }

    public class ParseResult
    {
        public Invoice? Invoice { get; set; }
        public string Status { get; set; } = "ok";
        public string Message { get; set; } = "";
        public bool IsSuccess => Invoice != null && Status == "ok";
    }

    // ==================== 字体解析 ====================
    public class SingleFontResolver : IFontResolver
    {
        private readonly string _path;
        public SingleFontResolver(string path) => _path = path;
        public byte[]? GetFont(string faceName) => File.ReadAllBytes(_path);
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            if (isBold || isItalic)
                return new FontResolverInfo(FontManager.Family, isBold, isItalic);
            return new FontResolverInfo(FontManager.Family);
        }
    }

    public static class FontManager
    {
        public const string Family = "InvFont";
        private static bool _inited;
        public static void Init()
        {
            if (_inited) return;
            _inited = true;
            var fontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var candidates = new[]
            {
                Path.Combine(fontDir, "msyh.ttf"),
                Path.Combine(fontDir, "msyhbd.ttf"),
                Path.Combine(fontDir, "Deng.ttf"),
                Path.Combine(fontDir, "simhei.ttf"),
                Path.Combine(fontDir, "simfang.ttf"),
            };
            var found = candidates.FirstOrDefault(File.Exists);
            if (found == null) throw new Exception("系统缺少中文字体，无法生成 PDF。");
            try { GlobalFontSettings.FontResolver = new SingleFontResolver(found); } catch { }
        }
    }

    // ==================== 主界面 ====================
    public class MainForm : Form
    {
        private TextBox? txtFolder;
        private Button? btnBrowse;
        private Button? btnStart;
        private RichTextBox? txtLog;
        private ToolStripProgressBar? progressBar;
        private CheckBox? chkShowAiDetail; // 【新增】控制是否显示 AI 交互的复选框
        private Button? btnAiSettings;      // 【新增】打开「AI 设置」窗口
        private Button? btnResetAi;         // 【新增】重置 AI 配置
        private ToolStripStatusLabel? lblAiStatus; // 【新增】状态栏显示当前 AI

        public MainForm() { InitUI(); }

        private void InitUI()
        {
            Text = "差旅费发票汇总工具 v1.4";
            Size = new Size(780, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Microsoft YaHei", 9);
            AllowDrop = true;

            // 尝试加载嵌入的资源图标
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("InvoiceTool.app.ico");
                if (stream != null) this.Icon = new Icon(stream);
            }
            catch { }

            var panelTop = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(15, 15, 15, 5) };
            var lblTip = new Label { Text = "请选择包含发票 PDF 的文件夹：", AutoSize = true, Location = new Point(15, 20) };
            txtFolder = new TextBox { Location = new Point(235, 17), Width = 260, ReadOnly = true, BackColor = Color.White };
            btnBrowse = new Button { Text = "选择文件夹", Location = new Point(505, 15), Width = 95, Height = 28, BackColor = Color.White };
            btnBrowse.Click += BtnBrowse_Click;
            panelTop.Controls.Add(lblTip);
            panelTop.Controls.Add(txtFolder);
            panelTop.Controls.Add(btnBrowse);

            var panelAction = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(15, 5, 15, 5) };
            
            btnStart = new Button
            {
                Text = "开始生成汇总 PDF",
                Location = new Point(140, 5),
                Width = 170,
                Height = 35,
                BackColor = Color.FromArgb(27, 58, 87),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 10, FontStyle.Bold)
            };
            btnStart.FlatAppearance.BorderSize = 0;
            btnStart.Click += BtnStart_Click;

            // 【新增】AI 设置入口：随时改服务商 / 密钥 / 保存位置
            btnAiSettings = new Button
            {
                Text = "AI 设置",
                Location = new Point(318, 9),
                Width = 76,
                Height = 28,
                BackColor = Color.White
            };
            btnAiSettings.Click += BtnAiSettings_Click;

            // 【新增】重置 AI：清空本机已保存的服务商 / 密钥 / 模型，重新引导设置
            btnResetAi = new Button
            {
                Text = "重置 AI",
                Location = new Point(402, 9),
                Width = 86,
                Height = 28,
                BackColor = Color.FromArgb(250, 240, 240),
                ForeColor = Color.FromArgb(160, 60, 40)
            };
            btnResetAi.Click += BtnResetAi_Click;

            // 【新增】AI 交互单选框
            chkShowAiDetail = new CheckBox
            {
                Text = "显示 AI 交互",
                Location = new Point(496, 13),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80),
                Font = new Font("Microsoft YaHei", 9)
            };
            // 绑定事件，改变 AiParser 的开关
            chkShowAiDetail.CheckedChanged += (s, e) => AiParser.ShowDebugLog = chkShowAiDetail.Checked;

            panelAction.Controls.Add(btnStart);
            panelAction.Controls.Add(btnAiSettings);
            panelAction.Controls.Add(btnResetAi);
            panelAction.Controls.Add(chkShowAiDetail);

            txtLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(15),
                ReadOnly = true,
                BackColor = Color.White,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9)
            };
            var panelLog = new Panel { Dock = DockStyle.Fill, Padding = new Padding(15) };
            panelLog.Controls.Add(txtLog);

            // 【新增】底部状态栏：显示当前用哪个 AI、密钥存放在哪
            var statusStrip = new StatusStrip
            {
                BackColor = Color.White,
                SizingGrip = false,
                RenderMode = ToolStripRenderMode.System,
                Font = new Font("Microsoft YaHei", 8.5f)
            };
            statusStrip.Items.Add(new ToolStripStatusLabel(" ") { AutoSize = false, Width = 8 });
            lblAiStatus = new ToolStripStatusLabel("AI：读取配置中…")
            {
                ForeColor = Color.FromArgb(80, 80, 80)
            };
            statusStrip.Items.Add(lblAiStatus);

            // 进度条：使用固定较大宽度，尽量贴近右边（兼容不支持 Spring/MinimumSize 的环境）
            progressBar = new ToolStripProgressBar
            {
                Width = 320,
                Style = ProgressBarStyle.Continuous,
                Visible = false
            };
            statusStrip.Items.Add(progressBar);

            Controls.Add(panelLog);
            Controls.Add(statusStrip);
            Controls.Add(panelAction);
            Controls.Add(panelTop);

            // 【新增】首次打开且没有密钥时，自动弹出配置窗口
            Shown += (s, e) =>
            {
                MaybeFirstRunSetup();
                UpdateAiStatus();
            };

            DragEnter += (s, e) =>
            {
                if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                    e.Effect = DragDropEffects.Copy;
            };
            DragDrop += (s, e) =>
            {
                var files = (string[]?)e.Data?.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0 && Directory.Exists(files[0]))
                {
                    txtFolder!.Text = files[0];
                    Log($"已选择文件夹：{files[0]}");
                }
            };
        }

        private void BtnBrowse_Click(object? sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog { Description = "请选择存放发票 PDF 的文件夹" };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                txtFolder!.Text = dialog.SelectedPath;
                Log($"已选择文件夹：{dialog.SelectedPath}");
            }
        }

        // ==================== AI 设置 ====================
        /// <summary>首次运行：没有可用密钥就引导用户自己配置</summary>
        private void MaybeFirstRunSetup()
        {
            var cfg = AiConfigStore.Resolve(AppContext.BaseDirectory);
            if (cfg.HasKey) return;
            ShowAiSetup(firstRun: true);
        }

        private void BtnAiSettings_Click(object? sender, EventArgs e) => ShowAiSetup(firstRun: false);

        /// <summary>重置 AI：清空注册表 + config.txt 里的 AI 配置，再重新引导设置</summary>
        private void BtnResetAi_Click(object? sender, EventArgs e)
        {
            var answer = MessageBox.Show(
                "将清空本机已保存的 AI 配置（注册表 + config.txt 中的服务商 / 密钥 / 模型），并重新引导设置。\n\n确定继续？",
                "重置 AI 配置", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;

            var cleared = new List<string>();
            var errors = new List<string>();
            AiConfigStore.ClearAll(AppContext.BaseDirectory, out cleared, out errors);

            // 让内存中的 AI 状态同步失效并重读
            AiParser.Reload(AppContext.BaseDirectory);

            Log("======================================");
            Log("[AI] 已重置配置" + (cleared.Count > 0 ? "：" + string.Join("、", cleared) : "（未找到已保存的配置）"));
            if (errors.Count > 0) Log("[AI] 部分清除失败：" + string.Join("；", errors));
            Log("======================================");

            UpdateAiStatus();
            ShowAiSetup(firstRun: true);   // 重新引导设置
        }

        private void ShowAiSetup(bool firstRun)
        {
            var cfg = AiConfigStore.Resolve(AppContext.BaseDirectory);
            using var dlg = new ApiSetupForm(cfg, AppContext.BaseDirectory, firstRun);
            bool saved = dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null;

            if (!saved)
            {
                if (firstRun)
                {
                    Log("未配置 AI 密钥，本次将使用本地正则识别。");
                    Log("想要更准的识别，随时点上方「AI 设置」填写自己的 API Key。");
                }
                UpdateAiStatus();
                return;
            }

            AiParser.LogHandler = Log;
            AiParser.Reload(AppContext.BaseDirectory);
            UpdateAiStatus();

            Log("======================================");
            Log($"[AI] 配置已保存：{AiParser.ProviderName} / {AiParser.Model}");
            if (AiParser.IsEnabled) Log($"     密钥来源：{AiParser.KeySource}");
            Log("======================================");
            if (!AiParser.IsEnabled)
                Log("[AI] 注意：AI 目前是关闭的或缺少密钥，本次仍用本地正则识别。");
        }

        /// <summary>刷新底部状态栏</summary>
        private void UpdateAiStatus()
        {
            if (lblAiStatus == null) return;
            var cfg = AiConfigStore.Resolve(AppContext.BaseDirectory);

            if (!cfg.Enabled)
            {
                lblAiStatus.Text = "AI：已关闭（仅本地正则，不联网）";
                lblAiStatus.ForeColor = Color.FromArgb(150, 90, 0);
            }
            else if (!cfg.HasKey)
            {
                lblAiStatus.Text = "AI：未配置密钥（仅本地正则）　→ 点上方「AI 设置」填写";
                lblAiStatus.ForeColor = Color.FromArgb(170, 60, 30);
            }
            else
            {
                lblAiStatus.Text = $"AI：{AiProviders.Find(cfg.Provider).ShortName} / {cfg.Model}　密钥来自{cfg.Source}";
                lblAiStatus.ForeColor = Color.FromArgb(27, 58, 87);
            }
        }

        private async void BtnStart_Click(object? sender, EventArgs e)
        {
            string dir = txtFolder?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show("请先选择一个有效的文件夹！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            txtLog!.Clear();
            btnStart!.Enabled = false;
            btnBrowse!.Enabled = false;
            btnAiSettings!.Enabled = false;
            btnResetAi!.Enabled = false;
            progressBar!.Visible = true;

            // 记录本次运行前的 Token 基数
            long startPromptTokens = AiParser.TotalPromptTokens;
            long startCompletionTokens = AiParser.TotalCompletionTokens;

            try
            {
                FontManager.Init();
                
                // 【新增】把 UI 的日志方法传给 AI 解析器
                AiParser.LogHandler = Log;
                
                AiParser.Init(AppContext.BaseDirectory);

                Log("======================================");
                Log("        发票汇总工具 v1.4 (GUI版)");
                Log("======================================");
                Log($"扫描目录：{dir}");
                if (!AiParser.IsEnabled)
                    Log("提示：本次未启用 AI（未配置密钥或已在设置里关闭），只用本地正则识别；点上方「AI 设置」可填写密钥。");
                Log("");

                var files = Directory.GetFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly)
                    .Where(f => !Path.GetFileName(f).StartsWith("发票汇总"))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Log($"发现 {files.Count} 个 PDF 文件\n");
                progressBar.Maximum = Math.Max(1, files.Count);
                progressBar.Value = 0;

                // 线程安全收集与计数（并发解析）
                var invoices = new ConcurrentBag<Invoice>();
                var seen = new ConcurrentDictionary<string, byte>();
                int okCnt = 0, notInvCnt = 0, scannedCnt = 0, lowScoreCnt = 0, multiCnt = 0, errCnt = 0;
                int completed = 0;

                // 限制并发，避免同时发起过多 AI 请求；以 CPU 数×2 为上限（默认最高 6）
                int maxConcurrency = Math.Clamp(Environment.ProcessorCount * 2, 1, 6);

                await Task.Run(() =>
                {
                    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency }, f =>
                    {
                        var name = Path.GetFileName(f);
                        try
                        {
                            var result = InvoiceParser.Parse(f);
                            switch (result.Status)
                            {
                                case "ok":
                                    {
                                        var inv = result.Invoice!;
                                        if (!seen.TryAdd(inv.Number, 0))
                                        {
                                            Log($"  - 重复跳过：{name}");
                                            Interlocked.Increment(ref notInvCnt);
                                        }
                                        else
                                        {
                                            invoices.Add(inv);
                                            Interlocked.Increment(ref okCnt);
                                            Log($"  ✓ {name}");
                                            Log($"      [{inv.Category}] 发票号 {inv.Number}  开票单位「{inv.SellerName}」");
                                            Log($"      金额 {inv.Amount:0.00}  税额 {inv.Tax:0.00}  价税合计 {inv.Total:0.00}");
                                        }
                                        break;
                                    }
                                case "scanned":
                                    Interlocked.Increment(ref scannedCnt);
                                    Log($"  ⚠ {name}  → 疑似扫描件：{result.Message}");
                                    break;
                                case "multi_number":
                                    Interlocked.Increment(ref multiCnt);
                                    Log($"  ⚠ {name}  → {result.Message}");
                                    break;
                                case "low_score":
                                    Interlocked.Increment(ref lowScoreCnt);
                                    Log($"  - 跳过（特征不足）：{name}  → {result.Message}");
                                    break;
                                case "not_invoice":
                                    Interlocked.Increment(ref notInvCnt);
                                    Log($"  - 跳过（非发票）：{name}");
                                    break;
                                default:
                                    Interlocked.Increment(ref errCnt);
                                    Log($"  ✗ {name}  → {result.Message}");
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref errCnt);
                            Log($"  ✗ {name}  解析失败：{ex.Message}");
                        }

                        // 进度合并：每 4 张或最后一张刷新一次，减少跨线程切换
                        int done = Interlocked.Increment(ref completed);
                        if (done % 4 == 0 || done == files.Count)
                        {
                            int snapshot = done;
                            try
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    if (progressBar!.Value < progressBar.Maximum)
                                        progressBar.Value = Math.Min(snapshot, progressBar.Maximum);
                                }));
                            }
                            catch { /* 窗口可能已关闭 */ }
                        }
                    });
                });

                var invoiceList = invoices.ToList();

                Log("\n======================================");
                Log($"成功：{okCnt}   非发票：{notInvCnt}   扫描件：{scannedCnt}");
                Log($"特征不足：{lowScoreCnt}   疑似汇总表：{multiCnt}   失败：{errCnt}");

                // 计算本次 Token 消耗和费用
                long usedPromptTokens = AiParser.TotalPromptTokens - startPromptTokens;
                long usedCompletionTokens = AiParser.TotalCompletionTokens - startCompletionTokens;
                long totalUsedTokens = usedPromptTokens + usedCompletionTokens;

                // 按服务商估算单价计费（价格见 AiProviderInfo，为参考估算，请以服务商官方计费为准）
                var price = AiProviders.Find(AiParser.ProviderId);
                decimal cost = (usedPromptTokens / 1_000_000m * price.InputCostPerM)
                             + (usedCompletionTokens / 1_000_000m * price.OutputCostPerM);
                Log("======================================");
                Log($"[AI 消耗统计]");
                if (totalUsedTokens == 0)
                {
                    Log($"  本次未产生 AI 调用（或调用失败回退正则）。");
                }
                else
                {
                    Log($"  输入 Tokens：{usedPromptTokens:N0}");
                    Log($"  输出 Tokens：{usedCompletionTokens:N0}");
                    Log($"  总计 Tokens：{totalUsedTokens:N0}");
                    Log($"  预估费用：￥{cost:F4} 元");
                }
                Log("======================================");

                if (invoiceList.Count == 0)
                {
                    Log("没有解析到有效发票，未生成汇总文件。");
                    MessageBox.Show("没有解析到有效发票！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Log("\n正在生成汇总 PDF，请稍候...");
                string timestamp = DateTime.Now.ToString("HHmmss");
                string outPath = Path.Combine(dir, $"发票汇总_{timestamp}.pdf");
                await Task.Run(() => ReportBuilder.Build(outPath, invoiceList));

                Log($"\n已生成汇总文件：\n{outPath}");
                Log("处理完成！");

                var answer = MessageBox.Show($"汇总完成！是否立即打开 PDF？\n\n成功 {okCnt} 张发票", "成功", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(outPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"\n发生错误：{ex.Message}");
                MessageBox.Show($"处理过程中发生错误：\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnResetAi!.Enabled = true;
                btnStart.Enabled = true;
                btnBrowse.Enabled = true;
                btnAiSettings!.Enabled = true;
                progressBar.Visible = false;
                UpdateAiStatus();
            }
        }

        private void Log(string message)
        {
            if (txtLog!.InvokeRequired) { txtLog.Invoke(new Action<string>(Log), message); return; }
            txtLog.AppendText(message + Environment.NewLine);
            txtLog.ScrollToCaret();
        }
    }

    // ==================== 程序入口 ====================
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    // ==================== 发票识别 + 解析 ====================
    public static class InvoiceParser
    {
        private static int CutIndex(string s, params string[] keys)
        {
            int best = -1;
            foreach (var k in keys)
            {
                int i = s.IndexOf(k, StringComparison.Ordinal);
                if (i >= 0 && (best < 0 || i < best)) best = i;
            }
            return best;
        }

        private static int FirstDigit(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (char.IsDigit(s[i])) return i;
            return -1;
        }

        private static decimal ParseDec(string s)
            => decimal.TryParse(s.Replace(",", ""), out var v) ? v : 0m;

        private static string CleanName(string raw)
        {
            string v = raw.Trim();
            int cut = CutIndex(v, "统一社会信用代码", "纳税人识别号", "地址", "电话", "开户行", "账号");
            if (cut > 0) v = v.Substring(0, cut);
            return v.Trim().Trim('*', ' ');
        }

        private static bool IsHeaderText(string v)
        {
            return v.Contains("项目名称") || v.Contains("规格型号") || v.Contains("单位")
                || v.Contains("数量") || v.Contains("单价") || v.Contains("金额")
                || v.Contains("税率") || v.Contains("税额") || v.Contains("合计");
        }

        private static int ScoreInvoice(string text)
        {
            int score = 0;
            if (Regex.IsMatch(text, @"发票号码\s*[：:]?\s*\d{8,25}")) score += 3;
            if (Regex.IsMatch(text, @"\*[^\r\n*]{2,20}\*")) score += 2;
            if (Regex.IsMatch(text, @"价税合计|小\s*写")) score += 1;
            if (Regex.IsMatch(text, @"开票日期")) score += 1;
            if (Regex.IsMatch(text, @"购买方")) score += 1;
            if (Regex.IsMatch(text, @"销售方|开票单位")) score += 1;
            if (Regex.IsMatch(text, @"统一社会信用代码|纳税人识别号")) score += 1;
            if (Regex.IsMatch(text, @"税率|税额")) score += 1;
            return score;
        }

        private static string? ExtractInvoiceNumber(string text)
        {
            var patterns = new[]
            {
                @"发票号码\s*[：:]?\s*(\d{8,25})",
                @"票据号\s*[：:]?\s*(\d{8,25})",
                @"客票号\s*[：:]?\s*(\d{8,25})",
                @"发票代码\s*[：:]?\s*(\d{10,25})",
                @"发票号\s*[：:]?\s*(\d{8,25})",
            };
            foreach (var p in patterns)
            {
                var m = Regex.Match(text, p);
                if (m.Success) return m.Groups[1].Value;
            }
            if (text.Contains("发票") || text.Contains("电子发票"))
            {
                var m = Regex.Match(text, @"(?<!\d)(\d{20})(?!\d)");
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }

        public static ParseResult Parse(string path)
        {
            string text;
            int pageCount = 0;
            try
            {
                using var doc = PdfPigDoc.Open(path);
                pageCount = doc.NumberOfPages;
                var sb = new StringBuilder();
                foreach (var page in doc.GetPages())
                {
                    sb.Append(page.Text);
                    sb.Append('\n');
                }
                text = sb.ToString();
            }
            catch (Exception ex)
            {
                return new ParseResult { Status = "error", Message = $"PDF读取失败：{ex.Message}" };
            }

            if (string.IsNullOrWhiteSpace(text))
                return new ParseResult { Status = "scanned", Message = "无文字层，疑似扫描件/图片PDF，需先OCR" };

            if (pageCount > 2)
                return new ParseResult { Status = "multi_number", Message = $"文档共 {pageCount} 页，非单张发票，已跳过" };

            var allNumbers = Regex.Matches(text, @"(?<!\d)(\d{20})(?!\d)")
                .Select(m => m.Groups[1].Value).Distinct().ToList();

            if (allNumbers.Count == 0)
            {
                allNumbers = Regex.Matches(text, @"(?:发票号码|发票号|票据号|客票号|发票代码)\s*[：:]?\s*(\d{8,25})")
                    .Select(m => m.Groups[1].Value).Distinct().ToList();
            }

            if (allNumbers.Count > 1)
                return new ParseResult { Status = "multi_number", Message = $"检测到 {allNumbers.Count} 个发票号码，疑似汇总表" };

            if ((text.Contains("汇总") || text.Contains("统计表") || text.Contains("明细表")))
            {
                var amounts = Regex.Matches(text, @"\d+\.\d{2}").Count;
                if (amounts >= 3)
                    return new ParseResult { Status = "multi_number", Message = "疑似汇总表/非单张发票" };
            }

            int score = ScoreInvoice(text);

            var ai = AiParser.TryParse(text, path, out bool aiSaysInvoice);
            if (ai != null && ai.Number.Length > 0)
            {
                if (aiSaysInvoice || score >= 3)
                    return new ParseResult { Invoice = ai, Status = "ok" };
                return new ParseResult { Status = "low_score", Message = $"AI 认为非发票或特征不足({score}分)" };
            }

            if (score < 5)
                return new ParseResult { Status = "low_score", Message = $"特征评分不足({score}分，需≥5)" };

            var inv = ParseWithRegex(text, path);
            if (inv == null)
                return new ParseResult { Status = "not_invoice", Message = "未提取到有效发票信息" };

            return new ParseResult { Invoice = inv, Status = "ok" };
        }

        private static Invoice? ParseWithRegex(string text, string path)
        {
            string compact = Regex.Replace(text, @"\s+", "");
            var inv = new Invoice { FilePath = path };

            var num = ExtractInvoiceNumber(text);
            if (string.IsNullOrEmpty(num)) return null;
            inv.Number = num;

            var m = Regex.Match(compact, @"开票日期[：:]?(\d{4})年(\d{1,2})月(\d{1,2})日");
            if (!m.Success) m = Regex.Match(compact, @"开票日期[：:]?(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})");
            if (!m.Success) m = Regex.Match(compact, @"(\d{4})年(\d{1,2})月(\d{1,2})日");
            if (!m.Success) m = Regex.Match(compact, @"(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})");
            if (m.Success)
                inv.Date = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            else
                inv.Date = DateTime.Today;

            var bn = Regex.Match(text, @"购买方信息[\s\S]{0,1000}?名称\s*[：:]?\s*([^\r\n]+)");
            if (bn.Success) inv.BuyerName = CleanName(bn.Groups[1].Value);
            if (string.IsNullOrEmpty(inv.BuyerName) || IsHeaderText(inv.BuyerName))
            {
                foreach (Match mm in Regex.Matches(text, @"名称\s*[：:]?\s*([^\r\n]+)"))
                {
                    string v = CleanName(mm.Groups[1].Value);
                    if (v.Length > 0 && !IsHeaderText(v)) { inv.BuyerName = v; break; }
                }
            }

            var sn = Regex.Match(text, @"销售方信息[\s\S]{0,1000}?名称\s*[：:]?\s*([^\r\n]+)");
            if (sn.Success) inv.SellerName = CleanName(sn.Groups[1].Value);
            if (string.IsNullOrEmpty(inv.SellerName) || IsHeaderText(inv.SellerName))
            {
                foreach (Match mm in Regex.Matches(text, @"名称\s*[：:]?\s*([^\r\n]+)"))
                {
                    string v = CleanName(mm.Groups[1].Value);
                    if (v.Length > 0 && !IsHeaderText(v) && v != inv.BuyerName) { inv.SellerName = v; break; }
                }
            }

            var bt = Regex.Match(text, @"购买方信息[\s\S]{0,1000}?(?:统一社会信用代码|纳税人识别号)[\s\S]{0,30}?[：:]\s*([0-9A-Za-z]{15,20})");
            if (bt.Success) inv.BuyerTaxId = bt.Groups[1].Value.ToUpperInvariant();
            if (string.IsNullOrEmpty(inv.BuyerTaxId))
            {
                var bt2 = Regex.Match(text, @"(?:统一社会信用代码|纳税人识别号)[\s\S]{0,30}?[：:]\s*([0-9A-Za-z]{15,20})");
                if (bt2.Success) inv.BuyerTaxId = bt2.Groups[1].Value.ToUpperInvariant();
            }

            var st = Regex.Match(text, @"销售方信息[\s\S]{0,1000}?(?:统一社会信用代码|纳税人识别号)[\s\S]{0,30}?[：:]\s*([0-9A-Za-z]{15,20})");
            if (st.Success) inv.SellerTaxId = st.Groups[1].Value.ToUpperInvariant();
            if (string.IsNullOrEmpty(inv.SellerTaxId) || inv.SellerTaxId == inv.BuyerTaxId)
            {
                foreach (Match mm in Regex.Matches(text, @"(?:统一社会信用代码|纳税人识别号)[\s\S]{0,30}?[：:]\s*([0-9A-Za-z]{15,20})"))
                {
                    string v = mm.Groups[1].Value.ToUpperInvariant();
                    if (v != inv.BuyerTaxId) { inv.SellerTaxId = v; break; }
                }
            }

            decimal rate = 0.06m;
            var rm = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*%");
            if (rm.Success) rate = ParseDec(rm.Groups[1].Value) / 100m;

            decimal total = 0;
            var tm = Regex.Match(text, @"小\s*写[^\d]{0,40}?[¥￥]?\s*(\d[\d,]*(?:\.\d{1,2})?)");
            if (tm.Success) total = ParseDec(tm.Groups[1].Value);
            if (total == 0)
            {
                foreach (Match mm in Regex.Matches(text, @"(?<!\d)(\d{1,8}\.\d{2})(?!\d)"))
                {
                    var v = ParseDec(mm.Groups[1].Value);
                    if (v > total) total = v;
                }
            }

            decimal amount = 0, tax = 0;
            if (total > 0 && rate > 0)
            {
                amount = Math.Round(total / (1 + rate), 2, MidpointRounding.AwayFromZero);
                tax = total - amount;
            }

            inv.Total = total;
            inv.Amount = amount;
            inv.Tax = tax;
            inv.Rate = rate;

            var segs = new List<string>();
            foreach (Match mm in Regex.Matches(text, @"\*([^*\r\n]+)"))
            {
                string s = mm.Groups[1].Value;
                int di = FirstDigit(s);
                if (di >= 0) s = s.Substring(0, di);
                s = s.Trim();
                if (s.Length > 0) segs.Add(s);
            }
            inv.ItemName = string.Join("*", segs);
            inv.Category = GuessCategory(text, inv.ItemName);

            switch (inv.Category)
            {
                case "机票款":
                    {
                        var pax = Regex.Match(text, @"乘机人\s*[：:]\s*([^\s；;，,、\r\n]+)");
                        var fl = Regex.Match(text, @"航班\s*[：:]\s*([^\s；;，,、\r\n]+)");
                        var fno = Regex.Match(text, @"航班号\s*[：:]\s*([A-Za-z0-9]+)");
                        var dep = Regex.Match(text, @"起飞时间\s*[：:]\s*([0-9]{4}-[0-9]{2}-[0-9]{2})");
                        var parts = new List<string>();
                        if (fl.Success) parts.Add(fl.Groups[1].Value.Replace("-", "→"));
                        if (fno.Success) parts.Add("航班" + fno.Groups[1].Value);
                        if (dep.Success) parts.Add(dep.Groups[1].Value + "起飞");
                        if (pax.Success) parts.Add("乘机人：" + pax.Groups[1].Value);
                        inv.Description = string.Join("，", parts);
                        break;
                    }
                case "保险费":
                    {
                        string policy = "";
                        var pol = Regex.Match(text, @"保单号\s*[：:]\s*([A-Za-z0-9]+)");
                        if (pol.Success) policy = pol.Groups[1].Value;
                        else
                        {
                            int ri = text.IndexOf("备注", StringComparison.Ordinal);
                            if (ri >= 0)
                            {
                                string tail = text.Substring(ri, Math.Min(200, text.Length - ri));
                                var pm = Regex.Match(tail, @"([0-9A-Za-z]{15,25})");
                                if (pm.Success) policy = pm.Groups[1].Value;
                            }
                        }
                        string nm = segs.Count > 1 ? segs[1] : inv.ItemName;
                        inv.Description = policy.Length > 0 ? $"{nm}，保单{policy}" : nm;
                        break;
                    }
                case "住宿费":
                    {
                        var stay = Regex.Match(text, @"(\d{1,2}月\d{1,2}日\s*[-—~至]\s*\d{1,2}月\d{1,2}日[^\r\n]*)");
                        string d = stay.Success ? stay.Groups[1].Value : "";
                        d = Regex.Replace(d, @"[，,]\s*共计[^\r\n，,。]*", "");
                        inv.Description = d.Trim();
                        break;
                    }
                default:
                    {
                        var pax = Regex.Match(text, @"乘机人\s*[：:]\s*([^\s；;，,、\r\n]+)");
                        inv.Description = pax.Success ? "乘机人：" + pax.Groups[1].Value : inv.ItemName;
                        break;
                    }
            }

            return inv;
        }

        private static string GuessCategory(string text, string itemName)
        {
            var src = text ?? "";
            if (src.Contains("机票款") || src.Contains("*机票") || src.Contains("航空运输")) return "机票款";
            if (src.Contains("住宿服务") || src.Contains("住宿")) return "住宿费";
            if (src.Contains("火车票") || src.Contains("铁路旅客") || src.Contains("高铁") || src.Contains("动车")) return "火车票";
            if (src.Contains("过路费") || src.Contains("通行费")) return "过路费";
            if (src.Contains("停车费") || src.Contains("停车")) return "停车费";
            if (src.Contains("加油") || src.Contains("成品油") || src.Contains("汽油") || src.Contains("柴油")) return "加油费";
            if (src.Contains("保险")) return "保险费";
            if (src.Contains("代订附加产品")) return "代理服务费";
            if (src.Contains("餐饮") || src.Contains("餐费") || src.Contains("食品")) return "餐饮费";
            if (src.Contains("快递") || src.Contains("物流")) return "快递费";
            if (src.Contains("培训")) return "培训费";
            if (src.Contains("会议")) return "会议费";
            if (!string.IsNullOrEmpty(itemName))
            {
                var parts = itemName.Split('*', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var last = parts[^1].Trim();
                    if (last.Length > 0 && !IsHeaderText(last)) return last;
                }
            }
            return "其他";
        }
    }

    // ==================== PDF 报告排版 ====================
    public class ReportBuilder : IDisposable
    {
        private readonly PdfDocument _doc = new();
        private XGraphics? _gfx;
        private double _y;

        private readonly double _pw = XUnit.FromMillimeter(210).Point;
        private readonly double _ph = XUnit.FromMillimeter(297).Point;
        
        private const double MX = 30;
        private const double MT = 60;
        private const double MB = 80;
        private const double RowH = 24;
        private double ContentW => _pw - 2 * MX;

        private static readonly XColor ColorPrimary = XColor.FromArgb(0x1B, 0x3A, 0x57);
        private static readonly XColor ColorText = XColor.FromArgb(0x1D, 0x1D, 0x1F);
        private static readonly XColor ColorMuted = XColor.FromArgb(0x6E, 0x76, 0x81);
        private static readonly XColor ColorLine = XColor.FromArgb(0xD9, 0xDE, 0xE4);
        private static readonly XColor ColorZebra = XColor.FromArgb(0xF7, 0xF9, 0xFB);
        private static readonly XColor ColorTotalBg = XColor.FromArgb(0xED, 0xF1, 0xF5);

        private static readonly XBrush BrushPrimary = new XSolidBrush(ColorPrimary);
        private static readonly XBrush BrushText = new XSolidBrush(ColorText);
        private static readonly XBrush BrushMuted = new XSolidBrush(ColorMuted);
        private static readonly XBrush BrushZebra = new XSolidBrush(ColorZebra);
        private static readonly XBrush BrushTotalBg = new XSolidBrush(ColorTotalBg);
        private static readonly XPen PenLine = new(ColorLine, 0.5);
        private static readonly XPen PenPrimary = new(ColorPrimary, 1.2);
        private static readonly XPen PenPrimaryThin = new(ColorPrimary, 0.6);

        private static readonly XFont FEnTag = new(FontManager.Family, 7.5, XFontStyleEx.Bold);
        private static readonly XFont FTitle = new(FontManager.Family, 20, XFontStyleEx.Bold);
        private static readonly XFont FSub = new(FontManager.Family, 9, XFontStyleEx.Regular);
        private static readonly XFont FLabel = new(FontManager.Family, 7.5, XFontStyleEx.Regular);
        private static readonly XFont FValue = new(FontManager.Family, 9.5, XFontStyleEx.Regular);
        private static readonly XFont FSection = new(FontManager.Family, 12, XFontStyleEx.Bold);
        private static readonly XFont FHead = new(FontManager.Family, 7.5, XFontStyleEx.Bold);
        private static readonly XFont FCell = new(FontManager.Family, 7.5, XFontStyleEx.Regular);
        private static readonly XFont FTotal = new(FontManager.Family, 8.5, XFontStyleEx.Bold);
        private static readonly XFont FBadge = new(FontManager.Family, 10, XFontStyleEx.Bold);
        private static readonly XFont FFooter = new(FontManager.Family, 7.5, XFontStyleEx.Regular);

        private static readonly XStringFormat FmtNearCenter = new() { Alignment = XStringAlignment.Near, LineAlignment = XLineAlignment.Center };
        private static readonly XStringFormat FmtCenterCenter = new() { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.Center };
        private static readonly XStringFormat FmtFarCenter = new() { Alignment = XStringAlignment.Far, LineAlignment = XLineAlignment.Center };

        public ReportBuilder() { NewPage(); _y = MT; }

        public void NewPage()
        {
            _gfx?.Dispose();
            var page = _doc.AddPage();
            page.Width = XUnit.FromMillimeter(210);
            page.Height = XUnit.FromMillimeter(297);
            _gfx = XGraphics.FromPdfPage(page);
            _y = MT;
        }

        private XGraphics G => _gfx!;

        private void Need(double h) { if (_y + h > _ph - MB) NewPage(); }

        private string FitText(string s, XFont font, double maxW)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (G.MeasureString(s, font).Width <= maxW) return s;
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                var t = s.Substring(0, mid) + "…";
                if (G.MeasureString(t, font).Width <= maxW) lo = mid; else hi = mid - 1;
            }
            return lo > 0 ? s.Substring(0, lo) + "…" : "";
        }

        public static void Build(string outPath, List<Invoice> all)
        {
            using var rb = new ReportBuilder();
            rb.Render(outPath, all);
        }

        private void Render(string outPath, List<Invoice> all)
        {
            var ordered = all.OrderBy(i => i.Category)
                             .ThenBy(i => i.Date)
                             .ThenBy(i => i.Number)
                             .ToList();

            string buyer = all.GroupBy(i => i.BuyerName).OrderByDescending(g => g.Count()).First().Key;
            string buyerTax = all.GroupBy(i => i.BuyerTaxId).OrderByDescending(g => g.Count()).First().Key;
            var minD = all.Min(i => i.Date);
            var maxD = all.Max(i => i.Date);
            decimal tAmt = ordered.Sum(x => x.Amount);
            decimal tTax = ordered.Sum(x => x.Tax);
            decimal tTot = ordered.Sum(x => x.Total);

            DrawCoverHeader(buyer, buyerTax, minD, maxD, ordered.Count, tTot);
            DrawInvoiceTable(ordered, tAmt, tTax, tTot);
            DrawThumbnails(ordered);

            _gfx?.Dispose();
            _gfx = null;
            DrawFooters();
            _doc.Save(outPath);
            _doc.Dispose();
        }

        private void DrawCoverHeader(string buyer, string buyerTax, DateTime minD, DateTime maxD, int count, decimal total)
        {
            _y = MT;
            G.DrawString("EXPENSE SUMMARY  ·  TRAVEL & ENTERTAINMENT", FEnTag, BrushPrimary, new XRect(MX, _y, ContentW, FEnTag.Height), FmtNearCenter);
            _y += FEnTag.Height + 8;
            G.DrawString("差旅费发票汇总表", FTitle, BrushText, new XRect(MX, _y, ContentW, FTitle.Height), FmtNearCenter);
            _y += FTitle.Height + 4;
            G.DrawString("Travel & Expense Invoice Summary", FSub, BrushMuted, new XRect(MX, _y, ContentW, FSub.Height), FmtNearCenter);
            _y += FSub.Height + 10;
            G.DrawLine(PenPrimary, MX, _y, MX + ContentW, _y);
            _y += 18;

            var colW = ContentW / 2;
            DrawMeta(MX, colW, "购买方 / BUYER", buyer);
            DrawMeta(MX + colW, colW, "票据张数 / COUNT", $"{count} 张");
            _y += 40;
            DrawMeta(MX, colW, "纳税人识别号 / TAX ID", string.IsNullOrEmpty(buyerTax) ? "-" : buyerTax);
            DrawMeta(MX + colW, colW, "价税合计 / TOTAL", $"¥ {total:#,##0.00}");
            _y += 40;
            DrawMeta(MX, colW, "开票日期 / PERIOD", $"{minD:yyyy-MM-dd}  至  {maxD:yyyy-MM-dd}");
            DrawMeta(MX + colW, colW, "生成时间 / GENERATED", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            _y += 50;
        }

        private void DrawMeta(double x, double w, string label, string value)
        {
            G.DrawString(label, FLabel, BrushMuted, new XRect(x, _y, w, FLabel.Height), FmtNearCenter);
            G.DrawString(value, FValue, BrushText, new XRect(x, _y + FLabel.Height + 4, w, FValue.Height), FmtNearCenter);
        }

        private void DrawInvoiceTable(List<Invoice> ordered, decimal tAmt, decimal tTax, decimal tTot)
        {
            Need(60);
            G.DrawString("Invoice Detail  /  发票明细", FSection, BrushPrimary, new XRect(MX, _y, ContentW, FSection.Height), FmtNearCenter);
            _y += FSection.Height + 6;
            G.DrawLine(PenPrimaryThin, MX, _y, MX + ContentW, _y);
            _y += 12;

            var widths = new double[] { 24, 160, 110, 55, 60, 55, ContentW - 464 };
            var headers = new[] { "No.", "开票单位（销售方）", "发票号码", "类别", "金额", "税额", "价税合计" };
            var aligns = new[]
            {
                XStringAlignment.Center, XStringAlignment.Near, XStringAlignment.Near,
                XStringAlignment.Near, XStringAlignment.Far, XStringAlignment.Far, XStringAlignment.Far
            };

            DrawTableHeader(headers, widths, aligns);
            for (int i = 0; i < ordered.Count; i++)
            {
                var inv = ordered[i];
                var cells = new[]
                {
                    (i + 1).ToString("00"), inv.SellerName, inv.Number, inv.Category,
                    inv.Amount.ToString("#,##0.00"), inv.Tax.ToString("#,##0.00"), inv.Total.ToString("#,##0.00")
                };
                if (_y + RowH > _ph - MB) { NewPage(); DrawTableHeader(headers, widths, aligns); }
                DrawTableRow(cells, widths, aligns, i % 2 == 1);
            }

            // 分类小计
            var groups = ordered.GroupBy(i => i.Category)
                                .Select(g => new { Category = g.Key, Total = g.Sum(x => x.Total) })
                                .OrderByDescending(g => g.Total)
                                .ToList();

            if (groups.Count > 0)
            {
                Need(RowH * 2);
                G.DrawRectangle(new XSolidBrush(XColor.FromArgb(248, 249, 250)), MX, _y, ContentW, RowH);
                G.DrawLine(PenLine, MX, _y + RowH, MX + ContentW, _y + RowH);

                var summaryText = "  分类小计：" + string.Join("   ·   ", groups.Select(g => $"{g.Category} ¥{g.Total:#,##0.00}"));
                
                var fmt = new XStringFormat { Alignment = XStringAlignment.Near, LineAlignment = XLineAlignment.Center };
                G.DrawString(summaryText, FCell, BrushText, new XRect(MX + 4, _y, ContentW - 8, RowH), fmt);
                _y += RowH;
            }

            if (_y + RowH > _ph - MB) { NewPage(); DrawTableHeader(headers, widths, aligns); }
            
            // 总计行
            DrawTotalRow(new[] { "", "合计  /  TOTAL", $"{ordered.Count} 张", "", tAmt.ToString("#,##0.00"), tTax.ToString("#,##0.00"), tTot.ToString("#,##0.00") }, widths, aligns);
        }

        private void DrawTableHeader(string[] headers, double[] widths, XStringAlignment[] aligns)
        {
            const double h = 22;
            G.DrawRectangle(BrushPrimary, MX, _y, ContentW, h);
            double x = MX;
            for (int i = 0; i < headers.Length; i++)
            {
                var fmt = new XStringFormat { Alignment = aligns[i], LineAlignment = XLineAlignment.Center };
                G.DrawString(headers[i], FHead, XBrushes.White, new XRect(x + 4, _y, widths[i] - 8, h), fmt);
                x += widths[i];
            }
            _y += h;
        }

        private void DrawTableRow(string[] cells, double[] widths, XStringAlignment[] aligns, bool zebra)
        {
            if (zebra) G.DrawRectangle(BrushZebra, MX, _y, ContentW, RowH);
            double x = MX;
            for (int i = 0; i < widths.Length; i++)
            {
                var fmt = new XStringFormat { Alignment = aligns[i], LineAlignment = XLineAlignment.Center };
                var t = FitText(cells[i] ?? "", FCell, widths[i] - 8);
                G.DrawString(t, FCell, BrushText, new XRect(x + 4, _y, widths[i] - 8, RowH), fmt);
                x += widths[i];
            }
            G.DrawLine(PenLine, MX, _y + RowH, MX + ContentW, _y + RowH);
            _y += RowH;
        }

        private void DrawTotalRow(string[] cells, double[] widths, XStringAlignment[] aligns)
        {
            G.DrawRectangle(BrushTotalBg, MX, _y, ContentW, RowH);
            G.DrawLine(PenPrimary, MX, _y, MX + ContentW, _y);
            G.DrawLine(PenPrimary, MX, _y + RowH, MX + ContentW, _y + RowH);
            double x = MX;
            for (int i = 0; i < widths.Length; i++)
            {
                var fmt = new XStringFormat { Alignment = aligns[i], LineAlignment = XLineAlignment.Center };
                G.DrawString(cells[i] ?? "", FTotal, BrushText, new XRect(x + 4, _y, widths[i] - 8, RowH), fmt);
                x += widths[i];
            }
            _y += RowH;
        }

        private void DrawThumbnails(List<Invoice> ordered)
        {
            var cards = new List<(int displayIndex, Invoice inv, SKBitmap img)>();
            
            for (int idx = 0; idx < ordered.Count; idx++)
            {
                var inv = ordered[idx];
                int displayIndex = idx + 1;
                try
                {
                    using var fs = File.OpenRead(inv.FilePath);
                    var images = PDFtoImage.Conversion.ToImages(fs).ToList();
                    
                    if (images.Count > 0)
                    {
                        cards.Add((displayIndex, inv, images[0]));
                    }
                    
                    for (int i = 1; i < images.Count; i++)
                    {
                        images[i].Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [警告] 加载缩略图失败：{inv.Number} - {ex.Message}");
                }
            }

            if (cards.Count == 0) return;

            for (int i = 0; i < cards.Count; i += 2)
            {
                NewPage();
                DrawThumbnailPageHeader();
                DrawSingleCard(cards[i].displayIndex, cards[i].inv, 0, cards[i].img);
                if (i + 1 < cards.Count)
                    DrawSingleCard(cards[i + 1].displayIndex, cards[i + 1].inv, 0, cards[i + 1].img);
            }

            foreach (var c in cards) c.img.Dispose();
        }

        private void DrawThumbnailPageHeader()
        {
            _y = MT;
            G.DrawString("APPENDIX  ·  INVOICE IMAGES", FEnTag, BrushPrimary, new XRect(MX, _y, ContentW, FEnTag.Height), FmtNearCenter);
            _y += FEnTag.Height + 8;
            G.DrawString("发票影像", FTitle, BrushText, new XRect(MX, _y, ContentW, FTitle.Height), FmtNearCenter);
            _y += FTitle.Height + 6;
            G.DrawLine(PenPrimary, MX, _y, MX + ContentW, _y);
            _y += 16;
        }

        private void DrawSingleCard(int displayIndex, Invoice inv, int pageIdx, SKBitmap img)
        {
            const double headerH = 46;
            const double maxImgH = 250;
            const double bottomMargin = 24;
            DrawCardHeader(displayIndex, inv, pageIdx);
            _y += headerH;

            if (img != null && img.Width > 0 && img.Height > 0)
            {
                double ratio = (double)img.Width / img.Height;
                double imgW = ContentW - 40;
                double imgH = imgW / ratio;
                if (imgH > maxImgH) { imgH = maxImgH; imgW = imgH * ratio; }
                double imgX = MX + (ContentW - imgW) / 2;
                using var data = img.Encode(SKEncodedImageFormat.Png, 90);
                using var ms = new MemoryStream(data.ToArray());
                using var xImg = XImage.FromStream(ms);
                
                G.DrawRectangle(XBrushes.White, imgX, _y, imgW, imgH);
                G.DrawRectangle(PenLine, imgX - 0.5, _y - 0.5, imgW + 1, imgH + 1);
                G.DrawImage(xImg, imgX, _y, imgW, imgH);
                _y += imgH + bottomMargin;
            }
        }

        private void DrawCardHeader(int idx, Invoice inv, int pageIdx)
        {
            const double badgeW = 36;
            const double badgeH = 22;
            G.DrawRectangle(BrushPrimary, MX, _y, badgeW, badgeH);
            G.DrawString(idx.ToString("00"), FBadge, XBrushes.White, new XRect(MX, _y, badgeW, badgeH), FmtCenterCenter);
            G.DrawString($"¥ {inv.Total:#,##0.00}", FTotal, BrushPrimary, new XRect(MX + ContentW - 120, _y, 120, badgeH), FmtFarCenter);
            
            string mid = $"发票号 {inv.Number}  ·  {inv.Category}";
            G.DrawString(mid, FHead, BrushPrimary, new XRect(MX + badgeW + 12, _y, ContentW - badgeW - 12 - 130, badgeH), FmtNearCenter);
            G.DrawString(inv.SellerName, FCell, BrushMuted, new XRect(MX, _y + 24, ContentW, 16), FmtNearCenter);
        }

        private void DrawFooters()
        {
            int total = _doc.PageCount;
            for (int i = 0; i < total; i++)
            {
                var page = _doc.Pages[i];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                double y = _ph - 42;
                gfx.DrawLine(PenLine, MX, y, _pw - MX, y);
                gfx.DrawString("差旅费发票汇总  ·  Confidential  ·  内部资料", FFooter, BrushMuted, new XRect(MX, y + 8, ContentW / 2, 12), FmtNearCenter);
                gfx.DrawString($"Page {i + 1} of {total}", FFooter, BrushMuted, new XRect(MX, y + 8, ContentW, 12), FmtFarCenter);
            }
        }

        public void Dispose() { _gfx?.Dispose(); _gfx = null; }
    }
}