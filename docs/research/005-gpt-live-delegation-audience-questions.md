# 005 — GPT-Live delegation and audience-question handling (research)

**Date:** 2026-09-22
**Why:** in a live run (20:10) the model said "I'll check on that for you" after an audience question, the presenter ignored the client delegation (`Presenter.OnDelegation`), and the advance timer moved to the next slide 5 s later.
**Sources:** two external agents (`agy` for the API facts, `pi` gpt-5.6-sol for the code-grounded design); key facts re-checked by the orchestrator against Microsoft's *Delegate work in GPT-Live* (ms.date 2026-09-04).

## Orchestrator verification

Confirmed on https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation:
- client delegation: `session.delegation.created` carries `id`, `target: "client"` and `offset_ms`, **no task text**; answer with `session.commentary.append` (spoken) or `session.thinking.append` (quiet) and `delegation_id`, ≤ 500 tokens each; repeated appends continue the same delegation;
- an `*.appended` acknowledgment "doesn't prove that the model consumed or spoke the result";
- Responses delegation (`type: "responses"`, example model `gpt-5.5`) is documented on Azure; GPT-Live manages the backend call and injects its output; results arrive in `response.event` envelopes; the mode is fixed per session (a new session to change it).

Not re-verified (treat as indicative): the latency figures and third-party benchmark numbers in part A.

## Part A — API facts (`agy`)


This report provides the external API facts governing GPT-Live delegation (Responses delegation and Client delegation) across OpenAI and Azure AI Foundry. Every documented fact is paired with its source URL; anything not specified in official documentation is marked as **UNKNOWN**. No code changes are proposed.

---

## 1. Responses Delegation Configuration

### 1.1 Exact `session.start` Shape
In GPT-Live, Responses delegation is configured inside the `session` object during initial session establishment. 

* **WebSocket Transport (`session.start`):**
  ```json
  {
    "type": "session.start",
    "event_id": "event_start_01",
    "session": {
      "model": "gpt-live-1",
      "instructions": "You are a professional conference presenter...",
      "audio": {
        "output": {
          "voice": "marin"
        }
      },
      "delegation": {
        "type": "responses",
        "responses": {
          "model": "gpt-5.6-terra",
          "instructions": "Answer questions about the presentation using provided deck context.",
          "tools": [
            { "type": "web_search" },
            {
              "type": "function",
              "name": "lookup_deck_notes",
              "description": "Look up detailed slide context and speaker notes.",
              "parameters": {
                "type": "object",
                "properties": {
                  "query": { "type": "string" }
                },
                "required": ["query"],
                "additionalProperties": false
              }
            }
          ],
          "tool_choice": "auto",
          "parallel_tool_calls": true,
          "max_output_tokens": 2048,
          "service_tier": "priority",
          "reasoning": {
            "effort": "medium",
            "summary": "auto"
          },
          "text": {
            "verbosity": "low"
          }
        }
      }
    }
  }
  ```
  *Sources:*
  - OpenAI Delegation and Tools: `https://developers.openai.com/api/docs/guides/live-delegation`
  - Azure GPT-Live Reference: `https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference`

### 1.2 Allowed Fields in `delegation.responses` and Documented Limits
The `session.start` configuration object is **strict** and rejects unknown fields. The allowed fields under `delegation.responses` are:

| Field | Type | Description / Allowed Values | Limits / Constraints |
|---|---|---|---|
| `model` | string | **Required**. Backend Responses model or deployment name (e.g. `gpt-5.6-terra`, `gpt-5.6-luna`, or `gpt-5.5` on Azure). | Must be specified at session creation. |
| `instructions` | string | System prompt for the backend Responses model. | Detailed task instructions. Max token limit for `responses.instructions` specifically is **UNKNOWN** (the outer `session.instructions` has a 16,384 token limit). |
| `tools` | array | Array of tool definitions available to the backend model. | Supports `function` and hosted `web_search` tool objects. |
| `tool_choice` | string / object | Tool execution mode: `"auto"`, `"required"`, `"none"`, or a specific function selector object `{"type": "function", "name": "..."}`. | Applies to backend model after delegation occurs. |
| `parallel_tool_calls` | boolean | Controls whether backend model may request multiple tool executions in parallel. | `true` or `false`. |
| `max_output_tokens` | integer | Upper limit on tokens produced by the backend model. | Must be **at least 16** when specified. |
| `service_tier` | string | Service tier for backend inference: `"auto"`, `"default"`, `"flex"`, or `"priority"`. | `"priority"` routes to Fast mode where available. |
| `reasoning` | object | Model reasoning settings (e.g., for reasoning-capable backend models):<br>- `effort`: `"low"`, `"medium"`, or `"high"`<br>- `summary`: `"auto"` | Supported effort values depend on the chosen backend model. |
| `text` | object | Text generation options (e.g., `verbosity`: `"low"`). | Supported fields depend on backend model. |

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference`

### 1.3 Supplying Deck Context (Instructions / Files / Prior Items) and Limits
* **Backend Instructions (`delegation.responses.instructions`):** Deck context, slide outlines, and presenter background knowledge can be passed directly as string instructions into `delegation.responses.instructions` at startup. During the session, instructions can be updated via `session.update` sending a modified `session.delegation.responses.instructions`.
* **Prior Items / History (`session.input`):** Prior conversation text history can be seeded at session startup via `session.input`. It accepts up to **128 messages** and **8,192 combined tokens** (`developer`, `user`, or `assistant` roles with `input_text` or `output_text`). Note that `session.input` seeds the live frontend model, which passes context to the backend when delegating.
* **Queuing Messages for Backend (`response.item.create`):** Typed user context can be queued for the backend using `response.item.create` with `item: { type: "message", role: "user", content: [{ type: "input_text", text: "..." }] }`.
* **File Uploads / Attachments:** Direct file attachment arrays (e.g. uploading raw `.pdf` or `.pptx` documents directly inside `session.start` or `delegation.responses`) are **UNKNOWN / NOT DOCUMENTED** in the GPT-Live API. Files must either be converted into text and injected into `instructions` / `messages`, or queried via custom function tools (e.g., vector search / RAG tool).

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-conversations`
- `https://developers.openai.com/api/docs/guides/live-delegation`

---

## 2. Event Flow for Responses Delegation

### 2.1 Trigger: `session.delegation.created`
When GPT-Live determines that user speech requires reasoning or tools outside its conversational context, the server emits `session.delegation.created`:

```json
{
  "type": "session.delegation.created",
  "event_id": "event_delegation_100",
  "offset_ms": 1000,
  "delegation": {
    "id": "item_delegation_456",
    "type": "delegation",
    "target": "responses",
    "response_id": "resp_abc123"
  }
}
```
* **`delegation.target`:** `"responses"` indicates that GPT-Live is delegating to the server-managed Responses backend.
* **`delegation.id`:** Identifies this delegated task unit.
* **`delegation.response_id`:** The identifier of the backend Responses object created by GPT-Live.

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation`

### 2.2 Behavior of the Live Model While the Backend Works
* The live model **continues speaking and listening independently** (full duplex).
* The live model typically produces conversational filler / acknowledgment (e.g., *"I'll check on that for you"*, *"Let me look into that"*), steered by the system prompt's delegation policy.
* Audio streams as standard `session.output_audio.delta` frames and transcript fragments as `session.output_transcript.delta`.
* The documentation explicitly notes: *"Live speech and delegated work continue independently. A completed backend response does not itself mean the user heard the answer."*

*Source:*
- `https://developers.openai.com/api/docs/guides/live-delegation`

