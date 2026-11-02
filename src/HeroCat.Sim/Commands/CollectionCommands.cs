namespace HeroCat.Sim.Commands
{
    // Collection commands (04 sections 6.3, 7.2 and 8.2; 00 section 5.1 vocabulary). Every equip names a party slot
    // 0..PARTY_SLOTS-1 (00 decision 109). Rejects, in this order: BadArgument/BAD_SLOT, InvalidTarget/UNKNOWN_ITEM,
    // NotOwned/ITEM_NOT_OWNED, InvalidTarget/ALREADY_EQUIPPED (already in this slot), InvalidTarget/<KIND>_IN_OTHER_SLOT
    // (an item is in at most one slot; a screen sends Unequip then Equip to move it). Handlers in Simulation.Summons.cs.

    /// <summary>Puts an owned weapon in a party slot, replacing the occupant (04 section 6.3).</summary>
    public sealed record EquipWeapon(int Slot, string WeaponId) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleEquipWeapon(this);
    }

    /// <summary>Empties a slot's weapon; its companion attacks unarmed (04 section 6.3). An already empty slot is accepted, no event.</summary>
    public sealed record UnequipWeapon(int Slot) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleUnequipWeapon(this);
    }

    /// <summary>Gives the occupied slots the best owned weapons, each where it adds the most rated DPS (04 section 6.3).</summary>
    public sealed record AutoEquipBestWeapons : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleAutoEquipBestWeapons(this);
    }

    /// <summary>Puts an owned skill in a party slot, replacing the occupant (04 section 7.2).</summary>
    public sealed record EquipSkill(int Slot, string SkillId) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleEquipSkill(this);
    }

    /// <summary>Empties a skill slot (04 section 7.2).</summary>
    public sealed record UnequipSkill(int Slot) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleUnequipSkill(this);
    }

    /// <summary>Gives the occupied slots the best owned skills, each where it adds the most rated DPS (04 section 7.2).</summary>
    public sealed record AutoEquipBestSkills : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleAutoEquipBestSkills(this);
    }

    /// <summary>
    /// Puts an owned companion in a party slot, replacing the occupant (04 section 8.2). Every slot is open; the slot keeps
    /// its weapon, skill, Awakening rank and MP pool (00 decision 109).
    /// </summary>
    public sealed record EquipCompanion(int Slot, string CompanionId) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleEquipCompanion(this);
    }

    /// <summary>Empties a companion slot (04 section 8.2).</summary>
    public sealed record UnequipCompanion(int Slot) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleUnequipCompanion(this);
    }

    /// <summary>
    /// Fills every party slot with the best owned companions: damage and BUFF_ATK first, MP_REGEN swapped in while it
    /// raises the rated DPS (04 section 8.2; CollectionRules.BestCompanions).
    /// </summary>
    public sealed record AutoEquipBestCompanions : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleAutoEquipBestCompanions(this);
    }

    /// <summary>
    /// Spends Rock Candy for one limit-break rank on an owned weapon, skill or companion (04 section 5.2): +1 effective
    /// level, up to LIMIT_BREAK_RANKS ranks, at LIMIT_BREAK_COST[rarity] * (rank + 1).
    /// </summary>
    public sealed record LimitBreak(string ItemId) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleLimitBreak(this);
    }

    /// <summary>Reject reasons of the summon and collection commands (04; 00 section 5.1 conventions).</summary>
    public static class CollectionRejectReasons
    {
        public const string ITEM_NOT_OWNED = "ITEM_NOT_OWNED";
        public const string UNKNOWN_ITEM = "UNKNOWN_ITEM";
        public const string ALREADY_EQUIPPED = "ALREADY_EQUIPPED";
        public const string WEAPON_IN_OTHER_SLOT = "WEAPON_IN_OTHER_SLOT";
        public const string SKILL_IN_OTHER_SLOT = "SKILL_IN_OTHER_SLOT";
        public const string COMPANION_IN_OTHER_SLOT = "COMPANION_IN_OTHER_SLOT";
        public const string BAD_SLOT = "BAD_SLOT";

        public const string BANNER_CLOSED = "BANNER_CLOSED";
        public const string SUMMON_BAD_COUNT = "SUMMON_BAD_COUNT";
        public const string SUMMON_BAD_PAYMENT = "SUMMON_BAD_PAYMENT";
        public const string SUMMON_BAD_PAYLOAD = "SUMMON_BAD_PAYLOAD";
        public const string SUMMON_ALREADY_APPLIED = "SUMMON_ALREADY_APPLIED";
        public const string SUMMON_STREAM_MISMATCH = "SUMMON_STREAM_MISMATCH";

        /// <summary>LimitBreak on an item already at LIMIT_BREAK_RANKS (04 section 5.2).</summary>
        public const string LIMIT_BREAK_CAPPED = "LIMIT_BREAK_CAPPED";

        /// <summary>NOT_ENOUGH_&lt;CURRENCY&gt; (00 decision 95), e.g. NOT_ENOUGH_DIAMOND, NOT_ENOUGH_TICKET_SUMMON_WEAPON.</summary>
        public static string NotEnough(Content.Currency c) => "NOT_ENOUGH_" + c;
    }
}
