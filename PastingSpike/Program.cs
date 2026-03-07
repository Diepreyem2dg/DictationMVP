using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PastingSpike
{
    public class HiddenForm : Form
    {
        // Win32 API for Global Keyboard Hook
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;

        // Recording state
        private bool _isRecording = false;
        private bool _ctrlPressed = false;
        private WaveInEvent? _waveIn;
        private WaveFileWriter? _waveWriter;
        private MemoryStream _recordedAudioStream = new MemoryStream();
        
        // Groq STT Integration
        private string _groqApiKey = GetGroqApiKey();
        private static readonly HttpClient _httpClient = new HttpClient();
        private string _currentTranscript = "";
        private Stopwatch _postSpeechTimer = new Stopwatch();

        private static string GetGroqApiKey()
        {
            // 1. Try Environment Variable first
            string key = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";
            if (!string.IsNullOrEmpty(key)) return key;

            // 2. Fallback to reading from a local config.txt file
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
            if (File.Exists(configPath))
            {
                key = File.ReadAllText(configPath).Trim();
                if (!string.IsNullOrEmpty(key)) return key;
            }
            
            return "";
        }

        public HiddenForm()
        {
            this.Opacity = 0;
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            _proc = HookCallback;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _hookID = SetHook(_proc);
            
            Console.WriteLine("=============================================");
            Console.WriteLine("⚡ Groq Dictation MVP - Instant REST ⚡");
            Console.WriteLine("=============================================");
            
            if (string.IsNullOrEmpty(_groqApiKey))
            {
                Console.WriteLine("\n[WARNING] Groq API Key is missing! Please add it to config.txt");
                Console.WriteLine("Transcription will fail. Please set it before recording.");
            }
            else 
            {
                Console.WriteLine($"\n[INFO] Loaded Groq API Key: {_groqApiKey.Substring(0, 5)}...");
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _groqApiKey);
                
                // CRITICAL FIX: Disable Expect: 100-continue header.
                // .NET HttpClient waits exactly 3000ms for the server to acknowledge form uploads before sending bytes.
                // Disabling this eliminates the arbitrary 3-second latency block.
                _httpClient.DefaultRequestHeaders.ExpectContinue = false;
                System.Net.ServicePointManager.Expect100Continue = false;
                
                // Pre-warm the TLS connection so the first dictation doesn't suffer a 3-second handshake delay
                Console.WriteLine("[INFO] Pre-warming Groq connection...");
                _ = Task.Run(async () => {
                    try { await _httpClient.GetAsync("https://api.groq.com/openai/v1/models"); } catch { }
                    Console.WriteLine("[INFO] Groq Pipeline Ready!");
                });
            }
            
            Console.WriteLine("\nHold LEFT CTRL and press SPACE to start dictating.");
            Console.WriteLine("Release SPACE to stop and trigger instant paste.");
            Console.WriteLine("Press Ctrl+C in this console to exit.\n");
        }

        private void PasteTranscriptAndReset()
        {
            _currentTranscript = _currentTranscript.Trim();
            Console.WriteLine($"✅ Final Transcript Built: \"{_currentTranscript}\"");

            if (!string.IsNullOrWhiteSpace(_currentTranscript))
            {
                this.Invoke((MethodInvoker)delegate {
                    Clipboard.SetText(_currentTranscript);
                    
                    Thread.Sleep(50); // Minor UI delay to let keys raise physically
                    SendKeys.SendWait("^v");
                    
                    Console.WriteLine($"📋 Pasted! STT Latency (Key-Release to Paste): {_postSpeechTimer.ElapsedMilliseconds}ms\n");
                });
            }

            // Mute the microphone stream but KEEP the Deepgram WebSocket fully alive.
            _currentTranscript = "";
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                
                // Track Left Ctrl state (VK_LCONTROL = 162)
                if (vkCode == 162) 
                {
                    if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN) _ctrlPressed = true;
                    if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP) _ctrlPressed = false;
                }

                // Track Space (VK_SPACE = 32)
                if (vkCode == 32)
                {
                    if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
                    {
                        if (_ctrlPressed && !_isRecording)
                        {
                            StartRecording(); 
                            return (IntPtr)1; 
                        }
                    }
                    else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
                    {
                        if (_isRecording)
                        {
                            _ = StopRecordingAsync(); 
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void StartRecording()
        {
            if (string.IsNullOrEmpty(_groqApiKey))
            {
                Console.WriteLine("[WARNING] Cannot record: Groq API Key is not set.");
                return;
            }

            _isRecording = true;
            _currentTranscript = ""; // Reset transcript for new dictation
            Console.WriteLine("🔴 Recording started...");

            try
            {
                _recordedAudioStream = new MemoryStream();
                
                // Setup Audio Capture
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = 0,
                    WaveFormat = new WaveFormat(16000, 16, 1) // 16kHz, 16-bit, Mono - optimal for Whisper
                };
                
                _waveWriter = new WaveFileWriter(_recordedAudioStream, _waveIn.WaveFormat);
                
                _waveIn.DataAvailable += (s, a) =>
                {
                    _waveWriter.Write(a.Buffer, 0, a.BytesRecorded);
                };
                
                _waveIn.StartRecording();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting recording: {ex.Message}");
                _isRecording = false;
            }
        }

        private async Task StopRecordingAsync()
        {
            _isRecording = false;
            _postSpeechTimer.Restart(); // Start true latency timer here!
            Console.WriteLine($"⏹️ Recording stopped. Sending {Math.Round((double)_recordedAudioStream.Length / 1024, 1)} KB to Groq...");

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }

            if (_waveWriter != null)
            {
                // CRITICAL FIX: We MUST call Dispose() on WaveFileWriter, otherwise the WAV RIFF header 
                // chunk sizes remain 0 (corrupted file). Groq's ffmpeg backend chokes on 0-length headers 
                // and stalls for 10 seconds trying to decode it manually.
                _waveWriter.Dispose(); 
                _waveWriter = null; 
            }

            try
            {
                using var form = new MultipartFormDataContent();
                
                // Extract the byte array. .ToArray() works perfectly even if the underlying MemoryStream 
                // was disposed by the WaveFileWriter above!
                byte[] audioBytes = _recordedAudioStream.ToArray();
                var fileContent = new ByteArrayContent(audioBytes);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
                form.Add(fileContent, "file", "dictation.wav");
                
                // Add the model parameter
                form.Add(new StringContent("whisper-large-v3-turbo"), "model");
                form.Add(new StringContent("en"), "language"); // Optional: forces English for slightly faster/accurate response

                var networkTimer = Stopwatch.StartNew();
                var response = await _httpClient.PostAsync("https://api.groq.com/openai/v1/audio/transcriptions", form);
                networkTimer.Stop();
                Console.WriteLine($"[Profile] Groq API Network Time: {networkTimer.ElapsedMilliseconds}ms");
                
                if (response.IsSuccessStatusCode)
                {
                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(jsonResponse))
                    {
                        if (doc.RootElement.TryGetProperty("text", out JsonElement textElement))
                        {
                            _currentTranscript = textElement.GetString() ?? "";
                        }
                    }
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[ERROR] Groq API returned status {response.StatusCode}: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Groq processing failed: {ex.Message}");
            }
            finally
            {
                _postSpeechTimer.Stop();
                PasteTranscriptAndReset();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            UnhookWindowsHookEx(_hookID);
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new HiddenForm());
        }
    }
}
