// ApiSetupForm.cs
// 「AI 设置」窗口：选择服务商 → 填写密钥 → 测试连接 → 选择保存到注册表和 / 或 config.txt
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace InvoiceSummary
{
    public sealed class ApiSetupForm : Form
    {
        private static readonly Color Primary = Color.FromArgb(27, 58, 87);
        private static readonly Color Muted = Color.FromArgb(110, 118, 129);
        private static readonly Color BgColor = Color.FromArgb(245, 247, 250);
        private static readonly Color TextDark = Color.FromArgb(60, 64, 70);
        private static readonly Color OkColor = Color.FromArgb(22, 130, 71);
        private static readonly Color ErrColor = Color.FromArgb(190, 40, 40);

        private const int CW = 620;   // 客户区宽

        private readonly string _baseDir;
        private readonly bool _firstRun;
        private bool _loading = true;

        private ComboBox? cboProvider;
        private TextBox? txtBase;
        private TextBox? txtModel;
        private TextBox? txtKey;
        private CheckBox? chkShowKey;
        private LinkLabel? lnkGetKey;
        private Label? lblHint;
        private Label? lblTest;
        private Button? btnTest;
        private Button? btnOk;
        private CheckBox? chkRegistry;
        private CheckBox? chkFile;
        private CheckBox? chkEnable;

        /// <summary>保存成功后的配置；null 表示用户取消</summary>
        public AiConfig? Result { get; private set; }

        public ApiSetupForm(AiConfig current, string baseDir, bool firstRun)
        {
            _baseDir = baseDir;
            _firstRun = firstRun;
            InitUI();
            LoadFrom(current);
        }

        // ==================== 界面 ====================
        private void InitUI()
        {
            SuspendLayout();

            Text = _firstRun ? "首次使用 · 配置 AI 识别" : "AI 设置";
            Font = new Font("Microsoft YaHei", 9);
            BackColor = BgColor;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(CW, 552);

            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("InvoiceTool.app.ico");
                if (stream != null) Icon = new Icon(stream);
            }
            catch { }

            // ---- 标题 / 说明 ----
            var lblTitle = new Label
            {
                Text = _firstRun ? "先配置一个 AI，识别会更准" : "AI 识别设置",
                Location = new Point(24, 12),
                AutoSize = true,
                Font = new Font("Microsoft YaHei", 13, FontStyle.Bold),
                ForeColor = Primary
            };

            var lblDesc = new Label
            {
                Text = _firstRun
                    ? "本工具用大模型把发票 PDF 里的文字整理成结构化数据。AI 由客户自己提供：选服务商 → 粘贴自己的 API Key → 保存；密钥只存在本机。"
                    : "换服务商、换密钥或改保存位置，改完点「保存」立即生效，不用重启程序。",
                Location = new Point(24, 46),
                Size = new Size(CW - 48, 44),
                AutoSize = false,
                ForeColor = Muted,
                AutoEllipsis = true
            };

            // ---- 服务商 ----
            var lblProvider = RowLabel("AI 服务商", 100);
            cboProvider = new ComboBox
            {
                Location = new Point(124, 96),
                Size = new Size(CW - 148, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false,
                MaxDropDownItems = 9
            };
            foreach (var p in AiProviders.All) cboProvider.Items.Add(new ProvItem(p));
            cboProvider.SelectedIndexChanged += CboProvider_Changed;

            // ---- 接口地址 ----
            var lblBase = RowLabel("接口地址", 138);
            txtBase = new TextBox { Location = new Point(124, 134), Size = new Size(CW - 148, 26) };

            // ---- 模型 ----
            var lblModel = RowLabel("模型名称", 176);
            txtModel = new TextBox { Location = new Point(124, 172), Size = new Size(CW - 148, 26) };

            // ---- 密钥 ----
            var lblKey = RowLabel("API Key", 214);
            txtKey = new TextBox
            {
                Location = new Point(124, 210),
                Size = new Size(372, 26),
                UseSystemPasswordChar = true
            };
            chkShowKey = new CheckBox
            {
                Text = "显示",
                Location = new Point(506, 213),
                AutoSize = true,
                ForeColor = Muted
            };
            chkShowKey.CheckedChanged += (s, e) =>
            {
                if (txtKey != null) txtKey.UseSystemPasswordChar = !chkShowKey!.Checked;
            };

            lnkGetKey = new LinkLabel
            {
                Text = "",
                Location = new Point(124, 240),
                AutoSize = true,
                LinkColor = Primary,
                ActiveLinkColor = Color.FromArgb(200, 60, 30),
                VisitedLinkColor = Primary
            };
            lnkGetKey.LinkClicked += (s, e) => OpenKeyPage();

            lblHint = new Label
            {
                Location = new Point(24, 264),
                Size = new Size(CW - 48, 34),
                AutoSize = false,
                ForeColor = Muted,
                Font = new Font("Microsoft YaHei", 8.5f),
                AutoEllipsis = true
            };

            // ---- 保存位置 ----
            var grpSave = new GroupBox
            {
                Text = "保存位置（至少勾选一项）",
                Location = new Point(24, 304),
                Size = new Size(CW - 48, 104),
                ForeColor = TextDark
            };

            chkRegistry = new CheckBox
            {
                Text = "保存到注册表（只对当前 Windows 用户生效）",
                Location = new Point(16, 22),
                AutoSize = true,
                Checked = true
            };
            var lblRegPath = new Label
            {
                Text = AiConfigStore.RegistryFullPath,
                Location = new Point(34, 43),
                Size = new Size(CW - 48 - 52, 16),
                AutoSize = false,
                AutoEllipsis = true,
                ForeColor = Muted,
                Font = new Font("Consolas", 8f)
            };

            chkFile = new CheckBox
            {
                Text = "保存到 config.txt（程序目录，方便备份 / 拷给别人）",
                Location = new Point(16, 62),
                AutoSize = true
            };
            var lblPath = new Label
            {
                Text = ShortPath(AiConfigStore.ConfigFile(_baseDir), 56),
                Location = new Point(34, 83),
                Size = new Size(CW - 48 - 52, 16),
                AutoSize = false,
                AutoEllipsis = true,
                ForeColor = Muted,
                Font = new Font("Consolas", 8f)
            };

            grpSave.Controls.Add(chkRegistry);
            grpSave.Controls.Add(lblRegPath);
            grpSave.Controls.Add(chkFile);
            grpSave.Controls.Add(lblPath);

            // ---- 启用开关 ----
            chkEnable = new CheckBox
            {
                Text = "启用 AI 识别（取消勾选 = 只用本地正则，不联网、不花钱）",
                Location = new Point(24, 418),
                AutoSize = true,
                Checked = true
            };

            // ---- 测试 + 按钮 ----
            btnTest = new Button
            {
                Text = "测试连接",
                Location = new Point(24, 448),
                Size = new Size(110, 30),
                BackColor = Color.White
            };
            btnTest.Click += BtnTest_Click;

            lblTest = new Label
            {
                Text = "",
                Location = new Point(148, 452),
                Size = new Size(CW - 148 - 24, 44),
                AutoSize = false,
                ForeColor = Muted,
                AutoEllipsis = true
            };

            btnOk = new Button
            {
                Text = "保存",
                Location = new Point(378, 500),
                Size = new Size(102, 34),
                BackColor = Primary,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 10, FontStyle.Bold)
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += BtnOk_Click;

            var btnCancel = new Button
            {
                Text = _firstRun ? "稍后再说" : "取消",
                Location = new Point(490, 500),
                Size = new Size(106, 34),
                BackColor = Color.White
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.Add(lblTitle);
            Controls.Add(lblDesc);
            Controls.Add(lblProvider);
            Controls.Add(cboProvider);
            Controls.Add(lblBase);
            Controls.Add(txtBase);
            Controls.Add(lblModel);
            Controls.Add(txtModel);
            Controls.Add(lblKey);
            Controls.Add(txtKey);
            Controls.Add(chkShowKey);
            Controls.Add(lnkGetKey);
            Controls.Add(lblHint);
            Controls.Add(grpSave);
            Controls.Add(chkEnable);
            Controls.Add(btnTest);
            Controls.Add(lblTest);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            ResumeLayout(false);
        }

        private static Label RowLabel(string text, int y) => new()
        {
            Text = text,
            Location = new Point(24, y + 4),
            AutoSize = true,
            ForeColor = TextDark
        };

        /// <summary>路径太长时保留末尾，前面用 … 省略</summary>
        private static string ShortPath(string path, int maxChars)
            => path.Length <= maxChars ? path : "…" + path.Substring(path.Length - maxChars + 1);

        private sealed class ProvItem
        {
            public AiProviderInfo Info { get; }
            public ProvItem(AiProviderInfo info) => Info = info;
            public override string ToString() => Info.Display;
        }

        // ==================== 数据进出 ====================
        private void LoadFrom(AiConfig cfg)
        {
            _loading = true;
            int idx = 0;
            for (int i = 0; i < cboProvider!.Items.Count; i++)
            {
                if (cboProvider.Items[i] is ProvItem pi &&
                    string.Equals(pi.Info.Id, cfg.Provider, StringComparison.OrdinalIgnoreCase))
                { idx = i; break; }
            }
            cboProvider.SelectedIndex = idx;
            _loading = false;

            txtBase!.Text = cfg.ApiBase;
            txtModel!.Text = cfg.Model;
            txtKey!.Text = cfg.ApiKey;
            chkEnable!.Checked = cfg.Enabled;

            // 注册表默认勾上；如果原来就是从 config.txt 读的，再把文件也勾上
            chkRegistry!.Checked = true;
            chkFile!.Checked = cfg.Source == AiConfigStore.ConfigFileName;

            UpdateProviderUi();

            if (cfg.HasKey)
                SetTest(null, $"已读取现有配置（密钥来自{cfg.Source}），可直接改完保存，或先点「测试连接」验证。");
        }

        private void CboProvider_Changed(object? sender, EventArgs e)
        {
            if (_loading) return;
            if (cboProvider?.SelectedItem is not ProvItem it) return;

            var p = it.Info;
            txtBase!.Text = p.ApiBase;
            txtModel!.Text = p.Model;
            txtKey!.Text = "";          // 换了服务商，旧密钥一般不通用
            UpdateProviderUi();
            SetTest(null, p.KeyOptional
                ? "本地模型可以不填密钥，直接点「测试连接」试试。"
                : "选择服务商后会自动填好接口地址和模型，只需要粘贴自己的 API Key。");
        }

        private void UpdateProviderUi()
        {
            if (cboProvider?.SelectedItem is not ProvItem it) return;
            var p = it.Info;
            lblHint!.Text = p.Hint;

            bool hasPage = !string.IsNullOrEmpty(p.KeyUrl);
            lnkGetKey!.Visible = hasPage;
            lnkGetKey.Text = hasPage ? $"没有密钥？点此打开 {p.ShortName} 的密钥申请页 →" : "";
        }

        private void OpenKeyPage()
        {
            if (cboProvider?.SelectedItem is not ProvItem it) return;
            var url = it.Info.KeyUrl;
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开浏览器失败，请手动访问：\n" + url + "\n\n" + ex.Message,
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void SetTest(bool? ok, string message)
        {
            if (lblTest == null) return;
            lblTest.Text = message;
            lblTest.ForeColor = ok == true ? OkColor : ok == false ? ErrColor : Muted;
        }

        // ==================== 校验 / 保存 ====================
        private bool ValidateForm(out AiConfig cfg, out string error)
        {
            var prov = (cboProvider?.SelectedItem as ProvItem)?.Info ?? AiProviders.All[0];
            var apiBase = txtBase?.Text.Trim() ?? "";
            var model = txtModel?.Text.Trim() ?? "";
            var key = txtKey?.Text.Trim() ?? "";

            cfg = new AiConfig();
            error = "";

            if (apiBase.Length == 0 ||
                !Uri.TryCreate(apiBase, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "接口地址不正确，应形如 https://api.deepseek.com/v1";
                return false;
            }

            if (model.Length == 0)
            {
                error = "请填写模型名称，例如 deepseek-chat";
                return false;
            }

            if (key.Length == 0 && !prov.KeyOptional)
            {
                error = $"请填写 {prov.ShortName} 的 API Key";
                return false;
            }

            cfg.Provider = prov.Id;
            cfg.ApiBase = apiBase.TrimEnd('/');
            cfg.Model = model;
            cfg.ApiKey = key;
            cfg.Enabled = chkEnable?.Checked ?? true;
            return true;
        }

        private async void BtnTest_Click(object? sender, EventArgs e)
        {
            if (!ValidateForm(out var cfg, out var error))
            {
                SetTest(false, error);
                return;
            }

            btnTest!.Enabled = false;
            btnOk!.Enabled = false;
            SetTest(null, "正在连接，请稍候…（最多 30 秒）");

            var baseUrl = cfg.ApiBase;
            var model = cfg.Model;
            var key = cfg.ApiKey;

            var (ok, message) = await Task.Run(() =>
            {
                var r = AiParser.TestConnection(baseUrl, model, key, out var m);
                return (r, m);
            });

            if (IsDisposed) return;
            btnTest.Enabled = true;
            btnOk.Enabled = true;
            SetTest(ok, message);
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            if (!ValidateForm(out var cfg, out var error))
            {
                MessageBox.Show(this, error, "请检查", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool toRegistry = chkRegistry?.Checked ?? false;
            bool toFile = chkFile?.Checked ?? false;

            if (!toRegistry && !toFile)
            {
                MessageBox.Show(this, "请至少勾选一个保存位置（注册表或 config.txt）。", "请检查",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var errors = new List<string>();
            var savedTo = new List<string>();

            if (toRegistry)
            {
                try
                {
                    AiConfigStore.SaveRegistry(cfg);
                    savedTo.Add("注册表");
                }
                catch (Exception ex)
                {
                    errors.Add("写入注册表失败：" + ex.Message);
                }
            }

            if (toFile)
            {
                try
                {
                    AiConfigStore.SaveFile(_baseDir, cfg);
                    savedTo.Add(AiConfigStore.ConfigFile(_baseDir));
                }
                catch (Exception ex)
                {
                    errors.Add("写入 config.txt 失败：" + ex.Message +
                               "\n（程序目录没有写权限时，可只勾选注册表）");
                }
            }

            if (savedTo.Count == 0)
            {
                MessageBox.Show(this, string.Join("\n\n", errors), "保存失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            cfg.Source = toRegistry ? "注册表" : AiConfigStore.ConfigFileName;
            Result = cfg;

            if (errors.Count > 0)
            {
                MessageBox.Show(this,
                    "已保存到：" + string.Join("、", savedTo) + "\n\n" + string.Join("\n\n", errors),
                    "部分保存成功", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
