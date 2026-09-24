using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Infrastructure.Live;
using PresenterAi.Infrastructure.Training;

namespace PresenterAi.Infrastructure.Tests.Training;

public sealed class ResponsesScriptReviserTests
{
    private const string PrimaryKey = "primary-test-key-0123456789";
    private const string FallbackKey = "fallback-test-key-9876543210";
    private const string PrimaryModel = "reasoner-primary";
    private const string FallbackModel = "reasoner-fallback";
    private const string AzureResponsesUrl = "https://x.services.ai.azure.com/openai/v1/responses";
    private const string OpenAiResponsesUrl = "https://api.openai.com/v1/responses";
    private const string TargetNarration = "Our programme started in 2020 and covers the north.";
    private const string RecentText = "Earlier the speaker said something unrelated.";

    [Fact]
    public async Task Posts_full_request_to_azure_responses_url_with_route_headers()
    {
        var handler = new StubHandler(_ => Json(Completed(RevisionJson(2, "New narration."))));
        var reviser = CreateReviser(handler);

        var result = await reviser.ReviseAsync(Request(), CancellationToken.None);

        var ok = Assert.IsType<ScriptRevisionResult.Ok>(result);
        Assert.Equal([new RevisedSlide(2, "New narration.")], ok.Slides);
        Assert.Equal("Added the 2025 figures", ok.Summary);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(AzureResponsesUrl, call.Url);
        Assert.Equal($"Bearer {PrimaryKey}", call.Header("Authorization"));
        Assert.Equal(PrimaryKey, call.Header("api-key"));

        var body = JsonNode.Parse(call.Body)!.AsObject();
        Assert.Equal(PrimaryModel, (string?)body["model"]);
        Assert.False((bool)body["store"]!);
        Assert.Equal("low", (string?)body["reasoning"]!["effort"]);
        Assert.Equal(8000, (int)body["max_output_tokens"]!);
        Assert.False(string.IsNullOrWhiteSpace((string?)body["instructions"]));
        var format = body["text"]!["format"]!;
        Assert.Equal("json_schema", (string?)format["type"]);
        Assert.Equal("script_revision", (string?)format["name"]);
        Assert.True((bool)format["strict"]!);
        var schema = format["schema"]!;
        Assert.False((bool)schema["additionalProperties"]!);
        Assert.Equal(["slides", "summary"], schema["required"]!.AsArray().Select(n => (string)n!));
        var item = schema["properties"]!["slides"]!["items"]!;
        Assert.False((bool)item["additionalProperties"]!);
        Assert.Equal(["number", "narration"], item["required"]!.AsArray().Select(n => (string)n!));

        var input = JsonNode.Parse((string)body["input"]!)!.AsObject();
        Assert.Equal("Demo talk", (string?)input["title"]);
        Assert.Equal(3, input["outline"]!.AsArray().Count);
        var target = Assert.Single(input["targets"]!.AsArray())!;
        Assert.Equal(2, (int)target["number"]!);
        Assert.Equal(TargetNarration, (string?)target["narration"]);
        Assert.Equal("Mention the 2025 figures", (string?)input["request"]!["feedback"]);
        Assert.Null(input["request"]!["exchange"]);
        Assert.Equal(RecentText, (string?)input["context"]!["recent"]![0]!["text"]);
        // Recent turns appear only under context, never inside the request.
        Assert.DoesNotContain(RecentText, input["request"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exchange_is_sent_inside_the_request()
    {
        var handler = new StubHandler(_ => Json(Completed(RevisionJson(2, "New narration."))));
        var request = Request() with { Exchange = new TrainingExchange("Câu hỏi?", "Câu trả lời.") };

        await CreateReviser(handler).ReviseAsync(request, CancellationToken.None);

        var input = JsonNode.Parse((string)JsonNode.Parse(Assert.Single(handler.Calls).Body)!["input"]!)!;
        Assert.Equal("Câu hỏi?", (string?)input["request"]!["exchange"]!["question"]);
        Assert.Equal("Câu trả lời.", (string?)input["request"]!["exchange"]!["answer"]);
    }

    public static TheoryData<string> RetryablePrimaryFailures => ["503", "429", "401", "malformed"];

    [Theory]
    [MemberData(nameof(RetryablePrimaryFailures))]
    public async Task Fallback_request_uses_fallback_url_headers_and_model(string failure)
    {
        var handler = new StubHandler(
            _ => failure == "malformed"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>gateway</html>") }
                : new HttpResponseMessage((HttpStatusCode)int.Parse(failure, System.Globalization.CultureInfo.InvariantCulture)),
            _ => Json(Completed(RevisionJson(2, "From fallback."))));

        var result = await CreateReviser(handler).ReviseAsync(Request(), CancellationToken.None);

        Assert.Equal("From fallback.", Assert.IsType<ScriptRevisionResult.Ok>(result).Slides[0].Narration);
        Assert.Equal(2, handler.Calls.Count);
        var primary = handler.Calls[0];
        var fallback = handler.Calls[1];
        Assert.Equal(AzureResponsesUrl, primary.Url);
        Assert.Equal(OpenAiResponsesUrl, fallback.Url);
        Assert.Equal($"Bearer {FallbackKey}", fallback.Header("Authorization"));
        Assert.Null(fallback.Header("api-key"));
        var primaryBody = JsonNode.Parse(primary.Body)!.AsObject();
        var fallbackBody = JsonNode.Parse(fallback.Body)!.AsObject();
        Assert.Equal(FallbackModel, (string?)fallbackBody["model"]);
        primaryBody.Remove("model");
        fallbackBody.Remove("model");
        Assert.Equal(primaryBody.ToJsonString(), fallbackBody.ToJsonString());
    }

    [Fact]
    public async Task Transport_failure_falls_back_and_all_routes_failing_is_upstream()
    {
        var handler = new StubHandler(
            _ => throw new HttpRequestException("connection refused"),
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await CreateReviser(handler).ReviseAsync(Request(), CancellationToken.None);

        Assert.Equal(new ScriptRevisionResult.Failed(ScriptEditErrors.Upstream), result);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task Client_error_4xx_is_one_call_without_retry(int status)
    {
        var handler = new StubHandler(
            _ => new HttpResponseMessage((HttpStatusCode)status),
            _ => Json(Completed(RevisionJson(2, "Should not be called."))));

        var result = await CreateReviser(handler).ReviseAsync(Request(), CancellationToken.None);

        Assert.Equal(new ScriptRevisionResult.Failed(ScriptEditErrors.Upstream), result);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Invalid_output_is_one_call_without_retry()
    {
        var handler = new StubHandler(
            _ => Json(Completed("{\"slides\":\"not an array\",\"summary\":\"x\"}")),
            _ => Json(Completed(RevisionJson(2, "Should not be called."))));

        var result = await CreateReviser(handler).ReviseAsync(Request(), CancellationToken.None);

        Assert.Equal(new ScriptRevisionResult.Failed(ScriptEditErrors.InvalidOutput), result);
        Assert.Single(handler.Calls);
    }

    public static TheoryData<string> OffSchemaRevisions => new()
    {
        // extra root property (the review's adversarial case)
        """{"slides":[{"number":2,"narration":"changed","extra":"x"}],"summary":"x","extra":"x"}""",
        """{"slides":[{"number":2,"narration":"changed"}],"summary":"x","extra":"x"}""",
        // extra slide property
        """{"slides":[{"number":2,"narration":"changed","extra":"x"}],"summary":"x"}""",
        // duplicated key (JsonDocument keeps both)
        """{"slides":[{"number":2,"narration":"changed"}],"summary":"x","summary":"y"}""",
        """{"slides":[{"number":2,"narration":"changed","number":3}],"summary":"x"}""",
        // missing slide property
        """{"slides":[{"number":2}],"summary":"x"}""",
        // wrong types
        """{"slides":[{"number":"2","narration":"changed"}],"summary":"x"}""",
        """{"slides":[{"number":2.5,"narration":"changed"}],"summary":"x"}""",
        """{"slides":[{"number":2,"narration":null}],"summary":"x"}""",
        """{"slides":[{"number":2,"narration":"changed"}],"summary":7}""",
        """{"slides":{"number":2,"narration":"changed"},"summary":"x"}""",
        """{"slides":["changed"],"summary":"x"}""",
        // not an object
        """[{"slides":[],"summary":"x"}]"""
    };

    [Theory]
    [MemberData(nameof(OffSchemaRevisions))]
    public async Task Output_outside_the_declared_schema_is_invalid_output_without_fallback(string revision)
    {
        var handler = new StubHandler(
            _ => Json(Completed(revision)),
            _ => Json(Completed(RevisionJson(2, "Should not be called."))));

        var result = await CreateReviser(handler).ReviseAsync(Request(), CancellationToken.None);

        Assert.Equal(new ScriptRevisionResult.Failed(ScriptEditErrors.InvalidOutput), result);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Parses_message_among_multiple_output_elements()
    {
        var envelope = new JsonObject
        {
            ["status"] = "completed",
            ["output"] = new JsonArray(
                new JsonObject { ["type"] = "reasoning", ["summary"] = new JsonArray() },
                new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "annotation_marker" },
                        new JsonObject { ["type"] = "output_text", ["text"] = RevisionJson(2, "Chosen text.") })
                },
                new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = RevisionJson(2, "Second message.") })
                }),
            ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 }
        };
        var handler = new StubHandler(_ => Json(envelope.ToJsonString()));

        var result = await CreateReviser(handler).ReviseAsync(Request(), CancellationToken.None);

        Assert.Equal("Chosen text.", Assert.IsType<ScriptRevisionResult.Ok>(result).Slides[0].Narration);
    }

