namespace YAVSOS.Vendor.MultiUserChest {
    /// <summary>
    /// YAVSOS addition (YAVSOS-LOCAL change 4, D116, X19 research §5.1 option B). The owner-side guard for answers to a
    /// sender whose item format is unknown (<see cref="PeerFormat"/>): this copy answers in format 2, which a format-1
    /// reader takes correctly only for a plain item. Anything else is refused before it leaves the chest. Plain values
    /// only, so it is tested without the game.
    /// </summary>
    public static class UnknownFormatGuard {
        /// <summary>Both formats read an item alike when it carries no cheated flag and no custom data (research §2.1).</summary>
        public static bool ReadsAlike(bool cheated, int customDataCount) => !cheated && customDataCount == 0;

        /// <summary>Whether a remove, consume or drop is refused, before anything is removed, instead of answered with this item.</summary>
        public static bool Refuses(bool peerUnknown, bool cheated, int customDataCount) {
            return peerUnknown && !ReadsAlike(cheated, customDataCount);
        }

        /// <summary>
        /// Whether a refused remove gives the request's switch item back. The sender gives that item up only for a swap
        /// (it doesn't stack with the chest item), and upstream's own refusal of a swap returns it too. It was written by
        /// this sender and read here without error, so the sender reads it back alike.
        /// </summary>
        public static bool ReturnsSwitchItem(bool hasSwitchItem, bool switchItemStacks) => hasSwitchItem && !switchItemStacks;
    }
}
