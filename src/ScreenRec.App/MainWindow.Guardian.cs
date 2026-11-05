using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ScreenRec.Core;
using ScreenRec.Core.Ai;
using ScreenRec.Core.Export;
using ScreenRec.Core.Guardian;
using ScreenRec.Core.Storage;

namespace ScreenRec.App;

public sealed partial class MainWindow
{
    // Guardian tab
    private readonly TextBox _gAddress = new();
    private readonly TextBox _gEmail = new() { PlaceholderText = "you@company.com" };
    private readonly TextBox _gPassword = new() { PasswordChar = '•', PlaceholderText = "Guardian password" };
    private readonly TextBox _gCode = new() { PlaceholderText = "6-digit code", IsVisible = false, MaxLength = 7 };
    private readonly Button _gSignIn = new() { Content = "Sign in" };
    private readonly Button _gBrowser = new() { Content = "Sign in with your browser (Microsoft / SSO)…" };
    private readonly Button _gSignOut = new() { Content = "Sign out" };
    private readonly TextBlock _gStatus = Label("");
    private readonly TextBlock _gError = Label("", "error");
    private readonly StackPanel _gSignedIn = new() { Spacing = 8 };
    private readonly StackPanel _gSignedOut = new() { Spacing = 10 };
    private readonly StackPanel _gInfo = new() { Spacing = 4 };
    private string? _challenge;

