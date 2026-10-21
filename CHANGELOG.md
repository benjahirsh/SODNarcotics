# Changelog

## 0.3.0
Save/load persistence overhaul and continuation of narcotics operations:

- Persist generated `NarcDrug_*` preset recipes and re-register the original drug keys and appearances before native save data is loaded.
- Restore active narcotics investigations with the original case, cook, distributor, dealers, lab and dead-drop assignments; avoid duplicate replacement operations.
- Preserve historical drug evidence identities and case links after an investigation is resolved; historical evidence-card images still need regression testing.
- Rebind native drug interactables in NPC inventories, tracking original holders and item identities without replenishing missing or confiscated drugs.
- Match saved operation snapshots to the selected native save using save identity, timestamp and game time, including hot-load safeguards to avoid restoring stale or ambiguous data.
- Restore cook-shift timing, remaining batches and between-shift scheduling rather than restarting the production clock.
- Restore buyer tracking, laboratory stash records and saved overdose scheduling without replaying sales or spawning replacement items.
- Preserve searchable drug evidence in dealer dead drops after collection and across save/reload.
- Track original world-item and laboratory evidence IDs and saved furniture placements instead of assigning substitute locations; static lab evidence visibility on cold load still needs verification.
- Persist pre-case operations before the first overdose; prevent the game's new-case bootstrap from replacing an operation awaiting restoration.
- Resume the original cook's travel goal after loading so arrival at the lab, production and distributor collection can continue.
- Supersede the 0.2.1 non-saveable-dose workaround: doses are once again eligible for native serialization, with custom presets restored by the 0.3.0 load hooks.

## 0.2.1
Fixed a save/reload persistence bug

## 0.2.0
Added 2 more drug types, fixed duplicate fingerprints bug

## 0.1.4
Limited drugs to one object type to fix supply-chain stall

## 0.1.3
Fixed a supply-chain stall that could occur with some of the drug items.

## 0.1.2
Readme update

## 0.1.1
Fixed git url

## 0.1.0
Initial commit
