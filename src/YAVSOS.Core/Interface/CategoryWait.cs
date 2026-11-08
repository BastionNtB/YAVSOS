using YAVSOS.Core.Classification;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>
/// X62 (D145), X71 (D159): what a player reads while categories aren't ready. Every place tells the same story as the
/// Categories mark: still loading, the server couldn't build them (a client of a dedicated server whose stage failed and
/// waits for a slow retry, D144), or YAVSOS couldn't build them (a stage of this game's own gave up, D087).
/// </summary>
public static class CategoryWait
{
    /// <summary>
    /// The server's categories failed, as far as this client can tell: it is a managed client, its categories aren't ready
    /// and the current session's state from the server says failed. A missing or unreadable state is never a failure.
    /// </summary>
    public static bool ServerFailed(bool managedClient, bool categoriesReady, ServerStageState? current) =>
        managedClient && !categoriesReady && current?.Condition == ServerStageCondition.Failed;

    /// <summary>
    /// X71 (D159): a client whose last good categories still work while the server says its latest rebuild failed. The
    /// Categories mark adds an amber line, as a host's does for its own failed rebuild.
    /// </summary>
    public static bool ServerRebuildFailed(bool managedClient, bool categoriesReady, ServerStageState? current) =>
        managedClient && categoriesReady && current?.Condition == ServerStageCondition.Failed;

    /// <summary>
    /// X71 (D159): why categories aren't ready, in the order the Categories mark uses: this game's own failure
    /// (<paramref name="gaveUp"/>, D087) first, then the server's (<see cref="ServerFailed"/>), else still loading. With
    /// categories ready it is <see cref="CategoryWaitState.Loading"/>, which nothing then shows.
    /// </summary>
    public static CategoryWaitState State(bool managedClient, bool categoriesReady, bool gaveUp, ServerStageState? current) =>
        categoriesReady ? CategoryWaitState.Loading
        : gaveUp ? CategoryWaitState.Failed
        : ServerFailed(managedClient, false, current) ? CategoryWaitState.ServerFailed
        : CategoryWaitState.Loading;

    /// <summary>Stash and cleanup refused because categories aren't ready.</summary>
    public static Message Run(CategoryWaitState state) => Message.Of(state switch
    {
        CategoryWaitState.ServerFailed => "yavsos_run_categories_server_failed",
        CategoryWaitState.Failed => "yavsos_run_categories_failed",
        _ => "yavsos_run_categories_loading"
    });

    /// <summary>A stash that moved nothing because categories aren't ready (D086 D4: Always items and held items still go).</summary>
    public static Message StashNothing(CategoryWaitState state) => Message.Of(state switch
    {
        CategoryWaitState.ServerFailed => "yavsos_stash_categories_server_failed",
        CategoryWaitState.Failed => "yavsos_stash_categories_failed",
        _ => "yavsos_stash_categories_partial"
    });

    /// <summary>AzuAutoStore stashed while categories aren't ready, so categories in rules accepted nothing.</summary>
    public static Message AzuPending(CategoryWaitState state) => Message.Of(state switch
    {
        CategoryWaitState.ServerFailed => "yavsos_azu_categories_server_failed",
        CategoryWaitState.Failed => "yavsos_azu_categories_failed",
        _ => "yavsos_azu_categories_pending"
    });
}
