using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LocalAssistant.Configuration;

namespace Ollama.Apis.v1
{
    /// <summary>
    /// A client for interacting with the Ollama API locally. 
    /// This class provides methods to send requests to the Ollama API and receive responses based on the provided prompts and models.
    /// </summary>
    public class OllamaServiceClient
    {
        public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
        private const string OllamaApi = "/api/generate";
        public static readonly int MaxPromptLength = 15000;

        /// <summary>
        /// One client for the life of the object, like the Gmail and Telegram clients - a new
        /// <c>HttpClient</c> per call leaks a socket into TIME_WAIT each time, which a daily run
        /// gets away with and the resident <c>serve</c> loop (one call per message, plus one
        /// availability probe) does not.
        ///
        /// Its own timeout is disabled because the two call sites need very different budgets
        /// and <c>HttpClient.Timeout</c> can't change after the first request; each passes a
        /// cancellation token instead.
        /// </summary>
        private readonly HttpClient _httpClient = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        /// <summary>Budget for one generation call - the framework default this used to inherit.</summary>
        private static readonly TimeSpan GenerateTimeout = TimeSpan.FromSeconds(100);

        /// <summary>Budget for an "is the server up" probe: it either answers at once or it's down.</summary>
        private static readonly TimeSpan AvailabilityProbeTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Sends a request to the Ollama API with the specified prompt and model, and returns the generated response.
        /// </summary>
        /// <param name="promptText">The text prompt to send to the Ollama API.</param>
        /// <param name="model">The model to use for generating the response.</param>
        /// <param name="options">Optional generation / sampling overrides. When null, Ollama uses the model's own defaults.</param>
        /// <param name="keepAlive">Optional. How long Ollama keeps the model loaded in VRAM after this call (e.g. "10m", "0" to unload immediately). When null, Ollama's default applies.</param>
        /// <param name="think">For reasoning models (e.g. qwen3.5), false disables the hidden &lt;think&gt; block so the whole token budget goes to the visible answer. Null leaves the model's default behaviour.</param>
        /// <param name="format">Optional Ollama structured-output constraint: a JSON Schema node the reply must conform to (or the bare string node <c>"json"</c>). When set, the model can only emit JSON matching it. Null leaves output unconstrained.</param>
        public async Task<string> MakeRequest(
            string promptText,
            string? model = null,
            OllamaRequestOptions? options = null,
            string? keepAlive = null,
            bool? think = null,
            JsonNode? format = null)
        {
            ValidateParameters(promptText, model);

            var prompt = new OllamaApiRequest
            {
                Model = model ?? Settings.DefaultModel,
                Prompt = promptText,
                Options = options,
                KeepAlive = keepAlive,
                Think = think,
                Format = format,
            };

            var promptAsJson = JsonSerializer.Serialize(
                prompt,
                OllamaJsonContext.Default.OllamaApiRequest
            );

            using var request = new HttpRequestMessage(HttpMethod.Post, OllamaUrl + OllamaApi)
            {
                Content = new StringContent(promptAsJson, Encoding.UTF8, "application/json")
            };

            using var timeout = new CancellationTokenSource(GenerateTimeout);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var llmResponse = JsonSerializer.Deserialize(
                responseJson,
                OllamaJsonContext.Default.OllamaApiResponse
            );

            var result = llmResponse?.Response ?? string.Empty;

            // A reasoning model that ran out of token budget inside its <think> block returns an
            // empty Response but a populated Thinking. Surface that so the caller isn't left blank.
            if (string.IsNullOrWhiteSpace(result) && !string.IsNullOrWhiteSpace(llmResponse?.Thinking))
            {
                result = llmResponse.Thinking;
            }

            return result;
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(AvailabilityProbeTimeout);
                using var response = await _httpClient.GetAsync(OllamaUrl + "/", timeout.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Polls <see cref="IsAvailableAsync"/> until the server responds or <paramref name="timeout"/> elapses.
        /// </summary>
        public async Task<bool> WaitUntilAvailableAsync(TimeSpan timeout, TimeSpan? pollInterval = null)
        {
            var interval = pollInterval ?? TimeSpan.FromSeconds(3);
            var deadline = DateTime.UtcNow + timeout;

            while (true)
            {
                if (await IsAvailableAsync())
                {
                    return true;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }
                await Task.Delay(interval);
            }
        }

        private static void ValidateParameters(string promptText, string? model)
        {
            if (string.IsNullOrWhiteSpace(promptText))
            {
                throw new ArgumentException("Prompt text cannot be null or empty.", nameof(promptText));
            }
            if (promptText.Length > MaxPromptLength)
            {
                throw new ArgumentException($"Prompt text cannot exceed {MaxPromptLength} characters.", nameof(promptText));
            }
            if (promptText.Length < 10)
            {
                throw new ArgumentException("Prompt text must be at least 10 characters long.", nameof(promptText));
            }
            if (string.IsNullOrWhiteSpace(model))
            {
                return; // null / blank -> the caller gets the default model; nothing to check.
            }
            if (model.Length is < 3 or > 100)
            {
                throw new ArgumentException("Model name must be between 3 and 100 characters.", nameof(model));
            }
            // Require an explicit tag (e.g. "llama3.2:3b") but not a particular model family -
            if (!model.Contains(':'))
            {
                throw new ArgumentException("Model name must include a tag, e.g. 'llama3.2:3b'.", nameof(model));
            }
        }
    }

    public class OllamaApiRequest
    {

        /// <summary>
        /// The model name
        /// </summary>
        [Required]
        [JsonPropertyName("model")]
        public string Model { get; set; } = Settings.DefaultModel;

        /// <summary>
        /// The prompt to generate a response for
        /// </summary>
        [Required]
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// If false the response will be returned as a single response object, rather than a stream of objects
        /// </summary>
        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;

        /// <summary>
        /// Per-request generation / sampling overrides. Omitted from the payload when null.
        /// </summary>
        [JsonPropertyName("options")]
        public OllamaRequestOptions? Options { get; set; }

        /// <summary>
        /// How long to keep the model loaded after the request (e.g. "10m", "0"). Omitted when null.
        /// </summary>
        [JsonPropertyName("keep_alive")]
        public string? KeepAlive { get; set; }

        /// <summary>
        /// Reasoning models only: false disables the hidden &lt;think&gt; step. Omitted when null.
        /// </summary>
        [JsonPropertyName("think")]
        public bool? Think { get; set; }

        /// <summary>
        /// Structured-output constraint: a JSON Schema (or the bare string <c>"json"</c>) the
        /// reply must conform to. Ollama grammar-constrains generation to it. Omitted when null.
        /// </summary>
        [JsonPropertyName("format")]
        public JsonNode? Format { get; set; }
    }

    /// <summary>
    /// Maps to the Ollama request "options" object. Every field is nullable: a null field is left
    /// out of the JSON entirely, so Ollama falls back to the model's own default for that setting.
    /// Set only what you want to override.
    /// </summary>
    public class OllamaRequestOptions
    {
        /// <summary>Hard cap on generated tokens. The main lever against runaway output. -1 = unlimited.</summary>
        [JsonPropertyName("num_predict")]
        public int? NumPredict { get; set; }

        /// <summary>Context window (prompt + output) for this call. Larger = more VRAM reserved.</summary>
        [JsonPropertyName("num_ctx")]
        public int? NumCtx { get; set; }

        /// <summary>Randomness. ~0.2 for factual/deterministic work, ~0.8+ for creative.</summary>
        [JsonPropertyName("temperature")]
        public float? Temperature { get; set; }

        [JsonPropertyName("top_p")]
        public float? TopP { get; set; }

        [JsonPropertyName("top_k")]
        public int? TopK { get; set; }

        /// <summary>Penalty for repeating recent tokens. ~1.1 is a mild, safe default.</summary>
        [JsonPropertyName("repeat_penalty")]
        public float? RepeatPenalty { get; set; }

        /// <summary>Penalty for any token that has appeared at all. High values suppress the stop token; keep at 0 for short output.</summary>
        [JsonPropertyName("presence_penalty")]
        public float? PresencePenalty { get; set; }

        [JsonPropertyName("frequency_penalty")]
        public float? FrequencyPenalty { get; set; }

        /// <summary>Fixed seed for reproducible output across runs.</summary>
        [JsonPropertyName("seed")]
        public int? Seed { get; set; }

        /// <summary>Extra strings that force generation to stop when produced.</summary>
        [JsonPropertyName("stop")]
        public string[]? Stop { get; set; }

        /// <summary>
        /// Preset tuned for short, factual, single-shot summaries on a memory-constrained GPU:
        /// bounded output, low randomness, neutralised penalties, small context, fixed seed.
        /// Reasoning helps on math, multi-step logic, and tricky instructions. "What is this email about and what should I do" isn't that — it's extraction. 
        /// </summary>
        public static OllamaRequestOptions ConciseSummary => new()
        {
            NumPredict = 200,
            NumCtx = 4096,
            Temperature = 0.2f,
            TopP = 0.9f,
            TopK = 40,
            RepeatPenalty = 1.1f,
            PresencePenalty = 0f,
            FrequencyPenalty = 0f,
            Seed = 42
        };

        /// <summary>
        /// Preset for the priority-triage pass: a single-label classification, so temperature is
        /// 0 (pick the most likely label, deterministically) and the output budget is tiny -
        /// the reply is just <c>{"priority":"..."}</c>, forced valid by a schema on the request.
        /// <c>num_ctx</c> matches <see cref="ConciseSummary"/> so the model stays loaded without a
        /// context resize between the two passes.
        /// </summary>
        public static OllamaRequestOptions Triage => new()
        {
            NumPredict = 40,
            NumCtx = 4096,
            Temperature = 0f,
            TopP = 0.9f,
            TopK = 40,
            RepeatPenalty = 1.1f,
            PresencePenalty = 0f,
            FrequencyPenalty = 0f,
            Seed = 42
        };

        /// <summary>
        /// Preset for the draft-reply pass (allowlisted senders only): a short piece of prose, so
        /// a larger output budget and a little more randomness than the extraction presets, while
        /// keeping the same <c>num_ctx</c> so the model isn't reloaded.
        /// </summary>
        public static OllamaRequestOptions Drafting => new()
        {
            NumPredict = 400,
            NumCtx = 4096,
            Temperature = 0.3f,
            TopP = 0.9f,
            TopK = 40,
            RepeatPenalty = 1.15f,
            PresencePenalty = 0f,
            FrequencyPenalty = 0f,
            Seed = 42
        };

        /// <summary>
        /// Preset for chat passthrough: open-ended conversation, so a larger output budget and
        /// more randomness than the extraction presets, and no fixed seed (each reply should
        /// vary). <c>num_ctx</c> is larger than the other presets to hold a rolling transcript
        /// alongside the prompt; the model is reloaded when it swaps in from a digest/draft run,
        /// but chat is a long-lived process that pays that cost once, not per-message.
        /// </summary>
        public static OllamaRequestOptions Chat => new()
        {
            NumPredict = 800,
            NumCtx = 8192,
            Temperature = 0.7f,
            TopP = 0.9f,
            TopK = 40,
            RepeatPenalty = 1.1f,
            PresencePenalty = 0f,
            FrequencyPenalty = 0f
        };
    }

    public class OllamaApiResponse
    {
        /// <summary>
        /// The model name used
        /// </summary>
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        /// <summary>
        /// Empty if the response was streamed, if not streamed, this will contain the full response
        /// </summary>
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;

        /// <summary>
        /// The hidden reasoning text, when a reasoning model produced one (separate from Response).
        /// </summary>
        [JsonPropertyName("thinking")]
        public string Thinking { get; set; } = string.Empty;

        /// <summary>
        /// Whether generation has finished
        /// </summary>
        [JsonPropertyName("done")]
        public bool Done { get; set; }

        /// <summary>
        /// Reason the generation stopped
        /// </summary>
        [JsonPropertyName("done_reason")]
        public string DoneReason { get; set; } = string.Empty;

        /// <summary>
        /// An encoding of the conversation used in this response, this can be sent in the next request to keep a conversational memory
        /// </summary>
        [JsonPropertyName("context")]
        public int[] Context { get; set; } = Array.Empty<int>();

        /// <summary>
        /// Time spent generating the response in nanoseconds
        /// </summary>
        [JsonPropertyName("total_duration")]
        public long TotalDuration { get; set; }

        /// <summary>
        /// Time spent loading the model in nanoseconds
        /// </summary>

        [JsonPropertyName("load_duration")]
        public long LoadDuration { get; set; }

        /// <summary>
        /// Number of tokens in the prompt
        /// </summary>
        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; set; }

        /// <summary>
        /// Time spent evaluating the prompt in nanoseconds
        /// </summary>
        [JsonPropertyName("prompt_eval_duration")]
        public long PromptEvalDuration { get; set; }

        /// <summary>
        /// Number of tokens in the response
        /// </summary>
        [JsonPropertyName("eval_count")]
        public int EvalCount { get; set; }

        /// <summary>
        /// Time spent generating tokens in nanoseconds
        /// </summary>
        [JsonPropertyName("eval_duration")]
        public long EvalDuration { get; set; }

        public int TokensPerSecond => EvalDuration > 0 ? (int)(EvalCount / (EvalDuration / 1_000_000_000.0)) : 0;
    }

    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(OllamaApiRequest))]
    [JsonSerializable(typeof(OllamaApiResponse))]
    [JsonSerializable(typeof(OllamaRequestOptions))]
    internal partial class OllamaJsonContext : JsonSerializerContext
    {
    }
}