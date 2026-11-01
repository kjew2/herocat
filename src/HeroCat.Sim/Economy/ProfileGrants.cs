using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Economy
{
    /// <summary>
    /// The profile-created grant (09 tutorial_steps <c>tut_welcome</c>, issue #46): TUTORIAL_DIAMONDS with reason TUTORIAL
    /// plus the starter weapon TUTORIAL_WEAPON_ID, equipped. Runtime's ProfileFactory applies it to the fresh state
    /// before any Simulation exists; it goes through the same <see cref="Ledger.Apply"/> path.
    /// </summary>
    public static class ProfileGrants
    {
        /// <summary>Applies the grant once, guarded by Tutorial.TutorialDiamondsGranted. Returns false when already applied.</summary>
        public static bool ApplyWelcome(GameState s, ContentDb db, long nowServerSeconds, IEventEmitter events = null)
        {
            if (s.Tutorial.TutorialDiamondsGranted) return false;
            foreach (var g in db.ProfileCreatedGrants)
            {
                if (g.Currency.HasValue)
                {
                    Ledger.Apply(s, new CurrencyDelta(g.Currency.Value, g.Amount), CurrencyReason.TUTORIAL, events);
                    continue;
                }
                // TODO(#91): item grants go through the collection path once it exists; until then only weapons are granted.
                if (g.ItemKind == "WEAPON")
                {
                    if (!s.Weapons.Items.TryGetValue(g.ItemId, out var item))
                        s.Weapons.Items[g.ItemId] = item = new OwnedItem { FirstAcquiredServerTime = nowServerSeconds };
                    item.Copies += (int)g.Amount;
                    if (s.Loadout.Weapon == null && g.ItemId == db.TutorialWeaponId) s.Loadout.Weapon = g.ItemId;
                }
            }
            s.Tutorial.TutorialDiamondsGranted = true;
            return true;
        }
    }
}
