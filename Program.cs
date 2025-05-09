﻿// Program.cs (.NET 7+ minimal API)

using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
var app     = builder.Build();

// ————— CONFIG —————
var remoteBase = Environment.GetEnvironmentVariable("OPENWEBUI_ENDPOINT");
var apiKey     = Environment.GetEnvironmentVariable("OPENWEBUI_API_KEY");
// ——————————————————

var httpClient = new HttpClient { BaseAddress = new Uri(remoteBase) };
httpClient.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", apiKey);


// Generic proxy helper
static async Task Proxy(HttpContext ctx, HttpClient client, string path, 
    MemoryStream? cache = null, string? contentType = null, bool logData = false)
{
    if (cache != null && cache.Length > 0)
    {
        if (logData)
            Console.Error.WriteLine("Ignoring LogData since it is cached!");
        ctx.Response.ContentType = contentType ?? "application/json";
        await ctx.Response.Body.WriteAsync(cache.GetBuffer(), 0, (int)cache.Length);
        return;
    }
    
    // build the upstream URI
    var upstream = path + ctx.Request.QueryString;

    // start building our outgoing HttpRequestMessage
    var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstream);

    // if there's a request body, attach it as stream content
    if (ctx.Request.ContentLength.GetValueOrDefault() > 0
        || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        // wrap the raw request body in StreamContent
        StreamContent sc;
        if (logData)
        {
            var mem = new MemoryStream();
            ctx.Request.EnableBuffering();
            await ctx.Request.Body.CopyToAsync(mem);
            mem.Position = 0;
            await Console.Error.WriteLineAsync("----------- RAW REQUEST -----------\n" + Encoding.UTF8.GetString(mem.GetBuffer(), 0, (int)mem.Length));
            sc = new StreamContent(mem);
        }
        else
        {
            sc = new StreamContent(ctx.Request.Body);
        }
        
        // copy the Content-Type header if present
        if (!string.IsNullOrEmpty(ctx.Request.ContentType))
            sc.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ctx.Request.ContentType);

        // **remove** any Content-Length header so HttpClient doesn't try to enforce it
        sc.Headers.Remove("Content-Length");

        req.Content = sc;

        // ask HttpClient to use chunked transfer encoding
        req.Headers.TransferEncodingChunked = true;
    }

    // copy *other* headers (User-Agent, Authorization, custom, etc.), but skip Host
    foreach (var h in ctx.Request.Headers)
    {
        if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
        req.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
    }

    // send upstream and ask to stream the response headers & body as they come
    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

    // copy status code
    ctx.Response.StatusCode = (int)resp.StatusCode;

    // copy response headers
    foreach (var h in resp.Headers)
        ctx.Response.Headers[h.Key] = h.Value.ToArray();
    foreach (var h in resp.Content.Headers)
        ctx.Response.Headers[h.Key] = h.Value.ToArray();

    // remove chunked encodings that Kestrel will set itself
    ctx.Response.Headers.Remove("transfer-encoding");
    if (cache != null)
    {
        if (logData)
            Console.Error.WriteLine("Ignoring LogData since it is cached!");
        await resp.Content.CopyToAsync(cache);
        cache.Position = 0;
        await ctx.Response.Body.WriteAsync(cache.GetBuffer(), 0, (int)cache.Length);
    }
    else
    {
        // finally, stream the response body back to the caller
        if (logData)
        {
            var pipe = new Pipe();
            await using var pipeReader = pipe.Reader.AsStream();
            var contentEncoding = resp.Content.Headers.ContentEncoding;
            Task logTask;
            {
                await using var pipeWriter = pipe.Writer.AsStream();
                logTask = Task.Run(async () =>
                {
                    var reader = ReadDecodedLines(contentEncoding, pipeReader);
                    // you can add BrotliStream, DeflateStream, etc. here
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (!string.IsNullOrEmpty(line))
                            Console.Error.WriteLine("---- RAW RESPONSE LINE (decompressed) ----\n" + line);
                    }
                });

                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int n;
                    var stream = await resp.Content.ReadAsStreamAsync();
                    while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        // 6a) to the client
                        await ctx.Response.Body.WriteAsync(buffer, 0, n);
                        // 6b) into our tap buffer
                        await pipeWriter.WriteAsync(buffer, 0, n);
                    }

                    pipeWriter.Flush();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            await logTask;
        }
        else
        {
            await resp.Content.CopyToAsync(ctx.Response.Body);
        }
    }
}

