using System;
using System.Security.Cryptography;
using System.Text;
using HeroCat.Sim.Content;
using HeroCat.Sim.Economy;
using HeroCat.Sim.Rng;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Profile
{
    /// <summary>
    /// Creates a new profile's genesis GameState (02 sections 3.4 and 8.3; issue #73 minimal). Seeds every one of the eight
    /// streams from the immutable RngRoot with SHA-256 (the sim stays hash-free), sizes the board and loadout from content,
    /// and applies the profile-created grant (tut_welcome: TUTORIAL_DIAMONDS and TUTORIAL_WEAPON_ID). The day is not rolled
    /// here: the first ApplyServerTime does that and pays the first login (06 section 4.2 rule 4).
    /// The install id is InstallId.LoadOrCreate (02 section 8.4). TODO(#246): the currency_changed{TUTORIAL} analytics row
    /// for the grant (item 4), since no Simulation exists yet to emit CurrencyChanged.
    /// </summary>
    public static class ProfileFactory
    {
        /// <param name="playerId">Server playerId if known, else Guid.Empty for a local guest.</param>
        /// <param name="rngRoot">Override for tests and bots; default: playerId if known, else a fresh random Guid (issue #9).</param>
        public static GameState Create(ContentDb db, Guid playerId, long nowServerSeconds, Guid deviceId, Guid? rngRoot = null)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            var root = rngRoot ?? (playerId != Guid.Empty ? playerId : Guid.NewGuid());
            if (root == Guid.Empty) throw new ArgumentException("RngRoot must not be Guid.Empty (02 R4)", nameof(rngRoot));
            var k = db.Constants;
            var s = new GameState
            {
                PlayerId = playerId,
                DeviceId = deviceId,
                RngRoot = root,
                ContentVersion = db.Version,
                ContentHash = db.ContentHash,
                Time = new TimeState
                {
                    CreatedServerTime = nowServerSeconds,
                    LastTickServerTime = nowServerSeconds,
                    AnchorServerTime = nowServerSeconds,
                    AnchorTick = 0,
                },
                Fish = new FishBoardState { Cells = new int[k.FishBoardW * k.FishBoardH], TankReleaseLevel = db.Tank.ReleaseLevel },
                Loadout = new LoadoutState
                {
                    Skills = new string[k.SkillSlots],
                    Companions = new string[k.CompanionSlots],
                    Runes = new string[k.RuneSlots],
                },
                Combat = new CombatState
                {
                    SkillCooldowns = new int[k.SkillSlots],
                    CompanionCooldowns = new int[k.CompanionSlots],
                },
            };
            foreach (var b in db.Banners.Values)
                if (b.Enabled) s.Banners[b.Id] = new BannerState();
            foreach (var name in RngStreams.All)
                s.Rng[name] = new RngStreamState { Seed = RngStreams.SeedToHex(StreamSeed(root, name)), Counter = 0 };
            ProfileGrants.ApplyWelcome(s, db, nowServerSeconds);
            return s;
        }

        /// <summary>seed(name) = first 8 bytes of SHA-256(RngRoot.ToByteArray() ++ UTF8(name)), little-endian (02 section 3.4).</summary>
        public static ulong StreamSeed(Guid rngRoot, string streamName)
        {
            var root = rngRoot.ToByteArray();
            var name = Encoding.UTF8.GetBytes(streamName);
            var input = new byte[root.Length + name.Length];
            Buffer.BlockCopy(root, 0, input, 0, root.Length);
            Buffer.BlockCopy(name, 0, input, root.Length, name.Length);
            byte[] h;
            using (var sha = SHA256.Create()) h = sha.ComputeHash(input);
            ulong v = 0;
            for (int i = 7; i >= 0; i--) v = (v << 8) | h[i];
            return v;
        }
    }
}
