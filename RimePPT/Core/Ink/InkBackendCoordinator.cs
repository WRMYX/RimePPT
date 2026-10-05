using System;
using System.Threading;
using System.Threading.Tasks;
using RimePPT.Windows;

namespace RimePPT.Core.Ink;

public sealed class InkBackendCoordinator
{
    private readonly IPresentationController _controller;
    private readonly AnnotationWindow _annotation;
    private readonly SemaphoreSlim _gate = new(1);
    public InkBackend EffectiveBackend { get; private set; } = InkBackend.Rime;
    public string Status => _controller is WpsPresentationController ? "WPS 自研批注（原生笔未验证）" : _controller is DebugPresentationController ? "自研（模拟放映；原生笔需 PowerPoint）" : EffectiveBackend == InkBackend.Native ? "PowerPoint 原生" : "RimePPT 自研";
    public InkBackendCoordinator(IPresentationController controller, AnnotationWindow annotation)
    { _controller = controller; _annotation = annotation; }
    public async Task SwitchBackendAsync(InkBackend backend)
    {
        await _gate.WaitAsync();
        try
        {
            _annotation.FinishInput(); _annotation.AnnotationEnabled = false;
            if (_controller is DebugPresentationController or WpsPresentationController) { EffectiveBackend = InkBackend.Rime; return; }
            await _controller.SetNativePointerAsync(NativePointerTool.Arrow, null);
            EffectiveBackend = backend;
        }
        finally { _gate.Release(); }
    }
    public async Task SetToolAsync(AnnotationTool? tool)
    {
        await _gate.WaitAsync();
        try
        {
            _annotation.FinishInput(); _annotation.AnnotationEnabled = false;
            if (EffectiveBackend == InkBackend.Native)
            {
                await _controller.SetNativePointerAsync(tool switch { AnnotationTool.Pen => NativePointerTool.Pen, AnnotationTool.Eraser => NativePointerTool.Eraser, _ => NativePointerTool.Arrow }, AppSettings.Instance.GetPenArgb());
            }
            else if (tool.HasValue) { _annotation.Tool = tool.Value; _annotation.AnnotationEnabled = true; }
        }
        catch
        {
            if (EffectiveBackend == InkBackend.Native)
                try { await _controller.SetNativePointerAsync(NativePointerTool.Arrow, null); } catch { }
            throw;
        }
        finally { _gate.Release(); }
    }
    public async Task ClearCurrentSlideAsync()
    {
        await _gate.WaitAsync();
        try { _annotation.FinishInput(); if (EffectiveBackend == InkBackend.Native) await _controller.ClearNativeInkAsync(); else _annotation.ClearCurrentSlide(); }
        finally { _gate.Release(); }
    }
}
