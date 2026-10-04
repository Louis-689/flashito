// Flashito — app de bandeja para Windows (sin instalador, sin servidor).
// Lee el texto que subrayás en cualquier programa (vía portapapeles),
// lo envía como JSON a la API de IA que configures y muestra la respuesta
// en una burbujita discreta abajo a la izquierda de la pantalla.
// Este programa no ejecuta comandos del sistema ni accede a bases de datos.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

namespace Flashito
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (new Mutex(true, "Flashito.SingleInstance", out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MessagePump());
            }
        }
    }

    // ---------------- Log de diagnóstico (%APPDATA%\Flashito\log.txt) ----------------

    internal static class Log
    {
        public static void Write(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Flashito",
                        "log.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    // ---------------- Configuración ----------------

    internal class Config
    {
        public string endpoint = "https://api.openai.com/v1/chat/completions";
        public string apiKey = "";
        public string model = "gpt-4o-mini";
        public string hotkey = "Ctrl+Alt+F";

        public static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Flashito");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.json");
            }
        }

        public static Config Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
            }
            catch { }
            return new Config();
        }

        public void Save()
        {
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this));
        }
    }

    // ---------------- Ventana mensajera (hotkey global + bandeja) ----------------

    internal class MessagePump : Form
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_C = 0x43;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private readonly Config _config = Config.Load();
        private NotifyIcon _tray;
        private bool _busy;

        public MessagePump()
        {
            Hide();
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Log.Write("handle creado, registrando atajo…");
            CreateTray();
            RegisterHotKeyWithParser();
            if (string.IsNullOrWhiteSpace(_config.apiKey))
                BeginInvoke(new Action(OpenSettings)); // diferido: fuera del ciclo de creación del handle
        }

        // Registra el atajo elegido; si el sistema lo tiene ocupado por otra app,
        // prueba combinaciones alternativas, avisa con un globo y guarda la que quedó.
        private void RegisterHotKeyWithParser()
        {
            TryRegister(_config.hotkey);
        }

        private void TryRegister(string combo)
        {
            UnregisterHotKey(Handle, HOTKEY_ID);
            uint mods; uint key;
            ParseHotkey(combo, out mods, out key);
            if (key == 0) { mods = 2 | 1; key = 0x46; } // Ctrl+Alt+F por defecto
            bool ok = RegisterHotKey(Handle, HOTKEY_ID, mods | 0x4000, key);
            Log.Write("RegisterHotKey(" + combo + ") = " + ok);
            if (ok) return;

            string[] fallbacks = { "Ctrl+Alt+G", "Ctrl+Alt+H", "Ctrl+Alt+J", "Ctrl+Alt+K", "Ctrl+Shift+F1", "Ctrl+Alt+F2" };
            foreach (string fb in fallbacks)
            {
                if (string.Equals(fb, combo, StringComparison.OrdinalIgnoreCase)) continue;
                ParseHotkey(fb, out mods, out key);
                if (RegisterHotKey(Handle, HOTKEY_ID, mods | 0x4000, key))
                {
                    _config.hotkey = fb;
                    _config.Save();
                    if (_tray != null)
                        _tray.ShowBalloonTip(
                            5000,
                            "Flashito",
                            "El atajo " + combo + " está ocupado por otra app. Ahora uso " + fb +
                            " (podés cambiarlo en Configuración).",
                            ToolTipIcon.Info);
                    return;
                }
            }
        }

        private static void ParseHotkey(string text, out uint mods, out uint key)
        {
            mods = 0; key = 0;
            if (string.IsNullOrEmpty(text)) return;
            var parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string p = parts[i].Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") mods |= 2;
                else if (p == "alt") mods |= 1;
                else if (p == "shift") mods |= 4;
                else if (p == "win") mods |= 8;
            }
            string k = parts[parts.Length - 1].Trim().ToUpperInvariant();
            if (k.Length == 1 && k[0] >= 'A' && k[0] <= 'Z') key = k[0];
            else if (k.Length == 1 && k[0] >= '0' && k[0] <= '9') key = k[0];
            else if (k.StartsWith("F") && k.Length <= 3)
            {
                int f;
                if (int.TryParse(k.Substring(1), out f) && f >= 1 && f <= 12) key = (uint)(0x70 + f - 1);
            }
        }

        private void CreateTray()
        {
            _tray = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
                Text = "Flashito — " + _config.hotkey + " para resolver lo subrayado",
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Configuración…", null, (s, e) => OpenSettings());
            menu.Items.Add("Probar ahora", null, (s, e) => RunSolve());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Salir", null, (s, e) => { _tray.Visible = false; Application.Exit(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => OpenSettings();
        }

        private void OpenSettings()
        {
            using (var f = new SettingsForm(_config))
            {
                if (f.ShowDialog() == DialogResult.OK)
                {
                    _config.Save();
                    TryRegister(_config.hotkey); // re-registra si cambiaron el atajo
                    if (_tray != null) _tray.Text = "Flashito — " + _config.hotkey + " para resolver lo subrayado";
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                Log.Write("WM_HOTKEY recibido, id=" + m.WParam.ToInt32());
                if (m.WParam.ToInt32() == HOTKEY_ID)
                {
                    RunSolve();
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private async void RunSolve()
        {
            if (_busy) return;
            Log.Write("RunSolve iniciado");
            _busy = true;
            try
            {
                BubbleForm.ShowStatus("Flashito está leyendo lo subrayado…");
                string selection = await CaptureSelectionAsync();
                Log.Write("selección leída: " + (selection == null ? "null" : selection.Length + " chars"));
                if (selection != null && selection.Length > 1200)
                {
                    // Entradas enormes hacen que los modelos razonadores quemen
                    // todos los tokens pensando y no respondan nada.
                    selection = selection.Substring(0, 1200);
                    Log.Write("selección recortada a 1200 chars");
                }
                if (string.IsNullOrWhiteSpace(selection))
                {
                    BubbleForm.ShowError("Subrayá el texto de la pregunta y volvé a apretar " + _config.hotkey);
                    return;
                }
                BubbleForm.ShowStatus("Pensando…");
                string answer = await AiClient.AskAsync(_config, selection);
                Log.Write("respuesta IA: " + answer);
                BubbleForm.ShowAnswer(answer);
            }
            catch (Exception ex)
            {
                BubbleForm.ShowError(ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        // Copia la selección al portapapeles con Ctrl+C sintético, la lee y
        // restaura el portapapeles. Primero coloca un marcador temporal para
        // no confundir el contenido anterior con una selección que no se copió.
        // Libera antes los modificadores (Ctrl/Alt/…)
        // porque el atajo recién apretado puede dejarlos "pegados": sin eso el
        // Ctrl+C llega como Ctrl+Alt+C y la ventana no copia nada.
        private static async Task<string> CaptureSelectionAsync()
        {
            IDataObject savedData = null;
            string marker = "Flashito.ClipboardMarker." + Guid.NewGuid().ToString("N");
            try
            {
                savedData = Clipboard.GetDataObject();
                Clipboard.SetText(marker);
            }
            catch (Exception ex) { Log.Write("guardar portapapeles falló: " + ex.Message); }

            Log.Write("foco='" + ForegroundTitle() + "', portapapeles preparado");

            await Task.Delay(90);
            ReleaseModifiers();
            await Task.Delay(30);

            // Ctrl+C vía keybd_event, con micro-pausas para que la ventana focalizada lo procese.
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            await Task.Delay(20);
            keybd_event(VK_C, 0, 0, UIntPtr.Zero);
            await Task.Delay(20);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(20);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            string text = null;
            for (int i = 0; i < 14; i++)
            {
                await Task.Delay(100);
                try { text = Clipboard.GetText(); } catch (Exception ex) { Log.Write("leer portapapeles falló: " + ex.Message); }
                if (!string.IsNullOrWhiteSpace(text) && text != marker) break;
                text = null;
            }
            Log.Write("tras Ctrl+C: " + (text == null ? "null" : text.Length + " chars"));

            try
            {
                if (savedData != null) Clipboard.SetDataObject(savedData, true);
                else Clipboard.Clear();
            }
            catch (Exception ex) { Log.Write("restaurar portapapeles falló: " + ex.Message); }
            return text;
        }

        private static void ReleaseModifiers()
        {
            keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Ctrl
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Alt
            keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Shift
            keybd_event(0x5B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Win izq
            keybd_event(0x5C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Win der
        }

        private static string ForegroundTitle()
        {
            var sb = new StringBuilder(256);
            GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
            return sb.ToString();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            UnregisterHotKey(Handle, HOTKEY_ID);
            base.OnFormClosed(e);
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    }

    // ---------------- Burbuja de respuesta (abajo a la izquierda) ----------------

    internal class BubbleForm : Form
    {
        private static BubbleForm _instance;
        private readonly Label _label;
        private readonly System.Windows.Forms.Timer _autoHide;

        public BubbleForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(17, 20, 26);
            TopMost = true;
            Padding = new Padding(10, 5, 10, 5);

            _label = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(243, 244, 246),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };
            Controls.Add(_label);
            _label.Click += (s, e) => HideBubble();

            _autoHide = new System.Windows.Forms.Timer { Interval = 2000 };
            _autoHide.Tick += (s, e) => HideBubble();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x8;        // WS_EX_TOPMOST
                cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW (sin aparición en Alt+Tab)
                cp.ExStyle |= 0x8000000;  // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath())
            {
                int r = 12;
                var b = new Rectangle(0, 0, Width - 1, Height - 1);
                path.AddArc(b.X, b.Y, r, r, 180, 90);
                path.AddArc(b.Right - r, b.Y, r, r, 270, 90);
                path.AddArc(b.Right - r, b.Bottom - r, r, r, 0, 90);
                path.AddArc(b.X, b.Bottom - r, r, r, 90, 90);
                path.CloseFigure();
                using (var brush = new SolidBrush(Color.FromArgb(235, 17, 20, 26)))
                {
                    Region = new Region(path);
                    e.Graphics.FillPath(brush, path);
                }
            }
            base.OnPaint(e);
        }

        private void Reposition()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int maxText = 380;
            Size textSize = TextRenderer.MeasureText(_label.Text, _label.Font);
            int w = Math.Min(maxText, textSize.Width) + Padding.Horizontal + 4;
            int h = _label.Font.Height + Padding.Vertical + 2;
            Size = new Size(Math.Max(w, 72), h);
            Location = new Point(wa.Left + 9, wa.Bottom - Height - 9);
        }

        private void ShowInternal(string prefix, Color prefixColor, string text, int hideMs)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => ShowInternal(prefix, prefixColor, text, hideMs))); return; }
            _label.Text = prefix + text;
            _label.ForeColor = prefixColor;
            Reposition();
            _autoHide.Stop();
            _autoHide.Interval = hideMs;
            _autoHide.Start();
            Show();
            TopMost = true;
            Refresh();
            Log.Write("burbuja visible (" + hideMs + "ms): " + prefix + text + " @ " + Location.X + "," + Location.Y + " " + Width + "x" + Height);
        }

        public void HideBubble()
        {
            _autoHide.Stop();
            Hide();
        }

        public static void ShowStatus(string text)
        {
            // El estado se queda hasta que lo reemplace la respuesta o un error
            // (60s de tope por si algo se cuelga).
            Get().ShowInternal("⏳ ", Color.FromArgb(147, 197, 253), text, 60000);
        }

        public static void ShowAnswer(string text)
        {
            Get().ShowInternal("✓ ", Color.FromArgb(74, 222, 128), text, 2000);
        }

        public static void ShowError(string text)
        {
            Get().ShowInternal("✕ ", Color.FromArgb(248, 113, 113), text, 10000);
        }

        private static BubbleForm Get()
        {
            if (_instance == null || _instance.IsDisposed)
                _instance = new BubbleForm();
            return _instance;
        }
    }

    // ---------------- Cliente de IA (HTTP + JSON) ----------------

    internal static class AiClient
    {
        static AiClient()
        {
            // Forzar TLS 1.2: sin esto algunos endpoints modernos rechazan la conexión.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        private const string SystemPrompt =
            "Sos un resolutor de quizzes. El usuario te manda un JSON con \"pregunta\" y \"opciones\". " +
            "Respondé ÚNICAMENTE con la respuesta correcta en UNA sola línea muy corta, sin explicaciones ni markdown. " +
            "Si hay opciones, empezá con la letra correcta y el texto de la opción (ej: \"D) 4\"). " +
            "Si no hay opciones, respondé brevemente. Respondé en el idioma de la pregunta.";

        public static async Task<string> AskAsync(Config cfg, string selectedText)
        {
            if (string.IsNullOrWhiteSpace(cfg.apiKey))
                throw new Exception("Falta la API key: abrí Flashito → Configuración.");

            Uri uri = ValidUri(cfg.endpoint);

            string question;
            List<string> options = PromptBuilder.ExtractOptions(selectedText, out question);
            var userContent = new Dictionary<string, object>
            {
                { "pregunta", question },
                { "opciones", (options.Count >= 2) ? options.ToArray() : new string[0] }
            };
            var payload = new Dictionary<string, object>
            {
                { "model", cfg.model },
                { "temperature", 0 },
                // Tope corto: alcanza para una respuesta de quiz y evita el error 402
                // de OpenRouter cuando el saldo es bajo.
                { "max_tokens", 4000 },
                { "messages", new object[]
                    {
                        new Dictionary<string, object> { { "role", "system" }, { "content", SystemPrompt } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", new JavaScriptSerializer().Serialize(userContent) } }
                    }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.apiKey);
            request.Content = new StringContent(
                new JavaScriptSerializer().Serialize(payload), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException || (ex is TimeoutException))
                    throw new Exception("Timeout: el modelo tardó demasiado.");
                string detail = ex.Message;
                if (ex.InnerException != null) detail += " — " + ex.InnerException.Message;
                throw new Exception("No pude conectar con el endpoint: " + detail);
            }

            string body = await response.Content.ReadAsStringAsync();
            Log.Write("HTTP " + (int)response.StatusCode + " body: " + Short(body, 500));
            if (!response.IsSuccessStatusCode)
                throw new Exception("Error " + (int)response.StatusCode + ": " + Short(body, 140));

            var map = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
            if (map == null) throw new Exception("Respuesta inválida del endpoint.");
            object choicesObj;
            if (!map.TryGetValue("choices", out choicesObj))
                throw new Exception("El modelo no devolvió respuesta.");
            // JavaScriptSerializer trae los arrays JSON como ArrayList (o object[]),
            // así que el cast correcto es IEnumerable no genérico.
            var choices = choicesObj as System.Collections.IEnumerable;
            if (choices == null) throw new Exception("El modelo no devolvió respuesta.");
            foreach (object choice in choices)
            {
                var choiceMap = choice as Dictionary<string, object>;
                object messageObj;
                if (choiceMap == null || !choiceMap.TryGetValue("message", out messageObj)) continue;
                var messageMap = messageObj as Dictionary<string, object>;
                if (messageMap == null) continue;

                object contentObj;
                messageMap.TryGetValue("content", out contentObj);
                string content = contentObj == null ? "" : contentObj.ToString().Trim();

                // Modelos razonadores: si "content" quedó vacío, el razonamiento
                // llega por "reasoning" (OpenRouter) o "reasoning_content".
                if (content.Length == 0)
                {
                    object reasoningObj;
                    if (messageMap.TryGetValue("reasoning", out reasoningObj) && reasoningObj != null)
                        return LastMeaningfulLine(reasoningObj.ToString());
                    if (messageMap.TryGetValue("reasoning_content", out reasoningObj) && reasoningObj != null)
                        return LastMeaningfulLine(reasoningObj.ToString());
                }
                if (content.Length > 0) return FirstLine(content);
            }
            throw new Exception("El modelo no devolvió respuesta.");
        }

        private static Uri ValidUri(string raw)
        {
            Uri uri;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new Exception("El endpoint debe ser una URL http(s) válida.");
            return uri;
        }

        private static string Short(string s, int max)
        {
            s = (s ?? "").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static string FirstLine(string text)
        {
            string t = StripThink(text);
            foreach (string line in t.Split('\n'))
            {
                string l = line.Trim().Trim('"', '\'', '`').Trim();
                if (l.Length > 0) return l.Length > 200 ? l.Substring(0, 200) : l;
            }
            return "Sin respuesta";
        }

        private static string LastMeaningfulLine(string text)
        {
            string t = StripThink(text);
            string last = "";
            foreach (string line in t.Split('\n'))
            {
                string l = line.Trim().Trim('"', '\'', '`').Trim();
                if (l.Length > 0) last = l;
            }
            return last.Length > 200 ? last.Substring(0, 200) : (last.Length > 0 ? last : "Sin respuesta");
        }

        // Saca bloques de razonamiento <think>…</think> que algunos modelos emiten.
        private static string StripThink(string text)
        {
            string t = (text ?? "").Trim();
            int close = t.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (close >= 0) t = t.Substring(close + 8);
            return t.Replace("<think>", "").Replace("</think>", "").Trim();
        }
    }

    // ---------------- Armado del prompt + detección de opciones A) B) C) ----------------

    internal static class PromptBuilder
    {
        // Detecta marcadores tipo "A)", "A.", "A:", "(A)", "Opción A:", en una o varias líneas.
        private static readonly Regex Marker = new Regex(
            @"(?:\bopci[oó]n(?:es)?|\balternativas?|\balt)?\s*\(?\s*([a-j])\s*[)\].:\-–—]\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<string> ExtractOptions(string rawText, out string question)
        {
            string raw = (rawText ?? "").Trim();
            question = raw;
            return Extract(raw, out question);
        }

        private static List<string> Extract(string raw, out string question)
        {
            question = raw;
            var marks = new List<Match>();
            Match m = Marker.Match(raw);
            while (m.Success)
            {
                marks.Add(m);
                if (m.Length == 0) break;
                m = Marker.Match(raw, m.Index + m.Length);
            }

            // Se queda con la secuencia más larga de letras consecutivas empezando en "a".
            List<Match> best = null;
            List<Match> cur = null;
            foreach (Match mk in marks)
            {
                char expected = cur == null ? 'a' : (char)('a' + cur.Count);
                if (char.ToLowerInvariant(mk.Groups[1].Value[0]) == expected)
                {
                    if (cur == null) cur = new List<Match>();
                    cur.Add(mk);
                }
                else
                {
                    if (cur != null && cur.Count >= 2 && (best == null || cur.Count > best.Count)) best = cur;
                    cur = (char.ToLowerInvariant(mk.Groups[1].Value[0]) == 'a') ? new List<Match> { mk } : null;
                }
            }
            if (cur != null && cur.Count >= 2 && (best == null || cur.Count > best.Count)) best = cur;
            if (best == null) return new List<string>();

            string q = raw.Substring(0, best[0].Index).Trim().TrimEnd(',', ';').Trim();
            var options = new List<string>();
            for (int i = 0; i < best.Count; i++)
            {
                int from = best[i].Index + best[i].Length;
                int to = (i + 1 < best.Count) ? best[i + 1].Index : raw.Length;
                string opt = raw.Substring(from, to - from).Trim().TrimEnd(',', ';').Trim();
                if (opt.Length > 0) options.Add(opt);
            }
            if (options.Count < 2) return new List<string>();
            question = (q.Length > 0) ? q : raw.Substring(0, best[0].Index).Trim();
            return options;
        }
    }

    // ---------------- Ventana de configuración ----------------

    internal class SettingsForm : Form
    {
        private readonly Config _config;
        private readonly TextBox _endpoint;
        private readonly TextBox _apiKey;
        private readonly TextBox _model;
        private readonly TextBox _hotkey;
        private readonly Label _status;

        public SettingsForm(Config config)
        {
            _config = config;
            Text = "Flashito — Configuración";
            Width = 460;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);

            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(14),
                WrapContents = false,
                AutoScroll = true
            };

            panel.Controls.Add(MakeLabel("Endpoint (compatible con OpenAI):"));
            _endpoint = MakeText(config.endpoint);
            panel.Controls.Add(_endpoint);

            panel.Controls.Add(MakeLabel("API key:"));
            _apiKey = MakeText(config.apiKey);
            _apiKey.UseSystemPasswordChar = true;
            panel.Controls.Add(_apiKey);

            panel.Controls.Add(MakeLabel("Modelo:"));
            _model = MakeText(config.model);
            panel.Controls.Add(_model);

            panel.Controls.Add(MakeLabel("Atajo global (ej: Ctrl+Alt+F):"));
            _hotkey = MakeText(config.hotkey);
            panel.Controls.Add(_hotkey);

            _status = new Label { Text = " ", ForeColor = Color.DimGray, AutoSize = true };
            panel.Controls.Add(_status);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            var save = new Button { Text = "Guardar", AutoSize = true };
            save.Click += (s, e) =>
            {
                _config.endpoint = _endpoint.Text.Trim();
                _config.apiKey = _apiKey.Text.Trim();
                _config.model = _model.Text.Trim();
                _config.hotkey = _hotkey.Text.Trim();
                DialogResult = DialogResult.OK;
                Close();
            };
            var test = new Button { Text = "Probar conexión", AutoSize = true };
            test.Click += async (s, e) =>
            {
                _status.Text = "Probando…";
                var probe = new Config
                {
                    endpoint = _endpoint.Text.Trim(),
                    apiKey = _apiKey.Text.Trim(),
                    model = _model.Text.Trim(),
                    hotkey = _hotkey.Text.Trim()
                };
                try
                {
                    string answer = await AiClient.AskAsync(probe, "Test de conexión. Respondé exactamente: OK");
                    _status.ForeColor = Color.ForestGreen;
                    _status.Text = "OK — el modelo respondió: " + answer;
                }
                catch (Exception ex)
                {
                    _status.ForeColor = Color.Firebrick;
                    _status.Text = ex.Message;
                }
            };
            buttons.Controls.Add(save);
            buttons.Controls.Add(test);
            panel.Controls.Add(buttons);

            var hint = new Label
            {
                Text = "Cómo usarlo: subrayá la pregunta (con sus opciones si tiene) y apretá el atajo.\nLa respuesta aparece abajo a la izquierda. Clic en la burbuja la cierra.",
                ForeColor = Color.DimGray,
                AutoSize = true,
                MaximumSize = new Size(410, 0)
            };
            panel.Controls.Add(hint);

            Controls.Add(panel);
            AcceptButton = save;
        }

        private static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 8, 0, 2) };
        }

        private static TextBox MakeText(string value)
        {
            return new TextBox { Text = value ?? "", Width = 410, Margin = new Padding(0, 0, 0, 2) };
        }
    }
}
