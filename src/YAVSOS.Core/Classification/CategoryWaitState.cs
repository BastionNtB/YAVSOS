namespace YAVSOS.Core.Classification;

/// <summary>
/// X71 (D159): why categories aren't ready yet, so every place that says so tells the same story as the Categories mark.
/// </summary>
public enum CategoryWaitState
{
    /// <summary>Still loading, or nothing better is known (no state from the server, or one this version can't read).</summary>
    Loading,
    /// <summary>On a client: the server says its categories failed and waits for a slow retry (X62, D144, D145).</summary>
    ServerFailed,
    /// <summary>A stage of this game's own gave up and waits for a slow retry (D087, D144): a hosted world or single player.</summary>
    Failed
}
