namespace SlateRhinoInside;

// Graphics initialization may block when Rhino has never been presented.
// An ordinary refusal preserves the authenticated pipe and source binding.
internal sealed class NativeCaptureReadiness
{
    private bool presented;
    internal bool Presented => presented;
    internal void ObserveHostedPresentation(bool authenticatedVisible) => presented |= authenticatedVisible;
    internal void Require(bool visible)
    {
        if (!presented && !visible)
            throw new ProtocolFailure("invalid_request", "Display the hosted Rhino view once before capturing its hidden viewport");
    }
}
