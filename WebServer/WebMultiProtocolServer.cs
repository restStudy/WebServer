using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace WebServer;

public class ModernWebServer
{
    private readonly List<(Regex regex, Func<HttpContext, Dictionary<string, string>, Task> handler)> _apiRoutes =
        new();

    private readonly int _httpPort, _httpsPort;
    private readonly Action<string> _log;
    private readonly string? _pfxFile, _pfxPassword;

    private readonly ConcurrentDictionary<string, List<WebSocket>> _wsClients = new();
    private readonly Dictionary<string, Func<WebSocket, HttpContext, Task>> _wsRoutes = new();
    private Func<HttpContext, Task<bool>>? _apiTokenVerifier;
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private bool _enableCors;
    private IHost? _host;
    private string _staticPath = ".";

    public ModernWebServer(int httpPort, int httpsPort, string? pfxFile, string? pfxPassword,
        Action<string>? log = null)
    {
        _httpPort = httpPort;
        _httpsPort = httpsPort;
        _pfxFile = pfxFile;
        _pfxPassword = pfxPassword;
        _log = log ?? Console.WriteLine;
    }

    public ModernWebServer WithCors(bool enable = true)
    {
        _enableCors = enable;
        return this;
    }

    public ModernWebServer WithTokenAuth(Func<HttpContext, Task<bool>> verifier)
    {
        _apiTokenVerifier = verifier;
        return this;
    }

    public ModernWebServer SetStaticFolder(string staticPath)
    {
        _staticPath = staticPath;
        return this;
    }

    public ModernWebServer RegisterApi(string routeTemplate,
        Func<HttpContext, Dictionary<string, string>, Task> handler)
    {
        var pattern = "^" + Regex.Replace(routeTemplate, @"\{(\w+)\}", m => $"(?<{m.Groups[1].Value}>[^/]+)") + "$";
        var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        _apiRoutes.Add((regex, handler));
        return this;
    }

    public ModernWebServer RegisterWs(string path, Func<WebSocket, HttpContext, Task> wsHandler)
    {
        if (!path.StartsWith("/")) path = "/" + path;
        _wsRoutes[path] = wsHandler;
        return this;
    }

