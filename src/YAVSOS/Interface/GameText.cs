using YAVSOS.Core.Messages;

namespace YAVSOS.Interface;

/// <summary>
/// The game side's player text helpers that every surface shares (A2-16: they used to live in <c>CleanupRunner</c>):
/// the on-screen message, and game names already in the player's language. Unity game thread only.
/// </summary>
internal static class GameText
{
    /// <param name="overInventory">A notice shown while the inventory is open: the top-left HUD line sits under the player's
    /// inventory grid there, so it goes to the middle of the screen, which draws above it (P9-02 batch C).</param>
    /// <returns>The message's text, without the "YAVSOS: " the screen shows before it.</returns>
    internal static string Say(Message message, bool overInventory = false)
    {
        var text = Texts.Get(message);
        if (Player.m_localPlayer)
            Player.m_localPlayer.Message(overInventory ? MessageHud.MessageType.Center : MessageHud.MessageType.TopLeft,
                Texts.Get(Message.Of("yavsos_hud_message").With("text", text)));
        return text;
    }

    /// <summary>A game token (<c>$item_wood</c>, a chest's <c>m_name</c>) in the player's language; the token itself without a localization.</summary>
    internal static string Localize(string token) => Localization.instance != null && !string.IsNullOrEmpty(token) ? Localization.instance.Localize(token) : token;

    /// <summary>An item's name in the player's language, from its prefab; the prefab ID when the game has no name for it.</summary>
    internal static string ItemName(string prefab)
    {
        var drop = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>() : null;
        var token = drop ? drop!.m_itemData?.m_shared?.m_name : null;
        return string.IsNullOrEmpty(token) ? prefab : Localize(token!);
    }
}
