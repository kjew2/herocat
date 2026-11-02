using HeroCat.Sim.Content;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// Where an art address lives until the Addressables catalog exists (07 sections 15.2-15.3, issue #291): a file named by
    /// its address under <c>Assets/Resources/Art/&lt;Kind&gt;/</c>, the kind chosen by the address prefix. Pixel sprites are the
    /// <c>spr_</c> battle sprites; everything else is illustrated. Engine-free so the rules are unit-tested.
    /// </summary>
    public static class ArtAddresses
    {
        public const string Root = "Art/";

        /// <summary>The <c>Resources.Load</c> path for <paramref name="address"/>, or null when no folder holds that kind.</summary>
        public static string ResourcePath(string address)
        {
            if (string.IsNullOrEmpty(address)) return null;
            string kind = KindOf(address);
            return kind == null ? null : Root + kind + "/" + address;
        }

        /// <summary>The Resources sub-folder for an address prefix, or null.</summary>
        public static string KindOf(string address)
        {
            if (address == null) return null;
            if (address.StartsWith("icon_", System.StringComparison.Ordinal)) return "Icons";
            if (address.StartsWith("portrait_", System.StringComparison.Ordinal)) return "Portraits";
            if (address.StartsWith("art_splash_", System.StringComparison.Ordinal)) return "Splash";
            if (address.StartsWith("spr_", System.StringComparison.Ordinal)) return "Pixel";
            return null;
        }

        /// <summary>Pixel art (07 section 15.3): point filtering, no compression, integer scaling.</summary>
        public static bool IsPixel(string address) => KindOf(address) == "Pixel";

        /// <summary>A companion's portrait, derived from its id (07 section 15.2).</summary>
        public static string PortraitFor(string companionId) => companionId == null ? null : "portrait_" + companionId;

        /// <summary>
        /// The splash for a Legendary or Mythic companion (00 decision 108), null for any other rarity or kind. The caller
        /// falls back to the portrait or the card when the file is missing.
        /// </summary>
        public static string SplashFor(Banner banner, string id, Rarity rarity) =>
            banner == Banner.COMPANION && id != null && rarity >= Rarity.LEGENDARY ? "art_splash_" + id : null;

        /// <summary>
        /// Every companion that has a splash (Legendary and Mythic, 00 decision 108), Mythic first, then by id: the order the
        /// debug splash viewer steps through.
        /// </summary>
        public static System.Collections.Generic.List<CompanionDef> SplashCompanions(ContentDb db)
        {
            var list = new System.Collections.Generic.List<CompanionDef>();
            foreach (var c in db.Companions.Values)
                if (SplashFor(Banner.COMPANION, c.Id, c.Rarity) != null) list.Add(c);
            list.Sort((a, b) => a.Rarity != b.Rarity ? b.Rarity.CompareTo(a.Rarity) : string.CompareOrdinal(a.Id, b.Id));
            return list;
        }
    }
}
