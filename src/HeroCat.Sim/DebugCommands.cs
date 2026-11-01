using System;
using HeroCat.Sim.State;

namespace HeroCat.Sim
{
    /// <summary>Always compiled (bots and tests need it); the client shows it only behind a developer flag.</summary>
    public sealed class DebugCommands
    {
        readonly Simulation _sim;

        internal DebugCommands(Simulation sim) { _sim = sim; }

        public void GrantGold(double amount)
        {
            _sim.Log(CommandKind.Debug, (long)DebugOp.GrantGold, BitConverter.DoubleToInt64Bits(amount));
            _sim.State.Currencies.Gold += amount;
        }

        public void GrantGems(long amount)
        {
            _sim.Log(CommandKind.Debug, (long)DebugOp.GrantGems, amount);
            _sim.State.Currencies.Gems += amount;
        }

        public void GrantHeroCopies(string hero, int copies)
        {
            if (hero == null || !_sim.Content.Heroes.ContainsKey(hero)) throw new ArgumentOutOfRangeException(nameof(hero));
            if (_sim.Logging) _sim.Log(CommandKind.Debug, (long)DebugOp.GrantHeroCopies | _sim.HeroIndexOf(hero) << 8, copies);
            _sim.State.Heroes.Copies.TryGetValue(hero, out int c);
            _sim.State.Heroes.Copies[hero] = c + copies;
            _sim.MarkDpsDirty();
            if (_sim.State.Heroes.AutoEquipBest) _sim.AutoEquipUnlogged();
        }

        public void SetStatLevel(int level)
        {
            _sim.Log(CommandKind.Debug, (long)DebugOp.SetStatLevel, level);
            _sim.State.Progression.StatLevel = Math.Max(0, level);
            _sim.MarkDpsDirty();
        }

        /// <summary>Jump to a stage: resets kills/boss, combat to Spawning, sets class advances to match.</summary>
        public void SetStage(int stage)
        {
            _sim.Log(CommandKind.Debug, (long)DebugOp.SetStage, stage);
            var k = _sim.Content.Constants;
            var st = _sim.State;
            st.Stage.Stage = Math.Max(1, Math.Min(stage, k.StageCap));
            st.Stage.KillsInStage = 0;
            st.Stage.Boss = BossPhase.None;
            st.Stage.BossFailsOnStage = 0;
            st.Combat.Phase = CombatPhase.Spawning;
            st.Combat.SpawnTimer = k.SpawnDelayTicks;
            st.Combat.EnemyIsBoss = false;
            st.Combat.EnemyHp = 0;
            st.Combat.BossTimer = 0;
            int adv = 0;
            foreach (var c in _sim.Content.ClassAdvances) if (c.Stage <= st.Stage.Stage) adv++;
            st.Progression.ClassAdvances = adv;
            _sim.MarkDpsDirty();
        }
    }
}