    private Control BuildGuardianTab()
    {
        var last = _ws.Accounts.LastAccount();
        _gAddress.Text = last?.Address ?? _ws.Settings.GuardianAddress;
        _gEmail.Text = last?.Email ?? "";
        _gSignIn.Click += async (_, _) => await SignInAsync();
        _gPassword.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await SignInAsync(); };
        _gCode.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await SignInAsync(); };
        _gBrowser.Click += async (_, _) => await BrowserSignInAsync();
        _gSignOut.Click += (_, _) =>
        {
            _ws.SignOut();
            _ws.Settings.Save();
            RefreshAccount();
        };

        _gSignedOut.Children.Add(Label("Sign in to Guardian", "h2"));
        _gSignedOut.Children.Add(Label("If you have a Guardian login, sign in to publish what was learned as Guardian skills and automations, upload recordings to Guardian's Video intake, and use Guardian's AI models.", "muted"));
        _gSignedOut.Children.Add(Field("Guardian address", _gAddress));
        _gSignedOut.Children.Add(Field("Email", _gEmail));
        _gSignedOut.Children.Add(Field("Password", _gPassword));
        _gSignedOut.Children.Add(_gCode);
        _gSignedOut.Children.Add(Row(8, _gSignIn, _gBrowser));
        _gSignedOut.Children.Add(Label("Microsoft (Entra) and other single sign-on accounts: use the browser button. Guardian asks which organization to connect, and you can revoke it in Guardian under Profile › API keys › Connected apps.", "muted"));

        var open = new Button { Content = "Open Guardian" };
        open.Click += (_, _) => Open((_ws.Guardian?.Address ?? _ws.Settings.GuardianAddress) + "/app");
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await RefreshGuardianAsync();
        _gSignedIn.Children.Add(Label("Signed in", "h2"));
        _gSignedIn.Children.Add(_gStatus);
        _gSignedIn.Children.Add(_gInfo);
        _gSignedIn.Children.Add(Row(8, open, refresh, _gSignOut));

        RefreshGuardianPanel();
        return Page(Stack(0, Card(_gSignedOut), Card(_gSignedIn), _gError));
    }

    private void RefreshGuardianPanel()
    {
        var signed = _ws.IsSignedIn;
        _gSignedOut.IsVisible = !signed;
        _gSignedIn.IsVisible = signed;
        if (_ws.GuardianAccount is { } a)
            _gStatus.Text = $"{a.Label} at {a.Address}{(a.OrgName is { } o ? $" · organization {o}" : "")} (via {a.Method}, {a.SignedInAt.ToLocalTime():g})";
    }

    private async Task SignInAsync()
    {
        _gError.Text = "";
        GuardianClient client;
        try { client = _ws.NewGuardianClient(_gAddress.Text); }
        catch (GuardianError e) { _gError.Text = e.Message; return; }
        _gSignIn.IsEnabled = _gBrowser.IsEnabled = false;
        try
        {
            GuardianSession session;
            if (_challenge is not null)
            {
                session = await client.VerifyTwoFactorAsync(_challenge, _gCode.Text ?? "", _gEmail.Text);
            }
            else
            {
                try
                {
                    session = await client.SignInAsync(_gEmail.Text ?? "", _gPassword.Text ?? "");
                }
                catch (GuardianError e) when (e.Kind == GuardianErrorKind.TwoFactorRequired)
                {
                    _challenge = e.Challenge;
                    _gCode.IsVisible = true;
                    _gCode.Focus();
                    _gError.Text = e.Message;
                    _keepClient = client;
                    return;
                }
            }
            await FinishSignInAsync(_keepClient ?? client, session, "password");
        }
        catch (GuardianError e)
        {
            _gError.Text = e.Message;
        }
        finally
        {
            _gSignIn.IsEnabled = _gBrowser.IsEnabled = true;
        }
    }

    private GuardianClient? _keepClient;

    private async Task BrowserSignInAsync()
    {
        _gError.Text = "";
        GuardianClient client;
        try { client = _ws.NewGuardianClient(_gAddress.Text); }
        catch (GuardianError e) { _gError.Text = e.Message; return; }
        _gSignIn.IsEnabled = _gBrowser.IsEnabled = false;
        _gError.Text = "Finish signing in in your browser…";
        try
        {
            var session = await GuardianBrowserSignIn.SignInAsync(client);
            await FinishSignInAsync(client, session, "browser");
        }
        catch (GuardianError e)
        {
            _gError.Text = e.Message;
        }
        finally
        {
            _gSignIn.IsEnabled = _gBrowser.IsEnabled = true;
            Activate();
        }
    }

    private async Task FinishSignInAsync(GuardianClient client, GuardianSession session, string method)
    {
        await _ws.CompleteSignInAsync(client, session, method);
        _challenge = null;
        _keepClient = null;
        _gCode.IsVisible = false;
        _gCode.Text = _gPassword.Text = "";
        _gError.Text = "";
        RefreshAccount();
        await RefreshGuardianAsync();
    }

    private async Task RefreshGuardianAsync()
    {
        if (_ws.Guardian is not { } g) return;
        _gInfo.Children.Clear();
        try
        {
            var me = await g.MeAsync();
            _gInfo.Children.Add(Label($"Modules: {string.Join(", ", me.Modules.DefaultIfEmpty("(none listed)"))}", "muted"));
            var canAutomate = me.Modules.Count == 0 || me.Modules.Contains("automations");
            if (!canAutomate) _gInfo.Children.Add(Label("Automations isn't among your Guardian modules — publishing an automation may be refused. Skills still work.", "error"));
            var models = await g.ModelsAsync();
            _guardianModels = models.Select(m => m.Id).ToList();
            _gInfo.Children.Add(Label($"Models Guardian offers you: {string.Join(", ", _guardianModels.DefaultIfEmpty("none"))}", "muted"));
            Ui(RefreshModelChoices);
        }
        catch (GuardianError e) when (e.Kind == GuardianErrorKind.Unauthorized)
        {
            _ws.SignOut();
            RefreshAccount();
            _gError.Text = e.Message;
        }
        catch (GuardianError e)
        {
            _gInfo.Children.Add(Label(e.Message, "error"));
        }
    }

    // MARK: Expert tab — combine recordings into one "be an expert at her stuff" skill

    private readonly TextBox _expertName = new() { PlaceholderText = "Expert's name" };
    private readonly TextBlock _expertStatus = Label("", "muted");
    private readonly StackPanel _expertView = new() { Spacing = 6 };

    private Control BuildExpertTab()
    {
        _expertName.Text = _ws.Settings.ExpertName;
        var build = new Button { Content = "Build expert profile" };
        var publish = new Button { Content = "Build and save to Guardian as a skill" };
        var folder = new Button { Content = "Show files" };
        folder.Click += (_, _) => Open(Path.Combine(AppPaths.Experts, Exporters.Slug(_expertName.Text ?? "")));
        async Task Run(bool toGuardian)
        {
            var name = _expertName.Text?.Trim() ?? "";
            if (name.Length == 0) { _expertStatus.Text = "Enter the expert's name (as typed when recording)."; return; }
            if (toGuardian && !_ws.IsSignedIn) { _expertStatus.Text = "Sign in to Guardian first."; return; }
            build.IsEnabled = publish.IsEnabled = false;
            _expertStatus.Text = "Combining every processed recording of " + name + "…";
            try
            {
                var (profile, skill, problem) = await Task.Run(() => _ws.BuildExpertAsync(name, toGuardian));
                ShowExpert(profile);
                _expertStatus.Text = skill is not null ? $"Saved in Guardian as the skill \"{skill}\" — Guardian chats and workers can now work the way {name} does."
                    : problem ?? "Profile built (saved on this computer).";
            }
            catch (Exception e)
            {
                _expertStatus.Text = "Couldn't build the profile: " + e.Message;
            }
            finally
            {
                build.IsEnabled = publish.IsEnabled = true;
            }
        }
        build.Click += async (_, _) => await Run(false);
        publish.Click += async (_, _) => await Run(true);

        var existing = Path.Combine(AppPaths.Experts, Exporters.Slug(_ws.Settings.ExpertName), "profile.json");
        if (_ws.Settings.ExpertName.Length > 0 && File.Exists(existing))
        {
            try
            {
                if (System.Text.Json.JsonSerializer.Deserialize<ExpertProfile>(File.ReadAllText(existing), AnalysisPipeline.Json) is { } p) ShowExpert(p);
            }
            catch (System.Text.Json.JsonException) { }
        }

        return Page(Stack(0,
            Card(Stack(10,
                Label("Train a person to be an expert", "h2"),
                Label("Record several of the expert's tasks and process them. This combines them into one profile — responsibilities, tasks, rules of thumb, mistakes to avoid, vocabulary and open questions — and saves it to Guardian as a skill with every procedure attached, so a trainee (or Guardian's AI) can learn and follow it.", "muted"),
                Field("Expert", _expertName),
                Row(8, build, publish, folder),
                _expertStatus)),
            Card(_expertView)));
    }

    private void ShowExpert(ExpertProfile p)
    {
        _expertView.Children.Clear();
        _expertView.Children.Add(Label($"{p.Name}{(p.Role.Length > 0 ? " — " + p.Role : "")}", "h2"));
        _expertView.Children.Add(Label(p.Overview));
        void Section(string title, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            _expertView.Children.Add(Label(title, "h2"));
            foreach (var i in list) _expertView.Children.Add(Label("• " + i));
        }
        Section("Tasks", p.Tasks.Select(t => $"{t.Name}{(t.When.Length > 0 ? $" ({t.When})" : "")} — {t.Summary}"));
        Section("Rules of thumb", p.Rules.Select(r => $"When {r.When} → {r.Then}"));
        Section("Mistakes to avoid", p.Pitfalls);
        Section("Still to learn from them", p.OpenQuestions);
        _expertView.Children.Add(Label($"From {p.SessionIds.Count} recording(s) · {p.Model} · {p.UpdatedAt.ToLocalTime():g}", "muted"));
    }
}
