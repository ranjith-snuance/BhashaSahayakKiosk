using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.IO;


namespace Kiosk.Infrastructure
{
    public static class AppConfig
    {
        public static IConfigurationRoot Configuration { get; }

        static AppConfig()
        {
            Debug.Assert(File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.json")), "appsettings.json not copied.");

            Configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
        }

        public static OpenAISettings OpenAI => Configuration.GetSection("OpenAI").Get<OpenAISettings>() ?? new();
        public static SpeechSettings Speech => Configuration.GetSection("Speech").Get<SpeechSettings>() ?? new();
        public static PdfSettings Pdf => Configuration.GetSection("Pdf").Get<PdfSettings>() ?? new();
    }

    public class OpenAISettings
    {
        public string Endpoint { get; set; } = "";
        public string DeploymentName { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }

    public class SpeechSettings
    {
        public string Endpoint { get; set; } = "";
        public string SubscriptionKey { get; set; } = "";
        public string RecognitionLanguage { get; set; } = "en-US";
        public int EndSilenceTimeoutMs { get; set; } = 800;
        public VoiceSettings Voices { get; set; } = new();
    }

    public class VoiceSettings
    {
        public string English { get; set; } = "en-US-GuyNeural";
        public string Kannada { get; set; } = "kn-IN-GaganNeural";
    }

    public class PdfSettings
    {
        public string FontFamily { get; set; } = "Nirmala UI";
    }

    internal static class SecretResolver
    {
        public static string Resolve(string value) =>
            (value.StartsWith("${") && value.EndsWith("}"))
                ? (Environment.GetEnvironmentVariable(value[2..^1]) ?? string.Empty)
                : value;
    }
}