# ModernWebServer

基于 .NET 8 的轻量级自托管 Web 服务组件。  
适合 WinForms/WPF/控制台运维工具集成，支持RESTful API、WebSocket、SwaggerUI（本地离线）、静态资源、安全中间件与跨平台部署。  
前后端完全解耦，前端界面热插拔，便于现代化、开箱即用的本地或企业级微服务开发。

---

## 特点

- ✅ 纯C#代码，一行即可启动完整Web服务
- ✅ 支持RESTful自定义API路由注册
- ✅ 多WebSocket路由&群发
- ✅ 全静态资源托管 (html/js/css/img)
- ✅ 支持本地Swagger UI离线文档
- ✅ 文件下载（断点续传）、文件上传
- ✅ CORS（跨域）、Token等中间件扩展
- ✅ 支持WinForms/WPF/Console多端后台嵌入
- ✅ 全部UI可html/js热更新，无需重编译
- ✅ 日志回调可集成桌面控件
- ✅ 可拓展上传多文件/多语言/i18n等

---

## 目录结构建议

```text
项目目录
│─ ModernWebServer.cs
│─ Program.cs / Form1.cs
│
└─ D:/static/
   ├─ ui.html               # 业务/测试主页，接口可视化调试
   ├─ swagger.html          # 本地离线Swagger UI
   ├─ swagger.json          # OpenAPI文档
   ├─ swagger/
   │    ├─ swagger-ui-bundle.js
   │    ├─ swagger-ui-standalone-preset.js
   │    ├─ swagger-ui.css
   │    └─ ...
   ├─ jquery.min.js
   ├─ bootstrap.min.css
   └─ 其它自定义资源...
```

---

## 快速开始

### 1. 环境要求

- Windows 10+/Linux/Mac，.NET 8+ SDK
- 推荐 Visual Studio 2022+/Rider/VSCode
- WinForms 应用配置 csproj（非 Web 模板须手动加 FrameworkReference）：

```xml
<PropertyGroup>
  <TargetFramework>net8.0-windows</TargetFramework>
  <UseWindowsForms>true</UseWindowsForms>
</PropertyGroup>
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>
```

### 2. 本地运行

- 拷贝 ModernWebServer.cs 到项目
- 建立静态目录 D:/static，放置 ui.html、swagger.html、swagger.json 及相关前端资源
- WinForms 集成参考：

```csharp
using WebServer;
public partial class Form1 : Form
{
    ModernWebServer? server;

    public Form1()
    {
        InitializeComponent();
        server = new ModernWebServer(
            8080, 8443, null, null,
            s=>this.Invoke(() => richTextBox1.AppendText(s+"\n")) // 日志输出
        ).SetStaticFolder(@"D:/static").WithCors();
        // 可注册API/WS路由等
        server.RegisterApi("/api/hello", async (ctx, par) => await ctx.Response.WriteAsync("{\"msg\":\"Hello!\"}"));
        server.RegisterWs("/ws1", async (ws, ctx) => { /* 省略 */ });
        server.Start();
        this.FormClosing += (s, e) => server?.Stop();
    }
}
```

---

## 主要实现要点

- 基于 `WebApplication`，直接在 WinForms 自实例启动
- 注册静态路由（/ui, /swagger, /swagger.json）均从 D:/static 内加载文件
- API/WS均可链式注册，参数自动匹配
- 内置断点下载、基础上传接口范例
- 使用 ConcurrentDictionary 管理多WS路由群发
- 可选 Token 认证、中间件、跨域扩展
- 日志输出可回调至任何委托（界面、多线程安全）

---

## HTTP API & WebSocket接口一览

### 1. 静态页面/文档

- `GET /ui`  
  响应：ui.html页面，集成API/WS调试工具和业务演示  
- `GET /swagger`
  响应：本地swagger UI页面（需 swagger.html +本地js/css资源）

- `GET /swagger.json`
  响应：OpenAPI接口描述json文件

### 2. RESTful API示例

#### 【GET】 `/api/hello`  
响应示例：  
```json
{"code":200,"msg":"Hello API!","time":"2024-06-14T12:30:00"}
```

#### 【GET】 `/api/time`
```json
{"time":"2024-06-14T12:30:00"}
```

#### 【GET】 `/api/echo/{val}`
参数说明：  
- `val` 路径参数，要回显的字符串
响应：  
```json
{"echo":"xxx"}
```

#### 【GET】 `/api/user/{id}`
参数：id (string)  
响应：  
```json
{"userId":"001","name":"测试用户"}
```

#### 【GET】【支持断点】 `/api/download/{filename}`
参数：filename 静态目录下任意文件名  
说明：支持 Range 头断点续传下载，二进制流返回。

#### 【POST】 `/api/upload/{filename}`
参数：
- `filename` 路径参数，目标保存文件名
- body: 文件流原样上传
说明：保存文件于静态目录下；无多文件/表单，仅单文件流。

响应：
```json
{"code":200,"msg":"上传完成","filename":"file.txt"}
```

### 3. WebSocket 路由

#### `ws://localhost:8080/ws1`
- 可双工通讯，全部连接可用服务端Broadcast群发
- DEMO：任何连接发送 `{"cmd":"broadcast", "text":"消息"}`，所有连接皆会收到群发

---

## 服务端功能调用示例

### 主动群发（Broadcast）

```csharp
// 群发消息给 /ws1 路由下所有 WebSocket 客户端
await server.Broadcast("/ws1", "系统公告：上线时间"+DateTime.Now);
```

### Token中间件校验

```csharp
server.WithTokenAuth(ctx =>
{
    var token = ctx.Request.Headers["Authorization"];
    return Task.FromResult(!string.IsNullOrEmpty(token) && token == "Bearer 123456");
});
```

---

## 静态资源维护说明

- **更换前端界面（ui.html、swagger.html）时只需覆盖文件，无需重启主程序**
- **swagger.json 可用 Swagger Editor、Apifox 等设计导出**
- **后台记录所有上传文件于静态目录，建议定期清理**
- **前端依赖建议本地托管，避免CDN失效**

---

## 参数说明/接口扩展指引

- 路由参数 `{xxx}` 可被 regsiterApi 自动捕获并传入
- GET/POST方法由前端或fetch指定，上传下载请注意Method、Headers
- 使用 `RegisterApi` 时，多参数按路由模板 {id}/{type} 拓展，无限制
- `RegisterWs` 多路径注册，/ws2,/ws3..., 群发只影响各自路由下的连接

---

## 常见问题与扩展

- 标签页切换需保证引用 bootstrap.bundle.min.js
- 上传404请确认是否已注册/upload路由且method为POST
- WebSocket群发见Broadcast用法；支持并发万级连接（具体上限随硬件及配置）
- 若控制台项目/WinForms找不到WebApplication类型，请加 FrameworkReference
- 建议生产环境采用`https`+强Token或JWT认证
- 更复杂需求（如多文件、多用户信息/定向推送/业务权限/导入导出等），可自定义扩展RegisterApi/RegisterWs

---

## 进阶拓展建议

- 前端界面支持多语言、模块化、拖拽、Markdown、文件管理等
- 支持API文档一键导出/自动生成（集成Swashbuckle或自定义脚本）
- 集成MiniProfiler/Web管理等调试工具

---

## License

MIT

---

欢迎根据自身业务灵活定制扩展！如有任何问题请提交 Issue 或直接与作者联系。
