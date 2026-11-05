using SkiaSharp;

namespace ScreenRec.Core.Recording;

/// <summary>A realistic made-up recording (an expense-approval web app: approve one with a receipt, reject one without, type a
/// comment, save) built without touching the real screen — to try the AI pipeline and Guardian publishing safely.</summary>
public static class DemoSession
{
    private sealed record Row(string Id, string Employee, string Amount, string Receipt);

    private static readonly Row[] Rows =
    [
        new("EX-1001", "J. Smith", "$42.10", "Yes"),
        new("EX-1002", "R. Diaz", "$1,250.00", "No"),
        new("EX-1003", "K. Patel", "$318.75", "Yes"),
    ];

    public static RecordingSession Create(string root, string expert = "Maria")
    {
        var session = RecordingSession.Create(root, new SessionManifest
        {
            StartedAt = DateTimeOffset.Now,
            Title = "Approve expense reports",
            Description = "Every morning I go through pending expenses: approve the ones that have a receipt, reject the ones without one and say why in the comment, then save.",
            Expert = expert, Platform = OperatingSystem.IsWindows() ? "windows" : "macos", Machine = Environment.MachineName,
            FramesPerSecond = 1, DisplayWidth = W, DisplayHeight = H, Status = SessionStatus.Recording,
        });

        // state per frame: statuses for each row, comment text, saved banner
        var states = new List<(double T, string[] Status, string Comment, bool Saved, int Focused)>
        {
            (0, ["Pending", "Pending", "Pending"], "", false, -1),
            (3, ["Approved", "Pending", "Pending"], "", false, -1),
            (6, ["Approved", "Rejected", "Pending"], "", false, -1),
            (9, ["Approved", "Rejected", "Pending"], "", false, 1),
            (12, ["Approved", "Rejected", "Pending"], "EX-1002 rejected: no receipt attached", false, 1),
            (15, ["Approved", "Rejected", "Approved"], "EX-1002 rejected: no receipt attached", false, -1),
            (18, ["Approved", "Rejected", "Approved"], "EX-1002 rejected: no receipt attached", true, -1),
        };
        var frames = new List<FrameInfo>();
        using (var avi = new MjpegAviWriter(Path.Combine(session.Folder, "recording.avi"), W, H, 1))
        {
            for (var i = 0; i < states.Count; i++)
            {
                var s = states[i];
                using var bmp = Draw(s.Status, s.Comment, s.Saved, s.Focused);
                var jpeg = ImageTools.Jpeg(bmp, 80);
                avi.WriteFrame(jpeg);
                var name = $"{i:D6}.jpg";
                File.WriteAllBytes(Path.Combine(session.FramesFolder, name), jpeg);
                frames.Add(new FrameInfo { Index = i, T = s.T, File = name, Width = W, Height = H, App = "Safari", Window = "Acme Expenses — Pending approvals", Change = i == 0 ? 1 : 0.05 });
            }
        }
        double X(double px) => Math.Round(px / W, 4);
        double Y(double px) => Math.Round(px / H, 4);
        var events = new List<InputEvent>
        {
            new() { T = 0, Kind = InputKind.Focus, App = "Safari", Window = "Acme Expenses — Pending approvals" },
            new() { T = 1.5, Kind = InputKind.MouseDown, X = X(ButtonX + 40), Y = Y(RowY(0) + 20), Button = "left" },
            new() { T = 1.6, Kind = InputKind.MouseUp, X = X(ButtonX + 40), Y = Y(RowY(0) + 20), Button = "left" },
            new() { T = 4.5, Kind = InputKind.MouseDown, X = X(ButtonX + 140), Y = Y(RowY(1) + 20), Button = "left" },
            new() { T = 4.6, Kind = InputKind.MouseUp, X = X(ButtonX + 140), Y = Y(RowY(1) + 20), Button = "left" },
            new() { T = 7.5, Kind = InputKind.MouseDown, X = X(400), Y = Y(CommentY + 20), Button = "left" },
            new() { T = 7.6, Kind = InputKind.MouseUp, X = X(400), Y = Y(CommentY + 20), Button = "left" },
        };
        var t = 9.5;
        foreach (var c in "EX-1002 rejected: no receipt attached") { events.Add(new() { T = Math.Round(t, 2), Kind = InputKind.Key, Button = c.ToString().ToUpperInvariant(), Text = c.ToString() }); t += 0.06; }
        events.Add(new() { T = 13, Kind = InputKind.Note, Text = "Anything over $1,000 without a receipt always gets rejected, no exceptions." });
        events.Add(new() { T = 13.5, Kind = InputKind.MouseDown, X = X(ButtonX + 40), Y = Y(RowY(2) + 20), Button = "left" });
        events.Add(new() { T = 13.6, Kind = InputKind.MouseUp, X = X(ButtonX + 40), Y = Y(RowY(2) + 20), Button = "left" });
        events.Add(new() { T = 16.5, Kind = InputKind.MouseDown, X = X(SaveX + 50), Y = Y(SaveY + 20), Button = "left" });
        events.Add(new() { T = 16.6, Kind = InputKind.MouseUp, X = X(SaveX + 50), Y = Y(SaveY + 20), Button = "left" });

        File.WriteAllLines(session.FramesFile, frames.Select(f => System.Text.Json.JsonSerializer.Serialize(f, RecordingSession.Line)));
        File.WriteAllLines(session.EventsFile, events.Select(e => System.Text.Json.JsonSerializer.Serialize(e, RecordingSession.Line)));
        var m = session.Manifest;
        m.VideoFile = "recording.avi";
        m.FrameCount = frames.Count;
        m.EventCount = events.Count;
        m.DurationSeconds = 20;
        m.EndedAt = m.StartedAt.AddSeconds(20);
        m.Status = SessionStatus.Recorded;
        session.Save();
        return session;
    }