// 1) /api/tags  → remote GET /api/models  → Ollama shape
MemoryStream? cachedData = null;
app.MapGet("/api/tags", async ctx =>
{
    if (cachedData != null)
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.Body.WriteAsync(cachedData.GetBuffer(), 0, (int)cachedData.Length);
        return;
    }
    
    // 1) fetch remote model list
    var upstream = await httpClient.GetAsync("/api/models");
    ctx.Response.StatusCode = (int)upstream.StatusCode;

    if (!upstream.IsSuccessStatusCode)
    {
        // proxy errors straight through
        await upstream.Content.CopyToAsync(ctx.Response.Body);
        return;
    }

    // 2) parse JSON
    using var stream = await upstream.Content.ReadAsStreamAsync();
    using var doc    = await JsonDocument.ParseAsync(stream);
    var root         = doc.RootElement;

    // 3) unwrap "data": [...] if present
    var arr = root;
    if (root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("data", out var maybeData)
        && maybeData.ValueKind == JsonValueKind.Array)
    {
        arr = maybeData;
    }

    // 4) include each model entry: unwrap provider metadata when available, otherwise include full entry
    var list = new List<JsonElement>();
    foreach (var item in arr.EnumerateArray())
    {
        JsonElement modelEntry;
        
        // ===========================
        // Helper to build `JsonElement` in the ollama shape
        // ===========================
        static JsonElement BuildOllamaElement(string id, long createdEpoch)
        {
            // Convert Unix epoch seconds → ISO 8601 string
            var modifiedAt = DateTimeOffset
                .FromUnixTimeSeconds(createdEpoch)
                .ToString("o");
            
            static string HashWithSHA256(string value)
            {
                using var hash = SHA256.Create();
                var byteArray = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
                return Convert.ToHexString(byteArray).ToLowerInvariant();
            }
            // Anonymous object that matches your ollama schema
            var ollamaObj = new
            {
                name        = id,
                model       = id,
                modified_at = modifiedAt,
                size        = 1000000L,      // fill in if you know it
                digest      = HashWithSHA256(id),      // same
                details     = new
                {
                    parent_model       = "local",
                    format             = "local",
                    family             = "local",
                    families           = new[] { id },
                    parameter_size     = "100b",
                    quantization_level = ""
                },
                urls = new[] { 0 }     // placeholder
            };

            // Serialize → parse back into a JsonElement
            var json = JsonSerializer.Serialize(ollamaObj);
            return JsonDocument.Parse(json).RootElement;
        }
        
        // 1) Already an ollama response?  Just unwrap it.
        if (item.TryGetProperty("ollama", out var ollamaVal))
        {
            modelEntry = ollamaVal.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(ollamaVal.GetString()!).RootElement
                : ollamaVal;
        }
        // 2) Nested OpenAI provider block?  Unwrap and map.
        else if (item.TryGetProperty("openai", out var openAiVal))
        {
           // continue;
            var id           = openAiVal.GetProperty("id").GetString()!;
            var createdEpoch = openAiVal.GetProperty("created").GetInt64();
            modelEntry = BuildOllamaElement(id, createdEpoch);
        }
        // 3) “Flat” other model object (the one with top‐level "id", "created", etc.)
        else if (item.TryGetProperty("id", out var flatId)
                 && item.TryGetProperty("created", out var flatCreated))
        {
           // continue;
            var id           = flatId.GetString()!;
            var createdEpoch = flatCreated.GetInt64();
            modelEntry = BuildOllamaElement(id, createdEpoch);
        }
        // 4) Otherwise give them back exactly as they came in
        else
        {
            Console.Error.WriteLine("Could not convert model: " + item);
            continue;
        }

        list.Add(modelEntry);
    }

    // 5) write back as JSON array of tags (models)
    ctx.Response.ContentType = "application/json";
    var mem = new MemoryStream();
    await JsonSerializer.SerializeAsync(mem, new { models = list });
    cachedData = mem;
    await ctx.Response.Body.WriteAsync(cachedData.GetBuffer(), 0, (int)cachedData.Length);
});

// 2) /api/generate  → remote /api/generate
app.MapPost("/api/generate", ctx => Proxy(ctx, httpClient, "/api/generate"));

