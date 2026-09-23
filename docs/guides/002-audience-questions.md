# 002 — Audience questions

How presenter-ai answers questions from the audience during a talk, how to configure it, and how to read the log
when something goes wrong. Design and decisions: plan 005 §4.5 (amendment A1); API research:
`docs/research/005-gpt-live-delegation-audience-questions.md`.

## What happens when someone asks a question

1. The AI stops talking and listens. The slide is **held**: it does not advance and the next narration part is not
   sent while a question is open.
2. **Deck first.** If the narration, speaker notes or background context cover the question, the AI answers
   immediately in one to three sentences.
3. **Backend otherwise.** If the material does not cover it, GPT-Live hands the question to a second model (the
   *delegation model*, `gpt-5.6-luna` by default). The AI says at most "One moment." and speaks the backend's answer
   when it arrives.
4. **Follow-up window.** After answering, the AI stays silent for the follow-up wait (5 s by default). A new question
   in that window starts again at step 1.
5. **Resume.** When the window passes quietly, the presenter tells the AI to say a short bridge ("Back to the slide")
   and restart the sentence it was in when it was interrupted. Normal slide timing then takes over.

If nothing answers within 15 s, the hold is released anyway and the AI is told to resume, so a missed or misheard
question never stalls the talk.

## Configuration

| Setting (.NET key) | Docker `.env` variable | Default | Meaning |
|---|---|---|---|
| `Upstream:DelegationModel` | `UPSTREAM_DELEGATION_MODEL` | `gpt-5.6-luna` | Model that answers questions the deck does not cover. OpenAI: model id. Azure: **deployment name** in the same resource as GPT-Live. Empty = deck-only answers. |
| `Upstream:Fallback:DelegationModel` | `FALLBACK_DELEGATION_MODEL` | `gpt-5.6-luna` | The same for the OpenAI fallback upstream. |
| `Presenter:FollowUpWaitMs` | `FOLLOW_UP_WAIT_MS` | `5000` | Quiet after an answer before the slide resumes. Allowed `2500`–`60000`; the API refuses to start outside that range. Below ~2.5 s the resume could fire inside a pause of the answer itself. |

The backend call settings are fixed in code (`LiveSession.CreateDelegation`): low reasoning effort, `priority`
service tier, low verbosity. The backend instructions name the talk title and ask for one to three short spoken
sentences.

Settings are read when the API starts; restart it after a change. The delegation model is also fixed per session,
so it applies from the next **Start**.

### Local .NET run (`dotnet run --project src/PresenterAi.Api`)

The local API does **not** read `.env`. It reads, later sources winning:

1. `src/PresenterAi.Api/appsettings.json` (the defaults above)
2. `src/PresenterAi.Api/appsettings.Development.json`
3. user-secrets, id `presenter-ai-api`: `%APPDATA%\Microsoft\UserSecrets\presenter-ai-api\secrets.json` (Windows) or
   `~/.microsoft/usersecrets/presenter-ai-api/secrets.json` (macOS/Linux)
4. environment variables (`Upstream__DelegationModel`, `Presenter__FollowUpWaitMs`, …)
5. command-line arguments (`--Presenter:FollowUpWaitMs=8000`)

Set values with user-secrets. The file stores flat keys (`"Presenter:FollowUpWaitMs": "8000"`), not nested objects:

```bash
dotnet user-secrets set "Presenter:FollowUpWaitMs" "8000" --project src/PresenterAi.Api
dotnet user-secrets set "Upstream:DelegationModel" "<azure-deployment-name>" --project src/PresenterAi.Api
dotnet user-secrets set "Upstream:DelegationModel" "" --project src/PresenterAi.Api      # deck-only
dotnet user-secrets remove "Upstream:DelegationModel" --project src/PresenterAi.Api       # back to the default
```

### Docker Compose

Put the variables in `.env` (see `.env.example`); `docker-compose.yml` maps them to the .NET keys. Leaving a
variable out keeps the default. `UPSTREAM_DELEGATION_MODEL=` (set but empty) turns the backend off.

## How a question flows through the code

```
Browser mic ──PCM──► PresenterBridge (/ws) ──► Presenter.SendAudioAsync ──► LiveSession.SendAudio ──► GPT-Live
                                                                          (session.input_audio.append)

session.start (LiveSession.CreateDelegation):
  delegation: { type: "responses", responses: { model: <DelegationModel>, instructions, reasoning, service_tier, text } }
  GPT-Live itself calls the delegation model; presenter-ai never calls it directly.

GPT-Live ──► LiveSession.ReceiveLoopAsync ──► LiveSession.HandleEvent
  session.input_transcript.delta   ──► Presenter.OnTranscript ──► OpenOrExtendQuestionHold
                                        hold opens, slide/part/wrap-up timers cleared, 15 s timer armed
  (deck covers it)
  session.output_audio.delta       ──► Presenter.OnAudio: first voiced audio after the question = the answer
                                        15 s timer stopped, follow-up timer (FollowUpWaitMs) armed
  (deck does not cover it)
  session.delegation.created       ──► Presenter.OnDelegation: "delegated (backend)"; speech is filler until…
  response.event (response.completed / failed)
                                   ──► LiveSession.HandleResponseEvent ──► DelegatedResponseFinished
                                   ──► Presenter.OnDelegatedResponse: "backend answer ready"; 15 s timer re-armed
  session.output_audio.delta       ──► Presenter.OnAudio: the backend answer, then the follow-up timer as above

follow-up timer fires ──► Presenter.OnSilence ──► HoldBlocksProgress ──► ResumeAfterQuestion
                          appends "slide-N-resume-K" (PromptBuilder.ResumeAfterQuestionInstruction)
                          then the ordinary timers: next part / next slide after the usual silence
```

