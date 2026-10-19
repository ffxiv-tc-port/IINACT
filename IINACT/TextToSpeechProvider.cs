using System.Speech.Synthesis;
using System.Web;
using NAudio.Wave;

namespace IINACT;

internal class TextToSpeechProvider : IDisposable
{
    private readonly object speechLock = new();
    private readonly HttpClient client = new();
    private readonly SpeechSynthesizer? speechSynthesizer;
    private readonly Configuration configuration;
    private int silencedReported;
    private int disposed;

    internal const string FallbackLabel = "Use Google TTS when Windows speech (SAPI) is unavailable";

    public TextToSpeechProvider(Configuration config)
    {
        this.configuration = config;
        if (!Dalamud.Utility.Util.IsWine())
        {
            try
            {
                speechSynthesizer = new SpeechSynthesizer();
                speechSynthesizer?.SetOutputToDefaultAudioDevice();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to initialize SAPI TTS engine");
                speechSynthesizer = null;
            }
        }
        
        Advanced_Combat_Tracker.ActGlobals.oFormActMain.TextToSpeech += Speak;
    }
    
    public void Speak(string message)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        Task.Run(() =>
        {
            try
            {
                if (configuration.ForceGoogleTts)
                    SpeakGoogle(message);
                else if (speechSynthesizer != null)
                    SpeakSapi(message);
                else if (configuration.GoogleTtsFallback)
                    SpeakGoogle(message);
                else
                    ReportSilencedOnce();
            }
            catch (Exception ex)
            {
                // 卸載時說到一半的 Speak 會擲 OperationCanceledException、之後的擲 ObjectDisposedException，不是錯誤。
                if (Volatile.Read(ref disposed) == 0)
                    Plugin.Log.Error(ex, $"TTS failed to play back {message}");
            }
        });
    }

    private void SpeakGoogle(string message)
    {
        var query = HttpUtility.UrlEncode(message);
        var lang = configuration.GoogleTtsLanguage;
        if (string.IsNullOrWhiteSpace(lang)) lang = "en";
        var url = $"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl={lang}&q={query}";
        var mp3Data = client.GetByteArrayAsync(url).Result;

        using var stream = new MemoryStream(mp3Data);
        using var reader = new Mp3FileReader(stream);
        using var waveOut = new WaveOutEvent();
        waveOut.DeviceNumber = configuration.TtsPlaybackDevice;
        waveOut.Init(reader);
        var waitHandle = new ManualResetEventSlim(false);
        
        lock (speechLock)
        {
            waveOut.Play();
            waveOut.PlaybackStopped += (s, e) => waitHandle.Set();
            waitHandle.Wait();
        }
    }
    
    private void ReportSilencedOnce()
    {
        if (Interlocked.Exchange(ref silencedReported, 1) != 0)
            return;
        Plugin.Log.Information(
            "TTS: Windows speech (SAPI) is unavailable, so IINACT is not speaking. " +
            $"To use Google TTS instead (sends the spoken text to Google), enable \"{FallbackLabel.Loc()}\" " +
            $"in the IINACT window's \"{"Text to Speech".Loc()}\" tab.");
    }

    private void SpeakSapi(string message)
    {
        lock (speechLock)
        {
            if (Volatile.Read(ref disposed) == 0)
                speechSynthesizer?.Speak(message);
        }
    }

    // SAPI 的背景執行緒在 Dispose 前一直活著，會讓卸載後的插件留在記憶體裡。
    // 刻意不拿 speechLock：Google TTS 播放時會握著它好幾秒；CancelAll 讓正在同步 Speak 的執行緒立刻返回。
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        var form = Advanced_Combat_Tracker.ActGlobals.oFormActMain;
        if (form != null)
            form.TextToSpeech -= Speak;

        try
        {
            speechSynthesizer?.SpeakAsyncCancelAll();
            speechSynthesizer?.Dispose();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to dispose SAPI TTS engine");
        }

        client.Dispose();
    }
}
