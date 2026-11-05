using Avalonia;
using Avalonia.Controls;
using ScreenRec.Core.Ai;
using ScreenRec.Core.Storage;

namespace ScreenRec.App;

public sealed partial class MainWindow
{
    private List<string> _guardianModels = new();
    private readonly ComboBox _sProvider = new() { MinWidth = 320 };
    private readonly TextBox _sSparkUrl = new();
    private readonly TextBox _sSparkModel = new();
    private readonly TextBox _sSparkKey = new() { PasswordChar = '•', PlaceholderText = "VLLM_API_KEY (empty if the Spark has none)" };
    private readonly TextBox _sOtherUrl = new();
    private readonly TextBox _sOtherModel = new() { PlaceholderText = "empty = the first model the server lists" };
    private readonly TextBox _sOtherKey = new() { PasswordChar = '•', PlaceholderText = "API key (if needed)" };
    private readonly ComboBox _sGuardianModel = new() { MinWidth = 320 };
    private readonly ComboBox _sEffort = new() { ItemsSource = new[] { "low", "high", "max" } };
    private readonly NumericUpDown _sFps = new() { Minimum = 0.5m, Maximum = 10, Increment = 0.5m, FormatString = "0.0" };
    private readonly NumericUpDown _sWidth = new() { Minimum = 960, Maximum = 3840, Increment = 160 };
    private readonly NumericUpDown _sSteps = new() { Minimum = 10, Maximum = 300, Increment = 10 };
    private readonly CheckBox _sTyped = new() { Content = "Record the text that is typed (off: only that typing happened — safer around passwords)" };
    private readonly CheckBox _sVideo = new() { Content = "Upload recordings to Guardian's Video intake when publishing" };
    private readonly TextBox _sFfmpeg = new() { PlaceholderText = "ffmpeg path (optional; finds it on PATH)" };
    private readonly TextBlock _sResult = Label("", "muted");
    private readonly StackPanel _sSparkBox = new() { Spacing = 8 };
    private readonly StackPanel _sOtherBox = new() { Spacing = 8 };
    private readonly StackPanel _sGuardianBox = new() { Spacing = 8 };

    private static readonly string[] ProviderNames =
    [
        "GLM-5.3 Flash on DGX Sparks (MiaAI-Lab recipe)",
        "Guardian's models (needs Guardian sign-in)",
        "Other OpenAI-compatible server (Vision / Spark Swapper / Ollama / LM Studio)",
    ];

    private Control BuildSettingsTab()
    {
        var s = _ws.Settings;
        _sProvider.ItemsSource = ProviderNames;
        _sProvider.SelectedIndex = (int)s.Provider;
        _sProvider.SelectionChanged += (_, _) => ShowProviderBoxes();
        _sSparkUrl.Text = s.SparkBaseUrl;
        _sSparkModel.Text = s.SparkModel;
        _sSparkKey.Text = _ws.Secrets.Read(SecretNames.SparkApiKey) ?? "";
        _sOtherUrl.Text = s.OtherBaseUrl;
        _sOtherModel.Text = s.OtherModel;
        _sOtherKey.Text = _ws.Secrets.Read(SecretNames.OtherApiKey) ?? "";
        _sEffort.SelectedItem = s.ReasoningEffort is "high" or "max" ? s.ReasoningEffort : "low";
        _sFps.Value = (decimal)s.FramesPerSecond;
        _sWidth.Value = s.FrameMaxWidth;
        _sSteps.Value = s.MaxSteps;
        _sTyped.IsChecked = s.CaptureTypedText;
        _sVideo.IsChecked = s.UploadVideoToGuardian;
        _sFfmpeg.Text = s.FfmpegPath;
        RefreshModelChoices();

        _sSparkBox.Children.Add(Field("Spark head address", _sSparkUrl, "The recipe serves http://<head>:8888/v1. Through a Spark Swapper HTTPS front use https://<spark>:11443/v1."));
        _sSparkBox.Children.Add(Field("Model id", _sSparkModel, "GLM-5.3-Flash-EXL3 (the recipe's served name)."));
        _sSparkBox.Children.Add(Field("API key", _sSparkKey));
        _sOtherBox.Children.Add(Field("Base URL", _sOtherUrl));
        _sOtherBox.Children.Add(Field("Model", _sOtherModel));
        _sOtherBox.Children.Add(Field("API key", _sOtherKey));
        _sGuardianBox.Children.Add(Field("Guardian model", _sGuardianModel, "Charged to you within your organization's Guardian limits."));

        var save = new Button { Content = "Save" };
        save.Click += (_, _) => SaveSettings();
        var test = new Button { Content = "Save and test the model" };
        test.Click += async (_, _) =>
        {
            SaveSettings();
            _sResult.Text = "Asking the model…";
            try
            {
                var client = await _ws.CreateChatClientAsync();
                var r = await Task.Run(() => client.CompleteAsync([ChatMessage.User("Reply with exactly: ready")], new ChatOptions { MaxTokens = 256, ReasoningEffort = _ws.Settings.ReasoningEffort }));
                _sResult.Text = $"✓ {client.Describe} answered: {r.Content.Trim()}";
            }
            catch (Exception e)
            {
                _sResult.Text = "✗ " + e.Message;
            }
        };
        var data = new Button { Content = "Open data folder" };
        data.Click += (_, _) => Open(AppPaths.Root);

        ShowProviderBoxes();
        return Page(Stack(0,
            Card(Stack(10, Label("AI processing", "h2"), Field("Model server", _sProvider), _sSparkBox, _sGuardianBox, _sOtherBox,
                Field("Thinking effort", _sEffort, "GLM-5.3 always thinks; low is fastest and keeps long structured answers reliable."),
                Field("Most steps described per recording", _sSteps))),
            Card(Stack(10, Label("Recording", "h2"),
                Field("Frames per second", _sFps, "2 is plenty for desk work; key frames are kept whenever something changes or you click."),
                Field("Largest frame width (pixels)", _sWidth), _sTyped)),
            Card(Stack(10, Label("Guardian", "h2"), _sVideo, Field("ffmpeg", _sFfmpeg, "With ffmpeg the upload is a small MP4; without it, the MJPEG AVI goes as is."))),
            Row(8, save, test, data), _sResult));
    }

