using System;
using System.Text;
using System.Windows.Forms;
using WebServer; // �ǵ���� ModernWebServer ��������ռ䣡

namespace Demo
{
    public partial class DemoForm : Form
    {
        // ���ڿ���Web����ȫ�ֱ���
        private ModernWebServer? _webServer;

        public DemoForm()
        {
            InitializeComponent();
            // �����Form�Ĺ�����Load�¼���
            this.Load += Form1_Load;
            this.FormClosing += Form1_FormClosing;
        }

        private void Form1_Load(object? sender, EventArgs e)
        {
            // ��������web����
            _webServer = new ModernWebServer(
                    8080,
                    8443,
                    null, // �������https������null
                    null, // ֤������
                    msg => this.Invoke((Action)(() =>
                    {
                        // ��־��Ϣ�������ĳ�������ı���
                        Console.WriteLine(msg);
                    })))
                .SetStaticFolder(@"F:\XiangMuYuanMa\C#(WebServer)\WebServer\www") // ��ľ�̬Ŀ¼��ȷ�����ڣ�
                .WithCors();

            // ע��API/WS����չ��ʾ
            _webServer.RegisterWs("/ws1", async (ws, ctx) =>
            {
                await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes("��ӭ����WS1"),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, default);
                var buf = new byte[4096];
                while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(buf, System.Threading.CancellationToken.None);
                    if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                    await ws.SendAsync(buf.AsMemory(0, res.Count), res.MessageType, res.EndOfMessage, default);
                    await _webServer.Broadcast("/ws1",
                        "�㲥��Ϣ��" + Encoding.UTF8.GetString(buf.AsMemory(0, res.Count).ToArray()));
                }
            });

            _webServer.Start();
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // �رշ���
            _webServer?.Stop();
        }
    }
}