### 2.3 Responses Lifecycle & `response.event` Envelope
All downstream events from the backend Responses execution arrive wrapped in a top-level `response.event` envelope:

```json
{
  "type": "response.event",
  "event_id": "event_response_wrapper_1",
  "delegation_id": "item_delegation_456",
  "event": {
    "type": "response.output_text.delta",
    "sequence_number": 4,
    "item_id": "msg_001",
    "output_index": 0,
    "content_index": 0,
    "delta": "The project roadmap includes...",
    "logprobs": []
  }
}
```
Client code must inspect the outer `delegation_id` and dispatch based on `envelope.event.type`. Nested events include `response.created`, `response.output_item.added`, `response.output_text.delta`, `response.output_item.done`, and `response.completed`.

*Source:*
- `https://developers.openai.com/api/docs/guides/live-delegation`

### 2.4 How the Result Reaches Speech
* In Responses delegation, the backend text generated by the Responses model is **automatically injected** into the live GPT-Live conversation session.
* The live model synthesizes the answer and speaks it aloud to the listener.
* The spoken output arrives as standard `session.output_transcript.delta` and `session.output_audio.delta` events on the primary connection (or media track in WebRTC).

*Sources:*
- `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation`
- `https://developers.openai.com/api/docs/guides/live-delegation`

### 2.5 Function / Tool Call Flow to the Client
If the Responses backend invokes a client-side `function` tool:
1. **Tool Invocation:** Delivered in a nested `response.output_item.done` event:
   ```json
   {
     "type": "response.event",
     "delegation_id": "item_delegation_456",
     "event": {
       "type": "response.output_item.done",
       "item": {
         "type": "function_call",
         "call_id": "call_987xyz",
         "name": "lookup_deck_notes",
         "arguments": "{\"query\":\"quarterly goals\"}"
       }
     }
   }
   ```
   *(Note: `response.completed` events forwarded during tool execution have `response.output: []`; clients must read individual `response.output_item.done` items).*
2. **Submitting Tool Output (`response.item.create`):**
   ```json
   {
     "type": "response.item.create",
     "event_id": "tool_res_1",
     "item": {
       "type": "function_call_output",
       "call_id": "call_987xyz",
       "output": "{\"result\":\"Q3 goal is 15% revenue expansion.\"}"
     }
   }
   ```
   *Note: `response.item.create` receives no standalone success acknowledgment.*
3. **Resuming the Response (`response.create`):**
   Once all pending tool outputs have been submitted (essential when `parallel_tool_calls: true`), the client must explicitly resume backend inference:
   ```json
   {
     "type": "response.create",
     "event_id": "continue_res_1"
   }
   ```

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation`

### 2.6 Completion, Failure, and Cancellation Events
* **Completion:** Delivered as a nested `response.completed` event inside `response.event`. It includes backend token `usage` (input, output, cached).
* **Failure:** Delivered either as an internal error in `response.event` or as a top-level `error` event:
  ```json
  {
    "type": "error",
    "event_id": "event_err_01",
    "error": {
      "type": "invalid_request_error",
      "code": "backend_error",
      "message": "Responses backend execution failed",
      "client_event_id": "continue_res_1"
    }
  }
  ```
* **Cancellation:** There is **NO** dedicated `response.cancel` or `delegation.cancel` command in the GPT-Live event schema. 
  - To abandon a pending tool call, the client simply omits sending `response.create`.
  - To stop speech or steer away from an in-flight backend answer, the client sends `session.instructions.append` (e.g., *"Stop speaking about that request. Refuse briefly, then wait."*).
  - Closing the session (`session.close`) drains or terminates pending Responses.

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://developers.openai.com/api/docs/guides/voice-server-controls`

---

## 3. Azure AI Foundry Support and Differences

### 3.1 Support on Azure AI Foundry Today
* **Responses Delegation:** **Supported**. Documented in Microsoft Learn *GPT-Live event API reference* and *Delegate work in GPT-Live* (updated September 2026).
* **Client Delegation:** **Supported**. Documented as the default delegation mode (`delegation: { type: "client" }` or omitted/null).

*Sources:*
- `https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live`

### 3.2 Models and Deployments
* **Live Model:** `gpt-live-1`.
* **Backend Responses Model on Azure:** Microsoft Foundry routes Responses delegation to a deployed Responses API model. The Microsoft Learn documentation explicitly uses `"model": "gpt-5.5"` in its configuration examples (`how-to/gpt-live-delegation`, line 108; `gpt-live-reference`, line 778).
* In Azure AI Foundry, `model` in `delegation.responses` refers to the deployment name in the Azure OpenAI resource that supports the Responses API.

*Sources:*
- `https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation`

### 3.3 API Versions and Regional Availability
* **Endpoint / Namespace:** `wss://<resource>.openai.azure.com/openai/v1/live/sessions` (Sideband attach: `wss://<resource>.openai.azure.com/openai/v1/live/sessions/{session_id}/attach`).
* **API-Version Parameter:** Neither the Microsoft connection guide nor the event reference mandates an `?api-version=...` query string on the WebSocket URL; the path is `/openai/v1/live/sessions`.
* **Supported Regions for `gpt-live-1`:**
  - Sweden Central
  - East US 2
  - Central US
  - France Central
  - South India
  - Canada Central

*Sources:*
- `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live`
- Microsoft Foundry Model Catalog / Azure documentation

### 3.4 Differences from OpenAI
1. **Base URLs & Namespaces:**
   - Azure: `wss://<resource>.openai.azure.com/openai/v1/live/sessions`
   - OpenAI: `wss://api.openai.com/v1/live/sessions`
2. **Authentication:**
   - Azure accepts both `Authorization: Bearer <token>` (Microsoft Entra ID token) and Azure API keys.
   - OpenAI accepts OpenAI API keys (`Authorization: Bearer $OPENAI_API_KEY`).
3. **Backend Model Naming:**
   - Azure documentation examples use `gpt-5.5` (referencing an Azure deployment).
   - OpenAI documentation examples use `gpt-5.6-terra` and `gpt-5.6-luna`.
4. **Session Update Discrepancy:**
   - OpenAI documentation explicitly states that `delegation.type` is immutable after startup, and sending `delegation: null` fails with `immutable_field_update`.
   - Azure's *Delegate work in GPT-Live* guide states: *"Replacing `delegation` in a later `session.update` requires a complete delegation object—nested fields aren't patched independently. Set `delegation` to `null` to reset to client delegation."* However, Azure's reference page notes that only `delegation.responses` settings can change.
5. **Storage and Session Forking:**
   - OpenAI documents `store: true`, session forking (`POST /v1/live/sessions/{id}/fork` and `wss://api.openai.com/v1/live/sessions/{id}/fork`), and recording download (`GET /v1/live/sessions/{id}/content`).
   - Azure documentation does **NOT** document `store`, session forking, or binary WAV recording download (marked as **UNKNOWN** on Azure).