    private void ShowProviderBoxes()
    {
        var p = (AiProvider)Math.Max(0, _sProvider.SelectedIndex);
        _sSparkBox.IsVisible = p == AiProvider.Spark;
        _sGuardianBox.IsVisible = p == AiProvider.Guardian;
        _sOtherBox.IsVisible = p == AiProvider.OpenAiCompatible;
    }

    private void RefreshModelChoices()
    {
        var items = new List<string> { "(first model Guardian offers)" };
        items.AddRange(_guardianModels);
        if (_ws.Settings.GuardianModel.Length > 0 && !items.Contains(_ws.Settings.GuardianModel)) items.Add(_ws.Settings.GuardianModel);
        _sGuardianModel.ItemsSource = items;
        _sGuardianModel.SelectedItem = _ws.Settings.GuardianModel.Length > 0 ? _ws.Settings.GuardianModel : items[0];
    }

    private void SaveSettings()
    {
        var s = _ws.Settings;
        s.Provider = (AiProvider)Math.Max(0, _sProvider.SelectedIndex);
        s.SparkBaseUrl = OpenAiChatClient.NormalizeBaseUrl(_sSparkUrl.Text) ?? s.SparkBaseUrl;
        s.SparkModel = _sSparkModel.Text?.Trim() ?? "";
        s.OtherBaseUrl = OpenAiChatClient.NormalizeBaseUrl(_sOtherUrl.Text) ?? s.OtherBaseUrl;
        s.OtherModel = _sOtherModel.Text?.Trim() ?? "";
        s.GuardianModel = _sGuardianModel.SelectedIndex > 0 ? _sGuardianModel.SelectedItem as string ?? "" : "";
        s.ReasoningEffort = _sEffort.SelectedItem as string ?? "low";
        s.FramesPerSecond = (double)(_sFps.Value ?? 2);
        s.FrameMaxWidth = (int)(_sWidth.Value ?? 1920);
        s.MaxSteps = (int)(_sSteps.Value ?? 60);
        s.CaptureTypedText = _sTyped.IsChecked == true;
        s.UploadVideoToGuardian = _sVideo.IsChecked == true;
        s.FfmpegPath = _sFfmpeg.Text?.Trim() ?? "";
        s.Save();
        void Secret(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) _ws.Secrets.Delete(name);
            else _ws.Secrets.Write(name, value.Trim());
        }
        Secret(SecretNames.SparkApiKey, _sSparkKey.Text);
        Secret(SecretNames.OtherApiKey, _sOtherKey.Text);
        _sSparkUrl.Text = s.SparkBaseUrl;
        _sOtherUrl.Text = s.OtherBaseUrl;
        _sResult.Text = "Saved.";
    }
}
