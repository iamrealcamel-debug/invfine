// AiParser.cs
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace InvoiceSummary
{
    public static class AiParser
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

        private static string _apiKey = "";
        private static string _apiBase = "https://api.deepseek.com/v1";
        private static string _model = "deepseek-chat";
        private static string _provider = "deepseek";
        private static string _source = "";   // 密钥来源：注册表 / config.txt / 内置
        private static bool _enabled;

        // 可选：想给客户内置一个 Key，就把 Key 用 Base64 编码后填到这里；留空 = 由用户自己填写
        private const string BuiltInKeyBase64 = "";

        // 记录 Token 消耗（并发解析时多个工作线程同时累加，用原子操作保证准确）
        private static long _totalPromptTokens;
        private static long _totalCompletionTokens;
        public static long TotalPromptTokens => Volatile.Read(ref _totalPromptTokens);
        public static long TotalCompletionTokens => Volatile.Read(ref _totalCompletionTokens);

        public static bool IsEnabled => _enabled;

        // 当前生效的配置（供界面显示）
        public static string ProviderId => _provider;
        public static string ProviderName => AiProviders.Find(_provider).ShortName;
        public static string ApiBase => _apiBase;
        public static string Model => _model;
        public static string KeySource => _source;

        // 【新增】控制是否在 UI 上显示详细的 AI 交互日志
        public static bool ShowDebugLog { get; set; } = false;

        // UI 传进来的日志打印委托
        public static Action<string>? LogHandler { get; set; }

        /// <summary>读取配置并初始化（每次调用都重新读，改了密钥不用重启程序）</summary>
        public static void Init(string baseDir) => Load(baseDir);

        /// <summary>配置变化后重新加载</summary>
        public static void Reload(string baseDir) => Load(baseDir);

        private static void Load(string baseDir)
        {
            var cfg = AiConfigStore.Resolve(baseDir);

            _provider = cfg.Provider;
            _apiBase = cfg.ApiBase.TrimEnd('/');
            _model = cfg.Model;
            _apiKey = cfg.ApiKey;
            _source = cfg.Source;

            if (string.IsNullOrEmpty(_apiKey))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(BuiltInKeyBase64))
                    {
                        _apiKey = Encoding.UTF8.GetString(Convert.FromBase64String(BuiltInKeyBase64));
                        _source = "内置";
                    }
                }
                catch { _apiKey = ""; }
            }

            if (!cfg.Enabled)
            {
                _enabled = false;
                Log("[AI] 已在设置中关闭，将只使用本地正则识别");
            }
            else if (string.IsNullOrEmpty(_apiKey))
            {
                _enabled = false;
                Log("[AI] 未配置 API Key，将只使用本地正则识别（可点「AI 设置」配置）");
            }
            else
            {
                _enabled = true;
                Log($"[AI] 已启用：{ProviderName} / {_model}（密钥来自{_source}）");
            }
        }

        private static void Log(string msg)
        {
            LogHandler?.Invoke(msg);
        }

        /// <summary>测试 接口地址 + 模型 + 密钥 是否可用（不计入 Token 统计）</summary>
        public static bool TestConnection(string apiBase, string model, string apiKey, out string message)
        {
            message = "";
            try
            {
                var payload = new
                {
                    model = model,
                    messages = new[] { new { role = "user", content = "这是一次连通性测试，请只回复两个字：可用" } },
                    temperature = 0.0,
                    max_tokens = 16
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, apiBase.TrimEnd('/') + "/chat/completions");
                if (!string.IsNullOrWhiteSpace(apiKey))
                    req.Headers.Add("Authorization", "Bearer " + apiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!resp.IsSuccessStatusCode)
                {
                    message = $"HTTP {(int)resp.StatusCode}：{Shorten(body)}";
                    return false;
                }

                var reply = ExtractContent(body);
                message = string.IsNullOrWhiteSpace(reply)
                    ? "连接成功（接口可访问，但模型没有返回内容）"
                    : $"连接成功，模型回复：{Shorten(reply)}";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.GetBaseException().Message;
                return false;
            }
        }

        public static Invoice? TryParse(string text, string filePath, out bool aiSaysInvoice)
        {
            aiSaysInvoice = false;
            if (!_enabled) return null;
            try
            {
                var prompt = BuildPrompt(text);

                if (ShowDebugLog)
                {
                    // 只截取并展示发给 AI 的 PDF 原文（前 120 个字符），把换行压成空格
                    string shortText = text.Length > 120 ? text.Substring(0, 120) + "..." : text;
                    shortText = System.Text.RegularExpressions.Regex.Replace(shortText, @"\s+", " ");
                    Log($"      [PDF原文] {shortText}");
                }

                var reply = CallApi(prompt);
                if (reply == null) return null;

                if (ShowDebugLog)
                {
                    // 把 JSON 里的多余空格去掉，单行展示
                    var singleLine = reply.Replace("\r", "").Replace("\n", " ").Trim();
                    Log($"      [AI 回复] {singleLine}");
                }

                var json = ExtractJson(reply);
                if (json == null) return null;

                return ParseJson(json, filePath, out aiSaysInvoice);
            }
            catch (Exception ex)
            {
                Log($"      [AI] 调用异常，回退正则：{ex.Message}");
                return null;
            }
        }

        private static string BuildPrompt(string text)
        {
            var sb = new StringBuilder();
            sb.AppendLine("你是发票信息提取助手。下面是发票 PDF 提取出的原始文本（可能顺序混乱）。");
            sb.AppendLine("请提取字段并以**严格的单行 JSON** 返回，不要任何解释、不要 Markdown 代码块。");
            sb.AppendLine("字段定义：");
            sb.AppendLine("- is_invoice: 布尔值。这段文本**是否是一张真实的发票**？如果只是报销单、合同、汇总表、请示等，请填 false");
            sb.AppendLine("- number: 发票号码（字符串，没有就空）");
            sb.AppendLine("- date: 开票日期（YYYY-MM-DD）");
            sb.AppendLine("- buyer_name: 购买方名称");
            sb.AppendLine("- buyer_tax_id: 购买方税号");
            sb.AppendLine("- seller_name: 销售方（开票单位）名称");
            sb.AppendLine("- seller_tax_id: 销售方税号");
            sb.AppendLine("- category: 根据发票项目名称和备注，给出简洁的中文费用类别。");
            sb.AppendLine("  示例：住宿费、机票款、火车票、高铁票、过路费、停车费、保险费、代理服务费、餐饮费、办公用品、加油费、培训费、会议费、快递费等。");
            sb.AppendLine("  如果项目名称有多个子项（例如「*生产生活服务*经纪代理服务*机票款」），取最具体的那个（例如「机票款」）。");
            sb.AppendLine("- amount: 不含税金额（数字）");
            sb.AppendLine("- tax: 税额（数字）");
            sb.AppendLine("- total: 价税合计（数字）");
            sb.AppendLine("- description: 行程/入住/保单等备注（字符串，没有就空字符串）");
            sb.AppendLine("金额只取数字，不带 ¥ 或逗号；以票面价税合计（小写）为准。");
            sb.AppendLine("找不到的字符串字段填 \"\"，数字字段填 0。");
            sb.AppendLine("发票原文：");
            sb.AppendLine("<<<");
            sb.AppendLine(text.Length > 6000 ? text.Substring(0, 6000) : text);
            sb.AppendLine(">>>");
            return sb.ToString();
        }

        private static string? CallApi(string prompt)
        {
            var payload = new
            {
                model = _model,
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.0
            };
            var body = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, _apiBase + "/chat/completions");
            req.Headers.Add("Authorization", "Bearer " + _apiKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            var content = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                var preview = Shorten(content, 200);
                Log($"      [AI] HTTP {(int)resp.StatusCode} 错误：{preview}");
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    Log("      [AI] 密钥无效或余额不足，请点「AI 设置」重新填写 API Key");
                return null;
            }

            using var doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt)) Interlocked.Add(ref _totalPromptTokens, pt.GetInt64());
                if (usage.TryGetProperty("completion_tokens", out var ct)) Interlocked.Add(ref _totalCompletionTokens, ct.GetInt64());
            }

            return ExtractContent(content);
        }

        /// <summary>从 /chat/completions 响应里取 choices[0].message.content</summary>
        private static string? ExtractContent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                    return null;
                var msg = choices[0].GetProperty("message");
                if (!msg.TryGetProperty("content", out var contentEl)) return null;
                return contentEl.GetString();
            }
            catch
            {
                return null;
            }
        }

        private static string Shorten(string? s, int max = 120)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static string? ExtractJson(string reply)
        {
            int start = reply.IndexOf('{');
            int end = reply.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            return reply.Substring(start, end - start + 1);
        }

        private static Invoice? ParseJson(string json, string filePath, out bool aiSaysInvoice)
        {
            aiSaysInvoice = false;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("is_invoice", out var isInvEl))
            {
                aiSaysInvoice = isInvEl.ValueKind == JsonValueKind.True
                    || (isInvEl.ValueKind == JsonValueKind.String && isInvEl.GetString()?.ToLower() == "true");
            }

            var inv = new Invoice { FilePath = filePath };
            inv.Number = GetStr(root, "number");
            if (inv.Number.Length == 0) return null;

            var dateStr = GetStr(root, "date");
            if (DateTime.TryParse(dateStr, out var dt)) inv.Date = dt;
            else inv.Date = DateTime.Today;

            inv.BuyerName = GetStr(root, "buyer_name");
            inv.BuyerTaxId = GetStr(root, "buyer_tax_id").ToUpperInvariant();
            inv.SellerName = GetStr(root, "seller_name");
            inv.SellerTaxId = GetStr(root, "seller_tax_id").ToUpperInvariant();
            inv.Description = GetStr(root, "description");

            var cat = GetStr(root, "category").Trim().Trim('*', ' ', '，', '。', '、', ';', '；');
            if (cat.Length == 0) cat = "其他";
            inv.Category = cat;

            inv.Amount = GetDec(root, "amount");
            inv.Tax = GetDec(root, "tax");
            inv.Total = GetDec(root, "total");

            if (inv.Total > 0 && inv.Amount > 0 && inv.Tax > 0
                && Math.Abs(inv.Amount + inv.Tax - inv.Total) > 0.05m)
            {
                var r = inv.Amount > 0 ? inv.Tax / inv.Amount : 0.06m;
                inv.Amount = Math.Round(inv.Total / (1 + r), 2, MidpointRounding.AwayFromZero);
                inv.Tax = inv.Total - inv.Amount;
            }
            else if (inv.Total == 0 && inv.Amount + inv.Tax > 0)
            {
                inv.Total = inv.Amount + inv.Tax;
            }
            else if (inv.Total > 0 && inv.Amount == 0 && inv.Tax == 0)
            {
                inv.Amount = Math.Round(inv.Total / 1.06m, 2, MidpointRounding.AwayFromZero);
                inv.Tax = inv.Total - inv.Amount;
            }

            inv.Rate = inv.Amount > 0 && inv.Tax > 0 ? Math.Round(inv.Tax / inv.Amount, 4) : 0.06m;
            if (inv.BuyerName.Length == 0) inv.BuyerName = "（未知购买方）";
            if (inv.SellerName.Length == 0) inv.SellerName = "（未知销售方）";
            return inv;
        }

        private static string GetStr(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v)) return "";
            if (v.ValueKind == JsonValueKind.String) return (v.GetString() ?? "").Trim();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            return "";
        }

        private static decimal GetDec(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v)) return 0m;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()?.Replace(",", "").Replace("¥", "").Replace("￥", "").Trim() ?? "";
                return decimal.TryParse(s, out var d) ? d : 0m;
            }
            return 0m;
        }
    }
}
