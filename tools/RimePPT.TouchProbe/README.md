# Desktop touch probe

Run on an unlocked interactive Windows desktop. Close an existing RimePPT instance first. The probe launches its own test process, opens the existing debug presentation through UI Automation, and uses native touch injection for annotation and toolbar actions. It terminates only the process it launched.

```powershell
dotnet build RimePPT/RimePPT.csproj -p:Platform=x64
dotnet run --project tools/RimePPT.TouchProbe -c Release -- RimePPT/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/RimePPT.exe .tasks/touch-probe-results.json --stress
```

Keep the desktop available during the run: injecting input affects real screen coordinates. Setup shows only the owned settings and toolbar windows; all assertions for writing, erasing and toolbar navigation use touch input, never a mouse fallback. Windows may assign different pointer IDs from the injection IDs. Injection API failures report the Win32 error code.

The normal run checks a circle, a second contact, cancellation and recovery, actual erasure changes, next/previous page transitions, and the current page texture budget. `--stress` adds a 1200-update stroke, 500 additional three-point strokes, and continuous erasure of the dense page. Logs confirm `Touch`, successful capture, actual committed dots, erasure changes and page numbers.

The process environment enables `RIMEPPT_INK_DIAGNOSTICS=1`; normal app use does not run detailed diagnostics. Summaries are written asynchronously to `%TEMP%/rimeppt_ink.jsonl`, with no coordinates or presentation filenames. The probe copies its own run's records into the results JSON. Timings cover CPU input callbacks and drawing-command submission, not GPU completion, screen presentation, or physical touch-to-photon latency. Per-contact allocation counts also include other work on the UI thread and are diagnostic estimates.

Teaching-machine acceptance remains manual: Seewo/HiteVision HID drivers, real touch/pen contacts, palm interaction, Windows 10/11, 1080p/4K, 100/125/150/200 percent scaling, fast handwriting/circles, repeated tool switching, slide changes during contact, PowerPoint retaining focus, transparent pass-through, and repeated presentation start/end. Device-loss recovery is implemented but requires a dedicated hardware/runtime test. The 64 MiB budget applies to the historical texture, not all app/GPU memory.
