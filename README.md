# CloudLiteIDE

单文件 Web IDE（C++ / Python）最小闭环实现：
- 申请执行器实例（`executorId`）
- 通过 SSE 订阅编译/运行过程与结果
- 提交执行请求
- 执行器续期与自动释放

## 运行

```bash
dotnet run --project /home/runner/work/CloudLiteIDE/CloudLiteIDE/src/api/CloudLiteIDE.Api.csproj
```

打开浏览器访问：`http://localhost:5000` 或控制台显示的地址。

## 配置

通过环境变量或配置文件 `Executor` 节点设置：
- `Executor:Mode`（默认 `Native`，运行期间不动态切换）
- `Executor:LeaseTtlSeconds`
- `Executor:CompileTimeoutSeconds`
- `Executor:RunTimeoutSeconds`
- `Executor:MaxOutputBytes`
- `Executor:MaxMemoryMb`
- `Executor:MaxCpuPercent`

## 目录

- `src/api/`：Minimal API 路由、SSE、续期与调度入口
- `src/agents/`：执行器抽象、原生执行器实现、实例管理与清理
- `src/frontend/`：HTML/CSS/JS + Monaco 编辑器
