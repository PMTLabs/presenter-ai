# Node MVP retired

Plan 003 removed the Node MVP: its server, static page, tests, and live/headless/browser scripts. The .NET API and
React app now provide the supported stack; parity was proven in plan 002.

The implementation remains available in history, including the presenter at:

```text
git show 2a0b6a1:src/server/presenter.js
```

Replacements:

- `presenter-cli smoke` and `presenter-cli run` replace the live-smoke and headless-run workflows.
- The built React app, served by the .NET API or Vite during development, replaces the old static page.
- Chrome runs against the React app replace the browser-e2e workflow.
