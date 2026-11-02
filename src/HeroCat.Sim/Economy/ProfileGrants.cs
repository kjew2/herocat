using HeroCat.Sim.Content;
using HeroCat.Sim.Events;
using HeroCat.Sim.State;

namespace HeroCat.Sim.Economy
{
    /// <summary>
    /// The profile-created grant (09 tutorial_steps <c>tut_welcome</c>, issue #46): TUTORIAL_DIAMONDS with reason TUTORIAL
    /// plus the starter party (TUTORIAL_COMPANION_IDS in slots 0-4) and the starter weapon TUTORIAL_WEAPON_ID in slot 0
    /// (00 decision 109). Runtime's ProfileFactory applies it to the fresh state
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
                // TODO(#91): item grants go through the collection path once it exists; until then weapons and companions.
                var col = g.ItemKind == "WEAPON" ? s.Weapons : g.ItemKind == "COMPANION" ? s.Companions : null;
                if (col == null) continue;
                if (!col.Items.TryGetValue(g.ItemId, out var item))
                    col.Items[g.ItemId] = item = new OwnedItem { FirstAcquiredServerTime = nowServerSeconds };
                item.Copies += (int)g.Amount;
            }
            // The starter party (00 decision 109): TUTORIAL_COMPANION_IDS into slots 0.. in grant order, the weapon into slot 0.
            var slots = s.Party.Slots;
            var starters = db.TutorialCompanionIds;
            for (int i = 0; i < starters.Count && i < slots.Length; i++)
                if (slots[i].Companion == null) slots[i].Companion = starters[i];
            if (db.TutorialWeaponId != null && slots.Length > 0 && slots[0].Weapon == null) slots[0].Weapon = db.TutorialWeaponId;
            s.Tutorial.TutorialDiamondsGranted = true;
            return true;
        }
    }
}
