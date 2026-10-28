// AiSettings.cs
// AI 服务商目录 + 配置的读取 / 保存（注册表、config.txt）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace InvoiceSummary
{
    // ==================== 服务商目录 ====================
    /// <summary>一个可用的大模型服务商（均为 OpenAI 兼容的 /chat/completions 接口）</summary>
    public sealed class AiProviderInfo
    {
        public string Id { get; }
        public string Display { get; }
        public string ApiBase { get; }
        public string Model { get; }
        /// <summary>去申请密钥的页面，空表示没有（自定义/本地）</summary>
        public string KeyUrl { get; }
        public string Hint { get; }
        /// <summary>本地模型可以不带密钥</summary>
        public bool KeyOptional { get; }

        /// <summary>估算指导价（元 / 每百万 token），仅用于费用预估，非实时计费，请以服务商官方计费为准</summary>
        public decimal InputCostPerM { get; }
        public decimal OutputCostPerM { get; }

        public AiProviderInfo(string id, string display, string apiBase, string model,
                              string keyUrl, string hint, bool keyOptional = false,
                              decimal inputCostPerM = 0, decimal outputCostPerM = 0)
        {
            Id = id;
            Display = display;
            ApiBase = apiBase;
            Model = model;
            KeyUrl = keyUrl;
            Hint = hint;
            KeyOptional = keyOptional;
            InputCostPerM = inputCostPerM;
            OutputCostPerM = outputCostPerM;
        }

        /// <summary>去掉括号/后缀说明的短名字，用于状态栏</summary>
        public string ShortName
        {
            get
            {
                var s = Display;
                int i = s.IndexOf('（');
                if (i > 0) s = s.Substring(0, i);
                i = s.IndexOf('·');
                if (i > 0) s = s.Substring(0, i);
                return s.Trim();
            }
        }
    }

    public static class AiProviders
    {
        public static readonly AiProviderInfo[] All =
        {
            new("deepseek", "DeepSeek 深度求索（推荐）", "https://api.deepseek.com/v1", "deepseek-chat",
                "https://platform.deepseek.com/api_keys",
                "deepseek-chat 便宜、中文强，识别发票完全够用；充值后按量计费。",
                inputCostPerM: 1.0m, outputCostPerM: 2.0m),

            new("qwen", "通义千问 · 阿里云百炼", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus",
                "https://bailian.console.aliyun.com/",
                "百炼控制台 → API-KEY 管理里创建；新用户有免费额度。",
                inputCostPerM: 0.8m, outputCostPerM: 2.0m),

            new("kimi", "Kimi · 月之暗面", "https://api.moonshot.cn/v1", "moonshot-v1-8k",
                "https://platform.moonshot.cn/console/api-keys",
                "moonshot-v1-8k 单价低、长文本友好，适合一页发票。",
                inputCostPerM: 12.0m, outputCostPerM: 12.0m),

            new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash",
                "https://bigmodel.cn/usercenter/apikeys",
                "glm-4-flash 有免费额度，速度较快。",
                inputCostPerM: 0.1m, outputCostPerM: 0.1m),

            new("siliconflow", "硅基流动 SiliconFlow", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-14B-Instruct",
                "https://cloud.siliconflow.cn/account/ak",
                "聚合平台，模型名可换成任意 OpenAI 兼容模型（如 Qwen/Qwen2.5-7B-Instruct）。",
                inputCostPerM: 0.6m, outputCostPerM: 0.6m),

            new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini",
                "https://platform.openai.com/api-keys",
                "国内直连不通，需要自备代理，把上面的接口地址改成中转地址。",
                inputCostPerM: 1.1m, outputCostPerM: 4.3m),

            new("custom", "自定义 / 本地模型（OpenAI 兼容）", "http://127.0.0.1:11434/v1", "qwen2.5:7b",
                "",
                "可填 vLLM、Ollama、One-API 等地址；本地模型不填密钥也能用（接口地址 + /chat/completions）。",
                keyOptional: true, inputCostPerM: 0m, outputCostPerM: 0m),
        };

        public static AiProviderInfo Find(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                var hit = All.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return All[0];
        }

        /// <summary>根据 api_base 反推服务商，用于识别手写的 config.txt / 老版本配置</summary>
        public static string GuessId(string? apiBase)
        {
            if (string.IsNullOrWhiteSpace(apiBase)) return All[0].Id;
            var b = apiBase.TrimEnd('/').ToLowerInvariant();
            foreach (var p in All)
            {
                if (p.Id == "custom") continue;
                if (b == p.ApiBase.TrimEnd('/').ToLowerInvariant()) return p.Id;
            }
            if (b.Contains("deepseek")) return "deepseek";
            if (b.Contains("dashscope") || b.Contains("aliyun")) return "qwen";
            if (b.Contains("moonshot")) return "kimi";
            if (b.Contains("bigmodel") || b.Contains("zhipu")) return "zhipu";
            if (b.Contains("siliconflow")) return "siliconflow";
            if (b.Contains("openai.com")) return "openai";
            return "custom";
        }
    }

    // ==================== 配置对象 ====================
    public sealed class AiConfig
    {
        public string Provider { get; set; } = "deepseek";
        public string ApiBase { get; set; } = "https://api.deepseek.com/v1";
        public string Model { get; set; } = "deepseek-chat";
        public string ApiKey { get; set; } = "";
        public bool Enabled { get; set; } = true;

        /// <summary>密钥的来源：注册表 / config.txt / 内置，仅用于界面提示</summary>
        public string Source { get; set; } = "";

        public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

        public AiProviderInfo ProviderInfo => AiProviders.Find(Provider);

        public AiConfig Clone() => new()
        {
            Provider = Provider,
            ApiBase = ApiBase,
            Model = Model,
            ApiKey = ApiKey,
            Enabled = Enabled,
            Source = Source,
        };
    }

    // ==================== 配置存储 ====================
    public static class AiConfigStore
    {
        public const string RegistryPath = @"Software\InvoiceTool";
        public const string RegistrySubPath = "AI";
        public const string ConfigFileName = "config.txt";

        public static string RegistryFullPath => @"HKEY_CURRENT_USER\" + RegistryPath + @"\" + RegistrySubPath;

        public static string ConfigFile(string baseDir) => Path.Combine(baseDir, ConfigFileName);

        /// <summary>局部配置，只记录文件/注册表里真实出现过的项</summary>
        internal sealed class Partial
        {
            public string? Provider, ApiBase, Model, ApiKey;
            public bool? Enabled;
        }

        // ---------- 合并读取：注册表 > config.txt ----------
        public static AiConfig Resolve(string baseDir)
        {
            var cfg = new AiConfig();
            string? explicitProvider = null;

            if (TryReadFile(baseDir, out var file))
            {
                Apply(cfg, file);
                if (!string.IsNullOrWhiteSpace(file.Provider)) explicitProvider = file.Provider;
                if (!string.IsNullOrWhiteSpace(file.ApiKey)) cfg.Source = ConfigFileName;
            }

            if (TryReadRegistry(out var reg))
            {
                Apply(cfg, reg);
                if (!string.IsNullOrWhiteSpace(reg.Provider)) explicitProvider = reg.Provider;
                if (!string.IsNullOrWhiteSpace(reg.ApiKey)) cfg.Source = "注册表";
            }

            // 老配置只有 api_base 没写 provider 时，按地址猜一个
            cfg.Provider = explicitProvider != null
                ? AiProviders.Find(explicitProvider).Id
                : AiProviders.GuessId(cfg.ApiBase);

            cfg.ApiBase = cfg.ApiBase.TrimEnd('/');
            return cfg;
        }

        private static void Apply(AiConfig cfg, Partial p)
        {
            if (!string.IsNullOrWhiteSpace(p.Provider)) cfg.Provider = p.Provider!;
            if (!string.IsNullOrWhiteSpace(p.ApiBase)) cfg.ApiBase = p.ApiBase!;
            if (!string.IsNullOrWhiteSpace(p.Model)) cfg.Model = p.Model!;
            if (p.Enabled.HasValue) cfg.Enabled = p.Enabled.Value;
            if (!string.IsNullOrWhiteSpace(p.ApiKey)) cfg.ApiKey = p.ApiKey!;
        }

        // ---------- config.txt ----------
        internal static bool TryReadFile(string baseDir, out Partial p)
        {
            p = new Partial();
            try
            {
                var path = ConfigFile(baseDir);
                if (!File.Exists(path)) return false;

                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";") || t.StartsWith("//")) continue;
                    int idx = t.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = t.Substring(0, idx).Trim().ToLowerInvariant().Replace('-', '_');
                    var val = t.Substring(idx + 1).Trim().Trim('"');
                    if (val.Length == 0) continue;

                    switch (key)
                    {
                        case "enable_ai":
                        case "enable":
                        case "enabled":
                            p.Enabled = val.Equals("true", StringComparison.OrdinalIgnoreCase)
                                        || val.Equals("1", StringComparison.Ordinal)
                                        || val.Equals("yes", StringComparison.OrdinalIgnoreCase)
                                        || val.Equals("是", StringComparison.Ordinal);
                            break;
                        case "api_key":
                        case "apikey":
                        case "key":
                        case "token":
                            p.ApiKey = val;
                            break;
                        case "api_base":
                        case "apibase":
                        case "base_url":
                        case "baseurl":
                        case "endpoint":
                            p.ApiBase = val;
                            break;
                        case "model":
                        case "model_name":
                            p.Model = val;
                            break;
                        case "provider":
                        case "vendor":
                            p.Provider = val;
                            break;
                    }
                }
                return true;
            }
            catch
            {
                p = new Partial();
                return false;
            }
        }

        /// <summary>写 config.txt，保留文件里原有的注释和其它行</summary>
        public static void SaveFile(string baseDir, AiConfig cfg)
        {
            var path = ConfigFile(baseDir);
            var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();

            var wanted = new List<KeyValuePair<string, string>>
            {
                new("enable_ai", cfg.Enabled ? "true" : "false"),
                new("provider", cfg.Provider),
                new("api_base", cfg.ApiBase),
                new("model", cfg.Model),
                new("api_key", cfg.ApiKey),
            };

            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                int idx = t.IndexOf('=');
                if (idx <= 0) continue;
                var key = t.Substring(0, idx).Trim();
                foreach (var kv in wanted)
                {
                    if (!string.Equals(key, kv.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    lines[i] = kv.Key + "=" + kv.Value;
                    done.Add(kv.Key);
                }
            }

            if (lines.Count == 0)
                lines.Add("# 发票汇总工具 AI 配置（可手工修改；程序里的「AI 设置」也会写到这个文件）");

            foreach (var kv in wanted)
                if (!done.Contains(kv.Key)) lines.Add(kv.Key + "=" + kv.Value);

            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        // ---------- 注册表 ----------
        internal static bool TryReadRegistry(out Partial p)
        {
            p = new Partial();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath + @"\" + RegistrySubPath, false);
                if (key == null) return false;

                if (key.GetValue("provider") as string is { Length: > 0 } sp) p.Provider = sp;
                if (key.GetValue("api_base") as string is { Length: > 0 } sb) p.ApiBase = sb;
                if (key.GetValue("model") as string is { Length: > 0 } sm) p.Model = sm;

                var rawKey = DecodeKey(key.GetValue("api_key") as string);
                if (rawKey.Length > 0) p.ApiKey = rawKey;

                if (key.GetValue("enable_ai") as string is { Length: > 0 } se)
                    p.Enabled = se.Equals("true", StringComparison.OrdinalIgnoreCase) || se.Equals("1", StringComparison.Ordinal);
                return true;
            }
            catch
            {
                p = new Partial();
                return false;
            }
        }

        public static void SaveRegistry(AiConfig cfg)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath + @"\" + RegistrySubPath, true)
                            ?? throw new InvalidOperationException("无法创建注册表项：" + RegistryFullPath);

            key.SetValue("provider", cfg.Provider, RegistryValueKind.String);
            key.SetValue("api_base", cfg.ApiBase, RegistryValueKind.String);
            key.SetValue("model", cfg.Model, RegistryValueKind.String);
            key.SetValue("enable_ai", cfg.Enabled ? "true" : "false", RegistryValueKind.String);
            key.SetValue("api_key", EncodeKey(cfg.ApiKey), RegistryValueKind.String);
            key.SetValue("saved_at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), RegistryValueKind.String);
        }

        // ---------- 重置 ----------
        /// <summary>清空本机已保存的 AI 配置（注册表 AI 子键 + config.txt 中的 AI 键），用于重新设置</summary>
        public static void ClearAll(string baseDir, out List<string> cleared, out List<string> errors)
        {
            cleared = new List<string>();
            errors = new List<string>();

            // 1) 删除注册表 AI 子键
            try
            {
                using var parent = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
                if (parent != null)
                {
                    parent.DeleteSubKeyTree(RegistrySubPath, throwOnMissingSubKey: false);
                    cleared.Add("注册表");
                }
            }
            catch (Exception ex) { errors.Add("注册表：" + ex.Message); }

            // 2) 从 config.txt 移除 AI 相关键（保留注释和其它行）
            try
            {
                var path = ConfigFile(baseDir);
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path);
                var remove = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "enable_ai","enable","enabled",
                    "provider","vendor",
                    "api_base","apibase","base_url","baseurl","endpoint",
                    "model","model_name",
                    "api_key","apikey","key","token"
                };
                var kept = new List<string>();
                foreach (var line in lines)
                {
                    var t = line.Trim();
                    int idx = t.IndexOf('=');
                    bool drop = idx > 0 && remove.Contains(t.Substring(0, idx).Trim().ToLowerInvariant().Replace('-', '_'));
                    if (!drop) kept.Add(line);
                }
                if (kept.Count != lines.Length)
                {
                    File.WriteAllLines(path, kept, new UTF8Encoding(false));
                    cleared.Add("config.txt");
                }
            }
            catch (Exception ex) { errors.Add("config.txt：" + ex.Message); }
        }

        // 密钥存储：优先用 Windows DPAPI（当前用户）加密为 dp:xxx；旧格式 b64:xxx / 明文仍可读取。
        // DPAPI 的密文与当前 Windows 用户绑定，换用户/换机器后无法解密（会退回重新填写）。
        private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("InvoiceTool.AI.v1");

        private static string EncodeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            try
            {
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), KeyEntropy, DataProtectionScope.CurrentUser);
                return "dp:" + Convert.ToBase64String(bytes);
            }
            catch
            {
                // 兜底：DPAPI 不可用时退回 Base64（仅防一眼看到，不是加密）
                return "b64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
            }
        }

        private static string DecodeKey(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            if (raw.StartsWith("dp:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(raw.Substring(3)), KeyEntropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch { return ""; } // 解密失败（换了用户/机器）按无密钥处理
            }
            if (raw.StartsWith("b64:", StringComparison.OrdinalIgnoreCase))
            {
                try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw.Substring(4))); }
                catch { return ""; }
            }
            return raw.Trim(); // 手工填的明文也认
        }
    }
}
