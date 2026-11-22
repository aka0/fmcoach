// FM Coach v0.6 - AI chat client (OpenRouter). Pure managed code on the CoreCLR side of BepInEx: HttpClient on a
// background Task, results handed back to the Unity main thread through a queue. Never touches game state.
// The API key is read from a local file at request time and is never logged, stored elsewhere or embedded.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public sealed class ChatResult
{
    public int Id; public int Generation; public string Question; public string Reply; public string Mood = "neutral";
    public int Status; public long LatencyMs; public int PromptTokens, CompletionTokens; public double Cost;
    public string Error; public bool FromTestHook; public string Model; public string Finish;
}

public sealed class CoachChat
{
    public const string Endpoint = "https://openrouter.ai/api/v1/chat/completions"; // the only host the plugin talks to
    public string Model = "anthropic/claude-haiku-5.5";
    public int MaxTokens = 600;
    public int TimeoutSeconds = 60;
    public string KeyFile;
    public readonly ConcurrentQueue<ChatResult> Done = new();
    public bool Busy => Volatile.Read(ref busy) != 0;
    int busy, nextId, generation;
    public bool IsCurrent(ChatResult result) => result.Generation == Volatile.Read(ref generation);
    static HttpClient http;
    // conversation for this session: (role, content); content of assistant turns keeps its [mood:x] tag
    readonly List<(string role, string content)> history = new();
    const int MaxHistoryMessages = 20; // ~10 turns

    static readonly Regex MoodTag = new(@"\[\s*mood\s*:\s*([a-zA-Z]+)\s*\]", RegexOptions.Compiled);
    static readonly string[] Moods = { "neutral", "pleased", "disgusted", "worried", "angry", "smug", "disagree" };

    public static string ReadKey(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
        foreach (var raw in File.ReadAllLines(file))
        {
            var l = raw.Trim().Trim('\uFEFF');
            if (l.Length == 0 || l.StartsWith("#")) continue;
            if (l.StartsWith("KEY=", StringComparison.OrdinalIgnoreCase)) l = l.Substring(4).Trim();
            l = l.Trim('"', '\'', ' ');
            if (l.Length > 10) return l;
        }
        return null;
    }

    public static string Redact(string s) => s == null ? null : Regex.Replace(s, @"sk-[A-Za-z0-9_\-]{6,}", "[redacted]");

    /// Splits "text ... [mood:x]" into clean text and a mood tag (neutral if missing/unknown).
    public static (string text, string mood) SplitMood(string reply)
    {
        if (reply == null) return ("", "neutral");
        string mood = "neutral";
        foreach (Match m in MoodTag.Matches(reply)) { var t = m.Groups[1].Value.ToLowerInvariant(); if (Array.IndexOf(Moods, t) >= 0 || t == "disapproving") mood = t == "disapproving" ? "disagree" : t; }
        var text = MoodTag.Replace(reply, "").Trim();
        return (text, mood);
    }

    public void ClearHistory()
    {
        lock (history) { generation++; history.Clear(); }
        while (Done.TryDequeue(out _)) { }
    }

    /// Starts one request on a background task. systemPrompt and contextJson are built on the main thread.
    public bool Ask(string question, string systemPrompt, string contextJson, bool fromTestHook, string about = null)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        int id = Interlocked.Increment(ref nextId);
        List<(string role, string content)> hist; int requestGeneration;
        lock (history) { hist = new List<(string, string)>(history); requestGeneration = generation; }
        string model = Model; int maxTok = MaxTokens; int timeout = TimeoutSeconds; string keyFile = KeyFile;
        Task.Run(async () =>
        {
            var r = new ChatResult { Id = id, Generation = requestGeneration, Question = question, FromTestHook = fromTestHook, Model = model };
            var sw = Stopwatch.StartNew();
            try
            {
                var key = ReadKey(keyFile);
                if (key == null) { r.Error = "No API key: put KEY=... in " + keyFile + " (or set [AI] KeyFile in BepInEx\\config\\local.fmcoach.poc.cfg)"; return; }
                http ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var msgs = new List<Dictionary<string, string>>
                {
                    new() { ["role"] = "system", ["content"] = systemPrompt },
                    new() { ["role"] = "system", ["content"] = "CONTEXT JSON (read from the game just now; the only facts you may use):\n" + contextJson },
                };
                foreach (var (hrole, hcontent) in hist) msgs.Add(new() { ["role"] = hrole, ["content"] = hcontent });
                // v0.6.3: every manager turn carries the player on screen when it was asked, so later turns with a
                // different (or no) player open do not make the model "correct" earlier, valid answers.
                var tagged = "[about: " + (string.IsNullOrWhiteSpace(about) ? "no player open" : about) + "] " + question;
                msgs.Add(new() { ["role"] = "user", ["content"] = tagged });
                var body = new Dictionary<string, object>
                {
                    ["model"] = model, ["messages"] = msgs, ["max_tokens"] = maxTok, ["temperature"] = 0.7,
                    ["reasoning"] = new Dictionary<string, object> { ["enabled"] = false },
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                req.Headers.TryAddWithoutValidation("X-Title", "FM Coach (local plugin)");
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
                r.Status = (int)resp.StatusCode;
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!resp.IsSuccessStatusCode || !root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                {
                    string msg = json.Length > 300 ? json.Substring(0, 300) : json;
                    if (root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var em)) msg = em.GetString();
                    r.Error = "HTTP " + r.Status + ": " + Redact(msg); return;
                }
                var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                if (choices[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) r.Finish = fr.GetString();
                if (root.TryGetProperty("usage", out var u))
                {
                    if (u.TryGetProperty("prompt_tokens", out var pt)) r.PromptTokens = pt.GetInt32();
                    if (u.TryGetProperty("completion_tokens", out var ct)) r.CompletionTokens = ct.GetInt32();
                    if (u.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number) r.Cost = c.GetDouble();
                }
                var (text, mood) = SplitMood(content);
                r.Reply = text; r.Mood = mood;
                lock (history)
                {
                    if (requestGeneration != generation) return;
                    history.Add(("user", tagged)); history.Add(("assistant", content.Trim()));
                    while (history.Count > MaxHistoryMessages) history.RemoveAt(0);
                }
            }
            catch (TaskCanceledException) { r.Error = "Timed out after " + timeout + " s"; }
            catch (Exception ex) { r.Error = ex.GetType().Name + ": " + Redact(ex.Message); }
            finally
            {
                r.LatencyMs = sw.ElapsedMilliseconds;
                if (IsCurrent(r)) Done.Enqueue(r);
                Volatile.Write(ref busy, 0);
            }
        });
        return true;
    }
}