*Sources:*
- `https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation`
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://developers.openai.com/api/docs/guides/live-conversations`

---

## 4. Latency and Cost

### 4.1 Latency
* **Spoken Response Latency (Conversational Fast-Path):**
  - For conversational responses that the live model answers directly without delegating, median response latency is typically **300–600 ms** (time-to-first-audio chunk).
  - Independent benchmarks (e.g. Agora Media Lab tests on GPT-Live in the ChatGPT app) observed a median response latency of approximately **1.3 seconds** across general interaction turns.
* **Delegation Latency (Reasoning / Tools Slow-Path):**
  - When the model delegates, the live model produces immediate conversational acknowledgment/filler within **~300–800 ms**.
  - The actual spoken answer to the delegated question is delivered after the backend model completes its inference and tool calls. Total time from user question to spoken delegated answer typically spans **1.5 to 4.0+ seconds**, depending on backend model reasoning effort (`low`/`medium`/`high`), Fast mode (`service_tier: "priority"`), and custom tool execution latency.
  - No single official fixed latency SLA is documented by OpenAI or Microsoft; latency is workload-dependent.

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://developers.openai.com/api/docs/guides/voice-latency-cost`

### 4.2 Cost Breakdown
Total cost combines two distinct, separately billed components:

1. **Live Voice Session Duration:**
   - **$0.05 per minute** ($0.000833 per second), billed per second based on cumulative active duration reported in `session.usage.updated`.
   - Billed continuously across user speaking, model speaking, silence, and backend processing time.
   - For WebRTC sessions created via `POST /v1/live/sessions`, an initial 15 seconds is billed at creation and credited toward running session duration.
2. **Backend Responses Calls:**
   - Billed at standard Responses API rates for the chosen model (e.g., `gpt-5.6-terra`, `gpt-5.6-luna`, or `gpt-5.5` on Azure) based on input tokens, output tokens, cached input tokens, and tool usage (such as `$5/1k calls` for hosted `web_search`).

**Billing Formula:**
$$\text{Total Cost} = \left(\frac{\text{Billable Voice Seconds}}{60} \times \$0.05\right) + \text{Backend Model \& Tool Costs}$$

*Example from Documentation:*
A 90-second voice session costs $\$0.075$ ($90 / 60 \times \$0.05$). If backend model and tool costs total $\$0.02$, the total interaction costs $\$0.095$.

*Sources:*
- `https://developers.openai.com/api/docs/models/gpt-live-1`
- `https://developers.openai.com/api/docs/guides/voice-latency-cost`

---

## 5. Client Delegation Mechanics

### 5.1 What the Model Does After `session.delegation.created`
When configured with `delegation: { type: "client" }`:
* The server emits `session.delegation.created`:
  ```json
  {
    "type": "session.delegation.created",
    "event_id": "event_delegation_01",
    "offset_ms": 1000,
    "delegation": {
      "id": "item_delegation_123",
      "type": "delegation",
      "target": "client"
    }
  }
  ```
* **Payload Content:** Contains **metadata only** (`id`, `type`, `target`, `offset_ms`). It contains **NO task text or user utterance**. The client must reconstruct the user's intent by inspecting accumulated `session.input_transcript.delta` events.
* **Model Behavior:** The model does not lock up or disconnect. It continues full-duplex conversational listening. Guided by its system instructions, it speaks an acknowledgment (filler like *"Let me check that"*) and then waits or converses while the client processes the task.

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation`

### 5.2 Documented Ways to Answer
The client returns context to GPT-Live using one of three append events, passing `delegation_id`:

| Event | Intended Purpose | Model Behavior |
|---|---|---|
| `session.commentary.append` | Spoken answer | Model is trained to **speak the content aloud**, paraphrasing it naturally to fit the conversation. |
| `session.thinking.append` | Quiet background facts | Injected into the model's internal reasoning context. **Not spoken on arrival**, but available for future replies. |
| `session.instructions.append` | System-level steering / direction | Modifies conversational behavior or immediately redirects/interrupts the model. |

* **Payload Rules:**
  - Content is plain string text, strictly limited to **≤ 500 tokens per append**.
  - `delegation_id`: Must match the client delegation `id` (`item_delegation_123`) to bind the result to that task.
  - Acknowledged via `session.commentary.appended`, `session.thinking.appended`, or `session.instructions.appended` containing `client_event_id`.
  - Acknowledgment confirms context injection on the session timeline, not that the model has finished speaking.

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://developers.openai.com/api/docs/guides/live-conversations`

### 5.3 Delegation "Done" and "Cancel" Events
* **Is there a "Done" event?** **NO**. The documentation states: *"There is no explicit 'done' event for client delegation. Repeated appends can continue the same client delegation."*
* **Is there a "Cancel" event?** **NO**. There is no client or server event to cancel a delegation. Cancellation is managed entirely in the client application:
  - If a task is abandoned or superseded by new user speech, the client cancels its own backend work.
  - The client can redirect the live model using `session.instructions.append` (`delegation_id: null`).

*Sources:*
- `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation`
- `https://developers.openai.com/api/docs/guides/live-delegation`

### 5.4 Client Response Timing & Timeouts
* **Server-side Timeout:** **UNKNOWN / NOT DOCUMENTED**. The GPT-Live server does not document a hard timeout that closes the delegation or session if the client takes too long.
* **Conversational Constraint:** While the server does not terminate the delegation, awkward silence will occur if the client does not provide updates or conversational commentary within 2–5 seconds, leading the user to speak again or trigger stall recovery.

*Source:*
- `https://developers.openai.com/api/docs/guides/live-delegation`

### 5.5 Can a Client Answer "Cannot Help, Answer Yourself"?
Yes. The documentation provides two patterns:
1. **Instruct to Decline / Redirect (`session.instructions.append`):**
   ```json
   {
     "type": "session.instructions.append",
     "event_id": "guardrail_1",
     "delegation_id": "item_delegation_123",
     "content": "Briefly explain that our presentation materials do not cover this topic, then ask the audience if they have another question."
   }
   ```
2. **Provide Speakable Refusal Commentary (`session.commentary.append`):**
   ```json
   {
     "type": "session.commentary.append",
     "event_id": "refusal_1",
     "delegation_id": "item_delegation_123",
     "content": "That topic is outside the scope of today's slide deck. We will address it in a future session."
   }
   ```
3. **Instruct to Answer from General Knowledge:**
   `session.instructions.append` with: *"The slide context does not cover this. Briefly answer the question from your general knowledge in one sentence, then bridge back to the slide."*

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://developers.openai.com/api/docs/guides/live-prompting`

---

## 6. Controlling When the Model Delegates

### 6.1 Steering Delegation via System Instructions
GPT-Live determines whether to delegate based on its system prompt (`session.instructions`). OpenAI defines a recommended prompt structure:

```text
Delegation policy:
Backend tools:
- [capability]: [what the backend can do]

Delegate to the backend when:
- The request needs a backend capability or careful reasoning.
- A correction changes the work already requested.

Do not delegate to the backend when:
- You can answer from the conversation or a still-current result.
- You need a brief clarification to understand the request.

Delegate before giving an answer that depends on backend work.
Do not guess the result while waiting.
```