    public static TheoryData<string> InvalidEnvelopes => new()
    {
        // refusal
        """{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}""",
        // refusal next to otherwise valid text
        """{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"no"},{"type":"output_text","text":"{\"slides\":[{\"number\":2,\"narration\":\"x\"}],\"summary\":\"x\"}"}]}]}""",
        // incomplete
        """{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[{"type":"message","content":[{"type":"output_text","text":"{\"slides\":[],\"summary\":\"x\"}"}]}]}""",
        // no message
        """{"status":"completed","output":[{"type":"reasoning","summary":[]}]}""",
        // message without text
        """{"status":"completed","output":[{"type":"message","content":[]}]}""",
        // text is not JSON
        """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"Sure! Here it is."}]}]}""",
        // missing summary
        """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"{\"slides\":[]}"}]}]}"""
    };

    [Theory]
    [MemberData(nameof(InvalidEnvelopes))]
    public async Task Refusal_incomplete_or_missing_text_is_invalid_output(string envelope)
    {
        var handler = new StubHandler(_ => Json(envelope), _ => Json(Completed(RevisionJson(2, "Should not be called."))));

        var result = await CreateReviser(handler).ReviseAsync(Request(), CancellationToken.None);

        Assert.Equal(new ScriptRevisionResult.Failed(ScriptEditErrors.InvalidOutput), result);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Timeout_returns_timeout_failure()
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json(Completed(RevisionJson(2, "Too late.")));
        });
        var reviser = CreateReviser(handler, time: time, budget: TimeSpan.FromSeconds(60));

        var pending = reviser.ReviseAsync(Request(), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new ScriptRevisionResult.Failed(ScriptEditErrors.Timeout), result);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_returns_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(async (_, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return Json(Completed(RevisionJson(2, "Too late.")));
        });

        var result = await CreateReviser(handler).ReviseAsync(Request(), cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new ScriptRevisionResult.Failed(ScriptEditErrors.Cancelled), result);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Cancellation_between_primary_failure_and_fallback_makes_no_second_call()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(
            _ =>
            {
                cts.Cancel();
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            },
            _ => Json(Completed(RevisionJson(2, "Should not be called."))));

        var result = await CreateReviser(handler).ReviseAsync(Request(), cts.Token);

        Assert.Equal(new ScriptRevisionResult.Failed(ScriptEditErrors.Cancelled), result);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Usage_is_logged_without_payload_or_secrets()
    {
        var logger = new CapturingLogger();
        var envelope = JsonNode.Parse(Completed(RevisionJson(2, "Rewritten narration text.")))!.AsObject();
        envelope["usage"] = new JsonObject { ["input_tokens"] = 1234, ["output_tokens"] = 56 };
        var handler = new StubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Json(envelope.ToJsonString()));

        await CreateReviser(handler, logger: logger).ReviseAsync(Request(), CancellationToken.None);

        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("route=primary", logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains($"model={PrimaryModel}", logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains("status=503", logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains("route=fallback", logger.Messages[1], StringComparison.Ordinal);
        Assert.Contains("status=200", logger.Messages[1], StringComparison.Ordinal);
        Assert.Contains("tokens_in=1234", logger.Messages[1], StringComparison.Ordinal);
        Assert.Contains("tokens_out=56", logger.Messages[1], StringComparison.Ordinal);
        var all = string.Join('\n', logger.Messages);
        Assert.DoesNotContain(PrimaryKey, all, StringComparison.Ordinal);
        Assert.DoesNotContain(FallbackKey, all, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", all, StringComparison.Ordinal);
        Assert.DoesNotContain(TargetNarration, all, StringComparison.Ordinal);
        Assert.DoesNotContain("Rewritten narration text.", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Mention the 2025 figures", all, StringComparison.Ordinal);
        Assert.DoesNotContain(RecentText, all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_route_with_a_delegation_model_is_unavailable()
    {
        var handler = new StubHandler();
        var routes = UpstreamRoutes.From(new UpstreamOptions
        {
            Endpoint = "https://x.services.ai.azure.com",
            Key = PrimaryKey,
            DelegationModel = "",
            Fallback = new UpstreamOptions.FallbackOptions { Key = "" }
        });
        var reviser = new ResponsesScriptReviser(new StubFactory(handler), routes, TimeSpan.FromSeconds(60));

        Assert.False(reviser.IsAvailable);
        Assert.Equal(
            new ScriptRevisionResult.Failed(ScriptEditErrors.Upstream),
            await reviser.ReviseAsync(Request(), CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    private static ResponsesScriptReviser CreateReviser(
        StubHandler handler,
        FakeTimeProvider? time = null,
        TimeSpan? budget = null,
        ILogger<ResponsesScriptReviser>? logger = null)
    {
        var routes = UpstreamRoutes.From(new UpstreamOptions
        {
            Endpoint = "https://x.services.ai.azure.com",
            Key = PrimaryKey,
            DelegationModel = PrimaryModel,
            Fallback = new UpstreamOptions.FallbackOptions { Key = FallbackKey, DelegationModel = FallbackModel }
        });
        return new ResponsesScriptReviser(
            new StubFactory(handler),
            routes,
            budget ?? TimeSpan.FromSeconds(60),
            (TimeProvider?)time ?? TimeProvider.System,
            logger);
    }

    private static ScriptRevisionRequest Request() => new(
        "Demo talk",
        [new ScriptOutlineEntry(1, "Intro"), new ScriptOutlineEntry(2, "History"), new ScriptOutlineEntry(3, "Close")],
        [new ScriptRevisionTarget(2, "History", TargetNarration, null)],
        "Mention the 2025 figures",
        null,
        [new RecentTurn("user", RecentText)]);

    private static string RevisionJson(int number, string narration) =>
        new JsonObject
        {
            ["slides"] = new JsonArray(new JsonObject { ["number"] = number, ["narration"] = narration }),
            ["summary"] = "Added the 2025 figures"
        }.ToJsonString();

    private static string Completed(string text) =>
        new JsonObject
        {
            ["status"] = "completed",
            ["output"] = new JsonArray(new JsonObject
            {
                ["type"] = "message",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text })
            })
        }.ToJsonString();

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record RecordedCall(HttpMethod Method, string Url, Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] _responses;
        private readonly ConcurrentQueue<RecordedCall> _calls = new();

        public StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = responses
                .Select(respond => (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((request, _) => Task.FromResult(respond(request))))
                .ToArray();
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _responses = [respond];
        }

        public IReadOnlyList<RecordedCall> Calls => _calls.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(CancellationToken.None);
            var index = _calls.Count;
            _calls.Enqueue(new RecordedCall(request.Method, request.RequestUri!.ToString(), headers, body));
            if (index >= _responses.Length)
            {
                throw new InvalidOperationException($"Unexpected call {index + 1}");
            }

            return await _responses[index](request, cancellationToken);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(ResponsesScriptReviser.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    private sealed class CapturingLogger : ILogger<ResponsesScriptReviser>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                text += " " + string.Join(' ', values.Select(v => $"{v.Key}:{v.Value}"));
            }

            _messages.Enqueue(text + (exception is null ? string.Empty : " " + exception));
        }
    }
}