// 3) /api/chat      → remote /api/chat/completions
// Replace your old /api/chat proxy with this:
app.MapPost("/api/chat", async ctx =>
{
    // 1) Buffer the incoming JSON so we can inject "stream": true
    using var msIn = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(msIn);
    var inJson = Encoding.UTF8.GetString(msIn.ToArray());

    using var inDoc = JsonDocument.Parse(inJson);
    var root       = inDoc.RootElement;
    
    string? model = null;

    // 2) Re-serialize, copying all props and forcing stream=true
    using var msBody = new MemoryStream();
    await using (var w = new Utf8JsonWriter(msBody))
    {
        w.WriteStartObject();
        var hasStream = false;
        foreach (var p in root.EnumerateObject())
        {
            if (p.Name == "stream")
            {
                hasStream = true;
            }

            if (p.Name == "model")
            {
                model = p.Value.GetString();
            }

            if (p.Name == "keep_alive" || p.Name == "options")
            {
                // TODO: how to do this in ollama?
                continue;
            }

            p.WriteTo(w);
        }

        if (!hasStream)
        {
            w.WriteBoolean("stream", true);
        }
        w.WriteEndObject();
    }
    var newBody = msBody.ToArray();

    // 3) Send to remote with stream=true
    var req = new HttpRequestMessage(HttpMethod.Post,
                "/api/chat/completions")
    {
        Content = new ByteArrayContent(newBody)
    };
    req.Content.Headers.ContentType =
        new MediaTypeHeaderValue("application/json");

    using var upResp = await httpClient.SendAsync(
        req,
        HttpCompletionOption.ResponseHeadersRead
    );

    // 4) Prepare our response
    ctx.Response.StatusCode  = (int)upResp.StatusCode;
    if (upResp.StatusCode != HttpStatusCode.OK)
    {
        ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        var error = await upResp.Content.ReadAsStringAsync();
        Console.Error.WriteLine("Upstream responded with:" + error);
        
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new { error = "some error occured", details = error });
        return;
    }
    ctx.Response.ContentType = "application/x-ndjson";
    //ctx.Response.Headers["X-Accel-Buffering"] = "no";

    // helper: read lines from SSE stream
    async IAsyncEnumerable<string> ReadLines(Stream s)
    {
        using var rdr = new StreamReader(s);
        while (!rdr.EndOfStream)
        {
            var line = await rdr.ReadLineAsync();
            if (line is not null) yield return line;
        }
    }

    var sw = Stopwatch.StartNew();
    
    // 5) Transform and forward each SSE chunk
    bool doneSent = false;
    await foreach (var line in ReadLines(await upResp.Content.ReadAsStreamAsync()))
    {
        if (doneSent && !string.IsNullOrEmpty(line) && !line.Contains("[DONE]"))
        {
            Console.Error.WriteLine("ADDITIONAL LINE RECEIVED AFTER DONE: " + line);
            continue;
        }
        
        if (!line.StartsWith("data:")) continue;
        var payload = line["data:".Length..].Trim();
        if (payload == "[DONE]") break;

        using var chunkDoc = JsonDocument.Parse(payload);
        var chunk = chunkDoc.RootElement;

        // pull common fields
        model = chunk.GetProperty("model").GetString()!;
        var createdSecs = chunk.GetProperty("created").GetInt64();
        var createdAt = DateTimeOffset
                          .FromUnixTimeSeconds(createdSecs)
                          .UtcDateTime
                          .ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "Z";

        // choice & delta
        var choices = chunk.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            Console.Error.WriteLine("No choices found: " + chunk);
            continue;
        }
        var choice = chunk.GetProperty("choices")[0];
        var delta  = choice.GetProperty("delta");
        var content = delta.TryGetProperty("content", out var c)
                      ? c.GetString() ?? ""
                      : "";

        // finish_reason
        var finishTok = choice.GetProperty("finish_reason");
        var isDone    = finishTok.ValueKind != JsonValueKind.Null;
        if (isDone)
        {
            doneSent = true;
            sw.Stop();
        }

        // Start writing our Ollama object
        string json = await WriteOllamaObject(model, createdAt, content, isDone, finishTok.GetString(), chunk, sw);
        await ctx.Response.WriteAsync(json + (isDone ? "\n\n" : "\n"));
        await ctx.Response.Body.FlushAsync();

    }

    if (!doneSent)
    {
        sw.Stop();
        var createdAt = DateTimeOffset.UtcNow
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "Z";
        string json = await WriteOllamaObject(model ?? "unknown", createdAt, 
            "", true, "stop", null, sw);
        await ctx.Response.WriteAsync(json + "\n\n");
        await ctx.Response.Body.FlushAsync();
    }
});



