using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace YoutubeClipper;

internal sealed class MainForm : Form
{
    private readonly TextBox _url = new() { PlaceholderText = "https://www.youtube.com/watch?v=..." };
    private readonly TextBox _start = new() { Text = "0:00" };
    private readonly TextBox _length = new() { Text = "30:00" };
    private readonly TextBox _clip = new() { Text = "9" };
    private readonly TextBox _prefix = new() { Text = "clip" };
    private readonly TextBox _output = new();
    private readonly Button _browse = new() { Text = "Ordner…", Width = 90 };
    private readonly Button _go = new() { Text = "Clips erstellen" };
    private readonly Button _stop = new() { Text = "Stop", Enabled = false };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private CancellationTokenSource? _cts;
    private Process? _proc;

    public MainForm()
    {
        Text = "YouTube zu SL-Clips";
        ClientSize = new Size(640, 520);
        MinimumSize = new Size(560, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        _output.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "sl-clips");

        var hint = new Label
        {
            AutoSize = false,
            Height = 32,
            Text = "WAV, 44,1 kHz, 16-bit, mono. ffmpeg und yt-dlp im PATH oder im Ordner tools.",
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 3,
            RowCount = 9,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        AddRow(root, 0, "YouTube-Link", _url, span: 2);
        AddRow(root, 1, "Start", _start, span: 2);
        AddRow(root, 2, "Länge", _length, span: 2);
        AddRow(root, 3, "Cliplänge (Sek.)", _clip, span: 2);
        AddRow(root, 4, "Prefix", _prefix, span: 2);
        AddRow(root, 5, "Ausgabeordner", _output);
        root.Controls.Add(_browse, 2, 5);
        root.Controls.Add(hint, 0, 6);
        root.SetColumnSpan(hint, 3);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        _go.AutoSize = true;
        _stop.AutoSize = true;
        buttons.Controls.Add(_go);
        buttons.Controls.Add(_stop);
        root.Controls.Add(buttons, 0, 7);
        root.SetColumnSpan(buttons, 3);

        _log.Dock = DockStyle.Fill;
        root.Controls.Add(_log, 0, 8);
        root.SetColumnSpan(_log, 3);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Controls.Add(root);

        _browse.Click += (_, _) => Browse();
        _go.Click += async (_, _) => await RunAsync();
        _stop.Click += (_, _) => Cancel();
        FormClosing += (_, _) =>
        {
            SaveSettings();
            Cancel();
        };
        AcceptButton = _go;
        LoadSettings();
    }

    private static string AppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YoutubeClipper");

    private static string SettingsPath =>
        Path.Combine(AppDir, "settings.txt");

    private void LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return;
        var lines = File.ReadAllLines(SettingsPath);
        if (lines.Length > 0) _url.Text = lines[0];
        if (lines.Length > 1 && lines[1].Length > 0) _start.Text = lines[1];
        if (lines.Length > 2 && lines[2].Length > 0) _length.Text = lines[2];
        if (lines.Length > 3 && lines[3].Length > 0) _clip.Text = lines[3];
        if (lines.Length > 4 && lines[4].Length > 0) _prefix.Text = lines[4];
        if (lines.Length > 5 && lines[5].Length > 0) _output.Text = lines[5];
    }

