using System;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Bots.Measure
{
    /// <summary>
    /// 03 criterion 19's perf fixture (issue #160): a stage-300 profile with a full party (00 decision 109): five companions,
    /// five weapons and five skills equipped, owned and off cooldown, every slot at Awakening rank 3, and Training sized so
    /// enemies die. Needs an MVP content build (Awakening is MVP). Shared by the `bench` verb and HeroCat.Sim.Tests' Perf group, which links this
    /// file, so both measure the same loadout.
    /// </summary>
    internal static class PerfFixture
    {
        public const int Stage = 300;

        /// <summary>Every cast type, one skill per slot: INSTANT, DOT and BUFF (03 section 2.3).</summary>
        public static readonly string[] Skills = { "skill_l_01", "skill_l_02", "skill_l_03", "skill_e_07", "skill_l_05" };

        /// <summary>Every ability type: MP_REGEN restores all five pools (03 section 2.4), plus two DAMAGE, a BUFF_ATK and a BUFF_GOLD.</summary>
        public static readonly string[] Companions = { "comp_l_06", "comp_l_01", "comp_l_03", "comp_l_04", "comp_l_05" };

        /// <summary>Five weapons, one per slot (04 section 6.3).</summary>
        public static readonly string[] Weapons = { "weapon_l_01", "weapon_l_02", "weapon_l_03", "weapon_l_04", "weapon_e_01" };

        const int AwakenRank = 3;
        const int AtkLevel = 30_000;
        const int AtkSpdLevel = 100;

        /// <summary>
        /// Equips the loadout on <paramref name="genesis"/> at <paramref name="stage"/> (frontier, everything below cleared) and
        /// returns its Simulation (not anchored to a clock; the caller sets the event mask and coalescing).
        /// </summary>
        public static Simulation Create(GameState genesis, ContentDb mvp, int stage = Stage)
        {
            if (mvp.Companions.Count == 0) throw new ArgumentException("the perf fixture needs an MVP content build (companions)", nameof(mvp));
            if (stage < 1) throw new ArgumentOutOfRangeException(nameof(stage));
            var s = genesis;
            s.Combat.Stage = stage;
            s.Combat.Fight = null;
            s.Progress.HighestCleared = stage - 1;
            foreach (var x in s.Party.Slots) x.AwakenRank = AwakenRank;
            s.Training.Level[(int)Stat.ATK] = AtkLevel;
            s.Training.Level[(int)Stat.ATK_SPD] = AtkSpdLevel;
            var slots = s.Party.Slots;
            Equip(mvp, s.Skills, slots, Skills, id => mvp.Skills.ContainsKey(id), (x, id) => x.Skill = id);
            Equip(mvp, s.Companions, slots, Companions, id => mvp.Companions.ContainsKey(id), (x, id) => x.Companion = id);
            Equip(mvp, s.Weapons, slots, Weapons, id => mvp.Weapons.ContainsKey(id), (x, id) => x.Weapon = id);
            Array.Clear(s.Combat.SkillCooldowns, 0, s.Combat.SkillCooldowns.Length);
            Array.Clear(s.Combat.CompanionCooldowns, 0, s.Combat.CompanionCooldowns.Length);
            return new Simulation(s, mvp);
        }

        static void Equip(ContentDb db, CollectionState owned, PartySlot[] slots, string[] ids, Func<string, bool> known, Action<PartySlot, string> set)
        {
            if (slots.Length < ids.Length) throw new InvalidOperationException($"{ids.Length} ids for {slots.Length} slots");
            for (int i = 0; i < ids.Length; i++)
            {
                if (!known(ids[i])) throw new InvalidOperationException($"perf fixture: {ids[i]} is not in content {db.Version}");
                owned.Items[ids[i]] = new OwnedItem { Copies = 1 };
                set(slots[i], ids[i]);
            }
        }
    }
}
