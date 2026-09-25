# 003 — Backlog: transcribed Ask with `gpt-transcribe` (plan 012)

## Goal

Let a listener ask a question longer than 25 s, and show what they said accurately in the chat panel, by transcribing
the recorded Ask with `gpt-transcribe`. Let them clear the recording and ask again from the start.

## Deferred because

The owner chose to finish plan 011 first (fixes, the full live regression, then the PRs for 010 and 011) and plan this
separately, on its own branch stacked on 011 (2026-09-24).

## Owner decisions (planning interview, 2026-09-24)

- **Model:** `gpt-transcribe`, deployed on the same Azure resource and key as the realtime upstream.
- **Batch, not real time.** The model is not real time, so it cannot show live text or detect a spoken "ask done". It
  runs once per Ask, on the recording made between **Ask** and **Ask done**.
- **Questions over 25 s.** Today's cap exists because the upstream ingests only about 30 s of an unpaced burst (P-1).
  Up to 25 s of kept speech is still sent as audio. A longer question is sent as the transcript text, with an
  instruction to answer it. The new cap is chosen in the plan (for example 90 s).
- **Chat transcript.** The "You:" line for an Ask comes from the `gpt-transcribe` text, not from the upstream's
  transcription.
- **Clear.** A **Clear** (or similar) button during listening discards the recording so far, so the listener can
  rephrase and ask again from the beginning.

## Sketch

Implement the existing `IAskTranscriber` port (plan 011 §4.1; the only registration today is `DisabledAskTranscriber`)
as a batch transcriber: on Ask done, send the kept recording to `gpt-transcribe`, then either send the burst (short) or
append the transcript as the question (long). Measure the transcription latency live and in a probe before fixing the
cap and the timeouts.

## Related, same follow-up

- The chat panel starts a new "Presenter:" line at every 1 s pause (`web/app/src/store/presenterStore.ts:138-143`,
  since `d5bbc56`). One narration should stay one line until the other side speaks or the slide changes.
- Narration that was flushed at Ask and never heard still appears in the chat panel.