Where it lives:

| Concern | File |
|---|---|
| Delegation payload, startup fallback, `response.event` handling | `src/PresenterAi.Infrastructure/Live/LiveSession.cs` |
| Hold, follow-up window, resume | `src/PresenterAi.Application/Presenting/Presenter.cs` |
| Prompt rules and instruction texts | `src/PresenterAi.Application/Presenting/PromptBuilder.cs` |
| Settings | `UpstreamOptions.cs`, `PresenterOptions.cs` (`src/PresenterAi.Infrastructure/Live/`), `PresenterSettings.cs` |

All presenter decisions run on a single event loop (`Presenter.RunLoopAsync`), so timers and upstream events never
race each other. Every timer is generation-counted, so a stale timer that fires after a pause or a slide change does
nothing.

### Deck-only mode

With an empty delegation model, or when Azure rejects the delegation model at session start, the session runs in
*client* delegation mode. `LiveSession.ConnectAsync` retries the start once in that mode, and the log says why. When
the AI then delegates, the presenter immediately appends `PromptBuilder.ClientDelegationAnswerNowInstruction`:
answer from the material now, or say plainly that it does not cover the question.

## Timers at a glance

| Timer | Default | Starts | Effect |
|---|---|---|---|
| Question hold | 15 s | a question is heard, or the backend is called or answers | releases the hold and resumes if no answer came |
| Follow-up wait | `Presenter:FollowUpWaitMs` (5 s) | each piece of answer audio | resumes the slide |
| Advance silence | `Presenter:AdvanceSilenceMs` (3 s) | each piece of narration audio | next slide (or wrap-up close) |
| Part gap | min(2.5 s, 80 % of advance silence) | each piece of narration audio while parts remain | sends the next narration part |

Nudges (the AI says nothing after a slide starts) keep running during a hold.

## Log lines

These appear in the Log panel of the Present page.

| Line | Meaning |
|---|---|
| `question: hold opened` | Audience speech heard; the slide is held. |
| `question: delegated (backend)` | GPT-Live sent the question to the delegation model. |
| `question: backend answer ready` | The delegation model finished; its answer is being spoken. |
| `question: backend answer failed (<type>)` (warn) | The backend call failed; the AI answers without it. |
| `question: delegated (client)` | Deck-only mode; the AI was told to answer from the material. |
| `question: answered after N ms` | First answer audio, N ms after the question. |
| `question: no follow-up after N ms; resuming` | The follow-up window passed; the AI was told to resume. |
| `question: released after 15 s without an answer` | Nothing answered; the talk carries on. |
| `delegation: backend unavailable (<code>); answering from the deck only` (warn) | Session started in deck-only mode because the delegation model was rejected. |

The API log (`dotnet run` output) also shows `<< session.delegation.created`, `<< response.event` and
`Delegated response completed: id=…` for backend questions.

## Troubleshooting

| Symptom | Likely cause | What to do |
|---|---|---|
| `delegation: backend unavailable` at start | No deployment with that name in the Azure resource | Create the deployment, or set `Upstream:DelegationModel` to an existing one |
| `backend answer failed` on questions | Backend quota, content filter or deployment problem | Check the Azure resource; the talk continues deck-only for that question |
| The AI resumes too soon after answering | Follow-up wait too short for your audience | Raise `Presenter:FollowUpWaitMs` |
| Long pauses after every answer | Follow-up wait too long | Lower it (not below 2500) |
| `hold opened` with nobody speaking | Room noise or speaker echo transcribed as speech | Use headphones or a headset; the echo gate (plan 005 §4.2) reduces this. A false hold costs at most one follow-up wait |
| The AI says "I'll check on that" and moves on | Older build (before plan 005 A1) | Run the current branch |

## Limitations

- The live model decides whether the deck covers a question; presenter-ai does not classify questions itself.
- Backend answers are billed separately (Responses rates, priority tier), only for delegated questions.
- The backend gets the conversation context that GPT-Live passes along, plus instructions that name the talk title.
  The background context file and the speaker notes are not in its instructions, so for deck questions the live
  model's own answer is the reliable path.
