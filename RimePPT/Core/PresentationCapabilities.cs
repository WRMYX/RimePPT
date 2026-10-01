namespace RimePPT.Core;

public enum InkBackend { Native, Rime }
public enum NativePointerTool { Arrow = 1, Pen = 2, Eraser = 5 }
public readonly record struct PresentationCapabilities(bool NativePointer, bool Navigation, bool NativeClear);
