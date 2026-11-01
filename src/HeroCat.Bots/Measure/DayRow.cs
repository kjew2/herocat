using System;

namespace HeroCat.Bots.Measure
{
    /// <summary>One row of the tuning table (specs/09 section 6.2), snapshotted after the night's catch-up.</summary>
    public sealed record DayRow(
        int Day, int Stage, double Dps, int StatLvl, double StatMult, double GearMult, double HeroMult, double ClassMult,
        long Pulls, int LPlus, int FirstWallStage, double LongestWallHours);

    /// <summary>Share of ln(DPS) per layer (specs/09 section 6.3). Reporting only.</summary>
    public readonly struct LayerShares
    {
        public LayerShares(double stat, double gear, double hero, double cls, double @base)
        {
            Stat = stat; Gear = gear; Hero = hero; Class = cls; Base = @base;
        }

        public double Stat { get; }
        public double Gear { get; }
        public double Hero { get; }
        public double Class { get; }
        public double Base { get; }

        public static LayerShares Of(DayRow r, double baseAtk)
        {
            double l = Math.Log(r.Dps);
            return new LayerShares(Math.Log(r.StatMult) / l, Math.Log(r.GearMult) / l, Math.Log(r.HeroMult) / l,
                                   Math.Log(r.ClassMult) / l, Math.Log(baseAtk) / l);
        }

        public (string Layer, double Share)[] Layers => new[] { ("stat", Stat), ("gear", Gear), ("hero", Hero), ("class", Class) };
    }
}
