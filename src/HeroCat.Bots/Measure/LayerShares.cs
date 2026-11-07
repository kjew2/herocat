using System;
using System.Collections.Generic;
using HeroCat.Runtime.Power;
using HeroCat.Sim.Power;

namespace HeroCat.Bots.Measure
{
    /// <summary>
    /// Layer shares of log-power (10 section 3.5, 03 section 8.5): <c>ln(factor) / ln(RatedDps / BASE_ATK)</c> per factor of
    /// 00 section 7. <see cref="Base"/> is <c>ln(BASE_ATK) / ln(RatedDps)</c>, reported only (never floor-checked). Computed
    /// with Math.Log in the harness: reporting only, never fed back into the sim. Runes are P1 and not reported.
    /// </summary>
    public sealed record LayerShares(double Training, double PartyLevel, double Weapons, double Skills, double Companions, double FishBoard,
                                     double Research, double Awakening, double Crit, double Speed, double Base)
    {
        /// <summary>Layer names as acceptance-targets.json and the reports spell them, in formula order.</summary>
        public static readonly string[] Names =
            { "Training", "PartyLevel", "Weapons", "Skills", "Companions", "FishBoard", "Research", "Awakening", "Crit", "Speed", "Base" };

        public static readonly LayerShares Zero = new LayerShares(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>
        /// Shares over 03 section 7.4's slot-weighted layer factors (Runtime's LayerFactors): their logs add up to
        /// ln(RatedDps), so the layers other than Base sum to 1 over <c>ln(RatedDps / Base)</c>, Base being BASE_ATK times
        /// the party-size term, reported in its own column.
        /// </summary>
        public static LayerShares From(PowerBreakdown p)
        {
            double b = LayerFactors.Factor(p, PowerLayer.Base);
            double total = Math.Log(p.RatedDps / b);
            if (!(total > 0) || double.IsInfinity(total)) return Zero;
            double S(PowerLayer l) => Math.Log(LayerFactors.Factor(p, l)) / total;
            double baseShare = p.RatedDps > 1 ? Math.Log(b) / Math.Log(p.RatedDps) : 0;
            return new LayerShares(S(PowerLayer.Training), S(PowerLayer.PartyLevel), S(PowerLayer.Weapons), S(PowerLayer.Skills),
                                   S(PowerLayer.Companions), S(PowerLayer.Fish), S(PowerLayer.Research), S(PowerLayer.Awakening),
                                   S(PowerLayer.Crit), S(PowerLayer.Speed), baseShare);
        }

        /// <summary>The share of <paramref name="name"/> (one of <see cref="Names"/>).</summary>
        public double Get(string name)
        {
            switch (name)
            {
                case "Training": return Training;
                case "PartyLevel": return PartyLevel;
                case "Weapons": return Weapons;
                case "Skills": return Skills;
                case "Companions": return Companions;
                case "FishBoard": return FishBoard;
                case "Research": return Research;
                case "Awakening": return Awakening;
                case "Crit": return Crit;
                case "Speed": return Speed;
                case "Base": return Base;
                default: throw new ArgumentException("unknown layer " + name);
            }
        }

        /// <summary>The factor behind <paramref name="name"/> in a breakdown (non-zero multiplier test, unblock report).</summary>
        public static double Factor(PowerBreakdown p, string name)
        {
            switch (name)
            {
                case "Training": return LayerFactors.Factor(p, PowerLayer.Training);
                case "PartyLevel": return LayerFactors.Factor(p, PowerLayer.PartyLevel);
                case "Weapons": return LayerFactors.Factor(p, PowerLayer.Weapons);
                case "Skills": return LayerFactors.Factor(p, PowerLayer.Skills);
                case "Companions": return LayerFactors.Factor(p, PowerLayer.Companions);
                case "FishBoard": return LayerFactors.Factor(p, PowerLayer.Fish);
                case "Research": return LayerFactors.Factor(p, PowerLayer.Research);
                case "Awakening": return LayerFactors.Factor(p, PowerLayer.Awakening);
                case "Crit": return LayerFactors.Factor(p, PowerLayer.Crit);
                case "Speed": return LayerFactors.Factor(p, PowerLayer.Speed);
                case "Base": return LayerFactors.Factor(p, PowerLayer.Base);
                default: throw new ArgumentException("unknown layer " + name);
            }
        }

        public IEnumerable<(string name, double share)> All()
        {
            foreach (var n in Names) yield return (n, Get(n));
        }
    }
}
