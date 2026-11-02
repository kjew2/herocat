namespace HeroCat.Sim.Commands
{
    // Collection commands (04 sections 6.3, 7.2 and 8.2; 00 section 5.1 vocabulary). Handlers in Simulation.Summons.cs.

    /// <summary>Equips an owned weapon (04 section 6.3). There is no unequip command for weapons.</summary>
    public sealed record EquipWeapon(string WeaponId) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleEquipWeapon(this);
    }

    /// <summary>Equips the best owned weapon if strictly better than the current one (ranking: CollectionRules.RankOwned).</summary>
    public sealed record AutoEquipBestWeapon : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleAutoEquipBestWeapon(this);
    }

    /// <summary>Puts an owned skill in slot 0..SKILL_SLOTS-1, replacing the occupant (04 section 7.2).</summary>
    public sealed record EquipSkill(int Slot, string SkillId) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleEquipSkill(this);
    }

    /// <summary>Empties a skill slot (04 section 7.2).</summary>
    public sealed record UnequipSkill(int Slot) : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleUnequipSkill(this);
    }

    /// <summary>Fills the skill slots with the SKILL_SLOTS best owned skills, best in slot 0 (04 section 7.2).</summary>
    public sealed record AutoEquipBestSkills : Command
    {
        internal override ApplyResult Execute(Simulation sim) => sim.HandleAutoEquipBestSkills(this);
    }

    /// <summary>
    /// Puts an owned companion in slot 0..COMPANION_SLOTS-1, replacing the occupant (04 section 8.2). Slot 0 is open from
    /// the start; slots 1-3 open at Awakening ranks 1-3 (03 section 5.3) and are rejected Locked until then.
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
    /// Fills the open companion slots with the best owned companions, reserving one for the best MP_REGEN companion when
    /// the pure-damage loadout is MP-starved (04 section 8.2; CollectionRules.BestCompanions).
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
        public const string SKILL_IN_OTHER_SLOT = "SKILL_IN_OTHER_SLOT";
        public const string COMPANION_IN_OTHER_SLOT = "COMPANION_IN_OTHER_SLOT";

        /// <summary>EquipCompanion into a slot its Awakening rank has not opened yet (03 section 5.3), under Locked.</summary>
        public const string COMPANION_SLOT_LOCKED = "COMPANION_SLOT_LOCKED";
        public const string BAD_SLOT = "BAD_SLOT";
        public const string SLOT_EMPTY = "SLOT_EMPTY";

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
