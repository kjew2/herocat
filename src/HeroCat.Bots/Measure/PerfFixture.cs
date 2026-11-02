using System;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Bots.Measure
{
    /// <summary>
    /// 03 criterion 19's perf fixture (issue #160): a stage-300 profile with six skills and four companions equipped, owned
    /// and off cooldown, Awakening rank 3 (all four companion slots open) and Training sized so enemies die. Needs an MVP
    /// content build (companions are MVP). Shared by the `bench` verb and HeroCat.Sim.Tests' Perf group, which links this
    /// file, so both measure the same loadout.
    /// </summary>
    internal static class PerfFixture
    {
        public const int Stage = 300;

        /// <summary>Every cast type: three INSTANT, two DOT, one BUFF (03 section 2.3).</summary>
        public static readonly string[] Skills = { "skill_l_01", "skill_l_02", "skill_l_03", "skill_l_04", "skill_e_07", "skill_l_05" };

        /// <summary>Every ability type, MP_REGEN first so the shared pool feeds the other three (03 section 2.4).</summary>
        public static readonly string[] Companions = { "comp_l_06", "comp_l_03", "comp_l_04", "comp_l_05" };

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
            s.Hero.AwakenRank = AwakenRank;
            s.Training.Level[(int)Stat.ATK] = AtkLevel;
            s.Training.Level[(int)Stat.ATK_SPD] = AtkSpdLevel;
            Equip(mvp, s.Skills, s.Loadout.Skills, s.Combat.SkillCooldowns, Skills, id => mvp.Skills.ContainsKey(id));
            Equip(mvp, s.Companions, s.Loadout.Companions, s.Combat.CompanionCooldowns, Companions, id => mvp.Companions.ContainsKey(id));
            return new Simulation(s, mvp);
        }

        static void Equip(ContentDb db, CollectionState owned, string[] slots, int[] cooldowns, string[] ids, Func<string, bool> known)
        {
            if (slots.Length < ids.Length) throw new InvalidOperationException($"{ids.Length} ids for {slots.Length} slots");
            for (int i = 0; i < ids.Length; i++)
            {
                if (!known(ids[i])) throw new InvalidOperationException($"perf fixture: {ids[i]} is not in content {db.Version}");
                owned.Items[ids[i]] = new OwnedItem { Copies = 1 };
                slots[i] = ids[i];
            }
            Array.Clear(cooldowns, 0, cooldowns.Length);
        }
    }
}
