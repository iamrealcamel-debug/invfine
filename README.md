# 差旅费发票汇总工具（InvoiceTool）

把一整个文件夹里的发票 PDF 批量识别 → 分类 → 生成一份带发票影像的汇总 PDF。

识别优先调用大模型（AI），AI 不可用时自动回退到本地正则，不影响基本使用。

---

## 一、首次使用：AI 由客户自己配置

程序**第一次打开时会自动弹出配置窗口**，需要三步：

1. **选 AI 服务商**（下拉框，已内置 DeepSeek / 通义千问 / Kimi / 智谱 / 硅基流动 / OpenAI / 自定义本地模型）
   选定后接口地址、模型名称会自动填好。
2. **粘贴自己的 API Key**
   点窗口里的「没有密钥？点此打开 … 密钥申请页 →」会打开对应官网，登录后创建 Key 再复制回来。
3. **选保存位置**（可都勾）：
   - `保存到注册表`：只对当前 Windows 用户生效，换用户登录需要重新配置；
   - `保存到 config.txt`：写在程序目录，方便备份、拷给同事，但程序目录没有写权限时会失败（例如装在 `Program Files`）。

点「测试连接」可以当场验证 Key 和模型是否可用（会真实发一次极小的请求）。配置完点「保存」立即生效，**不用重启程序**。

以后再想改：主界面点「AI 设置」。

> 密钥只保存在本机，除了你选定的 AI 服务商，不会发给任何第三方；也不会写进生成的汇总 PDF。

---

## 二、配置的存放位置与优先级

读取顺序（**前者覆盖后者**）：

| 顺序 | 位置 | 说明 |
| --- | --- | --- |
| 1 | 注册表 `HKEY_CURRENT_USER\Software\InvoiceTool\AI` | 值：`provider` `api_base` `model` `enable_ai` `api_key` `saved_at`；`api_key` 存成 `b64:xxx`（Base64，只是避免一眼看到，不是加密） |
| 2 | 程序目录 `config.txt` | 也可以手工编辑，见下 |
| 3 | 程序内置密钥 | `AiParser.cs` 里的 `BuiltInKeyBase64`，**默认留空**（即强制由用户自己填） |

### config.txt 示例

```ini
# 发票汇总工具 AI 配置，可手工修改
enable_ai=true
provider=deepseek
api_base=https://api.deepseek.com/v1
model=deepseek-chat
api_key=sk-你的密钥
```

- `enable_ai=false` 表示只用本地正则（不联网、不花钱）。
- 只写 `api_key` / `api_base` / `model` 也能用（老版本格式），程序会根据 `api_base` 自动判断服务商。
- 通过窗口保存时会**保留文件里原有的注释和其它行**，只更新上面这几个键。

### 各服务商默认参数

| 服务商 | 接口地址 | 默认模型 |
| --- | --- | --- |
| DeepSeek（推荐） | `https://api.deepseek.com/v1` | `deepseek-chat` |
| 通义千问（阿里云百炼） | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-plus` |
| Kimi（月之暗面） | `https://api.moonshot.cn/v1` | `moonshot-v1-8k` |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-4-flash` |
| 硅基流动 | `https://api.siliconflow.cn/v1` | `Qwen/Qwen2.5-14B-Instruct` |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` |
| 自定义 / 本地 | `http://127.0.0.1:11434/v1`（Ollama 示例） | 自填 |

只要兼容 OpenAI 的 `/chat/completions` 接口，都可以选「自定义」填进去（vLLM、One-API、中转站等）。本地模型可以不填密钥。

---

## 三、日常使用

1. 选择（或拖入）存放发票 PDF 的文件夹；
2. 点「开始生成汇总 PDF」；
3. 日志里会逐张显示识别结果、本次 Token 消耗与预估费用；
4. 完成后在同目录生成 `发票汇总_HHmmss.pdf`（明细表 + 每张发票影像）。

底部状态栏始终显示当前用的是哪个 AI、密钥来自哪里。

「显示 AI 交互」勾上后，日志里会打印发给大模型的 PDF 原文片段和模型原始回复，排查识别错误时用。

---

## 四、常见问题

**Q：日志提示 `HTTP 401` / 密钥无效？**
Key 填错、过期或余额不足。点「AI 设置」重新粘贴，或先去对应官网控制台确认余额。

**Q：点了保存，却提示「写入 config.txt 失败」？**
程序装在 `Program Files` 之类的受保护目录，普通用户没有写权限。只勾「保存到注册表」即可，或把程序放到 `D:\InvoiceTool` 这类普通目录。

**Q：换了台电脑 / 换 Windows 用户，还要重新填吗？**
注册表配置跟着 Windows 用户走，换用户要重填；想带着走就勾「保存到 config.txt」，把程序和 `config.txt` 一起拷过去。

**Q：能不能不用 AI？**
可以。配置窗口里取消勾选「启用 AI 识别」，或 config.txt 写 `enable_ai=false`。此时只用本地正则，准确率略低但不联网、不花钱。

**Q：选哪个服务商最划算？**
识别一张发票的提示词约 1–2 千 Token，用 `deepseek-chat` 一张成本约 1–3 分钱，够用且中文稳定；有免费额度的话智谱 `glm-4-flash`、硅基流动更省。

**Q：想给客户内置一个 Key，不让客户自己填？**
把 Key 用 Base64 编码后填到 `AiParser.cs` 的 `BuiltInKeyBase64`。此时客户首次打开不会强制弹窗（已有内置 Key），但内置 Key 是明文可被提取的，注意限额。

---

## 五、版本

- **v1.4**：AI 密钥改为由客户自己配置（首次打开引导窗口）；新增服务商选择、测试连接、保存到注册表 / config.txt 的可选项；主界面新增「AI 设置」入口与底部状态栏；修复旧版把密钥硬编码在程序里的问题。
- v1.3：GUI 版；AI 识别 + 正则回退；汇总 PDF 带发票影像。