    public async Task Broadcast(string wsPath, string message)
    {
        if (_wsClients.TryGetValue(wsPath, out var list))
            foreach (var ws in list.ToArray())
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true,
                        CancellationToken.None);
    }

    public void Start()
    {
        if (_app != null)
        {
            _log("服务已在运行");
            return;
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.ListenAnyIP(_httpPort);
            if (!string.IsNullOrEmpty(_pfxFile) && !string.IsNullOrEmpty(_pfxPassword))
                options.ListenAnyIP(_httpsPort, listen => listen.UseHttps(_pfxFile, _pfxPassword));
        });
        _cts = new CancellationTokenSource();
        var app = builder.Build();

        // 1. CORS
        if (_enableCors)
            app.Use((ctx, next) =>
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
                if (ctx.Request.Method == "OPTIONS")
                {
                    ctx.Response.StatusCode = 204;
                    return Task.CompletedTask;
                }

                return next();
            });

        // 2. 静态资源托管
        if (!string.IsNullOrEmpty(_staticPath) && Directory.Exists(_staticPath))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(_staticPath),
                RequestPath = ""
            });
            _log($"静态文件目录：{_staticPath}");
        }

        // 3. /ui 输出 ui.html
        app.Map("/ui", uiApp =>
        {
            uiApp.Run(async ctx =>
            {
                var fname = Path.Combine(_staticPath, "ui.html");
                if (File.Exists(fname))
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.SendFileAsync(fname);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsync("ui.html not found");
                }
            });
        });

        // 4. /swagger 输出 swagger.html
        app.Map("/swagger", swaggerApp =>
        {
            swaggerApp.Run(async ctx =>
            {
                var fname = Path.Combine(_staticPath, "swagger.html");
                if (File.Exists(fname))
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.SendFileAsync(fname);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsync("swagger.html not found");
                }
            });
        });

        // 5. /swagger.json 读静态swagger.json
        app.Map("/swagger.json", jsonApp =>
        {
            jsonApp.Run(async ctx =>
            {
                var fname = Path.Combine(_staticPath, "swagger.json");
                if (File.Exists(fname))
                {
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.SendFileAsync(fname);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsync("{\"error\":\"swagger.json not found\"}");
                }
            });
        });

        // 6. WebSocket多路由
        app.UseWebSockets();
        foreach (var wsRoute in _wsRoutes)
        {
            var wsPath = wsRoute.Key;
            app.Map(wsPath, wsApp =>
            {
                wsApp.Run(async ctx =>
                {
                    if (ctx.WebSockets.IsWebSocketRequest)
                    {
                        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                        _wsClients.AddOrUpdate(wsPath, k => new List<WebSocket> { ws }, (k, l) =>
                        {
                            l.Add(ws);
                            return l;
                        });
                        try
                        {
                            await wsRoute.Value(ws, ctx);
                        }
                        catch (Exception ex)
                        {
                            _log("WS异常:" + ex);
                        }
                        finally
                        {
                            _wsClients[wsPath].Remove(ws);
                        }
                    }
                    else
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsync("WebSocket only");
                    }
                });
            });
            _log($"已注册WebSocket路由 {wsPath}");
        }

        // 7. RESTful API
        app.Map("/api/{**catchall}", async ctx =>
        {
            if (_apiTokenVerifier != null && !await _apiTokenVerifier(ctx))
            {
                ctx.Response.StatusCode = 401;
                await WriteJson(ctx, new { code = 401, error = "Token required" });
                return;
            }

            var path = ctx.Request.Path.Value ?? "";
            foreach (var (regex, handler) in _apiRoutes)
            {
                var match = regex.Match(path);
                if (match.Success)
                {
                    var routeParams = new Dictionary<string, string>();
                    foreach (var name in regex.GetGroupNames())
                        if (!int.TryParse(name, out _))
                            routeParams[name] = match.Groups[name].Value;
                    try
                    {
                        await handler(ctx, routeParams);
                    }
                    catch (Exception ex)
                    {
                        ctx.Response.StatusCode = 500;
                        await WriteJson(ctx, new { code = 500, error = ex.Message });
                        _log("API错误:" + ex);
                    }

                    return;
                }
            }

            ctx.Response.StatusCode = 404;
            await WriteJson(ctx, new { code = 404, error = "Not Found" });
        });

        // 8. 常用DEMO API/断点下载
        RegisterApi("/api/hello",
            async (ctx, par) => await WriteJson(ctx, new { code = 200, msg = "Hello API!", time = DateTime.Now }));
        RegisterApi("/api/time", async (ctx, par) => await WriteJson(ctx, new { time = DateTime.Now }));
        RegisterApi("/api/echo/{val}", async (ctx, par) => await WriteJson(ctx, new { echo = par["val"] }));
        RegisterApi("/api/download/{filename}", async (ctx, par) =>
        {
            var fname = par["filename"];
            var full = Path.Combine(_staticPath, fname);
            if (!File.Exists(full))
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Not found");
                return;
            }

            long total = new FileInfo(full).Length, start = 0, end = total - 1;
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            var range = ctx.Request.Headers["Range"].FirstOrDefault();
            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes="))
            {
                var parts = range[6..].Split('-');
                start = long.Parse(parts[0]);
                if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1])) end = long.Parse(parts[1]);
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
            }

            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = end - start + 1;
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[81920];
            var remain = end - start + 1;
            while (remain > 0)
            {
                var read = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remain)));
                if (read == 0) break;
                await ctx.Response.Body.WriteAsync(buf.AsMemory(0, read));
                remain -= read;
            }
        });
        RegisterApi("/api/upload/{filename}", async (ctx, par) =>
        {
            if (ctx.Request.Method != "POST")
            {
                ctx.Response.StatusCode = 405;
                await ctx.Response.WriteAsync("Only POST allowed");
                return;
            }

            var fileName = par["filename"];
            var savePath = Path.Combine(_staticPath + "\\files", fileName);

            // 读取body（原样存储）
            using var fs = File.OpenWrite(savePath);
            await ctx.Request.Body.CopyToAsync(fs);

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"code\":200,\"msg\":\"上传完成\",\"filename\":\"" + fileName + "\"}");
        });

        RegisterApi("/api/upload", async (ctx, par) =>
        {
            if (ctx.Request.Method != "POST")
            {
                ctx.Response.StatusCode = 405;
                await ctx.Response.WriteAsync("Only POST allowed");
                return;
            }

            // 检查Content-Type是否为 multipart/form-data
            if (!ctx.Request.ContentType?.StartsWith("multipart/form-data") ?? true)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("Content-Type must be multipart/form-data");
                return;
            }

            var form = await ctx.Request.ReadFormAsync();
            var files = form.Files;
            var savedFiles = new List<string>();
            var uploadDir = Path.Combine(_staticPath, "files");
            if (!Directory.Exists(uploadDir))
                Directory.CreateDirectory(uploadDir);

            foreach (var file in files)
            {
                if (file.Length > 0)
                {
                    // 建议过滤危险扩展名，防目录穿越
                    var safeFileName = Path.GetFileName(file.FileName);
                    var savePath = Path.Combine(uploadDir, safeFileName);

                    using (var stream = new FileStream(savePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }
                    savedFiles.Add(safeFileName);
                }
            }

            // 可读取表单其它字段
            var extraFields = form
                .Where(p => !files.Any(f => f.Name == p.Key))
                .ToDictionary(p => p.Key, p => p.Value.ToString());

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                code = 200,
                msg = "上传完成",
                files = savedFiles,
                fields = extraFields
            }));
        });
        RegisterApi("/api/notification", async (ctx, par) =>
        {
            if (ctx.Request.Method != "POST")
            {
                ctx.Response.StatusCode = 405;
                await ctx.Response.WriteAsync("Only POST allowed");
                return;
            }

            // 读取 POST JSON 原始字符串
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();

            // 记录日志或打印
            Console.WriteLine($"[通知] 收到推送: {body}");

            // 可选：校验是否为标准JSON
            try
            {
                var jdoc = System.Text.Json.JsonDocument.Parse(body);  // 如异常说明不是合法json
                // 或根据需要提取字段: jdoc.RootElement.GetProperty("yourKey")
            }
            catch (Exception ex)
            {
                Console.WriteLine($"解析JSON失败: {ex.Message}");
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("{\"code\":400,\"msg\":\"Invalid JSON.\"}");
                return;
            }

            // 响应
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"code\":200,\"msg\":\"通知已接收\"}");
        });


        // 9. 默认主页
        app.Map("/", ctx => ctx.Response.WriteAsync("ModernWebServer 已启动，见 /ui /swagger /api/hello"));

        _app = app;
        _ = app.RunAsync(_cts.Token);
        _log($"Web服务已启动 HTTP:{_httpPort} HTTPS:{_httpsPort}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _app = null;
            _log("Web服务已停止。");
        }
        catch (Exception ex)
        {
            _log("Stop Error:" + ex);
        }
    }

    private static async Task WriteJson(HttpContext ctx, object obj)
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(obj));
    }
}