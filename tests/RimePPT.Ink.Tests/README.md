# Ink regression tests

Run from the repository folder with the .NET 8 runtime and SDK:

```powershell
dotnet run --project tests/RimePPT.Ink.Tests -c Release -- .tasks/ink-test-results.json
```

No third-party test package is used. The program links the production geometry, input-session and eraser sources directly. A nonzero exit code indicates a failure.

Coverage includes single dots, a second contact, input cancellation/restart, sample ordering/filtering, final samples, fixed page/color snapshots, sparse circles, ellipses, sharp reversals, long strokes, live/final agreement, swept erasure, line intersections, repeated erasure, DPI coordinates, and old/empty JSON documents.

The baseline algorithm reproduces the interpolation, cubic subdivision and no-hit eraser list-building loops from `.tasks/backups/2026-09-30-ink/AnnotationWindow.cs`. It is a CPU-only reproduction, not a measurement of the original application's GPU rendering or touch latency. The benchmark uses 500 strokes with 400 points each, a warmed bounding-box cache, 100 eraser moves per trial, five trials, and the median. Its numbers describe a no-hit eraser scenario, not all writing or erasing.

`Argb` is a byte array and the existing System.Text.Json format stores it as Base64, not a JSON number array. The compatibility fixture preserves that format.
