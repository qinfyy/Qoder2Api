# Qoder2Api

把 Qoder 的后端接口包装成 **OpenAI 兼容 API** 的本地代理服务。

自带多账号号池调度、上游模型排队处理、Credits 查询，以及一个 Web 管理面板。
任何支持 OpenAI 协议的客户端（Claude Code、Codex 等）填上地址即可使用。

---

## 特性

**OpenAI 兼容层**
- Chat Completions 推理 API，流式 / 非流式都支持
- Models 模型发现

**多账号号池**（`QoderPool`）
- 加权抽签 + 短名单打散热点，避免流量集中在少数账号
- 分级惩罚：软冷却指数退避、连续 5xx 熔断、未知错误降权、会话连续失效判死号
- 单账号在途请求上限、在途租约兜底回收（防漏释放导致计数泄漏）

**模型排队**（`QoderQueue`）
- 完美支持 Qoder 的模型排队，轮询排队状态
- 排队期间向下游发 SSE 保活注释，避免长静默把下游读超时拖断
- 等待预算、轮询间隔、失败上限都可配

**模型目录**（`models.xml`）
- 人工维护 `displayName` / 描述；上游目录提供倍率、是否免费、错峰折扣
- 后台每 2 分钟同步，**只刷新已有模型不新增**
- 管理员手动同步时才会新增缺失模型，用于首次生成

**Credits 与活动**
- 余额（套餐内 / 资源包）、近一年消耗、日峰值、连续 / 累计活跃天数
- 每日福利一键领取
**管理面板**
- 账户管理、API Key、用量记录、系统设置
- OAuth 设备码 / PAT / Cockpit Tools 三种方式添加账号，支持多账号切换与解冻

---

## 快速开始

**环境要求**：.NET 10 SDK

```bash
git clone https://github.com/qinfyy/Qoder2Api.git
cd Qoder2Api
dotnet run --project Qoder2Api
```

打开 http://localhost:5000 ，点「新增账户」通过 OAuth 或 PAT 连接一个 Qoder 账号。
之后把下游客户端的 Base URL 指向 `http://localhost:5000/v1` 即可。

想快速验证：

```bash
curl http://localhost:5166/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer sk-any-key" \
  -d '{"model":"auto","stream":true,"messages":[{"role":"user","content":"Hi"}]}'
```

> 免密模式下 API Key 可填任意字符串；开启鉴权后需填「API Key」页面里已启用的 Key。

---

## 配置

配置文件在运行目录下，启动时读取，改完重启生效。

### `appsettings.xml`

| 段 | 说明 |
|---|---|
| `<Server>` | `<Urls>` 监听地址；`<DatabasePath>` SQLite 文件路径（默认 `save/SaveData.db`） |
| `<Pool>` | 号池参数：换号次数、熔断阈值、冷却时长、选号权重、落盘周期 |
| `<Queue>` | 排队参数：等待预算、轮询间隔、失败上限、SSE 保活间隔 |
| `<Refresh>` | 后台刷新周期。`<ModelCatalog>` 默认 2 分钟，设 0 关闭 |

**监听地址的优先级**：命令行 `urls` 参数 / `ASPNETCORE_URLS` 环境变量 > `appsettings.xml`。
Development 环境下由 `launchSettings.json` 决定（`dotnet run` 走 5166），配置文件里的值不生效。


- `key`：上游 Qoder 的模型标识，发给上游的 `model_config.key` / `X-Model-Key`
- `displayName`：**下游可请求的模型名**，同时也是发给上游的 `model_config.display_name`。
  实测官方客户端发的就是厂商官方名（如 `Qwen3.8-Flash`），所以这里用官方名；
  无官方名的档位模型（`auto` / `ultimate` / …）用上游 key
- 匹配规则见 `QoderConstants.ResolveModel`：`key` / `displayName` / `alias` 精确命中（大小写不敏感），
  命中不到直接 404，**不做任何猜测性兜底**
- `priceFactor` / `isFree` / `promotionLabel` 由上游同步写入，手工改会被下次同步覆盖

---

## API

### 下游接口（OpenAI 兼容）

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/v1/chat/completions` | 对话补全，支持 `stream` |
| GET | `/v1/models` | 模型列表 |

鉴权：`Authorization: Bearer <key>` 或 `x-api-key: <key>`。是否强制由「系统设置」页的开关决定。


---

### 请求流程

```
下游 POST /v1/chat/completions
  → 校验 API Key
  → 模型名精确匹配 models.xml（命中不到直接 404）
  → 号池租一个账号（加权抽签）
  → 取凭证 → COSY 签名 → 编码请求体 → 发上游
  → 上游返回「排队中」？
       是 → 轮询排队状态（期间发 SSE 保活），就绪后在原账号重试
       否 → 拿到首包
  → 首包正常 → 流式转发 / 聚合成 JSON
  → 账号级错误 → 换号重试（最多 MaxRotate 次）
  → 请求级错误（内容拦截 / 上下文超长）→ 换号也没用，直接透传上游原文
```

---

## 免责声明

1. 本项目仅供学习与个人使用，与 Qoder 官方无任何关联。
2. 使用本软件产生的账号风险由使用者自行承担。
3. 请遵守目标服务的使用条款。

## 许可

许可信息见 [LICENSE.txt](LICENSE.txt)。
