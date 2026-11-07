# Companion concepts

Presentation only: names, roles and look briefs for the 50 rows in `content_src/companions.csv`. Ids, rarities, ability types and numbers belong to `specs/04` section 8. Role tags map onto `ability_type` (`specs/04` section 8.1). Names and one-line descriptions are in `content_src/localization.csv` (`companion.<id>.name` and `.desc`). Issue #289.

Lucy, Valeria, Seraphine and Yuki come from the artist's prototypes in `art/reference/companions/` (issue #291). The other 46 are written briefs with no art yet (issue #292). They follow the house style picked by the splash style pilot (`splash-pilot.md`); this file sets what each character is, not how she is rendered.

## Rarity ladder

**The hottest and most stylish characters sit at the top** (agreed with the owner on 2026-11-07, `specs/04` section 8.1). Rarity sets how glamorous a design is, so a pull feels like an upgrade before the numbers do:

| Rarity | Who they are | Design |
|---|---|---|
| Common | Townsfolk and apprentices | Cute and grounded. Practical work clothes, two main colours, an everyday tool turned weapon. Fully covered. |
| Rare | Adventurers with a theme | Fitted outfits with some flair, two or three colours, a signature weapon. A little skin at most. |
| Epic | Glamorous specialists | Fashion-forward and confident: striking palettes, statement outfits, visible skin (bare shoulders, midriff, short hems). |
| Legendary | The stars | The hottest, most stylish designs: statement silhouettes (horns, tails, halos, ears), ornate detail, suggestive cuts within `specs/00` decision 105. Each gets splash art (decision 108). |
| Mythic | Otherworldly | Everything Legendary has, at a divine or demonic scale: oversized halos, floating weapons, light or shadow pouring off them. |

All of them are heroines (`specs/00` decision 104). Store art uses covered variants only (decision 105). Inside a rarity, no two share a silhouette, a signature prop or a palette.

## Roster

| Row | Rarity | Ability | Name | Role | Element | Reference |
|---|---|---|---|---|---|---|
| `comp_c_01` | Common | `DAMAGE` | Hazel | Warrior | LEAF | none yet |
| `comp_c_02` | Common | `DAMAGE` | Tilly | Warrior | NONE | none yet |
| `comp_c_03` | Common | `DAMAGE` | Mina | Mage | FIRE | none yet |
| `comp_c_04` | Common | `DAMAGE` | Rue | Caster | WATER | none yet |
| `comp_c_05` | Common | `DAMAGE` | Koko | Warrior | FIRE | none yet |
| `comp_c_06` | Common | `DAMAGE` | Wren | Warrior | NONE | none yet |
| `comp_c_07` | Common | `DAMAGE` | Dot | Mage | LIGHT | none yet |
| `comp_c_08` | Common | `DAMAGE` | Sable | Death Knight | DARK | none yet |
| `comp_c_09` | Common | `DAMAGE` | Juniper | Caster | LEAF | none yet |
| `comp_c_10` | Common | `BUFF_ATK` | Greta | Tank | NONE | none yet |
| `comp_c_11` | Common | `BUFF_ATK` | Poppy | Paladin | LIGHT | none yet |
| `comp_c_12` | Common | `BUFF_ATK` | Brina | Tank | WATER | none yet |
| `comp_c_13` | Common | `BUFF_ATK` | Nell | Paladin | FIRE | none yet |
| `comp_c_14` | Common | `BUFF_GOLD` | Penny | Merchant | NONE | none yet |
| `comp_c_15` | Common | `BUFF_GOLD` | Lark | Bard | LEAF | none yet |
| `comp_c_16` | Common | `BUFF_GOLD` | Clove | Merchant | FIRE | none yet |
| `comp_c_17` | Common | `MP_REGEN` | Faye | Cleric | LIGHT | none yet |
| `comp_c_18` | Common | `MP_REGEN` | Bea | Sage | NONE | none yet |
| `comp_r_01` | Rare | `DAMAGE` | Kestrel | Warrior | NONE | none yet |
| `comp_r_02` | Rare | `DAMAGE` | Ember | Mage | FIRE | none yet |
| `comp_r_03` | Rare | `DAMAGE` | Isolde | Caster | WATER | none yet |
| `comp_r_04` | Rare | `DAMAGE` | Rook | Death Knight | DARK | none yet |
| `comp_r_05` | Rare | `MP_REGEN` | Saffron | Sage | FIRE | none yet |
| `comp_r_06` | Rare | `DAMAGE` | Thistle | Warrior | LEAF | none yet |
| `comp_r_07` | Rare | `DAMAGE` | Mara | Mage | DARK | none yet |
| `comp_r_08` | Rare | `DAMAGE` | Hana | Caster | LIGHT | none yet |
| `comp_r_09` | Rare | `BUFF_ATK` | Astrid | Tank | WATER | none yet |
| `comp_r_10` | Rare | `BUFF_ATK` | Odette | Paladin | LIGHT | none yet |
| `comp_r_11` | Rare | `BUFF_ATK` | Gwen | Tank | LEAF | none yet |
| `comp_r_12` | Rare | `BUFF_GOLD` | Lottie | Merchant | NONE | none yet |
| `comp_r_13` | Rare | `BUFF_GOLD` | Cadence | Bard | WATER | none yet |
| `comp_r_14` | Rare | `MP_REGEN` | Maple | Cleric | LEAF | none yet |
| `comp_e_01` | Epic | `DAMAGE` | Scarlett | Warrior | FIRE | none yet |
| `comp_e_02` | Epic | `DAMAGE` | Mireille | Mage | WATER | none yet |
| `comp_e_03` | Epic | `DAMAGE` | Akane | Warrior | DARK | none yet |
| `comp_e_04` | Epic | `DAMAGE` | Celeste | Caster | LIGHT | none yet |
| `comp_e_05` | Epic | `DAMAGE` | Belladonna | Mage | LEAF | none yet |
| `comp_e_06` | Epic | `BUFF_ATK` | Dahlia | Paladin | NONE | none yet |
| `comp_e_07` | Epic | `BUFF_ATK` | Rhea | Tank | LEAF | none yet |
| `comp_e_08` | Epic | `BUFF_GOLD` | Gilda | Merchant | LIGHT | none yet |
| `comp_e_09` | Epic | `MP_REGEN` | Iris | Cleric | WATER | none yet |
| `comp_e_10` | Epic | `MP_REGEN` | Elowen | Sage | LEAF | none yet |
| `comp_l_01` | Legendary | `DAMAGE` | Lucy | Arcane / Caster | LIGHT | `lucy.jpeg`, `lucy_special.jpeg` |
| `comp_l_02` | Legendary | `DAMAGE` | Yuki | Mage / Shadow | DARK | `yuki.jpg`, `splash/yuki_splash.png`, `yuki_concept_parasol.png`, `yuki_concept_seated.jpg` |
| `comp_l_03` | Legendary | `DAMAGE` | Carmine | Warrior | FIRE | none yet |
| `comp_l_04` | Legendary | `BUFF_ATK` | Valeria | Tank / Paladin | LIGHT | `valeria.jpeg` |
| `comp_l_05` | Legendary | `BUFF_GOLD` | Marisol | Merchant | WATER | none yet |
| `comp_l_06` | Legendary | `MP_REGEN` | Shiori | Sage | LEAF | none yet |
| `comp_m_01` | Mythic | `DAMAGE` | Seraphine | Warrior / Death Knight | DARK | `seraphine.jpeg` |
| `comp_m_02` | Mythic | `BUFF_ATK` | Astraea | Paladin | LIGHT | none yet |

