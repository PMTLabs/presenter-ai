# 002 — Backlog: move a live talk between tabs

## Goal

Let a presenter move a running talk to another tab without ending the live session, so narration continues at the
current slide.

## Deferred because

The current take-over ends the run before releasing the presenter slot. Moving it safely needs explicit ownership
handover for the session recorder, output audio and microphone capture. Browser audio may only start after a user
gesture, so the receiving tab cannot reliably resume audible playback on its own.

## Sketch

Keep the live session and recorder with a transferable presenter owner, pause output while the new tab authenticates,
and require a user gesture in that tab to claim audio and microphone capture. Only after the new tab confirms those
resources should the old tab detach and the bridge switch the owner.
