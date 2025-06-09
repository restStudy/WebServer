using System;
using System.Text;
using System.Windows.Forms;
using WebServer; // 记得添加 ModernWebServer 类的命名空间！

namespace Demo
{
    public partial class DemoForm : Form
    {
        // 用于控制Web服务全局变量
        private ModernWebServer? _webServer;

        public DemoForm()
        {
            InitializeComponent();
            // 建议放Form的构造后或Load事件中
            this.Load += Form1_Load;
            this.FormClosing += Form1_FormClosing;
        }

        private void Form1_Load(object? sender, EventArgs e)
        {
            // 启动本地web服务
            _webServer = new ModernWebServer(
                    8080,
                    8443,
                    null, // 如果无需https，则填null
                    null, // 证书密码
                    msg => this.Invoke((Action)(() =>
                    {
                        // 日志信息可输出到某个多行文本框
                        Console.WriteLine(msg);
                    })))
                .SetStaticFolder(@"F:\XiangMuYuanMa\C#(WebServer)\WebServer\www") // 你的静态目录（确保存在）
                .WithCors();

            // 注册API/WS等扩展演示
            _webServer.RegisterWs("/ws1", async (ws, ctx) =>
            {
                await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes("欢迎进入WS1"),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, default);
                var buf = new byte[4096];
                while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(buf, System.Threading.CancellationToken.None);
                    if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                    await ws.SendAsync(buf.AsMemory(0, res.Count), res.MessageType, res.EndOfMessage, default);
                    await _webServer.Broadcast("/ws1",
                        "广播消息：" + Encoding.UTF8.GetString(buf.AsMemory(0, res.Count).ToArray()));
                }
            });

            _webServer.Start();
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // 关闭服务
            _webServer?.Stop();
        }
    }
}