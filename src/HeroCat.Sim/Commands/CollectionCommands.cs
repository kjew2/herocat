namespace HeroCat.Sim.Commands
{
    // Collection commands (04 sections 6.3 and 7.2; 00 section 5.1 vocabulary). Handlers in Simulation.Summons.cs.

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

    /// <summary>Reject reasons of the summon and collection commands (04; 00 section 5.1 conventions).</summary>
    public static class CollectionRejectReasons
    {
        public const string ITEM_NOT_OWNED = "ITEM_NOT_OWNED";
        public const string UNKNOWN_ITEM = "UNKNOWN_ITEM";
        public const string ALREADY_EQUIPPED = "ALREADY_EQUIPPED";
        public const string SKILL_IN_OTHER_SLOT = "SKILL_IN_OTHER_SLOT";
        public const string BAD_SLOT = "BAD_SLOT";
        public const string SLOT_EMPTY = "SLOT_EMPTY";

        public const string BANNER_CLOSED = "BANNER_CLOSED";
        public const string SUMMON_BAD_COUNT = "SUMMON_BAD_COUNT";
        public const string SUMMON_BAD_PAYMENT = "SUMMON_BAD_PAYMENT";
        public const string SUMMON_BAD_PAYLOAD = "SUMMON_BAD_PAYLOAD";
        public const string SUMMON_ALREADY_APPLIED = "SUMMON_ALREADY_APPLIED";
        public const string SUMMON_STREAM_MISMATCH = "SUMMON_STREAM_MISMATCH";

        /// <summary>NOT_ENOUGH_&lt;CURRENCY&gt; (00 decision 95), e.g. NOT_ENOUGH_DIAMOND, NOT_ENOUGH_TICKET_SUMMON_WEAPON.</summary>
        public static string NotEnough(Content.Currency c) => "NOT_ENOUGH_" + c;
    }
}