## Mythic

### Seraphine
- **Hook:** a death knight who took a reaper's scythe as payment and never gave it back.
- **Silhouette:** curling black horns with a red rose, long silver hair, a skull pauldron, a tattered crimson cape and a huge skull scythe.
- **Palette:** black, crimson and magenta, with gold trim.
- **Ability VFX (`vfx_comp_m_01`):** a crimson crescent slash crosses the screen and leaves fading magenta wisps.

### Astraea
`comp_m_02`, Paladin, LIGHT.
- **Hook:** a war goddess who stepped down from her constellation because mortals kept losing; when she raises her spear, the whole party fights as if blessed.
- **Look:** Floor-length white-gold hair, a sunburst crown, a sculpted gilded bodice over a high-slit white war skirt, gilded gauntlets and greaves, a halo of six floating golden blades behind her, and a spear of light taller than she is.
- **Hair:** floor-length white-gold hair in loose waves, with two thin braids woven with gold thread framing her face. It drifts as if underwater, a sign she is not quite mortal.
- **Outfit:**
  - a sunburst crown of tall gold rays;
  - a sculpted gilded bodice engraved with a rising sun;
  - one gilded pauldron, and a pair of large white feathered wings (owner, 2026-11-07);
  - a white war skirt with a high slit and gold hem plates;
  - gilded gauntlets, and greaves that end in strapped sandals.
- **Signature props:** a spear of solid light taller than she is, and a halo of six floating golden blades fanned behind her shoulders.
- **Face:** gold eyes with tiny starburst pupils, a serene faint smile, gold-leaf marks at the outer corners of her eyes. She looks down at the viewer kindly, the way a goddess would.
- **Motifs:** sun rays, constellation lines between stars, laurel, cracked white marble.
- **Palette:** White, gold and sunrise orange; marble-and-gilt finish.
- **Leave out:** real religious symbols and any real deity's likeness. She is our own goddess.
- **Ability VFX (`vfx_comp_m_02`):** The six halo blades fan out over the party and stamp a golden sigil under each companion; light motes rise from them while the buff lasts.
- **Splash:** `art_splash_comp_m_02` (decision 108).