    private void SaveSettings()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllLines(SettingsPath, new[]
        {
            _url.Text, _start.Text, _length.Text, _clip.Text, _prefix.Text, _output.Text,
        });
    }

    private static void AddRow(TableLayoutPanel root, int row, string label, Control field, int span = 1)
    {
        root.Controls.Add(new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
        }, 0, row);
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0, 4, 8, 4);
        root.Controls.Add(field, 1, row);
        if (span > 1) root.SetColumnSpan(field, span);
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Ordner für die WAV-Clips",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_output.Text) ? _output.Text : "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _output.Text = dialog.SelectedPath;
    }

    private void Cancel()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        try
        {
            if (_proc is { HasExited: false })
                _proc.Kill(entireProcessTree: true);
        }
        catch { /* process already gone */ }
    }

    private async Task RunAsync()
    {
        if (!TryReadInputs(out var job)) return;
        SaveSettings();

        var ytdlp = FindTool("yt-dlp");
        var ffmpeg = FindTool("ffmpeg");
        if (ytdlp is null || ffmpeg is null)
        {
            Log("ffmpeg oder yt-dlp fehlt. Lege sie in den Ordner tools, oder installiere sie im PATH.");
            return;
        }

        _go.Enabled = false;
        _stop.Enabled = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            var js = await EnsureJsRuntime(token);
            await Task.Run(() => Cut(job, ytdlp, ffmpeg, js, token), token);
        }
        catch (OperationCanceledException)
        {
            Log("Abgebrochen.");
        }
        catch (Exception ex)
        {
            Log(ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _go.Enabled = true;
            _stop.Enabled = false;
        }
    }

    private bool TryReadInputs(out Job job)
    {
        job = default;
        var url = _url.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log("Kein gültiger YouTube-Link.");
            return false;
        }
        if (!TryParseTime(_start.Text, out var start))
        {
            Log("Start nicht lesbar. Beispiele: 0:00, 1:30, 90");
            return false;
        }
        if (!TryParseTime(_length.Text, out var length) || length <= 0)
        {
            Log("Länge nicht lesbar. Beispiele: 30:00, 10:00, 90");
            return false;
        }
        if (!double.TryParse(_clip.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var clip) ||
            clip < 0.5 || clip > 30)
        {
            Log("Cliplänge zwischen 0,5 und 30 Sekunden.");
            return false;
        }
        var prefix = CleanPrefix(_prefix.Text);
        if (prefix.Length == 0)
        {
            Log("Prefix fehlt.");
            return false;
        }
        var output = _output.Text.Trim();
        if (output.Length == 0)
        {
            Log("Ausgabeordner fehlt.");
            return false;
        }
        job = new Job(url, start, length, clip, prefix, output);
        return true;
    }

    private async Task<string> EnsureJsRuntime(CancellationToken token)
    {
        var deno = FindTool("deno");
        var bundled = Path.Combine(AppDir, "deno.exe");
        if (deno is null && File.Exists(bundled)) deno = bundled;
        if (deno is not null) return "deno:" + deno;

        Log("YouTube braucht Deno. Einmaliger Download …");
        Directory.CreateDirectory(AppDir);
        var asset = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "deno-aarch64-pc-windows-msvc.zip"
            : "deno-x86_64-pc-windows-msvc.zip";
        var zipPath = Path.Combine(Path.GetTempPath(), asset);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("YoutubeClipper");
        using (var response = await http.GetAsync(
            "https://github.com/denoland/deno/releases/latest/download/" + asset,
            HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(token);
            await using var dst = File.Create(zipPath);
            await src.CopyToAsync(dst, token);
        }
        ZipFile.ExtractToDirectory(zipPath, AppDir, true);
        try { File.Delete(zipPath); } catch { /* temp leftover is fine */ }
        if (!File.Exists(bundled))
        {
            var nested = Directory.GetFiles(AppDir, "deno.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (nested is null) throw new InvalidOperationException("Deno-Download hat keine deno.exe ergeben.");
            File.Copy(nested, bundled, true);
        }
        Log("Deno ist da.");
        return "deno:" + bundled;
    }

    private void Cut(Job job, string ytdlp, string ffmpeg, string jsRuntime, CancellationToken token)
    {
        var toolDir = Path.GetDirectoryName(ytdlp)!;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", toolDir + Path.PathSeparator + pathEnv);
        Directory.CreateDirectory(job.Output);
        foreach (var old in Directory.GetFiles(job.Output, job.Prefix + "_*.wav"))
            File.Delete(old);
        var temp = Directory.CreateTempSubdirectory("ytclips_").FullName;
        try
        {
            Log("Lade Audio …");
            Run(ytdlp, temp, token,
                "-f", "ba[ext=m4a]/ba",
                "--no-playlist",
                "--no-mtime",
                "--js-runtimes", jsRuntime,
                "--remote-components", "ejs:npm",
                "-o", "source.%(ext)s",
                job.Url);

            var source = Directory.GetFiles(temp).FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
            if (source is null) throw new InvalidOperationException("Download hat keine Datei erzeugt.");

            var full = Path.Combine(temp, "full.wav");
            Log("Schneide " + FormatTs(job.Start) + " +" + FormatTs(job.Length) + " und wandle nach WAV …");
            Run(ffmpeg, temp, token,
                "-y",
                "-ss", FormatTs(job.Start),
                "-t", FormatTs(job.Length),
                "-i", source,
                "-vn", "-ac", "1", "-ar", "44100",
                "-c:a", "pcm_s16le", full);

            Log("Schneide in " + job.Clip.ToString(CultureInfo.InvariantCulture) + "s-Clips …");
            var pattern = Path.Combine(job.Output, job.Prefix + "_%03d.wav");
            Run(ffmpeg, temp, token,
                "-y", "-i", full,
                "-f", "segment",
                "-segment_time", job.Clip.ToString(CultureInfo.InvariantCulture),
                "-reset_timestamps", "1",
                "-segment_start_number", "1",
                "-c", "copy",
                pattern);

            var made = Directory.GetFiles(job.Output, job.Prefix + "_*.wav").Length;
            Log("Fertig. " + made + " Dateien in " + job.Output);
            Log("Im LSL-Skript: length " + job.Clip.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* temp leftover is fine */ }
        }
    }

    private void Run(string file, string workDir, CancellationToken token, params string[] args)
    {
        token.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Note(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        if (!proc.Start()) throw new InvalidOperationException("Konnte " + Path.GetFileName(file) + " nicht starten.");
        _proc = proc;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        _proc = null;
        token.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
        {
            var tail = sb.ToString().Trim();
            if (tail.Length > 800) tail = tail[^800..];
            throw new InvalidOperationException(Path.GetFileName(file) + " Fehler " + proc.ExitCode + (tail.Length == 0 ? "" : "\n" + tail));
        }
    }

    private void Note(string line)
    {
        if (line.Contains('%') || line.StartsWith("[download]", StringComparison.Ordinal))
            Log(line);
    }

    private void Log(string line)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => Log(line));
                return;
            }
            _log.AppendText(line + Environment.NewLine);
        }
        catch (InvalidOperationException)
        {
            // Form is already closing.
        }
    }

    private static string? FindTool(string name)
    {
        var names = new[] { name + ".exe", name, name + ".cmd" };
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < 6 && dir.Length > 0; i++)
        {
            foreach (var folder in new[] { dir, Path.Combine(dir, "tools") })
            {
                foreach (var candidate in names)
                {
                    var full = Path.Combine(folder, candidate);
                    if (File.Exists(full)) return full;
                }
            }
            dir = Path.GetDirectoryName(dir) ?? "";
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in names)
            {
                var full = Path.Combine(folder, candidate);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0) return false;
        if (!text.Contains(':'))
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0;

        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        double h = 0, m, s;
        if (parts.Length == 3)
        {
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out h)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out m)) return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out s)) return false;
        }
        else
        {
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out m)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s)) return false;
        }
        seconds = h * 3600 + m * 60 + s;
        return seconds >= 0;
    }

    private static string FormatTs(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int h = (int)(seconds / 3600);
        int m = (int)(seconds % 3600 / 60);
        double s = seconds - h * 3600 - m * 60;
        return string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00.000}");
    }

    private static string CleanPrefix(string raw)
    {
        var bad = Path.GetInvalidFileNameChars();
        var chars = raw.Trim().Select(c => bad.Contains(c) || c == '%' ? '_' : c).ToArray();
        return new string(chars).Trim('_', ' ', '.');
    }

    private readonly record struct Job(string Url, double Start, double Length, double Clip, string Prefix, string Output);
}
