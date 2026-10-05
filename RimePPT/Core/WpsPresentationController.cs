namespace RimePPT.Core;

/// <summary>WPS Windows presentation COM adapter. Never starts an absent WPS installation.</summary>
public sealed class WpsPresentationController : PowerPointController
{
    public WpsPresentationController() : base(new[] { "KWPP.Application", "WPP.Application" }) { }
    public override PresentationCapabilities Capabilities => new(false, IsPresenting, false);
}