## Legendary

### Lucy
- **Hook:** a stargazing witch who reads the sky like a map and redraws it when it disagrees with her.
- **Silhouette:** a wide-brim witch hat with a white lily, long wavy blonde hair with a braid, and an astrolabe staff taller than she is.
- **Palette:** violet, gold and cream; amethyst accents.
- **Ability VFX (`vfx_comp_l_01`):** a gold ring of crescent moons opens over the enemy and a starfall drops through it.
- **Variants:** `lucy.jpeg` is the in-game look; `lucy_special.jpeg` is a possible mature skin (`specs/00` decision 105). Store art uses the covered look only.

### Yuki
- **Hook:** a gothic shadow mage who never lowers her parasol; when she twirls it, the shade breaks into butterflies.
- **Silhouette:** very long straight black hair in a hime cut (heavy blunt bangs, side locks cut straight at the jaw) with white panels in the side locks and white strands through the length; a heart-and-cross bow; a thin halo of silver thorns; oversized black off-shoulder sleeves; chunky platform shoes; and a frilled black-and-white parasol.
- **Fashion:** jirai kei ("landmine" style) worn gothic:
  - a white frilled blouse with a big black bow at the collar, pinned with a heart gem, and a lace choker;
  - a black high-waisted corset belt with several buckled straps, over a short black frilled skirt with white lace layers;
  - oversized black off-shoulder sleeves laced up with white ribbon, frilled arm cuffs and buckled arm straps;
  - a buckled thigh strap with a frilled garter, white lace socks with frilled cuffs and bows;
  - black patent platform Mary Janes with heart buckles.
