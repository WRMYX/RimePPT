using System;
using System.Runtime.InteropServices;

namespace RimePPT.Services
{
    /// <summary>
    /// Controls a running PowerPoint slideshow via COM late-binding (no Interop assembly needed).
    /// Requires PowerPoint to already be running and presenting.
    /// </summary>
    public sealed class PptControlService
    {
        private dynamic? _pptApp;

        // ── Win32 COM helpers (Marshal.GetActiveObject removed in .NET 5+) ────
        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

        [DllImport("oleaut32.dll")]
        private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        // ── Connection ────────────────────────────────────────────────

        public bool TryConnect()
        {
            try
            {
                int hr = CLSIDFromProgID("PowerPoint.Application", out Guid clsid);
                if (hr != 0) { _pptApp = null; return false; }

                hr = GetActiveObject(ref clsid, IntPtr.Zero, out object obj);
                if (hr != 0) { _pptApp = null; return false; }

                _pptApp = obj;
                return true;
            }
            catch
            {
                _pptApp = null;
                return false;
            }
        }

        private dynamic? SlideShowView
        {
            get
            {
                try
                {
                    if (_pptApp == null) TryConnect();
                    return _pptApp?.SlideShowWindows[1]?.View;
                }
                catch { return null; }
            }
        }

        // ── Slide Navigation ──────────────────────────────────────────

        public void NextSlide()     => TryRun(() => SlideShowView?.Next());
        public void PreviousSlide() => TryRun(() => SlideShowView?.Previous());
        public void EndShow()       => TryRun(() => SlideShowView?.Exit());

        /// <summary>Returns 1-based current slide index, or 0 on failure.</summary>
        public int CurrentSlide()
        {
            try { return (int)(SlideShowView?.CurrentShowPosition ?? 0); }
            catch { return 0; }
        }

        /// <summary>Total slides in the presentation, or 0 on failure.</summary>
        public int TotalSlides()
        {
            try
            {
                if (_pptApp == null) TryConnect();
                return (int)(_pptApp?.SlideShowWindows[1]?.Presentation?.Slides?.Count ?? 0);
            }
            catch { return 0; }
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static void TryRun(Action action)
        {
            try { action(); }
            catch { /* silently ignore COM errors */ }
        }

        public void Disconnect()
        {
            if (_pptApp != null)
            {
                try { Marshal.ReleaseComObject(_pptApp); } catch { }
                _pptApp = null;
            }
        }
    }
}
