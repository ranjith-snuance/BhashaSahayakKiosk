using Azure;
using Azure.AI.OpenAI;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using OpenAI;
using OpenAI.Chat;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using WpfAnimatedGif;
using Kiosk.Infrastructure;
using System.Text.Json;
using System.IO;

namespace Kiosk
{
    public partial class MainWindow : Window
    {
        // Configuration-backed fields
        private string _currentLanguage = "en-US";
        private readonly OpenAISettings _openAI;
        private readonly SpeechSettings _speech;
        private readonly PdfSettings _pdf;
        private readonly Dictionary<string, List<string>> _templates;

        // Runtime state
        private ImageAnimationController? _avatarAnimationController;

        public MainWindow()
        {
            InitializeComponent();

            // Load configuration
            _openAI = AppConfig.OpenAI;
            _speech = AppConfig.Speech;
            _pdf = AppConfig.Pdf;

            // Load templates from external JSON (fallback to empty)
            var templatePath = Path.Combine(AppContext.BaseDirectory, "letterTemplates.json");
            if (File.Exists(templatePath))
                _templates = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(templatePath)) ?? new();
            else
                _templates = new();

            QuestPDF.Settings.License = LicenseType.Community;
            InitAvatar();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }
        DragMoveSafe();
    }

    private void DragMoveSafe()
    {
        try { DragMove(); } catch { }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void RestoreButton_Click(object sender, RoutedEventArgs e) => ToggleWindowState();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
        // -------- Avatar Handling --------
        private void InitAvatar()
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("Assets/avatar_talking.gif", UriKind.Relative);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            ImageBehavior.AddAnimationLoadedHandler(AvatarImage, (_, _) =>
            {
                var controller = ImageBehavior.GetAnimationController(AvatarImage);
                if (controller != null)
                {
                    _avatarAnimationController = controller;
                    StopAvatarAnimation(true);
                }
            });

            ImageBehavior.SetAnimatedSource(AvatarImage, bmp);
        }

        private void StartAvatarAnimation()
        {
            if (_avatarAnimationController == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(StartAvatarAnimation);
                return;
            }
            _avatarAnimationController.Play();
        }

        private void StopAvatarAnimation(bool resetToFirstFrame = false)
        {
            if (_avatarAnimationController == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => StopAvatarAnimation(resetToFirstFrame));
                return;
            }
            _avatarAnimationController.Pause();
            if (resetToFirstFrame)
            {
                try { _avatarAnimationController.GotoFrame(0); } catch { }
            }
        }

        private void ShowTalkingAvatar() => StartAvatarAnimation();
        private void ShowIdleAvatar() => StopAvatarAnimation(true);

        // -------- Event Handlers --------

       

        // OPTIONAL: central voice resolver
        private string GetVoiceForLanguage(string lang) => lang switch
        {
            "kn-IN" => _speech.Voices.Kannada,
            _ => _speech.Voices.English
        };
        private async void VoiceButton_Click(object sender, RoutedEventArgs e)
        {
            VoiceButton.IsEnabled = false;
            try
            {
                SubtitleText.Text = string.Empty;
                ShowIdleAvatar();
                Log($"Listening in {_currentLanguage}...");

                var speechConfig = CreateSpeechConfig(_currentLanguage);
                var firstUtterance = await RecognizeSingleUtteranceAsync(speechConfig);
                if (string.IsNullOrWhiteSpace(firstUtterance))
                {
                    Log("No speech detected. Exiting.");
                    return;
                }
                Log($"[You] {firstUtterance}");

                var systemPrompt = BuildSystemPrompt(_currentLanguage);
                var chatClient = CreateChatClient();
                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(systemPrompt),
                    new UserChatMessage(firstUtterance)
                };

                while (true)
                {
                    ShowIdleAvatar();
                    var assistantText = await GetAssistantReplyAsync(chatClient, messages);
                    messages.Add(new AssistantChatMessage(assistantText));

                    var voiceName = SelectVoice(assistantText);
                    var ttsConfig = CreateSpeechConfig(_currentLanguage);
                    ttsConfig.SpeechSynthesisVoiceName = voiceName;

                    ShowTalkingAvatar();
                    await SpeakAsync(ttsConfig, assistantText);
                    ShowIdleAvatar();

                    if (assistantText.Contains("[END_OF_LETTER]"))
                    {
                        SavePdf(assistantText);
                        Log("✅ Letter saved as PDF.");
                        break;
                    }

                    Log("Please answer (speak now)...");
                    var userTurn = await RecognizeSingleUtteranceAsync(speechConfig);
                    if (string.IsNullOrWhiteSpace(userTurn))
                    {
                        Log("No speech detected, asking again...");
                        continue;
                    }
                    Log($"[You] {userTurn}");
                    messages.Add(new UserChatMessage(userTurn));
                }
            }
            catch (Exception ex)
            {
                Log($"[Error] {ex.Message}");
            }
            finally
            {
                ShowIdleAvatar();
                VoiceButton.IsEnabled = true;
            }
        }

        // ADD handler
        private void LanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (LanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
                item.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                _currentLanguage = tag;
                if (SubtitleText != null)
                    Log($"Language set to: {_currentLanguage}");
            }
        }

        // -------- Chat + Speech Helpers --------
        private ChatClient CreateChatClient()
        {
            var key = SecretResolver.Resolve(_openAI.ApiKey);
            if (string.IsNullOrWhiteSpace(_openAI.Endpoint))
                throw new InvalidOperationException("OpenAI endpoint missing.");
            if (string.IsNullOrWhiteSpace(_openAI.DeploymentName))
                throw new InvalidOperationException("DeploymentName missing.");
            var client = new AzureOpenAIClient(new Uri(_openAI.Endpoint), new AzureKeyCredential(key));
            return client.GetChatClient(_openAI.DeploymentName);
        }

        private string BuildSystemPrompt(string targetLanguage)
        {
            var templateJson = JsonSerializer.Serialize(_templates);
            return $@"
            You are a public documentation assistant.
            User selected conversation language: {targetLanguage}
            All assistant questions and answers MUST be in that language unless explicitly asked to switch.
            Available letter templates (with required fields):
            {templateJson}
            Rules:
            1. Identify best matching template.
            2. Collect required fields step by step.
            3. Do not generate letter until all required fields + target language collected.
            4. Ask user for final output language.
            5. Then generate only the letter ending with [END_OF_LETTER]. No extra text.";
        }

        // Fixes for CS1998 and CA1822
        private static Task<string> GetAssistantReplyAsync(ChatClient chatClient, List<ChatMessage> messages)
        {
            // The method does not use 'await', so mark it static and return Task.FromResult
            var streamingResponse = chatClient.CompleteChatStreaming(messages);
            var sb = new StringBuilder();
            foreach (var update in streamingResponse)
                foreach (var part in update.ContentUpdate)
                    if (!string.IsNullOrEmpty(part.Text))
                        sb.Append(part.Text);
            return Task.FromResult(sb.ToString().Trim());
        }


        private SpeechConfig CreateSpeechConfig(string recognitionLanguage)
        {
            var key = SecretResolver.Resolve(_speech.SubscriptionKey);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Speech subscription key is missing.");
            const string region = "eastus";
            var config = SpeechConfig.FromSubscription(key, region);
            config.SpeechRecognitionLanguage = string.IsNullOrWhiteSpace(recognitionLanguage)
                ? (_speech.RecognitionLanguage ?? "en-US")
                : recognitionLanguage;
            config.SetProperty(
                PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs,
                (_speech.EndSilenceTimeoutMs > 0 ? _speech.EndSilenceTimeoutMs : 800).ToString());
            return config;
        }

        private string SelectVoice(string assistantText)
        {
            bool isKannada = assistantText.Any(c => c >= 0x0C80 && c <= 0x0CFF);
            return isKannada ? _speech.Voices.Kannada : _speech.Voices.English;
        }

        private async Task<string> RecognizeSingleUtteranceAsync(SpeechConfig config)
        {
            using var audio = AudioConfig.FromDefaultMicrophoneInput();
            using var recognizer = new SpeechRecognizer(config, audio);
            var result = await recognizer.RecognizeOnceAsync();
            return result.Reason switch
            {
                ResultReason.RecognizedSpeech => result.Text,
                ResultReason.NoMatch => string.Empty,
                ResultReason.Canceled => HandleCancel(result),
                _ => string.Empty
            };
        }

        private string HandleCancel(SpeechRecognitionResult r)
        {
            var details = CancellationDetails.FromResult(r);
            Log($"Recognition canceled: {details.Reason} {details.ErrorDetails}");
            return string.Empty;
        }

        private async Task SpeakAsync(SpeechConfig ttsConfig, string text)
        {
            using var synth = new SpeechSynthesizer(ttsConfig);
            var result = await synth.SpeakTextAsync(text);
            if (result.Reason == ResultReason.Canceled)
            {
                var details = SpeechSynthesisCancellationDetails.FromResult(result);
                Log($"[TTS Error] {details.ErrorDetails}");
            }
        }

        private void SavePdf(string content)
        {
            var file = $"{Guid.NewGuid():N}.pdf";
            Document.Create(c =>
            {
                c.Page(p =>
                {
                    p.Size(QuestPDF.Helpers.PageSizes.A4);
                    p.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
                    p.Content().Text(content).FontSize(12).FontFamily(_pdf.FontFamily);
                });
            }).GeneratePdf(file);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = true
            });
        }

        // -------- Logging --------
        private void Log(string message)
        {
            SubtitleText.Text = message;
        }
    }
}