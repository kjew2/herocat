using System;
using System.Collections.Generic;
using HeroCat.Sim.Power;

namespace HeroCat.Bots.Measure
{
    /// <summary>
    /// Layer shares of log-power (10 section 3.5, 03 section 8.5): <c>ln(factor) / ln(RatedDps / BASE_ATK)</c> per factor of
    /// 00 section 7. <see cref="Base"/> is <c>ln(BASE_ATK) / ln(RatedDps)</c>, reported only (never floor-checked). Computed
    /// with Math.Log in the harness: reporting only, never fed back into the sim. Runes are P1 and not reported.
    /// </summary>
    public sealed record LayerShares(double Training, double HeroLevel, double Weapons, double Skills, double Companions, double FishBoard,
                                     double Research, double Awakening, double Crit, double Speed, double Base)
    {
        /// <summary>Layer names as acceptance-targets.json and the reports spell them, in formula order.</summary>
        public static readonly string[] Names =
            { "Training", "HeroLevel", "Weapons", "Skills", "Companions", "FishBoard", "Research", "Awakening", "Crit", "Speed", "Base" };

        public static readonly LayerShares Zero = new LayerShares(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        public static LayerShares From(PowerBreakdown p)
        {
            double total = Math.Log(p.RatedDps / p.Base);
            if (!(total > 0) || double.IsInfinity(total)) return Zero;
            double S(double f) => Math.Log(f) / total;
            double baseShare = p.RatedDps > 1 ? Math.Log(p.Base) / Math.Log(p.RatedDps) : 0;
            return new LayerShares(S(p.Training), S(p.Hero), S(p.Weapons), S(p.Skills), S(p.Companions), S(p.Fish), S(p.Research),
                                   S(p.Awakening), S(p.Crit), S(p.Speed), baseShare);
        }

        /// <summary>The share of <paramref name="name"/> (one of <see cref="Names"/>).</summary>
        public double Get(string name)
        {
            switch (name)
            {
                case "Training": return Training;
                case "HeroLevel": return HeroLevel;
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
                case "Training": return p.Training;
                case "HeroLevel": return p.Hero;
                case "Weapons": return p.Weapons;
                case "Skills": return p.Skills;
                case "Companions": return p.Companions;
                case "FishBoard": return p.Fish;
                case "Research": return p.Research;
                case "Awakening": return p.Awakening;
                case "Crit": return p.Crit;
                case "Speed": return p.Speed;
                case "Base": return p.Base;
                default: throw new ArgumentException("unknown layer " + name);
            }
        }

        public IEnumerable<(string name, double share)> All()
        {
            foreach (var n in Names) yield return (n, Get(n));
        }
    }
}
