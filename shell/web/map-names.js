// Cache file name -> in-game map name, from https://www.halopedia.org/Map_file.
// Internal names stay the keys for native calls; only labels shown to the user use these.
(function () {
  const names = {
    '005_intro': 'Arrival', '010_jungle': 'Sierra 117', '020_base': "Crow's Nest", '030_outskirts': 'Tsavo Highway',
    '040_voi': 'The Storm', '050_floodvoi': 'Floodgate', '070_waste': 'The Ark', '100_citadel': 'The Covenant',
    '110_hc': 'Cortana', '120_halo': 'Halo', '130_epilogue': 'Epilogue',
    construct: 'Construct', salvation: 'Epitaph', guardian: 'Guardian', deadlock: 'High Ground', isolation: 'Isolation',
    zanzibar: 'Last Resort', chill: 'Narrows', shrine: 'Sandtrap', snowbound: 'Snowbound', cyberdyne: 'The Pit',
    riverworld: 'Valhalla', warehouse: 'Foundry', armory: "Rat's Nest", bunkerworld: 'Standoff', sidewinder: 'Avalanche',
    lockout: 'Blackout', ghosttown: 'Ghost Town', chillout: 'Cold Storage', descent: 'Assembly', spacecamp: 'Orbital',
    sandbox: 'Sandbox', fortress: 'Citadel', docks: 'Longshore', midship: 'Heretic',
    s3d_waterfall: 'Waterfall', s3d_edge: 'Edge', s3d_turf: 'Icebox'
  };
  const titles = {
    halo3: names,
    haloreach: {
      '20_sword_slayer':'Sword Base', '30_settlement':'Powerhouse', '35_island':'Spire', '45_aftship':'Zealot',
      '45_launch_station':'Countdown', '50_panopticon':'Boardwalk', '52_ivory_tower':'Reflection', '70_boneyard':'Boneyard',
      forge_halo:'Forge World', dlc_slayer:'Anchor 9', dlc_invasion:'Breakpoint', dlc_medium:'Tempest',
      condemned:'Condemned', trainingpreserve:'Highlands', cex_beaver_creek:'Battle Canyon', cex_beavercreek:'Battle Canyon', cex_headlong:'Breakneck',
      cex_hangemhigh:'High Noon', cex_damnation:'Penance', cex_timberland:'Ridgeline', cex_prisoner:'Solitary',
      ff50_park:'Beachhead', ff45_corvette:'Corvette', ff20_courtyard:'Courtyard', ff60_icecave:'Glacier',
      ff70_holdout:'Holdout', ff60_airview:'Outpost', ff10_prototype:'Overlook', ff30_waterfront:'Waterfront',
      ff_unearthed:'Unearthed', cex_ff_halo:'Installation 04',
      m05:'Noble Actual', m10:'Winter Contingency', m20:'ONI: Sword Base', m30:'Nightfall', m35:'Tip of the Spear',
      m45:'Long Night of Solace', m50:'Exodus', m52:'New Alexandria', m60:'The Package', m70:'The Pillar of Autumn',
      m70_a:'Epilogue', m70_bonus:'Lone Wolf'
    },
    halo4: {
      ca_blood_cavern:'Abandon', ca_warhouse:'Adrift', z05_cliffside:'Complex', ca_forge_erosion:'Erosion',
      ca_blood_crash:'Exile', wraparound:'Haven', ca_forge_bonanza:'Impact', ca_gore_valley:'Longbow',
      ca_canyon:'Meltdown', z11_valhalla:'Ragnarok', ca_forge_ravine:'Ravine', ca_tower:'Solace', ca_redoubt:'Vortex',
      zd_02_grind:'Harvest', dlc_dejewel:'Shatter', dlc_dejunkyard:'Wreckage', ca_port:'Landfall',
      ca_deadlycrossing:'Monolith', ca_rattler:'Skyline', dlc_forge_island:'Forge Island', ca_spiderweb:'Daybreak',
      ca_basin:'Outcast', ca_highrise:'Perdition', ca_creeper:'Pitfall', ca_dropoff:'Vertigo',
      m05_prologue:'Prologue', m10_crash:'Dawn', m020:'Requiem', m30_cryptum:'Forerunner', m60_rescue:'Infinity',
      m40_invasion:'Reclaimer', m70_liftoff:'Shutdown', m80_delta:'Composer', m90_sacrifice:'Midnight', m95_epilogue:'Epilogue',
      ff87_chopperbowl:'Quarry', ff86_sniperalley:'Sniper Alley', ff90_fortsw:'Fortress', ff84_temple:'The Refuge',
      ff82_scurve:'The Cauldron', ff81_courtyard:'The Gate', ff91_complex:'Galileo Base', ff92_valhalla:'Two Giants',
      dlc01_factory:'Lockup', ff151_mezzanine:'Control', ff153_caverns:'Warrens', ff152_vortex:'Cyclone',
      ff155_breach:'Harvester', dlc01_engine:'Infinity', ff154_hillside:'Apex'
    },
    groundhog: {
      ca_coagulation:'Bloodline', ca_lockout:'Lockdown', ca_sanctuary:'Shrine', ca_zanzibar:'Stonetown',
      ca_warlock:'Warlord', ca_ascension:'Zenith', ca_forge_skybox01:'Skyward', ca_forge_skybox02:'Nebula',
      ca_forge_skybox03:'Awash', ca_relic:'Remnant'
    }
  };
  function key(name) {
    return String(name || '').replace(/\\/g, '/').split('/').pop().replace(/\.map$/i, '').replace(/^REMOVED_/i, '').toLowerCase();
  }
  function display(name, title = 'halo3') {
    const id = key(name), table = titles[String(title).toLowerCase()];
    return (table && Object.hasOwn(table, id) ? table[id] : '') || (id ? id.charAt(0).toUpperCase() + id.slice(1) : String(name || ''));
  }
  function artworkKey(name, title = 'halo3') {
    title = String(title).toLowerCase();
    const id = key(name);
    // H2A uses the original map names in its pack; Forge canvases use labels.
    let texture = title === 'groundhog' && !id.startsWith('ca_forge_') ? id.replace(/^ca_/, '').toUpperCase()
      : display(name, title).replace(/[^a-z0-9]/gi, '').toUpperCase();
    if (title === 'halo3') texture = ({EDGE:'S3D_EDGE', WATERFALL:'S3D_WATERFALL', ICEBOX:'S3D_ICEBOX'})[texture] || texture;
    return title === 'halo3' ? texture : title + ':' + texture;
  }
  window.MccMapNames = { names:titles, display, artworkKey };
  // Retain the original API for existing consumers.
  window.Halo3MapNames = { names, display };
})();
