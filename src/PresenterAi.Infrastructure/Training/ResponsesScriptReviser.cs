using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Infrastructure.Training;

/// <summary>
/// Out-of-band script reviser over the Responses API of the existing upstream routes (plan 010 §4.1): every route with
/// a <c>DelegationModel</c>, in order (primary, then fallback), each with its own URL, headers and model. Strict
/// structured output; fallback on transport failure, 401/403/429/5xx and non-JSON bodies while the budget remains and the
/// caller has not cancelled; any other client error or an invalid result is final. Logs one line per call with route,
/// model, status, latency and token usage — never payloads, narration or header values.
/// </summary>
public sealed class ResponsesScriptReviser : IScriptReviser
{
    public const string HttpClientName = "responses";
    public const int MaxOutputTokens = 8000;

    private static readonly JsonSerializerOptions InputJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IReadOnlyList<Route> _routes;
    private readonly TimeSpan _budget;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ResponsesScriptReviser> _logger;

    /// <param name="budget">
    /// Upper bound for one <see cref="ReviseAsync"/> across all routes (<c>Training:ReviserTimeoutSeconds</c>); the
    /// caller's token bounds it as well.
    /// </param>
    public ResponsesScriptReviser(
        IHttpClientFactory httpClientFactory,
        UpstreamRoutes routes,
        TimeSpan budget,
        TimeProvider? timeProvider = null,
        ILogger<ResponsesScriptReviser>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        _httpClientFactory = httpClientFactory;
        _routes = routes.Upstreams
            .Where(route => !string.IsNullOrWhiteSpace(route.DelegationModel))
            .Select(route => new Route(route.Name, ResponsesUrlResolver.Resolve(route.LiveUrl), route.Headers, route.DelegationModel))
            .ToArray();
        _budget = budget;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ResponsesScriptReviser>.Instance;
    }

    public bool IsAvailable => _routes.Count > 0;

    public async Task<ScriptRevisionResult> ReviseAsync(ScriptRevisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_routes.Count == 0)
        {
            return new ScriptRevisionResult.Failed(ScriptEditErrors.Upstream);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ScriptRevisionResult.Failed(ScriptEditErrors.Cancelled);
        }

        var input = BuildInput(request);
        using var budget = new CancellationTokenSource(_budget, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        for (var i = 0; i < _routes.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ScriptRevisionResult.Failed(ScriptEditErrors.Cancelled);
            }

            if (budget.IsCancellationRequested)
            {
                return new ScriptRevisionResult.Failed(ScriptEditErrors.Timeout);
            }

            var attempt = await CallAsync(client, _routes[i], input, cancellationToken, budget, linked.Token)
                .ConfigureAwait(false);
            if (attempt.Result is not null)
            {
                return attempt.Result;
            }

            // Retryable: try the next route, if any.
        }