* **Preventing Delegation:** Instructions can explicitly suppress delegation:
  - *"NEVER DELEGATE, CHECK, ANSWER, SEARCH, OR USE TOOLS."* (Documented in the translation/interpreter prompt sample).
  - For presentations: *"Answer questions directly from the slide script and provided context. Only delegate when the question asks about external facts not mentioned in the presentation."*

*Source:*
- `https://developers.openai.com/api/docs/guides/live-prompting`

### 6.2 Tool Definitions and Tool Choice
* In Responses delegation, setting `delegation.responses.tool_choice: "none"` disables tool calls by the backend Responses model.
* However, `tool_choice` applies **after** GPT-Live delegates to the backend; it does not stop the live model from delegating. Prompt instructions are the only documented mechanism to prevent GPT-Live from initiating a delegation.

*Source:*
- `https://developers.openai.com/api/docs/guides/live-delegation`

### 6.3 Restricting Delegation Per Session
* There is **no configuration setting** such as `delegation: { type: "none" }`. Omitting `delegation` or setting it to `null` defaults to client delegation.
* Delegation can only be disabled or restricted through prompt engineering in `session.instructions` or dynamically steered via `session.instructions.append`.

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-conversations`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference`

---

## 7. Mid-Session Switching

### 7.1 Can a Session Switch Delegation Type Mid-Session?
* **NO.** The delegation mode (`type: "client"` vs `type: "responses"`) is **immutable after session creation**.
* In OpenAI GPT-Live, attempting to send `session.update` with a changed delegation type or `delegation: null` fails with:
  ```json
  {
    "type": "error",
    "event_id": "event_error",
    "error": {
      "type": "invalid_request_error",
      "code": "immutable_field_update",
      "message": "The delegation type cannot change after session startup.",
      "param": "session.delegation.type",
      "client_event_id": "event_update"
    }
  }
  ```
* To switch delegation modes, the application **must close the current session (`session.close`) and start a new session (`session.start`)**.

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://developers.openai.com/api/docs/guides/live-conversations`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference`

### 7.2 Consequences for Presenter-AI
* **Single Mode Selection:** Presenter-AI must establish whether it operates under Client delegation or Responses delegation when opening the GPT-Live WebSocket.
* **Tiered Strategy Architecture:** If Presenter-AI wishes to use deck knowledge first and a backend model second:
  - **With Client Delegation:** Presenter-AI handles the delegation itself. When `session.delegation.created` fires, Presenter-AI checks its local deck embeddings/text. If found, it appends `session.commentary.append` immediately. If not found, it calls an external backend model (via standard Chat Completions / Responses API) and appends the result. **This avoids restarting the GPT-Live session.**
  - **With Responses Delegation:** Presenter-AI delegates all backend tasks directly to OpenAI/Azure's Responses model. Tiering would require the backend model to run a custom function tool (e.g. `lookup_deck_notes`) that calls back to Presenter-AI.

---

## 8. Additional Factors for Real-Time Presentation Q&A

### 8.1 Speculative Context Retrieval via Transcript Deltas
* Both delegation modes emit `session.input_transcript.delta` with `start_ms` and `end_ms` as the user speaks:
  ```json
  {
    "type": "session.input_transcript.delta",
    "event_id": "event_transcript_1",
    "delta": "What was the revenue in",
    "start_ms": 1000,
    "end_ms": 1350
  }
  ```
* Applications do **not** need to wait for `session.delegation.created` to begin searching for answers.
* The documentation explicitly highlights **speculative lookups**:
  *"Start a speculative lookup when enough information is available—for example, checking availability while the user continues describing their preferences... Discard outdated results if later speech changes the request."*
* Pre-querying deck notes while the audience member is still speaking can shave 1–2 seconds off the perceived answer latency.

*Source:*
- `https://developers.openai.com/api/docs/guides/live-delegation`

### 8.2 Dynamic Context Updates via Sparse `session.update`
* In a session using Responses delegation, `session.update` can be called mid-session to update `session.delegation.responses`:
  ```json
  {
    "type": "session.update",
    "event_id": "update_deck_context_slide_3",
    "session": {
      "delegation": {
        "responses": {
          "instructions": "Current Slide: Slide 3 - Architecture. Details: ..."
        }
      }
    }
  }
  ```
* This allows the backend Responses model's instructions to be refreshed on slide transitions so it always has the active slide context.
* It is acknowledged with `session.updated`.

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-delegation`
- `https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference`

### 8.3 Interruption Dynamics and Slide Advance Timer Conflicts
* **Interruption Rule:** Appending instructions (`session.instructions.append`) can **interrupt and cut off** the model's current speech.
* **Race Condition in Presenter-AI:** In Presenter-AI's current architecture, a silence timer automatically fires `session.instructions.append` to narrate the next slide chunk when the model stops speaking.
* When the user interrupts and asks a question:
  1. The model speaks a filler acknowledgment (*"I'll check on that for you"*).
  2. The model then pauses to wait for the backend result.
  3. Because the model goes quiet, the silence timer expires (e.g. after 5 seconds) and triggers `session.instructions.append slide-X-part-Y`.
  4. This next-slide instruction immediately cancels the answer and forces the model to move to the next slide!
* **API Fact:** The model cannot prevent this; the host application must suppress slide advancement while an audience delegation or question is pending.

*Sources:*
- `https://developers.openai.com/api/docs/guides/live-prompting`
- `https://developers.openai.com/api/docs/guides/live-conversations`

### 8.4 Content Size Limit on Appends (500 Tokens)
* All context appends (`session.instructions.append`, `session.thinking.append`, and `session.commentary.append`) strictly enforce a maximum size of **500 tokens per append**.
* If a slide's speaker notes or a generated answer exceeds 500 tokens, it cannot be sent in a single event.

*Source:*
- `https://developers.openai.com/api/docs/guides/live-conversations`

### 8.5 Context Window and Automatic In-Session Summarization
* GPT-Live sessions have a default context window of **128,000 tokens**.
* Older history is summarized in the background. When context usage exceeds **90%**, GPT-Live automatically restarts an internal replacement voice engine within the same session.
* The replacement engine preserves startup instructions and up to 8,192 tokens of recent/summarized history. Detailed application state or slide history must be maintained by the client application.

*Source:*
- `https://developers.openai.com/api/docs/guides/live-conversations`

---

## Implications for Our Design (Facts Only, No Code)

1. **Client Delegation fits our tiered architecture better than Responses Delegation:**
   - Client delegation lets Presenter-AI inspect the user's transcript delta immediately and search its local deck context. If answered locally, it appends `session.commentary.append` in milliseconds. If not covered locally, Presenter-AI dispatches to its own backend model.
   - Responses delegation delegates directly to an OpenAI/Azure backend model, requiring a round-trip function call back to Presenter-AI (`response.output_item.done` $\rightarrow$ `response.item.create` $\rightarrow$ `response.create`) just to check deck notes.
2. **Client Delegation requires transcript reconstruction:**
   - `session.delegation.created` carries no utterance text; Presenter-AI must accumulate `session.input_transcript.delta` text to know what was asked.