- **Signature prop:** a white plush bunny with stitched X eyes and a heart-gem bow, hanging from her belt or held. It shows up in every concept, so keep it.
- **Face:** violet-grey eyes, half-lidded and languid, lips slightly parted; aloof and a little sad. Soft red under-eye makeup, a small beauty mark under one eye. Cross earrings and an ear chain.
- **Motifs:** hearts, crosses, buckles and ribbons; floating crystal shards, ribbons and pale butterflies; white lilies; a mirror or a second, larger echo of her face behind her.
- **Palette:** near-monochrome black, white and silver, with lavender and violet light. Violet stays the only colour accent.
- **Leave out:** bandages, stitches or scars on the face or body, self-harm motifs, and the barcode and text graphics in the concepts (the game draws its own UI).
- **References:** `yuki_concept_parasol.png` (standing with the parasol, low angle) and `yuki_concept_seated.jpg` (seated with the bunny, mirror echo) are the owner's concepts for her look (2026-11-07). They win over `yuki.jpg` where they differ.
- **Ability VFX (`vfx_comp_l_02`):** violet shadow butterflies swarm out of the parasol and burst on the enemy.
- **Added after the pilot**, the same way: stand-ins cut by `tools/art/cut_pilot_assets.py`.
- **Style pilot:** Yuki is the character for the splash style pilot that picks the house illustration style (`splash-pilot.md`, issue #287).
- **Splash pilot:** the first dedicated vertical splash (`splash/yuki_splash.png`, 1024 x 1536). The splash stand-in is cut from it, not from the sheet; icon, portrait and chibi still come from the sheet, whose face crop has more pixels.

### Carmine
`comp_l_03`, Warrior, FIRE.
- **Hook:** a dragon-blooded duelist who fights in a cropped jacket because armour only slows her down.
- **Look:** Swept-back dark red horns, a crimson bob with one long side braid, a scaled tail, a cropped black moto-style jacket over a fitted battle top, gold chain belts, thigh-high boots, and a serrated greatsword whose edge glows like cooling lava.
- **Hair and horns:** an asymmetric crimson bob, shorter on one side, with one long side braid capped by gold rings. Dark red horns sweep back from her temples, with ember-orange cracks at the tips.
- **Outfit:**
  - a cropped black moto jacket with flame-orange lining, the collar popped and the sleeves pushed up;
  - a fitted black battle crop top that bares her midriff;
  - two gold chain belts slung low over black shorts;
  - fingerless gloves, and thigh-high black boots with gold buckles.
- **Tail:** a crimson scaled tail with an ember glow at the tip. It curls when she is pleased.
- **Signature prop:** a serrated greatsword as tall as she is, rested on her shoulder. Its edge glows like cooling lava.
- **Face:** warm tan skin (owner, 2026-11-07), amber eyes with slit pupils, a cocky grin showing one fang, small crimson scale patches on her cheekbones.
- **Motifs:** embers, cracked magma, dragon scales, gold hardware.
- **Palette:** Crimson, black and molten orange; gold hardware.
- **Ability VFX (`vfx_comp_l_03`):** She drags the greatsword through a magma arc; the cut cracks open and erupts under the enemy.
- **Splash:** `art_splash_comp_l_03` (decision 108).

### Valeria
- **Hook:** a paladin captain who leads from the front; the banner she plants turns a skirmish into a charge.
- **Silhouette:** a winged circlet, long braided blonde hair, a gold-trimmed white plate, a big cross shield and a lance-banner.
- **Palette:** white, gold and royal blue; fleur-de-lis motif.
- **Ability VFX (`vfx_comp_l_04`):** a blue-and-gold banner unfurls behind the squire; a golden aura stays on the hero while the buff lasts.

### Marisol
`comp_l_05`, Merchant, WATER.
- **Hook:** a pirate queen who treats every battlefield as a treasure map; her crew has the loot counted before the dust settles.
- **Look:** A wide tricorn with a white plume, wavy dark hair to the waist, an open long captain's coat over a laced corset and ruffled blouse that slips off one shoulder, high cuffed boots, a cutlass on one hip and a gold spyglass on the other.
- **Hair:** waist-length wavy black hair with a deep teal sheen, a few thin braids threaded with gold coins.
- **Outfit:**
  - a wide black tricorn with gold trim, a white plume and a teal ribbon;
  - an open, long deep-teal captain's coat with gold buttons, wide gold-embroidered cuffs and flared tails, worn off the shoulders;
  - a white ruffled blouse slipping off one shoulder, under a laced black corset;
  - a gold-and-teal sash with coin charms, short black fitted breeches, high cuffed black boots.
- **Signature props:** a cutlass with a gold basket hilt on one hip and a gold spyglass on the other. She flips a gold coin when she's thinking.
- **Face:** warm tan skin (owner, 2026-11-07), sea-green eyes, a confident smirk, a beauty mark under one eye, gold hoop earrings.
- **Motifs:** a compass rose, waves, gold coins, rope and rigging, an open treasure chest.
- **Palette:** Deep-sea teal, black and gold.
- **Leave out:** real pirate flags and ship names, and any readable text on maps or coins.
- **Ability VFX (`vfx_comp_l_05`):** A treasure chest lands with a thud, springs open and showers gold coins across the screen; the coin glint stays on the gold counter while the buff lasts.
- **Splash:** `art_splash_comp_l_05` (decision 108).

### Shiori
`comp_l_06`, Sage, LEAF.
- **Hook:** a nine-tailed fox spirit who keeps a mountain shrine and lends her foxfire to anyone polite enough to ask.
- **Look:** Tall fox ears, nine fluffy tails fanned out behind her, long pink-silver hair with a red string ornament, a layered off-shoulder kimono worn loose under a wide obi bow, a kagura bell staff, and paper lanterns of foxfire orbiting her.
- **Hair and ears:** long, flowing pink-silver hair with soft side tufts, tied loosely with red string and small gold bells. Tall fox ears with pale pink insides.
- **Outfit:**
  - a layered kimono worn off both shoulders: ivory outer layer, vermilion middle, plum inner collar;
  - long split sleeves, with a hem cut short at the front and trailing at the back;
  - a wide plum obi tied in a large bow at the back, with a red braided cord;
  - white tabi, and tall black lacquered sandals.
- **Tails:** nine fluffy tails, ivory with pink-silver tips, fanned out behind her like a halo. They are her silhouette.
- **Signature props:** a kagura bell staff (a cluster of gold bells on a red-ribboned handle) and glowing orbs of pale green foxfire orbiting her (orbs, not paper lanterns; owner, 2026-11-07).
- **Face:** plum eyes with a red eyeliner flick, a gentle sly smile, a small vermilion mark on her forehead.
- **Motifs:** a torii gate, a mountain shrine, mist, falling leaves, foxfire.
- **Palette:** Vermilion, ivory and plum; foxfire in pale green.
- **Leave out:** readable kanji or any other text on lanterns, charms or signs, and real shrine names.
- **Ability VFX (`vfx_comp_l_06`):** Green foxfire wisps leave her lanterns and sink into each companion, refilling their mana with a soft bell chime.
- **Splash:** `art_splash_comp_l_06` (decision 108).

## Epic

### Scarlett
`comp_e_01`, Warrior, FIRE.
- **Hook:** a mercenary with a price on her head and a sword that is always on fire.
- **Look:** Long fiery red hair in a high ponytail, a cropped red leather jacket, a black fitted top and combat shorts, fingerless gloves, and a broad greatsword wrapped in flame.
- **Palette:** Red, black and chrome.
- **Ability VFX (`vfx_comp_e_01`):** A rising flame slash and a burst of embers.

### Mireille
`comp_e_02`, Mage, WATER.
- **Hook:** an ice sorceress who dresses for the opera and fights like it is the finale.
- **Look:** White hair in an elegant updo, a sleek white strapless gown with a frost-crystal train, long evening gloves, and a slender crystal staff.
- **Palette:** Ice blue, white and silver.
- **Ability VFX (`vfx_comp_e_02`):** A frost lance forms above her and shatters on the enemy into glittering shards.

### Akane
`comp_e_03`, Warrior, DARK.
- **Hook:** a kunoichi who is paid to vanish and charges extra to come back.
- **Look:** A high black ponytail tied with a long red ribbon, a half-face mask, a short indigo shinobi dress with mesh sleeves, thigh wraps, and twin kunai on long red cords.
- **Palette:** Indigo, black and blood red.
- **Ability VFX (`vfx_comp_e_03`):** She blinks out and shadow afterimages strike the enemy from three sides.

### Celeste
`comp_e_04`, Caster, LIGHT.
- **Hook:** a sun oracle who reads the future in mirrors and burns whatever she does not like in it.
- **Look:** Long golden hair under a sun-disc headdress, a draped white-and-gold dress with sheer sleeves and a bare midriff, and four floating mirror discs.
- **Palette:** Sun gold, sky blue and white.
- **Ability VFX (`vfx_comp_e_04`):** The mirrors line up and focus a beam of sunlight on the enemy.

### Belladonna
`comp_e_05`, Mage, LEAF.
- **Hook:** a poisoner alchemist who signs her work with a flower.
- **Look:** A dark green bob with nightshade flowers in it, a black lace-up corset dress, a beaked half-mask pushed up on her head, and a bandolier of glowing vials.
- **Palette:** Deep green, black and violet.
- **Ability VFX (`vfx_comp_e_05`):** A vial arcs over and bursts into a toxic bloom that eats into the enemy.

### Dahlia
`comp_e_06`, Paladin, NONE.
- **Hook:** a parade captain whose war horn turns a march into a stampede.
- **Look:** Short wavy chestnut hair under a plumed shako, a navy military tailcoat with gold epaulettes and braid, white gloves, tall riding boots, a sabre, and a brass war horn.
- **Palette:** Navy, gold and white.
- **Ability VFX (`vfx_comp_e_06`):** The horn blasts, gold soundwaves wash over the party, and ribbons stream from each companion while the buff lasts.

### Rhea
`comp_e_07`, Tank, LEAF.
- **Hook:** an amazon queen who taught the party one rule: hit back harder.
- **Look:** Tall and athletic, sun-tanned, long braided dark hair with a laurel circlet, a bronze breastplate and greaves over a short green warrior skirt, a leopard pelt over one shoulder, and a huge round bronze shield.
- **Palette:** Bronze, emerald and leopard gold.
- **Ability VFX (`vfx_comp_e_07`):** She slams her shield into the ground; a bronze shockwave rings out and leaves a laurel glow on the party.

### Gilda
`comp_e_08`, Merchant, LIGHT.
- **Hook:** an heiress who has never lost a bet and does not plan to start.
- **Look:** Platinum blonde victory rolls, a champagne sequin gown with a feather stole, long black gloves, a jewelled lorgnette, and a gold-leaf fan.
- **Palette:** Champagne, black and white gold.
- **Ability VFX (`vfx_comp_e_08`):** She snaps the fan open and gold coins pour down like confetti.

### Iris
`comp_e_09`, Cleric, WATER.
- **Hook:** a lotus priestess whose spring water tastes like a second wind.
- **Look:** Long pastel pink hair threaded with lotus flowers, sheer layered white-and-aqua robes, bare feet with anklets, and a lotus-topped staff dripping water.
- **Palette:** Pastel pink, aqua and white.
- **Ability VFX (`vfx_comp_e_09`):** A lotus opens under the party and rains glowing droplets that refill their mana.

### Elowen
`comp_e_10`, Sage, LEAF.
- **Hook:** an elven druid who talks to trees, and the trees answer in mana.
- **Look:** Long sage-green hair, a crown of antlers wrapped in ivy, pointed ears, a moss-green gown with a leaf-cut hem and gold vine jewellery, and a living-wood staff holding a seed of light.
- **Palette:** Sage green, cream and gold.
- **Ability VFX (`vfx_comp_e_10`):** Roots of light rise around the party and pulse mana into each companion.

## Rare

### Kestrel
`comp_r_01`, Warrior, NONE.
- **Hook:** a fencer who will duel anyone and apologise to no one.
- **Look:** A wide-brim musketeer hat with a long red feather, a short cape over one shoulder, a fitted crimson doublet, and a rapier.
- **Palette:** Crimson, black and gold.
- **Ability VFX (`vfx_comp_r_01`):** Three quick rapier thrusts that leave gold sparks.

### Ember
`comp_r_02`, Mage, FIRE.
- **Hook:** a street fire-dancer who turned her act into a weapon.
- **Look:** Wild orange curls, a crop top and flowing harem pants with a coin belt, and two flaming poi chains.
- **Palette:** Flame gold and bronze.
- **Ability VFX (`vfx_comp_r_02`):** The poi spin into a wheel of fire that rolls into the enemy.

### Isolde
`comp_r_03`, Caster, WATER.
- **Hook:** a tide witch who keeps the sea in a bottle and lets it out when she is annoyed.
- **Look:** Long wavy sea-green hair, a sea-glass blue robe hung with pearl strands, a coral staff, and a corked bottle on her belt.
- **Palette:** Aqua and pearl.
- **Ability VFX (`vfx_comp_r_03`):** A wave pours out of the bottle and crashes down on the enemy.

### Rook
`comp_r_04`, Death Knight, DARK.
- **Hook:** a knight who plays every battle like chess, and is always three moves ahead.
- **Look:** Short ash hair, chequered plate pauldrons, a castle-tower helm carried under one arm, a red king token on a chain, and a halberd.
- **Palette:** Slate grey and ivory, one red accent.
- **Ability VFX (`vfx_comp_r_04`):** A tower-shaped shadow drops onto the enemy.

### Saffron
`comp_r_05`, Sage, FIRE.
- **Hook:** a tea sage whose brew could wake the dead, or at least their mana.
- **Look:** Twin buns, round spectacles, a long-sleeved saffron and indigo robe, a brass kettle in hand and a tea set strapped to her back.
- **Palette:** Saffron and indigo.
- **Ability VFX (`vfx_comp_r_05`):** A spiral of steam and tea-leaf sparkles drifts over the party and refills their mana.

### Thistle
`comp_r_06`, Warrior, LEAF.
- **Hook:** a highland brawler who settles arguments with a claymore.
- **Look:** Freckles, a wild auburn mane, a tartan kilt and sash, bare arms with blue woad markings, and a claymore as tall as she is.
- **Palette:** Green and purple tartan, auburn.
- **Ability VFX (`vfx_comp_r_06`):** A spinning claymore sweep that scatters thistle down.

### Mara
`comp_r_07`, Mage, DARK.
- **Hook:** a hex witch whose black cat does most of the cursing.
- **Look:** Cat ears and tail, a pointed witch hat with a crooked tip, a short black dress with a ragged hem, striped stockings, a black cat on her shoulder, and a crooked wand.
- **Palette:** Black and acid green.
- **Ability VFX (`vfx_comp_r_07`):** A green curse sigil flashes like a cat's eye and fires a hex bolt.

### Hana
`comp_r_08`, Caster, LIGHT.
- **Hook:** a shrine maiden who folds her paper charms into cranes and sends them to war.
- **Look:** Long dark brown hair tied low with a white ribbon, a white haori and red hakama, and a fan of paper talismans.
- **Palette:** White and vermilion.
- **Ability VFX (`vfx_comp_r_08`):** Paper cranes fold themselves in the air, dive at the enemy and burst into light.

### Astrid
`comp_r_09`, Tank, WATER.
- **Hook:** a shieldmaiden from the frozen north who has never taken a step back.
- **Look:** Platinum braids under a winged helm, a fur-trimmed cloak over chain mail, a round painted shield, and a bearded axe.
- **Palette:** Ice blue, fur grey and iron.
- **Ability VFX (`vfx_comp_r_09`):** A shield bash sends out a ring of frost and leaves a blue rune on each companion.

### Odette
`comp_r_10`, Paladin, LIGHT.
- **Hook:** a swan knight with a dancer's poise and a lance's reach.
- **Look:** White hair in a ballet bun, white feathered pauldrons, an armoured skirt cut like a tutu, and a slim lance.
- **Palette:** White and pale gold, black accents.
- **Ability VFX (`vfx_comp_r_10`):** White feathers swirl around the party and a swan-wing sigil glows on each companion.

### Gwen
`comp_r_11`, Tank, LEAF.
- **Hook:** a forest warden whose armour is still growing.
- **Look:** Mossy green hair, bark plate armour sprouting new leaves, a moss cloak, and a slab of a bark shield.
- **Palette:** Bark brown and lichen green.
- **Ability VFX (`vfx_comp_r_11`):** Roots burst from the ground and wrap the party in a leafy glow.

### Lottie
`comp_r_12`, Merchant, NONE.
- **Hook:** a card dealer who always knows what is in the deck.
- **Look:** A sleek black bob, a dealer's vest and bowtie, a short skirt, sleeve garters, and a fan of oversized cards in one hand.
- **Palette:** Black and white, one suit red.
- **Ability VFX (`vfx_comp_r_12`):** Cards flip across the screen and each one turns into a gold coin.

### Cadence
`comp_r_13`, Bard, WATER.
- **Hook:** a harbour songstress whose songs bring ships, and their cargo, home.
- **Look:** Long wavy periwinkle hair, pearl earrings, a flowing off-shoulder blouse over a fishtail skirt, and a shell lyre.
- **Palette:** Periwinkle and silver.
- **Ability VFX (`vfx_comp_r_13`):** Music notes ripple outward and gold coins bob up like buoys.

### Maple
`comp_r_14`, Cleric, LEAF.
- **Hook:** a forest priestess who taps the old trees for sap and brews it into mana syrup.
- **Look:** Rust-red hair in a braided crown, an autumn-leaf cape over a cream priest robe, and a sap lantern that glows amber.
- **Palette:** Rust orange and cream.
- **Ability VFX (`vfx_comp_r_14`):** Amber droplets fall from the lantern onto the party and refill their mana.

## Common

### Hazel
`comp_c_01`, Warrior, LEAF.
- **Hook:** a woodcutter's daughter who swings an axe bigger than she is.
- **Look:** Short brown hair under a green kerchief, rolled-up sleeves, a work apron-dress, and an oversized wood axe.
- **Palette:** Moss green and tan.
- **Ability VFX (`vfx_comp_c_01`):** A chopping axe arc that throws wood chips.

### Tilly
`comp_c_02`, Warrior, NONE.
- **Hook:** a tavern girl who learned to fight closing up on rowdy nights.
- **Look:** A high ponytail, a plain work dress and apron, a dish towel over one shoulder, and a cast-iron frying pan.
- **Palette:** Cream and brick red.
- **Ability VFX (`vfx_comp_c_02`):** A frying-pan swing and a cartoon clang star.

### Mina
`comp_c_03`, Mage, FIRE.
- **Hook:** an apprentice mage whose spells work, mostly.
- **Look:** A cat-ear hood, singed robes too long for her, and a wand too big for her.
- **Palette:** Orange and charcoal.
- **Ability VFX (`vfx_comp_c_03`):** A wobbly fireball that puffs smoke on impact.

### Rue
`comp_c_04`, Caster, WATER.
- **Hook:** a fisher girl who casts spells with a fishing rod, and sometimes catches a fish.
- **Look:** A yellow raincoat and rain hat, rubber boots, and a fishing rod with a glowing lure.
- **Palette:** Sunflower yellow and teal.
- **Ability VFX (`vfx_comp_c_04`):** The lure splashes down and a water spout hits the enemy.

### Koko
`comp_c_05`, Warrior, FIRE.
- **Hook:** a cat-eared blacksmith's apprentice who tests her hammers on monsters.
- **Look:** Cat ears, sooty cheeks, goggles pushed up on her forehead, a leather apron, and a forge hammer.
- **Palette:** Red and soot grey.
- **Ability VFX (`vfx_comp_c_05`):** A hammer smash that throws sparks.

### Wren
`comp_c_06`, Warrior, NONE.
- **Hook:** a shepherd who is very protective of her flock, and very good with a sling.
- **Look:** Curly white-blonde hair, a woolly shawl, a shepherd's crook, and a sling.
- **Palette:** Sky blue and wool white.
- **Ability VFX (`vfx_comp_c_06`):** A sling stone hits with a sheep-shaped puff of cloud.

### Dot
`comp_c_07`, Mage, LIGHT.
- **Hook:** the town's lamplighter, who found out her lamp can do more than light streets.
- **Look:** A bob under a flat cap, a short work coat, and a long lantern pole.
- **Palette:** Navy and lamplight amber.
- **Ability VFX (`vfx_comp_c_07`):** The lantern flashes and a bolt of light streaks to the enemy.

### Sable
`comp_c_08`, Death Knight, DARK.
- **Hook:** a gravedigger's daughter who is friends with every ghost in town.
- **Look:** Messy grey twin tails, an oversized black hooded cloak, a spade, and a little ghost floating at her shoulder.
- **Palette:** Grey and plum.
- **Ability VFX (`vfx_comp_c_08`):** The ghost dives at the enemy and bursts in a purple puff.

### Juniper
`comp_c_09`, Caster, LEAF.
- **Hook:** a hedge-witch apprentice who throws thornberries harder than spells.
- **Look:** Braided green-black hair, a patched olive cloak, berry pouches on her belt, and a twig wand.
- **Palette:** Dark olive and berry pink.
- **Ability VFX (`vfx_comp_c_09`):** A spray of thorny berries.

### Greta
`comp_c_10`, Tank, NONE.
- **Hook:** a gate guard who never lets anything through, enemies included.
- **Look:** Blonde braids under a round helmet, a town tabard, and a wooden shield as big as a door.
- **Palette:** Steel and mustard.
- **Ability VFX (`vfx_comp_c_10`):** A shield thump and a mustard ring around the party.

### Poppy
`comp_c_11`, Paladin, LIGHT.
- **Hook:** a temple novice whose bell rings everyone back into the fight.
- **Look:** Short red hair, a white novice habit, and a mace with a bell for a head.
- **Palette:** White and poppy red.
- **Ability VFX (`vfx_comp_c_11`):** A bell chime rings out and gold glints settle on the party.

### Brina
`comp_c_12`, Tank, WATER.
- **Hook:** a dockhand who uses a ship's anchor as a shield.
- **Look:** A bandana, a striped shirt, rolled trousers, a coil of rope, and a broad anchor-shaped shield.
- **Palette:** Slate blue and rope tan.
- **Ability VFX (`vfx_comp_c_12`):** The anchor slams down and a ring of water glows blue around the party.

### Nell
`comp_c_13`, Paladin, FIRE.
- **Hook:** a militia drummer whose beat keeps the party in step.
- **Look:** A pillbox cap, a scarlet jacket with brass buttons, and a snare drum with sticks.
- **Palette:** Scarlet, black and brass.
- **Ability VFX (`vfx_comp_c_13`):** Each drum beat sends a red pulse ring through the party.

### Penny
`comp_c_14`, Merchant, NONE.
- **Hook:** a peddler with a backpack of everything, and a price for all of it.
- **Look:** A giant patchwork backpack with pots and trinkets dangling, a denim smock, and a coin pouch.
- **Palette:** Denim blue and coin gold.
- **Ability VFX (`vfx_comp_c_14`):** The backpack bounces and coins spill out.

### Lark
`comp_c_15`, Bard, LEAF.
- **Hook:** a street fiddler who plays for coins, and gets them.
- **Look:** A feathered cap, a mint waistcoat, and a fiddle.
- **Palette:** Mint and coral.
- **Ability VFX (`vfx_comp_c_15`):** Notes flutter up and turn into coins.

### Clove
`comp_c_16`, Merchant, FIRE.
- **Hook:** a spice-cart girl whose curry is worth its weight in gold.
- **Look:** A headscarf, a cinnamon tunic, a ladle, and a wooden spice box.
- **Palette:** Cinnamon brown and turquoise.
- **Ability VFX (`vfx_comp_c_16`):** A puff of spice with gold sparkles in it.

### Faye
`comp_c_17`, Cleric, LIGHT.
- **Hook:** a village nurse with a teapot for every ailment.
- **Look:** A nurse's cap, a pale blue dress with a white apron, a satchel, and a teapot.
- **Palette:** Pale blue and white.
- **Ability VFX (`vfx_comp_c_17`):** The teapot pours glowing tea into a ring of cups around the party.

### Bea
`comp_c_18`, Sage, NONE.
- **Hook:** a library page who has read every book on mana, and is carrying most of them.
- **Look:** Round glasses, a messy bun with a pencil through it, a burgundy cardigan, and an armful of books.
- **Palette:** Burgundy and parchment.
- **Ability VFX (`vfx_comp_c_18`):** Pages flutter out of her books and glow over the party, refilling their mana.

## Pilot asset status (issue #291)
The images in Unity are **stand-ins cut from the reference sheets** by `tools/art/cut_pilot_assets.py`. They don't replace the artist's deliverables:

| Asset | Stand-in | Final, from the artist |
|---|---|---|
| Portrait `portrait_comp_*` (256 rp) | crop of the splash art | clean portrait |
| Icon `icon_comp_*` (128 rp) | face crop | icon drawn for 88-128 rp |
| Splash `art_splash_*` (1080 x 1920 rp, full screen) | 9:16 window of the splash without the baked-in text (Yuki: from her vertical splash; the others are upscaled about 2x from the sheets, so they look soft) | clean full-screen illustration |
| Battle chibi `spr_comp_*` (about 48 px native) | the reference chibi sampled on its own pixel grid, background and pedestal removed, 1 frame | idle 4 + cast 6 pixel frames |
| VFX `vfx_comp_*` | none (the generic ring placeholder) | particle system |

### Splash pilot notes (Yuki)
- **What arrived:** a 2:3 vertical splash (1024 x 1536) with the title, class and element chips baked into the top 265 px.
- **What the game needs:** full-screen character art at 1080 x 1920 rp (9:16) with no baked-in text (`specs/07` section 15.2). The UI draws name, rarity and role. The reveal scales it to cover the screen, so taller phones trim the sides and wider screens trim the top and bottom. Keep the face and the key silhouette inside the centred 810 x 1440 rp safe zone.
- **Stand-in cut:** x 204-916, y 270-1536 (everything below the chips), scaled up about 1.5x. The left hair ends and the outer half of the parasol canopy are lost.
- **Ask the artist for:** 1080 x 1920 or larger, no text, with the character composed for 9:16 and inside the safe zone. Then nothing is cropped beyond the screen trim and nothing is upscaled.
