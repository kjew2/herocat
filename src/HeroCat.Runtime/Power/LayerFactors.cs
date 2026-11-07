using System;
using HeroCat.Sim.Power;

namespace HeroCat.Runtime.Power
{
    /// <summary>
    /// The layer factors of 03 section 7.4 (proposal, 03 section 12 question 11; 00 decision 109): the party's damage is a
    /// sum over slots, so a per-slot layer's factor is the DPS-weighted geometric mean of its slot factors,
    /// <c>exp(sum_i w_i ln f_i)</c> with <c>w_i = SlotDps_i / sum_j SlotDps_j</c>; party-wide layers are their scalar; the
    /// party BUFF_ATK term and each companion's rarity attack multiplier join Companions and missiles join Fish; Base is <c>BASE_ATK * exp(H(w))</c>, the party-size term.
    /// The logs add up to <c>ln RatedDps</c> exactly. Takes logs, so it lives outside the sim (00 contract 1): reporting only,
    /// never fed back into it.
    /// </summary>
    public static class LayerFactors
    {
        /// <summary>The factor of <paramref name="layer"/>; 1 for every layer (and 0 for Base) with an empty party.</summary>
        public static double Factor(in PowerBreakdown p, PowerLayer layer)
        {
            switch (layer)
            {
                case PowerLayer.Training: return p.Training;
                case PowerLayer.PartyLevel: return p.Party;
                case PowerLayer.Research: return p.Research;
                case PowerLayer.Runes: return p.Runes;
                case PowerLayer.Crit: return p.Crit;
                case PowerLayer.Speed: return p.Speed;
            }
            double sum = 0.0;
            for (int i = 0; i < p.SlotCount; i++) if (p.Occupied(i)) sum += p.SlotDps(i);
            if (!(sum > 0)) return layer == PowerLayer.Base ? 0.0 : 1.0;
            double lnW = 0.0, lnS = 0.0, lnO = 0.0, lnA = 0.0, entropy = 0.0;
            for (int i = 0; i < p.SlotCount; i++)
            {
                if (!p.Occupied(i)) continue;
                double w = p.SlotDps(i) / sum;
                if (!(w > 0)) continue;
                lnW += w * Math.Log(p.WeaponFactor(i));
                lnS += w * Math.Log(p.SkillFactor(i));
                lnO += w * Math.Log(p.OwnFactor(i) * p.CompanionAtkMult(i));   // the companion's own attack (00 decision 111) is its layer too
                lnA += w * Math.Log(p.Awakening(i));
                entropy -= w * Math.Log(w);
            }
            switch (layer)
            {
                case PowerLayer.Weapons: return Math.Exp(lnW);
                case PowerLayer.Skills: return Math.Exp(lnS);
                case PowerLayer.Companions: return Math.Exp(lnO) * p.PartyBuff;
                case PowerLayer.Awakening: return Math.Exp(lnA);
                case PowerLayer.Fish: return p.Fish * (p.AutoDps > 0 ? p.RatedDps / p.AutoDps : 1.0);
                case PowerLayer.Base: return p.Base * Math.Exp(entropy);
                default: return 1.0;
            }
        }
    }
}