3. **Speculative search reduces latency:**
   - Because `session.input_transcript.delta` streams in real time as the audience speaks, Presenter-AI can run speculative vector/keyword lookups across deck notes before speech ends, minimizing the gap between the filler (*"I'll check on that"*) and the answer.
4. **Slide advance timers must be paused during delegation:**
   - GPT-Live will go silent after acknowledging the question while waiting for the backend.
   - The silence timer that triggers `session.instructions.append` for subsequent slides must be disarmed when user speech or a delegation is active, otherwise the next slide append interrupts and destroys the answer.
5. **Answers must use `session.commentary.append` (not `session.instructions.append`):**
   - `session.instructions.append` interrupts active speech and enforces behavior changes.
   - `session.commentary.append` provides the exact text the model is trained to paraphrase aloud to the audience.
6. **Responses and Appends are strictly limited to 500 tokens:**
   - Any answer provided via `session.commentary.append` must be under 500 tokens.
7. **Delegation mode cannot be switched dynamically:**
   - Choosing Responses delegation vs Client delegation is a one-way decision per WebSocket connection. A failure cannot fall back between delegation types without terminating the connection and creating a new session.
8. **Prompt must explicitly define what to delegate:**
   - The system prompt must contain a clear `Delegation policy:` section directing the model to answer directly from slide narration and speaker notes, and only delegate when external information is requested.

---

## Sources

* **OpenAI — Delegation and tools in GPT-Live:**
  `https://developers.openai.com/api/docs/guides/live-delegation`
* **OpenAI — Managing GPT-Live sessions:**
  `https://developers.openai.com/api/docs/guides/live-conversations`
* **OpenAI — Getting started with GPT-Live:**
  `https://developers.openai.com/api/docs/guides/live`
* **OpenAI — Prompting GPT-Live:**
  `https://developers.openai.com/api/docs/guides/live-prompting`
* **OpenAI — Server-side controls (Sideband & Guardrails):**
  `https://developers.openai.com/api/docs/guides/voice-server-controls`
* **OpenAI — Cost optimization (GPT-Live pricing & latency):**
  `https://developers.openai.com/api/docs/guides/voice-latency-cost`
* **OpenAI — GPT-Live 1 Model Reference:**
  `https://developers.openai.com/api/docs/models/gpt-live-1`
* **Microsoft Learn — GPT-Live event API reference:**
  `https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference`
* **Microsoft Learn — Delegate work in GPT-Live:**
  `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation`
* **Microsoft Learn — Use GPT-Live for real-time voice:**
  `https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live`


## Part B — design grounded in the code (`pi` sol)


## Recommendation in one sentence

Keep **client delegation**, let GPT-Live answer deck-covered questions directly, treat a client delegation as the routing decision for an out-of-deck question, call a backend Responses model, and return short sentence-sized answer chunks through delegation-scoped `session.commentary.append`; independently, add a 15-second question hold to `Presenter` so no automatic part/slide/wrap-up transition can occur before post-question voiced output has finished.

## 1. Root-cause confirmation

### Failure 1: the delegated question is deliberately discarded

- `LiveSession` starts every session with `delegation: { type: "client" }` (`src/PresenterAi.Infrastructure/Live/LiveSession.cs:499-511`).
- It receives `session.delegation.created`, extracts only the `delegation` object and raises `Delegation` (`LiveSession.cs:433-434`).
- `Presenter.OnDelegation` validates the session, then only logs that the delegation is ignored (`src/PresenterAi.Application/Presenting/Presenter.cs:572-579`). It neither appends context nor starts backend work.
- This is not caused by a missing transport primitive: delegation-scoped `AppendInstructions`, `AppendThinking`, and `AppendCommentary` already exist (`src/PresenterAi.Application/Presenting/ILiveSession.cs:26-30`; implementations at `LiveSession.cs:121-133`, payload including `delegation_id` at `LiveSession.cs:470-485`).