        return new ScriptRevisionResult.Failed(ScriptEditErrors.Upstream);
    }

    private async Task<Attempt> CallAsync(
        HttpClient client,
        Route route,
        string input,
        CancellationToken callerToken,
        CancellationTokenSource budget,
        CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        string status = "error";
        long? tokensIn = null;
        long? tokensOut = null;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, route.Url)
            {
                Content = new StringContent(BuildBody(route.Model, input), Encoding.UTF8, "application/json")
            };
            foreach (var header in route.Headers)
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            status = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return IsRetryable(response.StatusCode)
                    ? Attempt.Retry
                    : Attempt.Final(new ScriptRevisionResult.Failed(ScriptEditErrors.Upstream));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                status += " malformed";
                return Attempt.Retry;
            }

            using (document)
            {
                (tokensIn, tokensOut) = ReadUsage(document.RootElement);
                return Attempt.Final(ParseEnvelope(document.RootElement));
            }
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            status = "cancelled";
            return Attempt.Final(new ScriptRevisionResult.Failed(ScriptEditErrors.Cancelled));
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            status = "timeout";
            return Attempt.Final(new ScriptRevisionResult.Failed(ScriptEditErrors.Timeout));
        }
        catch (OperationCanceledException)
        {
            // An HttpClient timeout (the named client uses an infinite one, but a host may configure it).
            status = "timeout";
            return Attempt.Final(new ScriptRevisionResult.Failed(ScriptEditErrors.Timeout));
        }
        catch (HttpRequestException)
        {
            status = "transport_error";
            return Attempt.Retry;
        }
        catch (IOException)
        {
            status = "transport_error";
            return Attempt.Retry;
        }
        finally
        {
            var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _logger.LogInformation(
                "Script reviser route={Route} model={Model} status={Status} ms={Ms} tokens_in={In} tokens_out={Out}",
                route.Name,
                route.Model,
                status,
                elapsedMs,
                tokensIn,
                tokensOut);
        }
    }

    private static bool IsRetryable(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is 401 or 403 or 429 || code >= 500;
    }

    private static (long? In, long? Out) ReadUsage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (ReadLong(usage, "input_tokens"), ReadLong(usage, "output_tokens"));

        static long? ReadLong(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
                ? n
                : null;
    }

    /// <summary>
    /// <c>status == "completed"</c>; the first <c>output[*]</c> of type <c>message</c>; its first <c>output_text</c>
    /// parsed against the schema. Refusals, incomplete responses, a missing message or text, or schema mismatches are
    /// <see cref="ScriptEditErrors.InvalidOutput"/>.
    /// </summary>
    private static ScriptRevisionResult ParseEnvelope(JsonElement root)
    {
        var invalid = new ScriptRevisionResult.Failed(ScriptEditErrors.InvalidOutput);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String
            || status.GetString() != "completed"
            || !root.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return invalid;
        }

        JsonElement? messageElement = null;
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "message")
            {
                messageElement = item;
                break;
            }
        }

        if (messageElement is not { } message
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return invalid;
        }

        string? text = null;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("type", out var partType)
                || partType.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var kind = partType.GetString();
            if (kind == "refusal")
            {
                return invalid;
            }

            if (kind == "output_text"
                && text is null
                && part.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String)
            {
                text = textElement.GetString();
            }
        }

        return string.IsNullOrWhiteSpace(text) ? invalid : ParseRevision(text) ?? (ScriptRevisionResult)invalid;
    }

    private static ScriptRevisionResult.Ok? ParseRevision(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!HasExactlyProperties(root, RootProperties)
                || !root.TryGetProperty("slides", out var slides)
                || slides.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("summary", out var summary)
                || summary.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var revised = new List<RevisedSlide>();
            foreach (var slide in slides.EnumerateArray())
            {
                if (!HasExactlyProperties(slide, SlideProperties)
                    || !slide.TryGetProperty("number", out var number)
                    || number.ValueKind != JsonValueKind.Number
                    || !number.TryGetInt32(out var n)
                    || !slide.TryGetProperty("narration", out var narration)
                    || narration.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                revised.Add(new RevisedSlide(n, narration.GetString()!));
            }

            return new ScriptRevisionResult.Ok(revised, summary.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The property sets Schema() declares with additionalProperties:false and every property required. Strict request
    // formatting asks the provider to comply; the response is still checked here, so an extra, missing or repeated
    // key is invalid_output rather than silently ignored.
    private static readonly string[] RootProperties = ["slides", "summary"];
    private static readonly string[] SlideProperties = ["number", "narration"];

    private static bool HasExactlyProperties(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0 || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return seen.Count == allowed.Length;
    }

    private static string BuildInput(ScriptRevisionRequest request)
    {
        var requestNode = new JsonObject { ["feedback"] = request.Feedback };
        if (request.Exchange is { } exchange)
        {
            requestNode["exchange"] = new JsonObject
            {
                ["question"] = exchange.Question,
                ["answer"] = exchange.Answer
            };
        }

        var input = new JsonObject
        {
            ["title"] = request.Title,
            ["outline"] = new JsonArray(request.Outline
                .Select(entry => (JsonNode)new JsonObject { ["number"] = entry.Number, ["title"] = entry.Title })
                .ToArray()),
            ["targets"] = new JsonArray(request.Targets
                .Select(target => (JsonNode)new JsonObject
                {
                    ["number"] = target.Number,
                    ["title"] = target.Title,
                    ["narration"] = target.Narration,
                    ["notes"] = target.Notes
                })
                .ToArray()),
            ["request"] = requestNode,
            ["context"] = new JsonObject
            {
                ["recent"] = new JsonArray(request.Recent
                    .Select(turn => (JsonNode)new JsonObject { ["role"] = turn.Role, ["text"] = turn.Text })
                    .ToArray())
            }
        };
        return input.ToJsonString(InputJsonOptions);
    }

    private static string BuildBody(string model, string input)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = PromptBuilder.ScriptReviserInstructions(),
            ["input"] = input,
            ["reasoning"] = new JsonObject { ["effort"] = "low" },
            ["store"] = false,
            ["max_output_tokens"] = MaxOutputTokens,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "script_revision",
                    ["strict"] = true,
                    ["schema"] = Schema()
                }
            }
        };
        return body.ToJsonString(InputJsonOptions);
    }

    /// <summary>Strict mode: every property required and <c>additionalProperties:false</c> at both levels.</summary>
    private static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("slides", "summary"),
        ["properties"] = new JsonObject
        {
            ["slides"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("number", "narration"),
                    ["properties"] = new JsonObject
                    {
                        ["number"] = new JsonObject { ["type"] = "integer" },
                        ["narration"] = new JsonObject { ["type"] = "string" }
                    }
                }
            },
            ["summary"] = new JsonObject { ["type"] = "string" }
        }
    };

    private sealed record Route(string Name, Uri Url, IReadOnlyDictionary<string, string> Headers, string Model);

    private readonly record struct Attempt(ScriptRevisionResult? Result)
    {
        public static Attempt Retry => new(null);

        public static Attempt Final(ScriptRevisionResult result) => new(result);
    }
}
