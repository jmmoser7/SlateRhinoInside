namespace SlateRhinoInside;

// Native command admission stays separate from document/agent authority. A
// refused edit has not started an undo record or written a draft file.
internal static class NativeCommandPolicy
{
    internal static void RequireIdleDraft(int commandDepth)
    {
        if (commandDepth > 0)
            throw new ProtocolFailure("invalid_request",
                "Finish or cancel the active Rhino command before editing or saving this isolated draft.");
    }
}