    private const int W = 1280, H = 800;
    private const int ButtonX = 900, CommentY = 470, SaveX = 60, SaveY = 540;
    private static int RowY(int i) => 230 + i * 60;

    private static SKBitmap Draw(string[] status, string comment, bool saved, int focused)
    {
        var bmp = new SKBitmap(W, H);
        using var c = new SKCanvas(bmp);
        c.Clear(new SKColor(250, 250, 250));
        using var bar = new SKPaint { Color = new SKColor(225, 228, 232) };
        c.DrawRect(0, 0, W, 50, bar);
        using var title = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 34);
        using var font = new SKFont(SKTypeface.Default, 20);
        using var small = new SKFont(SKTypeface.Default, 16);
        using var ink = new SKPaint { Color = new SKColor(30, 30, 30), IsAntialias = true };
        using var grey = new SKPaint { Color = new SKColor(110, 110, 110), IsAntialias = true };
        c.DrawText("expenses.acme.example/approvals", 20, 32, small, grey);
        c.DrawText("Acme Expenses — Pending approvals", 60, 120, title, ink);
        c.DrawText("Approve only with a receipt. Rejections need a comment.", 60, 160, small, grey);
        string[] heads = ["ID", "Employee", "Amount", "Receipt", "Status", "Action"];
        int[] xs = [60, 200, 400, 560, 700, 900];
        using var line = new SKPaint { Color = new SKColor(200, 200, 200), StrokeWidth = 1 };
        for (var h = 0; h < heads.Length; h++) c.DrawText(heads[h], xs[h], 205, font, ink);
        c.DrawLine(60, 215, 1220, 215, line);
        for (var r = 0; r < Rows.Length; r++)
        {
            var y = RowY(r);
            var bg = status[r] switch { "Approved" => new SKColor(220, 245, 220), "Rejected" => new SKColor(250, 222, 222), _ => SKColors.White };
            using var rowPaint = new SKPaint { Color = bg };
            c.DrawRect(60, y, 1160, 50, rowPaint);
            var row = Rows[r];
            string[] cells = [row.Id, row.Employee, row.Amount, row.Receipt, status[r]];
            for (var k = 0; k < cells.Length; k++) c.DrawText(cells[k], xs[k], y + 32, font, ink);
            Button(c, ButtonX, y + 8, 90, "Approve", new SKColor(46, 160, 67));
            Button(c, ButtonX + 100, y + 8, 80, "Reject", new SKColor(207, 34, 46));
        }
        c.DrawText("Comment", 60, CommentY - 10, font, ink);
        using var box = new SKPaint { Color = focused == 1 ? new SKColor(59, 130, 246) : new SKColor(170, 170, 170), Style = SKPaintStyle.Stroke, StrokeWidth = focused == 1 ? 2 : 1 };
        c.DrawRect(60, CommentY, 700, 40, box);
        c.DrawText(comment, 70, CommentY + 27, font, ink);
        Button(c, SaveX, SaveY, 110, "Save", new SKColor(37, 99, 235));
        if (saved)
        {
            using var ok = new SKPaint { Color = new SKColor(46, 160, 67), IsAntialias = true };
            c.DrawText("Saved: 2 approved, 1 rejected. Notifications sent.", 200, SaveY + 28, font, ok);
        }
        return bmp;
    }

    private static void Button(SKCanvas c, float x, float y, float w, string text, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true };
        c.DrawRoundRect(x, y, w, 34, 6, 6, p);
        using var f = new SKFont(SKTypeface.Default, 17);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        c.DrawText(text, x + 12, y + 23, f, white);
    }
}
