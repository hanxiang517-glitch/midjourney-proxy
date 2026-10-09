# Discord 可靠性改造（第一批）

基于上游 `0f87175e28fbcd0d9c4459dd03245b87797f1ca8`。
本分支改善错误处理，不模拟真人，也不保证账号不被限制。

## 已实现

- 同一 DiscordService 实例的 interaction HTTP 提交串行执行；等待 429 冷却期间，后续提交不能插队。图片生成本身仍由原来的队列管理。
- 仅明确的 HTTP 429 可以重试，最多发送 3 次。取 `Retry-After`（秒或 HTTP 日期）与 JSON `retry_after` 的较大值，支持小数秒，至少等待 1 秒。
- 等待时间缺失、超过 5 分钟、连续限流或冷却被中断时暂停账号，不缩短服务端要求的等待时间。
- HTTP 401/403、验证响应和不可重连的 Gateway 关闭码暂停账号，禁止这条失败路径触发自动登录。
- 网络异常、超时、HTTP 408/5xx 视为提交结果未知：暂停并要求人工核对，不自动重发。
- 不再在 404 后猜测其他 message_id 并重放请求；原有任务可能需要人工检查消息。
- Gateway 恢复和重新连接共用按账号的进程内锁，重连指数退避（2、4、8、16、32 秒，函数上限 60 秒），保留上游 5 分钟内最多 5 次的限制。
- 收到 `Action required to continue` 验证弹窗后停用账号，不再调用自动验证服务。
- 新的 interaction 失败日志不输出提示词、用户 Token、完整请求或验证码响应。

## 人工恢复

1. 在 Discord / Midjourney 官方客户端检查账号状态和已生成消息，完成必要的验证或修正配置。
2. 对“提交结果未知”的任务逐一核对，确认没有成功出图后再决定是否新建任务。不要批量重放旧队列。
3. 在管理后台重新启用账号，并重建该账号实例；最明确的恢复方式是检查队列后重启服务。

## 验证

需要 .NET 8 SDK：

```sh
dotnet test src/Midjourney.Transport.Tests/Midjourney.Transport.Tests.csproj
dotnet build src/Midjourney.API/Midjourney.API.csproj
```

测试通过模拟响应覆盖：小数/日期限流、冲突等待值、并发冷却、连续限流、鉴权失败、验证要求、超时与网络异常、取消、404、终止关闭码与退避上限。
独立测试直接链接生产 transport 源码，不加载数据库、Redis、授权 DLL，也不访问真实 Discord。

## 当前边界

- 暂停比自动恢复更保守：暂时的 403/5xx 也可能需要人工恢复，这是有意的可用性取舍。
- 串行控制仅覆盖一个服务实例的 `/interactions` 提交，不包含所有附件、消息端点，不提供跨进程或多账号的全局限流协调。首轮验证只应使用单服务实例、单账号配置。
- 尚未实现持久化任务幂等账本；服务崩溃后以及调用方重新创建任务时的完整去重仍需后续改造。本批只保证此 HTTP 提交器不会在结果未知时重放。
- 上游仍包含其他自动化行为和自动登录入口，本批没有完成整个项目的行为审计。部署前应关闭自动登录/自动验证相关配置。
- 上游依赖 `src/lib/Midjourney.License.dll`，这部分没有随仓库提供源码；本批未修改或绕过它。
- 构建存在上游 MailKit、MimeKit、ImageSharp 依赖漏洞告警，以及既有编译警告，需要在部署前单独评估升级。
- 尚未部署，未做真实账号登录、提交或扣费测试；本地编译成功不等于生产运行已验证。

参考：[Discord 限流](https://docs.discord.com/developers/topics/rate-limits)、[Gateway 状态码](https://docs.discord.com/developers/topics/opcodes-and-status-codes)。
