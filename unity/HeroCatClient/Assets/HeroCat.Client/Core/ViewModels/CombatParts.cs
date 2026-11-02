using System;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The client-side Tap Zap throttle (07 section 4.3; issue #113): taps are accumulated per frame and at most
    /// <c>TAP_ZAP_MAX_PER_SEC</c> are accepted in any one-second window; an extra tap still plays the tap VFX but sends
    /// nothing. The sim keeps its own guard (03 section 2.5), so this is the bot-tap guard's friendly front, not the rule.
    /// </summary>
    public sealed class TapZapThrottle
    {
        readonly double[] _accepted;   // ring of accepted tap times
        int _head, _count, _pending;

        public TapZapThrottle(int maxPerSec)
        {
            MaxPerSec = maxPerSec < 1 ? 1 : maxPerSec;
            _accepted = new double[MaxPerSec];
        }

        public int MaxPerSec { get; }

        /// <summary>Taps accepted since the last <see cref="Flush"/>.</summary>
        public int Pending => _pending;

        public long TotalAccepted { get; private set; }
        public long TotalIgnored { get; private set; }

        /// <summary>One tap at <paramref name="now"/> seconds. True: it counts toward this frame's TapZap.</summary>
        public bool Tap(double now)
        {
            while (_count > 0 && now - _accepted[_head] >= 1.0)
            {
                _head = (_head + 1) % _accepted.Length;
                _count--;
            }
            if (_count >= MaxPerSec)
            {
                TotalIgnored++;
                return false;
            }
            _accepted[(_head + _count) % _accepted.Length] = now;
            _count++;
            _pending++;
            TotalAccepted++;
            return true;
        }

        /// <summary>The count for this frame's <c>TapZap(count)</c> (0: send nothing); resets the frame's count.</summary>
        public int Flush()
        {
            int n = _pending;
            _pending = 0;
            return n;
        }

        /// <summary>Backgrounding: forget pending taps.</summary>
        public void Cancel() => _pending = 0;
    }

    /// <summary>Who dealt a damage number; same-source hits within one tick merge (07 section 13).</summary>
    public enum DamageSource { Attack, Skill, Companion, Fish, Tap }

    /// <summary>One live damage label.</summary>
    public struct DamageLabel
    {
        public bool Active;
        public double Amount;
        public bool Crit;
        public DamageSource Source;
        public long Tick;
        public double SpawnTime;

        /// <summary>Bumped when this slot is (re)used, so the view restarts the float animation.</summary>
        public int Serial;
    }

    /// <summary>
    /// The damage-number pool (07 sections 4.3 and 13, AC 13; issue #113): at most <see cref="UiConstants.DamageLabelPool"/>
    /// labels live at once; above that the oldest is recycled. Hits of the same source within one tick merge into one
    /// number (per-tick aggregation). Engine-free: the Unity view keeps one pooled TMP label per slot.
    /// </summary>
    public sealed class DamageNumberPool
    {
        readonly DamageLabel[] _slots;
        int _serial;

        public DamageNumberPool(int capacity = UiConstants.DamageLabelPool, double lifeSec = UiConstants.DamageNumberLifeSec)
        {
            _slots = new DamageLabel[capacity];
            LifeSec = lifeSec;
        }

        public int Capacity => _slots.Length;
        public double LifeSec { get; }

        public int LiveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _slots.Length; i++) if (_slots[i].Active) n++;
                return n;
            }
        }

        public long Recycled { get; private set; }

        public ref DamageLabel this[int i] => ref _slots[i];

        /// <summary>Adds a hit; returns the slot index it landed in.</summary>
        public int Add(double amount, bool crit, DamageSource source, long tick, double now)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                ref var s = ref _slots[i];
                if (s.Active && s.Tick == tick && s.Source == source)
                {
                    s.Amount += amount;
                    s.Crit |= crit;
                    return i;
                }
            }
            int free = -1, oldest = -1;
            double oldestTime = double.PositiveInfinity;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (!_slots[i].Active) { free = i; break; }
                if (_slots[i].SpawnTime < oldestTime) { oldestTime = _slots[i].SpawnTime; oldest = i; }
            }
            if (free < 0)
            {
                free = oldest;
                Recycled++;
            }
            _slots[free] = new DamageLabel
            {
                Active = true, Amount = amount, Crit = crit, Source = source, Tick = tick, SpawnTime = now, Serial = ++_serial,
            };
            return free;
        }

        /// <summary>Expires labels older than <see cref="LifeSec"/>.</summary>
        public void Update(double now)
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Active && now - _slots[i].SpawnTime >= LifeSec) _slots[i].Active = false;
        }

        public void Clear()
        {
            for (int i = 0; i < _slots.Length; i++) _slots[i].Active = false;
        }
    }

    /// <summary>A skill or companion icon in the combat viewport's rows (07 section 4.3).</summary>
    public readonly struct SlotLook : IEquatable<SlotLook>
    {
        public SlotLook(string itemId, string iconId, HeroCat.Sim.Content.Rarity rarity, int level, float cooldown01)
        {
            ItemId = itemId;
            IconId = iconId;
            Rarity = rarity;
            Level = level;
            Cooldown01 = cooldown01;
        }

        public string ItemId { get; }
        public string IconId { get; }
        public HeroCat.Sim.Content.Rarity Rarity { get; }
        public int Level { get; }

        /// <summary>0 = ready; 1 = just cast. Quantized to 1/100.</summary>
        public float Cooldown01 { get; }

        public bool IsEmpty => ItemId == null;

        public bool Equals(SlotLook o) => ItemId == o.ItemId && Level == o.Level && Cooldown01 == o.Cooldown01 && Rarity == o.Rarity;
        public override bool Equals(object obj) => obj is SlotLook o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(ItemId, Level, Cooldown01);
    }
}
