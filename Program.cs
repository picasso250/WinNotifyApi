using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Forms;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:8787");

builder.Services.AddSingleton<NotificationQueue>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<NotificationQueue>());

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "WinNotifyApi",
    endpoints = new[] { "POST /notify" }
}));

app.MapPost("/notify", async (NotifyRequest request, NotificationQueue queue) =>
{
    var title = string.IsNullOrWhiteSpace(request.Title) ? "Notification" : request.Title.Trim();
    var message = string.IsNullOrWhiteSpace(request.Message) ? request.Text : request.Message;

    if (string.IsNullOrWhiteSpace(message))
    {
        return Results.BadRequest(new { error = "message or text is required" });
    }

    var durationMs = Math.Clamp(request.DurationMs ?? 5000, 1000, 60000);
    await queue.EnqueueAsync(new NotificationPayload(title, message.Trim(), durationMs));

    return Results.Accepted($"/notify", new
    {
        accepted = true,
        title,
        durationMs
    });
});

app.Run();

public sealed record NotifyRequest(string? Title, string? Message, string? Text, int? DurationMs);

public sealed record NotificationPayload(string Title, string Message, int DurationMs);

public sealed class NotificationQueue : BackgroundService
{
    private readonly Channel<NotificationPayload> channel = Channel.CreateUnbounded<NotificationPayload>();
    private readonly ILogger<NotificationQueue> logger;
    private readonly TaskCompletionSource<NotificationApplicationContext> uiReady = new();

    public NotificationQueue(ILogger<NotificationQueue> logger)
    {
        this.logger = logger;
    }

    public ValueTask EnqueueAsync(NotificationPayload payload)
    {
        return channel.Writer.WriteAsync(payload);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var uiThread = new Thread(RunUiThread)
        {
            IsBackground = false,
            Name = "WinNotifyApi UI"
        };

        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        await using var stopRegistration = stoppingToken.Register(() =>
        {
            if (uiReady.Task.IsCompletedSuccessfully)
            {
                uiReady.Task.Result.ExitThread();
            }
        });

        var uiContext = await uiReady.Task.WaitAsync(stoppingToken);
        await foreach (var payload in channel.Reader.ReadAllAsync(stoppingToken))
        {
            uiContext.ShowNotification(payload);
        }
    }

    private void RunUiThread()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var uiContext = new NotificationApplicationContext();
            uiReady.SetResult(uiContext);
            Application.Run(uiContext);
        }
        catch (Exception ex)
        {
            uiReady.TrySetException(ex);
            logger.LogError(ex, "WinNotifyApi UI thread failed");
        }
    }
}

public sealed class NotificationApplicationContext : ApplicationContext
{
    private readonly Form invoker = new()
    {
        ShowInTaskbar = false,
        Opacity = 0,
        FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-32000, -32000),
        Size = new Size(1, 1)
    };

    public NotificationApplicationContext()
    {
        invoker.Load += (_, _) => invoker.Hide();
        invoker.Show();
        new StartupSplashForm().Show();
    }

    public void ShowNotification(NotificationPayload payload)
    {
        invoker.BeginInvoke(() => new NotificationForm(payload).Show());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            invoker.Dispose();
        }

        base.Dispose(disposing);
    }
}

public sealed class StartupSplashForm : TimedPopupForm
{
    public StartupSplashForm()
        : base("WinNotifyApi", "WinNotifyApi running", 300, 86, 1200)
    {
    }
}

public abstract class TimedPopupForm : Form
{
    private readonly System.Windows.Forms.Timer closeTimer = new();

    protected TimedPopupForm(string title, string body, int width, int height, int durationMs)
    {
        Text = title;
        Width = width;
        Height = height;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        Controls.Add(new Label
        {
            AutoSize = false,
            Text = body,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill
        });

        closeTimer.Interval = durationMs;
        closeTimer.Tick += (_, _) => Close();
        closeTimer.Start();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(
            workingArea.Right - Width - 20,
            workingArea.Bottom - Height - 20);

        Activate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        WindowState = FormWindowState.Normal;
        NativeMethods.ShowWindow(Handle, ShowWindowCommand.Show);
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.TopMost,
            Left,
            Top,
            Width,
            Height,
            SetWindowPosFlags.ShowWindow);
        BringToFront();
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closeTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}

public sealed class NotificationForm : TimedPopupForm
{
    private const int FormWidth = 380;
    private const int FormMinHeight = 104;
    private const int MarginSize = 18;
    private const int BottomPadding = 24;
    private const int MaxMessageLines = 10;

    public NotificationForm(NotificationPayload payload)
        : base(payload.Title, string.Empty, FormWidth, FormMinHeight, payload.DurationMs)
    {
        Controls.Clear();

        var messageLabel = new Label
        {
            AutoSize = false,
            Text = payload.Message,
            ForeColor = Color.FromArgb(30, 41, 59),
            Location = new Point(MarginSize, MarginSize),
            MaximumSize = new Size(FormWidth - MarginSize * 2, 0),
            Size = new Size(FormWidth - MarginSize * 2, 1),
            AutoEllipsis = true
        };
        messageLabel.Height = GetPreferredLabelHeight(messageLabel, payload.Message, MaxMessageLines);

        var contentHeight = Math.Max(FormMinHeight, messageLabel.Bottom + BottomPadding);
        ClientSize = new Size(FormWidth, contentHeight);

        Controls.Add(messageLabel);
    }

    private static int GetPreferredLabelHeight(Label label, string text, int maxLines)
    {
        var proposedSize = new Size(label.Width, int.MaxValue);
        var preferred = TextRenderer.MeasureText(text, label.Font, proposedSize, TextFormatFlags.WordBreak);
        var lineHeight = TextRenderer.MeasureText("Ag", label.Font).Height;
        var maxHeight = lineHeight * maxLines + 8;
        return Math.Max(Math.Min(preferred.Height + 8, maxHeight), 38);
    }

}

public enum ShowWindowCommand
{
    Show = 5
}

[Flags]
public enum SetWindowPosFlags
{
    ShowWindow = 0x0040
}

public static partial class NativeMethods
{
    public static readonly IntPtr TopMost = new(-1);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, ShowWindowCommand nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        SetWindowPosFlags uFlags);
}