The log wording is accurate about the event: a client delegation contains metadata, not task text. The API requires the application to reconstruct the request from transcript events and application state. It does **not** justify ignoring the request. [OpenAI delegation guide](https://developers.openai.com/api/docs/guides/live-delegation) [Microsoft delegation guide](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation)

### Failure 2: ordinary silence timing remains active

- A user transcript merely calls `ArmAfterVoice()` when presenting and `_heardOutput` is already true (`Presenter.cs:541-557`). There is no “question open” state.
- Every voiced output frame also calls `ArmAfterVoice()` (`Presenter.cs:500-537`). Thus “I’ll check on that for you” is enough output to arm the ordinary timer.
- With no pending narration part, `ArmAfterVoice()` arms `AdvanceSilenceMs` (`Presenter.cs:914-926`). When it expires, `OnSilence()` advances to the next slide (`Presenter.cs:604-625`). That explains the observed five-second hop.

### Every other automatic path that can move on while a question is open

| Path | Current behavior and consequence |
|---|---|
| **Part gap** | If `PartsPending`, post-speech silence is only `PartGapFor(AdvanceSilenceMs)` and `OnPartGap()` sends the next narration part (`Presenter.cs:596-602`, `:914-923`). A promise or acknowledgment can therefore resume narration after at most 2.5 seconds, even before an answer. |
| **Wrap-up after voiced output** | During wrap-up, any voiced output arms normal silence; `OnSilence()` then ends the session (`Presenter.cs:608-614`). An audience question or “I’ll check” can therefore be followed by close rather than an answer. |
| **Wrap-up fallback** | If wrap-up has produced no voiced output, its independent 15-second fallback ends the run (`Presenter.cs:663-669`, timer at `:939-948`). User transcript does not cancel it. |
| **Nudge escalation / stall pause** | Before the first voiced output, user transcript does not affect `_heardOutput` or the nudge timer. Nudges at 15/30 seconds and pause at 45 seconds continue (`Presenter.cs:629-660`). A question asked during initial silence can therefore receive a slide nudge and eventually a stall pause. |
| **Empty narration** | `PresentSlide` sets `_heardOutput = true` and arms ordinary silence for an empty slide (`Presenter.cs:475-481`); a user transcript only restarts that same deadline. |
| **Manual navigation** | `Next`, `Prev`, and `Goto` intentionally change slides immediately (`Presenter.cs:690-739`). This should remain an explicit override, but it must cancel question/backend state so a late answer cannot leak onto the new slide. |

The per-slide diagnostic counters do not control movement. They only count all user transcript characters and output frames while active (`Presenter.cs:541-552`, `:901-912`).

## 2. Tiered answering options

### Option A — client delegation plus an application-owned Responses call (**recommended**)

**Route.** Strengthen the live prompt so GPT-Live answers immediately when narration, current speaker notes, or background context is sufficient and delegates only when it needs external knowledge. Treat `session.delegation.created(target="client")` as that routing decision: always start the backend for a valid active question. This avoids a second classifier call.

Immediately append a delegation-scoped instruction such as:

> A backend answer is being prepared for this audience question. Do not resume the deck. Do not claim that you checked or found anything. If you need to speak before the result, give one brief neutral acknowledgment, then wait for commentary.

Call a backend Responses model over HTTP with separately labelled trusted deck context and recent conversation transcript. Return concise, verified output using `AppendCommentary(..., delegationId)`; commentary is intended to be spoken and may be paraphrased, while thinking is quiet context. Appends are limited to 500 tokens and repeated appends may continue one client delegation. Stream only complete sentence-sized chunks, not raw token deltas, to avoid fragmented or repeatedly paraphrased speech. [OpenAI delegation guide](https://developers.openai.com/api/docs/guides/live-delegation) [Microsoft delegation guide](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation) [Azure GPT-Live reference](https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference)

- **Recovering question text:** retain a bounded rolling window of `session.input_transcript.delta` and `session.output_transcript.delta`. On delegation, use `offset_ms` plus each fragment’s `start_ms/end_ms` to select the latest user utterance and a small amount of preceding conversational context. There is no complete-turn event or item ID, fragments can be partial/uneven, and the delegation itself has no utterance, so preserve fragments and timestamps rather than concatenating one global string. [OpenAI conversations guide](https://developers.openai.com/api/docs/guides/live-conversations) [OpenAI delegation guide](https://developers.openai.com/api/docs/guides/live-delegation)
- **Deck-can-answer decision:** GPT-Live makes it under the strengthened prompt. No delegation means direct live answer; a client delegation means backend. If a delegation arrives with no recoverable nonblank recent user text, fail closed with scoped commentary asking the audience to repeat rather than guessing.
- **What the audience hears:** deck question: immediate answer. Delegated question: at most one short neutral acknowledgment, then the backend answer. Optional `thinking` progress is not spoken; use commentary only when a delay update is genuinely useful.
- **Failure/timeout:** use an 8–10 second backend deadline inside the overall 15-second hold. On HTTP error, invalid output, or timeout, append scoped commentary: “I couldn’t retrieve that quickly. Please say that the material doesn’t cover it, then return to the current slide.” Cancel/ignore stale work by question generation. Never append a late answer after navigation, pause, end, or hold expiry.
- **Latency:** lowest for deck questions; one backend round trip only for delegated questions. Sentence buffering adds a small delay but gives coherent speech.
- **Cost:** backend tokens only on delegated questions; however deck/background context sent on each call can be expensive, so cache/reuse prepared context where the provider permits and cap the transcript window.
- **Complexity:** medium/high: transcript correlation, an `IQuestionAnswerer` port, provider HTTP implementations, cancellation, and stale-result suppression.
- **Azure support risk:** low for the live side because client delegation and scoped appends are explicitly supported. The separate Responses endpoint/model still needs a deployed, configured Azure model; OpenAI fallback needs its own route/configuration. This is operationally more work but does not depend on managed Responses delegation parity.

### Option B — managed Responses delegation configured in `session.start`

Configure `delegation.type = "responses"` with a backend model, instructions, and any tools at session creation. GPT-Live supplies conversation context, starts delegated work, and injects delegated output into the live conversation. The response model is selected independently from the voice model. [OpenAI delegation guide](https://developers.openai.com/api/docs/guides/live-delegation) [Azure GPT-Live reference](https://learn.microsoft.com/en-us/azure/foundry/openai/gpt-live-reference)

- **Recovering question text:** the application does not reconstruct it for the backend; GPT-Live supplies conversation context. Continue retaining transcript fragments for the hold, diagnostics, UI, and error handling.
- **Deck-can-answer decision:** live prompt tells GPT-Live to answer from deck context first and delegate otherwise. Put the relevant deck-answering policy/context in managed backend instructions too. Startup instructions can be up to 16,384 tokens; duplicating a large background context must be budgeted. [OpenAI conversations guide](https://developers.openai.com/api/docs/guides/live-conversations)
- **What the audience hears:** direct deck answer or a live acknowledgment while managed work runs. Delegated work and live speech are independent; backend completion does not prove the audience heard the answer, so the hold must still watch post-question audio/output transcript. [OpenAI delegation guide](https://developers.openai.com/api/docs/guides/live-delegation)
- **Failure/timeout:** expose `response.event` and delegated errors from `LiveSession`; on failure append a general corrective instruction/commentary and release only through the same 15-second policy.
- **Latency/cost:** likely lower orchestration latency and less application code; backend Responses usage is still charged. Measure on the actual deck and deployment.
- **Complexity:** medium: startup configuration and `response.event` handling, but no custom backend request assembly.
- **Azure support risk:** medium. Azure documents Responses delegation, models/tools, and `session.update`, but the configured Responses model must exist in the Foundry resource/deployment and Live supports only a subset of standalone Responses settings. The OpenAI fallback may require a different backend model name/configuration. Delegation mode cannot be switched in-place; changing mode requires a new session. [Microsoft delegation guide](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation) [OpenAI delegation guide](https://developers.openai.com/api/docs/guides/live-delegation)

### Option C — application transcript router, no reliance on model delegation

On a likely completed user utterance (short silence inferred from transcript timestamps/audio), run a cheap classifier or retrieval check against narration, notes, and background context. Append a deck answer as context or call a larger backend model for the rest. Client delegation can remain as a safety signal, but the application starts work before it.

- **Recovering question text:** same rolling transcript window, but the application must infer turn completion because transcript events have no completed-turn marker. [OpenAI conversations guide](https://developers.openai.com/api/docs/guides/live-conversations)
- **Deck-can-answer decision:** cheap model or retrieval score; ambiguous cases go to the backend.
- **What the audience hears:** the live model should give a short acknowledgment while routing runs; direct live answers must be suppressed or deduplicated to avoid two answers.
- **Failure/timeout:** same 15-second fallback and scoped apology.
- **Trade-offs:** potentially starts backend work earlier, but adds a classifier call/cost, turn-detection latency, duplicated-answer races, and substantially more state. Azure Live feature risk is low; application correctness risk is highest.

### Why A

Option A is the smallest reliable extension of the current, cross-provider design. It preserves the fast zero-extra-call path for deck questions, uses the delegation the model already emitted rather than paying for another classifier, and retains control over context, validation, timeout, and fallback. Option B should be the first follow-up benchmark; adopt it later if both Azure and OpenAI routes prove compatible and measurably faster.

## 3. Hold-until-answered in the single-threaded event loop

### State

All mutations stay in `Presenter.RunLoopAsync`; backend continuations enqueue immutable events rather than touching fields.

Suggested fields/types:

```text
QuestionState? _question
  Generation                 // rejects stale timers/backend completions
  OpenedAt / LastInputEndMs
  AnswerVoiced               // eligible post-question voiced audio observed
  DelegationId?              // client delegation bound to this question
  WaitingForDelegatedResult  // promise/ack audio is not an answer
  ResultInjected             // backend commentary accepted for sending
  BackendCancellation

Queue<TranscriptFragment> _recentTranscript // role, delta, start/end; bounded by time/chars
ITimer? _questionFallbackTimer
long _questionFallbackGeneration
```

Do not overload `_heardOutput`: it means the slide has ever produced voice and drives stall behavior. A question needs independent lifecycle state.

### Event rules

1. **Nonblank user transcript while presenting**
   - Always retain the fragment and count it in T6 diagnostics.
   - Create or extend `_question`; increment only for a genuinely new question, but restart its fallback from the latest input fragment because one utterance arrives as multiple deltas.
   - Clear `_silenceTimer` immediately, including a pending part gap. If wrapping up, clear `_wrapUpTimer` too. Do **not** clear/restart the slide nudge timer: if the model has never voiced output, T4 remains applicable.
   - Ignore whitespace-only deltas. Retain timestamps and require at least one letter/digit before launching backend work.

2. **Voiced output after the question**
   - It is eligible only when its timeline is later than `LastInputEndMs` (when timestamps exist).
   - For a direct/deck answer, mark `AnswerVoiced = true` and re-arm the ordinary post-voice timer on every voiced frame. The question remains open during speech.
   - Once a client delegation is created, reset `AnswerVoiced = false` and set `WaitingForDelegatedResult = true`; this prevents an earlier “I’ll check” from satisfying the hold. Only voice observed after delegated commentary has been injected is eligible.
   - Silent audio frames do nothing, as today.

3. **Silence/part-gap event**
   - If a question is open and `AnswerVoiced` is false, ignore the event.
   - If a question is open and `AnswerVoiced` is true, this event means the answer has gone quiet: clear question/backend/fallback state, then perform the normal action represented by current state—send the pending part for a part-gap, advance/start wrap-up for ordinary silence, or end a voiced wrap-up. This keeps the existing pacing after the answer.

4. **Delegation created**
   - Parse and require `id` and `target=client`; bind it to the current question using `offset_ms` and recent transcript. If transcript delivery is late, allow a short bounded correlation window rather than inventing task text.
   - Reset answer eligibility, append the scoped wait instruction, and start `IQuestionAnswerer.AnswerAsync` without awaiting it in the event loop.
   - Queue `QuestionAnswerChunk`, `QuestionAnswerCompleted`, or `QuestionAnswerFailed` events carrying generation and delegation ID.

5. **Delegation answered**
   - On each complete sentence chunk, append commentary with the same delegation ID. Optional factual progress goes through thinking.
   - Completion/result injection changes the phase to waiting for voiced answer; it does **not** clear the hold. An append acknowledgment proves estimated context injection, not consumption, speech, or playback. [Microsoft delegation guide](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation)
   - Error/timeout appends the scoped honest failure wording and waits for that wording to be voiced, bounded by the overall fallback.

6. **15-second question fallback**
   - Starts/restarts at the latest nonblank user fragment. On expiry without eligible answer voice, cancel backend work, invalidate its generation, append the scoped honest failure/return instruction if possible, clear `_question`, and restore normal timing.
   - If `_heardOutput` is true, arm ordinary silence/part-gap from that point; do not advance synchronously. If false, leave T4’s nudge escalation running (or arm it if absent). During an unvoiced wrap-up, restart the existing wrap-up fallback rather than closing immediately.

7. **Pause/resume**
   - Explicit pause and T4 stall pause cancel and clear the question/backend/fallback state; `PauseCore` already blocks automatic movement and instructs silence. This avoids a backend result speaking through pause.
   - Resume starts the existing slide/wrap-up recovery and T4 nudge sequence from its first nudge; it does not resurrect an ambiguous old question. The audience can repeat it.

8. **Navigation/end/session close**
   - `PresentSlide` (manual or automatic), `StartWrapUp` when entered without an open question, `Next/Prev/Goto`, `End`, close, and disposal cancel/invalidate question work and clear its timer. Manual navigation remains an explicit override.
   - A stale queued backend event is discarded by generation/delegation ID.

9. **T4 nudges and T6 diagnostics**
   - If voice existed before the question, `_heardOutput` remains true and T4 is already canceled; the 15-second question fallback is the protection.
   - If no voice ever existed, the existing nudge schedule continues while the hold suppresses part/slide movement. Pause at T4’s terminal escalation clears the question as above.
   - T6 counters remain per slide segment: question transcript characters and answer audio count naturally; the line is delayed until the held slide actually ends. Stall pause still completes the diagnostic line, and resume starts the existing new segment. Questions during wrap-up are not attributed to a completed slide because `StartWrapUp` already closes slide diagnostics.

### Sequence

```mermaid
sequenceDiagram
    participant A as Audience
    participant L as GPT-Live
    participant P as Presenter loop
    participant B as Responses backend

    A->>L: asks question (audio)
    L-->>P: input_transcript.delta(s)
    P->>P: open/extend hold; cancel silence/part-gap; arm 15 s fallback
    alt deck/context can answer
        L-->>P: answer audio
        P->>P: mark eligible voice; re-arm post-voice silence
        P->>P: silence expires; clear hold
    else external knowledge needed
        L-->>P: session.delegation.created(id, offset)
        P->>L: instructions.append(id): wait; do not resume/promote unverified result
        P-->>B: recent transcript + trusted deck context
        opt brief acknowledgment
            L-->>A: “One moment.”
        end
        B-->>P: complete sentence answer chunk(s)
        P->>L: commentary.append(chunk, delegation_id=id)
        L-->>A: speaks/paraphrases answer
        L-->>P: answer audio
        P->>P: mark eligible voice; re-arm post-voice silence
        P->>P: silence expires; clear hold
    end
    P->>L: pending narration part / next slide / wrap-up action

    opt no eligible answer by 15 s
        P->>B: cancel; invalidate generation
        P->>L: honest failure + return instruction
        P->>P: clear hold; restore normal timers (T4 still applies)
    end
```

### False triggers

The plan-005 echo gate materially reduces self-speech reaching GPT-Live, but it cannot prove speaker identity. Room noise can still become a `user` transcript, and transcript fragments can be mistaken or incomplete. The safe policy is:

- ignore blank/punctuation-only fragments for opening backend work;
- use timestamps and a bounded recent window;
- make the hold immediately reversible by manual navigation/pause;
- cap the disruption at 15 seconds;
- log question-open, delegation-bound, answered, and fallback transitions for live diagnosis.

Do not add a semantic “is this a question?” classifier merely to engage the hold: audience corrections and commands also deserve not to be talked over, and classifier latency would recreate the race. If live evidence shows frequent false holds after the echo gate, add a minimum voiced-duration/input-confidence signal only if GPT-Live exposes a reliable one; the current transcript contract does not.

## 4. Prompt changes

In `PromptBuilder.SystemInstructions` (`src/PresenterAi.Application/Presenting/PromptBuilder.cs:39-42`), replace the audience rules with behavior equivalent to:

- Stop and listen when the audience speaks.
- If narration, current speaker notes, or background context contains the answer, answer immediately in one to three sentences.
- Otherwise delegate the question for external help. **Never say “I’ll check,” “I found,” or imply an external lookup has happened unless a delegated result has arrived.** While waiting, at most say “One moment.”
- If delegation fails or no result is supplied, say plainly that the answer is not available; do not invent one.
- Do not resume narration or start another slide until the question has been answered or the controller explicitly tells you to continue.
- After the answer, bridge back and resume the current narration from the interruption point.

Add dedicated builders for the delegation wait, delegation failure, and question-timeout instructions so tests pin their wording. These changes do not conflict with the tiered path: the live model owns the deck-answer path, while the application supplies externally verified commentary only after delegation. The key conflict in the current prompt is “If the answer is not in your material, say so honestly,” which permits an immediate dead-end instead of delegation; make delegation the preferred next action and honesty the failure fallback.

## 5. Proposed plan-005 tasks

| # | Change | Files | Verify | Test that dies if this hop breaks |
|---|---|---|---|---|
| T8 | Add question hold, dedicated 15 s timer/generation, and suppression of advance, part gap, wrap-up fallback, and stale automatic events | `src/PresenterAi.Application/Presenting/Presenter.cs`; `tests/PresenterAi.Application.Tests/Presenting/PresenterTests.cs` | `dotnet test` Application | `Question_holds_slide_until_post_question_voice_goes_quiet`; `Question_holds_pending_part_until_answer_finishes`; `Question_during_wrap_up_prevents_close`; `Unanswered_question_releases_to_normal_timing_after_15_seconds`; `Question_before_any_output_keeps_stall_escalation_active`. Existing `Harness` + `FakeTimeProvider`; extend `FakeSession.Hear`/`Speak` to accept timeline ranges. |
| T9 | Retain bounded transcript fragments, correlate delegation by `offset_ms`, preserve delegation ID in fake sends, and queue backend completions through the presenter loop | `Presenter.cs`, `ILiveSession.cs` only if a typed delegation event is chosen, `tests/.../FakeSession.cs`, `PresenterTests.cs` | Application tests | `Client_delegation_uses_latest_timestamped_user_turn`; `Delegation_without_question_text_asks_for_repeat`; `Stale_backend_answer_after_navigation_is_dropped`. `FakeSession.RaiseDelegation(json)` plus a fake `IQuestionAnswerer`; `Sent` tuple must include `DelegationId`. |
| T10 | Add the application port and Infrastructure HTTP Responses implementation with timeout, cancellation, bounded context, provider-specific route/config, and sentence-sized streaming | new `src/PresenterAi.Application/Presenting/IQuestionAnswerer.cs`; new Infrastructure live/answers implementation; Infrastructure DI/options; API and CLI configuration wiring; focused Infrastructure tests | Application + Infrastructure tests; options validation on startup | `ResponsesQuestionAnswerer_sends_recent_transcript_and_deck_context_as_separate_roles`; `Streams_only_complete_bounded_chunks`; `Cancellation_stops_and_suppresses_late_chunks`; `Provider_error_returns_typed_failure`. Use a fake `HttpMessageHandler`, never a real endpoint. |
| T11 | Handle delegation: immediate scoped wait instruction, backend result commentary/thinking, honest failure, and no hold release on append acknowledgment alone | `Presenter.cs`, `PromptBuilder.cs`, `PresenterTests.cs`, `PromptBuilderTests.cs`, prompt goldens | Application tests | `Delegation_sends_wait_instruction_then_scoped_commentary`; `Append_ack_does_not_release_question_hold`; `Backend_failure_sends_honest_scoped_fallback`; `Promise_audio_before_delegation_does_not_count_as_answer`. Existing presenter `Harness`, extended `FakeSession`, fake answerer. |
| T12 | Pin Live transport payloads/events needed by the design (`offset_ms`, delegation object, delegation-scoped append); keep client delegation startup | `src/PresenterAi.Infrastructure/Live/LiveSession.cs`; `tests/PresenterAi.Infrastructure.Tests/Live/LiveSessionTests.cs`; `FakeLiveServer.cs` | Infrastructure tests | `Delegation_event_preserves_id_target_and_offset`; `Commentary_append_carries_client_delegation_id`; existing `Connect_sends_session_start_and_receives_started` continues to pin `type=client`. Fake WebSocket server. |
| T13 | Prompt policy and regression matrix across pause/resume, manual navigation, diagnostics, empty narration, multi-part slides, wrap-up, and echo-like transcript | `PromptBuilder.cs`, prompt goldens, `PresenterTests.cs`; plan §7 live runbook | full .NET suite; live loudspeaker run, ask one deck and one external question; record usage seconds | `Pause_cancels_open_question_and_late_answer`; `Manual_navigation_cancels_open_question`; `Question_answer_counts_in_current_slide_diagnostics`; `Echo_like_user_delta_can_hold_for_at_most_15_seconds`; updated prompt golden fails if “never promise/delegate/do not resume” policy disappears. |

Mutation evidence expected before completion: remove the question guard from `OnPartGap` and see the pending-part test fail; count pre-delegation promise audio and see that test fail; omit `delegation_id` and see both Application and Infrastructure payload tests fail; remove generation checking and see the stale-answer navigation test fail.

## 6. Risks and open questions

| Risk/open question | Recommended answer |
|---|---|
| **Which option should ship in plan 005?** | Ship Option A. Benchmark Option B separately against both configured upstreams before changing delegation mode. |
| **What exactly counts as “deck can answer”?** | Let GPT-Live decide under the strengthened prompt using narration, current notes, and background context. A delegation is the backend route; do not add a second classifier in this amendment. |
| **How much transcript/context goes to the backend?** | Latest user turn selected around delegation `offset_ms`, preceding assistant turn, current slide narration/notes, outline, and bounded background context. Keep trusted context separate from untrusted transcript and cap both by configuration. |
| **Can a commentary acknowledgment be treated as answered?** | No. It confirms estimated context injection, not that the model consumed or spoke it. Require eligible output audio followed by silence, with the 15-second escape hatch. [Microsoft delegation guide](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/gpt-live-delegation) |
| **Should raw backend tokens be streamed?** | No. Buffer complete short sentences (and stay under 500 tokens per append). This preserves low perceived latency without producing choppy/repeated paraphrases. |
| **What should happen at 15 seconds?** | Cancel/invalidate backend work, send one honest scoped fallback if possible, release the hold, and restore the timer appropriate to pending part/slide/wrap-up. Do not jump slides synchronously. |
| **Should backend work continue during pause or after navigation?** | No. Cancel and invalidate it. Preventing a stale spoken answer on another slide is more important than salvaging the request. |
| **Could a promise still be mistaken for an answer?** | Direct path audio is necessarily heuristic, but creation of a delegation resets answer eligibility; delegated voice counts only after result injection. Strengthened prompt forbids promises. Output-transcript keyword matching should not be the correctness mechanism. |
| **Could echo/noise hold the deck?** | Yes, despite the echo gate. Ignore empty/punctuation-only fragments, instrument it, preserve manual override, and cap at 15 seconds. Do not weaken real barge-in by requiring a question mark or classifier. |
| **Backend model/deployment and budget?** | User must choose one Azure Responses deployment and one OpenAI fallback model, with an 8–10 second request timeout and explicit per-answer token ceiling. Validate all option keys at startup; do not derive HTTP endpoints or credentials from the Live WebSocket URL. |
| **Privacy/retention?** | Keep only a bounded in-memory transcript window for live routing unless existing session recording policy already authorizes persistence. Never put credentials, private reasoning, or unredacted tool output into Live appends; OpenAI explicitly advises keeping secrets in the backend. [OpenAI conversations guide](https://developers.openai.com/api/docs/guides/live-conversations) |
| **How is success observed live?** | Logs should include question generation, transcript range, delegation ID (not transcript contents at info level), backend latency/outcome, first eligible answer voice, hold duration, and fallback reason. The live run must ask one in-deck and one out-of-deck question and confirm no part/slide/wrap-up hop before each answer finishes. |