// 4) /api/embed     → remote /api/embed
app.MapPost("/api/embed",    ctx => Proxy(ctx, httpClient, "/api/embed"));

// 5) /api/version   → remote /api/version  (if supported)
app.MapGet("/api/version",   ctx => Proxy(ctx, httpClient, "/api/version"));

// 6) /   → remote /  (if supported)
//var statusCache = new MemoryStream();
app.MapGet("/",  async ctx =>
{
    await ctx.Response.WriteAsync("Ollama is running");
    await ctx.Response.Body.FlushAsync();
});

app.MapPost("/openai/api/chat/completions",   ctx => Proxy(ctx, httpClient, "/api/chat/completions", logData: true));
app.MapGet("/openai/api/models",   ctx => Proxy(ctx, httpClient, "/api/models", logData: true));


app.Run("http://*:4222");

async Task<string> WriteOllamaObject(string mode, string createdAt, string content, bool done, string? finishReason,
    JsonElement? rawRoot, Stopwatch stopwatch)
{
    using var outMs = new MemoryStream();
    await using (var jw = new Utf8JsonWriter(outMs))
    {
        jw.WriteStartObject();

        jw.WriteString("model",      mode);
        jw.WriteString("created_at", createdAt);

        // message:{role,content}
        jw.WritePropertyName("message");
        jw.WriteStartObject();
        jw.WriteString("role",    "assistant");
        jw.WriteString("content", content);
        jw.WriteEndObject();

        if (!done)
        {
            // intermediate chunk
            jw.WriteBoolean("done", false);
        }
        else
        {
            // final chunk
            jw.WriteString   ("done_reason", finishReason);
            jw.WriteBoolean  ("done",        true);

            // pull the usage object if present
            if ((rawRoot?.TryGetProperty("usage", out var usage) ?? false) 
                && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("total_duration", out var td))
                    jw.WriteNumber("total_duration", td.GetInt64());
                else
                {
                    jw.WriteNumber("total_duration", (long)stopwatch.Elapsed.TotalNanoseconds);
                }
                if (usage.TryGetProperty("load_duration", out var ld))
                    jw.WriteNumber("load_duration",  ld.GetInt64());
                else
                {
                    jw.WriteNumber("load_duration", 0L);
                }
                if (usage.TryGetProperty("prompt_eval_count", out var pec))
                    jw.WriteNumber("prompt_eval_count", pec.GetInt32());
                else
                {
                    jw.WriteNumber("prompt_eval_count", 0);
                }
                if (usage.TryGetProperty("prompt_eval_duration", out var ped))
                    jw.WriteNumber("prompt_eval_duration", ped.GetInt64());
                else
                {
                    jw.WriteNumber("prompt_eval_duration", 0L);
                }
                if (usage.TryGetProperty("eval_count", out var ec))
                    jw.WriteNumber("eval_count", ec.GetInt32());
                else
                {
                    jw.WriteNumber("eval_count", 0);
                }
                if (usage.TryGetProperty("eval_duration", out var ed))
                    jw.WriteNumber("eval_duration", ed.GetInt64());
                else
                {
                    jw.WriteNumber("eval_duration", 0L);
                }
            }
            else
            {
                jw.WriteNumber("total_duration", (long)stopwatch.Elapsed.TotalNanoseconds);
                jw.WriteNumber("load_duration", 0L);
                jw.WriteNumber("prompt_eval_count", 0);
                jw.WriteNumber("prompt_eval_duration", 0L);
                jw.WriteNumber("eval_count", 0);
                jw.WriteNumber("eval_duration", 0L);
            }
        }

        jw.WriteEndObject();
    }

    // flush the JSON
    var json1 = Encoding.UTF8.GetString(outMs.ToArray());
    return json1;
}

static StreamReader ReadDecodedLines(ICollection<string> contentEncoding, Stream memoryStream)
{
    Stream? decoded = null;
    if (contentEncoding.Contains("gzip"))
    {
        decoded = new GZipStream(memoryStream, CompressionMode.Decompress);
    }
    else if (contentEncoding.Contains("br"))
    {
        decoded = new BrotliStream(memoryStream, CompressionMode.Decompress);
    }
    else
    {
        // no known encoding, assume plain
        decoded = memoryStream;
    }

    return new StreamReader(decoded);
}