namespace OpenIPC.Viewer.App.Services;

/// <summary>
/// A page view model or overlay dialog with levels of its own (a settings
/// section over its list, a remote folder over its parent). System Back asks
/// it first and only leaves the page or closes the sheet once it says no.
/// </summary>
public interface IBackNavigable
{
    /// <summary>Steps one level up inside; false when already at the top.</summary>
    bool TryGoBack();
}
