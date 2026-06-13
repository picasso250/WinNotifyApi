using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Forms;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:8787");

builder.Services.AddSingleton<NotificationQueue>();
builder.Services.AddHostedService<NotificationUiWorker>();

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

public sealed class NotificationQueue
{
    private readonly Channel<NotificationPayload> channel = Channel.CreateUnbounded<NotificationPayload>();

    public ValueTask EnqueueAsync(NotificationPayload payload)
    {
        return channel.Writer.WriteAsync(payload);
    }

    public IAsyncEnumerable<NotificationPayload> ReadAllAsync(CancellationToken cancellationToken)
    {
        return channel.Reader.ReadAllAsync(cancellationToken);
    }
}

public sealed class NotificationUiWorker(NotificationQueue queue, ILogger<NotificationUiWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var payload in queue.ReadAllAsync(stoppingToken))
        {
            var thread = new Thread(() => ShowNotification(payload, logger))
            {
                IsBackground = true,
                Name = "WinNotifyApi UI"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    private static void ShowNotification(NotificationPayload payload, ILogger logger)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new NotificationForm(payload));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to show notification window");
        }
    }
}

public sealed class NotificationForm : Form
{
    private const int FormWidth = 380;
    private const int FormMinHeight = 130;
    private const int MarginSize = 18;

    private readonly System.Windows.Forms.Timer closeTimer = new();

    public NotificationForm(NotificationPayload payload)
    {
        Text = payload.Title;
        Width = FormWidth;
        MinimumSize = new Size(FormWidth, FormMinHeight);
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var titleLabel = new Label
        {
            AutoSize = false,
            Text = payload.Title,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(MarginSize, MarginSize),
            Size = new Size(FormWidth - 72, 24)
        };

        var closeButton = new Button
        {
            Text = "x",
            FlatStyle = FlatStyle.Flat,
            Size = new Size(28, 28),
            Location = new Point(FormWidth - 46, 12),
            TabStop = false
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => Close();

        var messageLabel = new Label
        {
            AutoSize = false,
            Text = payload.Message,
            ForeColor = Color.FromArgb(30, 41, 59),
            Location = new Point(MarginSize, 50),
            MaximumSize = new Size(FormWidth - MarginSize * 2, 0),
            Size = new Size(FormWidth - MarginSize * 2, 1)
        };
        messageLabel.Height = GetPreferredLabelHeight(messageLabel, payload.Message);

        var contentHeight = Math.Max(FormMinHeight, messageLabel.Bottom + MarginSize + 8);
        Height = contentHeight;

        Controls.Add(titleLabel);
        Controls.Add(closeButton);
        Controls.Add(messageLabel);

        closeTimer.Interval = payload.DurationMs;
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
        ShowWindow(Handle, ShowWindowCommand.Show);
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

    private static int GetPreferredLabelHeight(Label label, string text)
    {
        var proposedSize = new Size(label.Width, int.MaxValue);
        var preferred = TextRenderer.MeasureText(text, label.Font, proposedSize, TextFormatFlags.WordBreak);
        return Math.Max(preferred.Height + 8, 38);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, ShowWindowCommand nCmdShow);

    private enum ShowWindowCommand
    {
        Show = 5
    }
}
